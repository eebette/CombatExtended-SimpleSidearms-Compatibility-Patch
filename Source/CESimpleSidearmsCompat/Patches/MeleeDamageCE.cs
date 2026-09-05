using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Patches SS's melee ranking to score CE blades on CE's per-tool damage and speed.
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.getMeleeDPSBiased),
                  new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) })]
    public static class StatCalculator_getMeleeDPSBiased_Patch
    {
        public static bool Prepare()
        {
            // We edit only the damage term (below); a change to the SURROUNDING formula would
            // leave that edit landing in a body it no longer fits. Fingerprint the method so a
            // reshape is a loud re-verify error, not silent mis-ranking.
            UpstreamFingerprint.Verify(typeof(StatCalculator), "getMeleeDPSBiased",
                UpstreamFingerprint.MeleeDpsBiasedHash,
                "the damage term this transpiler overrides in Simple Sidearms' melee-DPS formula");
            return PatchGuard.Require(typeof(StatCalculator), "getMeleeDPSBiased",
                new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) },
                "Simple Sidearms will rank CE blades by their handles (a club beats a longsword everywhere).");
        }

        // Override only SS's inline damage term (its AverageWeighted result, zeroed for CE blade
        // tools) with CE's per-tool damage; SS's formula runs untouched. Anchor: the sole
        // AverageWeighted call — on any other count, skip and log.
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int at = -1, count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Call && list[i].operand is MethodInfo mi
                    && mi.Name == "AverageWeighted")
                {
                    at = i;
                    count++;
                }
            }
            if (count != 1)
            {
                Log.Error(PatchGuard.LogPrefix + "getMeleeDPSBiased: expected one AverageWeighted call, found "
                          + count + " — CE blades keep vanilla's zeroed melee damage. "
                          + "(Simple Sidearms reshaped the method.)");
                return list;
            }
            // The damage is on the stack after the call; pass it + the weapon (arg0) through
            // CorrectDamage, whose result flows into SS's own store.
            list.Insert(at + 1, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(at + 2, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(MeleeDamageWeights), nameof(MeleeDamageWeights.CorrectDamage))));
            return list;
        }
    }

    /// <summary>
    /// Patches SS's melee-speed read to CE's per-tool speed for blade tools.
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
            if (!MeleeDamageWeights.TryCompute(weapon, out _, out float speed))
            {
                return true;
            }
            __result = speed;
            return false;
        }
    }

    internal static class MeleeDamageWeights
    {
        /// <summary>
        /// CE's per-tool damage for a ToolCE weapon, else the passed-through original.
        /// Called from the getMeleeDPSBiased transpiler with SS's own computed damage as the
        /// fallback for non-CE weapons.
        /// </summary>
        internal static float CorrectDamage(float original, ThingWithComps weapon)
        {
            try
            {
                return TryCompute(weapon, out float damage, out _) ? damage : original;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE melee-damage override failed; Simple "
                              + "Sidearms' own damage read is used. " + e, 0x43455315);
                return original;
            }
        }

        /// <summary>
        /// Selection-weighted damage and cooldown over a weapon's CE tools, healthy where
        /// vanilla's are zeroed. False for weapons without CE tools.
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
                // The tool's damage, averaged over its maneuvers' damage defs — vanilla picks
                // one at random, so the uniform average is its expected damage.
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
                // Vanilla's weight shape, chance × damage² — recomputed, not read via
                // AdjustedMeleeSelectionWeight, which squares the same zeroed damage. Its constant
                // commonality factor is dropped: it cancels in the average below.
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
