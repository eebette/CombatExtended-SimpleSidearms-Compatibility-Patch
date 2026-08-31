using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 13: SS's melee DAMAGE and SPEED inputs are dead for CE's bladed weapons —
    /// the sibling of axis 12's dead penetration input, found while the Tactics module
    /// consumed these numbers.
    ///
    /// CE tags every ToolCE with a linkedBodyPartsGroup describing the WEAPON's
    /// anatomy (Blade, Point, Edge, Shaft — its own hit-location/durability model).
    /// Vanilla's AdjustedMeleeDamageAmount multiplies each tool's damage by the
    /// ATTACKER's average natural-part efficiency in that group, and a human has no
    /// body part in "Blade": the multiplier is 0. Handle and Head happen to also name
    /// real human part groups (hands, head), so blunt tools survive. The result is
    /// that every vanilla melee accessor — damage, and the damage-squared selection
    /// weight vanilla builds from it — reports a knife as its 1-damage handle poke,
    /// while a mace reports correctly. SS's StatCalculator reads exactly those
    /// accessors, so under CE, SS's melee ranking compares maces against the HANDLES
    /// of blades: a club outranks a longsword every time, everywhere SS ranks melee
    /// (preference picks, CQC swaps, the Tactics module's re-ranks).
    ///
    /// Fix in the axis-12 pattern: own SS's INPUT numbers for ToolCE weapons, keep
    /// SS's formula and call sites. Per tool, damage is vanilla's own healthy base —
    /// tool.AdjustedBaseMeleeDamageAmount (power × weapon damage multiplier × stuff
    /// sharp/blunt multiplier), averaged over the tool's maneuvers' damage defs —
    /// with the weapon-anatomy group term dropped, not re-derived: it describes the
    /// weapon, not the wielder. Tools combine under vanilla's selection-weight shape
    /// (chanceFactor × damage²). Attacker-side factors (life stage, part efficiency
    /// of real hands) are deliberately omitted, as in axis 12: one pawn ranks all
    /// their carried weapons, so they scale every candidate equally.
    ///
    /// SS's behavioral contract, reproduced from its own reads (not its code):
    /// score = damage × (1 + penetration) / biased speed, where biased speed =
    /// speed + (speed − averageSpeed) × (speedBias − 1). Penetration comes from
    /// StatCalculator.MeleePenetration — axis 12's corrected value.
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.getMeleeDPSBiased),
                  new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) })]
    public static class StatCalculator_getMeleeDPSBiased_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "getMeleeDPSBiased",
            new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) },
            "Simple Sidearms will rank CE blades by their handles (a club beats a longsword everywhere).");

        [HarmonyPrefix]
        public static bool Prefix(ThingWithComps weapon, Pawn pawn, float speedBias, float averageSpeed,
                                  ref float __result)
        {
            try
            {
                return PrefixInner(weapon, pawn, speedBias, averageSpeed, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE melee-damage scoring failed; Simple Sidearms' "
                              + "own read is used instead. " + e, 0x43455315);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(ThingWithComps weapon, Pawn pawn, float speedBias, float averageSpeed,
                                        ref float __result)
        {
            if (!P13Weights.TryCompute(weapon, out float damage, out float speed))
            {
                return true; // vanilla-tool weapon: SS's own read is correct for it
            }
            float penetration = StatCalculator.MeleePenetration(weapon, pawn);
            float biasedSpeed = speed + (speed - averageSpeed) * (speedBias - 1f);
            __result = biasedSpeed <= 0f ? 0f : damage * (1f + penetration) / biasedSpeed;
            return false;
        }
    }

    /// <summary>
    /// Same weighting for the bare speed read: SS averages MeleeSpeed over candidates
    /// to build the averageSpeed its bias pivots on, and the vanilla weights zero out
    /// blade tools there too (a knife's "speed" was its handle's).
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.MeleeSpeed),
                  new[] { typeof(ThingWithComps), typeof(Pawn) })]
    public static class StatCalculator_MeleeSpeed_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "MeleeSpeed",
            new[] { typeof(ThingWithComps), typeof(Pawn) },
            "Simple Sidearms' melee speed bias will read CE blades as their handles.");

        [HarmonyPrefix]
        public static bool Prefix(ThingWithComps weapon, ref float __result)
        {
            try
            {
                return PrefixInner(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE melee-speed scoring failed; Simple Sidearms' "
                              + "own read is used instead. " + e, 0x43455316);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(ThingWithComps weapon, ref float __result)
        {
            if (!P13Weights.TryCompute(weapon, out _, out float speed))
            {
                return true;
            }
            __result = speed;
            return false;
        }
    }

    internal static class P13Weights
    {
        /// <summary>
        /// Selection-weighted damage and cooldown over a weapon's CE tools, healthy
        /// where vanilla's are zeroed. False for weapons without CE tools.
        /// </summary>
        internal static bool TryCompute(ThingWithComps weapon, out float damage, out float speed)
        {
            damage = 0f;
            speed = 0f;
            List<Tool> tools = weapon?.def?.tools;
            if (tools == null || tools.Count == 0 || !(tools[0] is ToolCE))
            {
                return false;
            }
            float weightSum = 0f;
            float damageSum = 0f;
            float speedSum = 0f;
            foreach (Tool tool in tools)
            {
                if (!(tool is ToolCE))
                {
                    continue;
                }
                // The tool's damage under each of its maneuvers' damage defs (Cut and
                // Stab differ only via stuff multipliers), averaged uniformly the way
                // vanilla picks maneuvers at random.
                float toolDamage = 0f;
                int defs = 0;
                foreach (ManeuverDef maneuver in tool.Maneuvers)
                {
                    DamageDef def = maneuver.verb?.meleeDamageDef;
                    if (def == null)
                    {
                        continue;
                    }
                    toolDamage += tool.AdjustedBaseMeleeDamageAmount(weapon, def);
                    defs++;
                }
                if (defs == 0)
                {
                    continue;
                }
                toolDamage /= defs;
                // Vanilla's melee selection weight shape: chance × damage².
                float weight = tool.chanceFactor * toolDamage * toolDamage;
                if (weight <= 0f)
                {
                    continue;
                }
                weightSum += weight;
                damageSum += weight * toolDamage;
                speedSum += weight * tool.AdjustedCooldown(weapon);
            }
            if (weightSum <= 0f)
            {
                return false;
            }
            damage = damageSum / weightSum;
            speed = speedSum / weightSum;
            return true;
        }
    }
}
