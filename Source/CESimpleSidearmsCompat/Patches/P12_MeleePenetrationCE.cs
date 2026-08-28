using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 12: SS's melee ranking is (damage + damage x penetration) / speed — penetration
    /// carries half the numerator. Its penetration term reads the vanilla tool field, which
    /// CE weapons leave unset (CE puts armor penetration in ToolCE's own sharp/blunt
    /// fields), so vanilla falls back to its derived stub — a near-uniform damage x 0.015
    /// for every weapon. The signal SS weights so heavily is dead under CE, the same
    /// disease axis 2 fixed for ranged accuracy: a longsword and a club rank on raw
    /// damage-per-second while CE melee outcomes against armor are dominated by exactly the
    /// number being ignored.
    ///
    /// The fix patches SS's INPUT function only — SS's formula stays SS's. The replacement
    /// value is CE's own per-tool penetration (chance-weighted, times the instance's
    /// CE MeleePenetrationFactor for material and quality), normalized onto the
    /// dimensionless scale SS's formula expects:
    ///
    ///   penetration = max(sharp / 2.25 mmRHA, blunt / 5.625 MPa) x MeleePenetrationFactor
    ///
    /// The references are the sharpest and heaviest base-game CE melee tools (a steel
    /// spear's point, a mace's head), so a weapon at the top of either scale roughly
    /// doubles its damage term — the same order of effect vanilla's own penetration
    /// multiplier has. Sharp and blunt live on different physical scales (mmRHA vs MPa)
    /// and cannot be averaged; taking the better of the two normalized values ranks each
    /// weapon by the armor-defeating mode it is actually built for. The shooter's melee
    /// skill factor is deliberately omitted: one pawn ranks all their carried weapons, so
    /// it scales every candidate equally. Target armor stays out on purpose — ranking
    /// against the actual enemy's armor is target-aware selection, which belongs to the
    /// Tactics module, not a compatibility patch.
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.MeleePenetration),
                  new[] { typeof(ThingWithComps), typeof(Pawn) })]
    public static class StatCalculator_MeleePenetration_Patch
    {
        /// <summary>The sharpest base-game CE melee tool (spear point, 2.25 mmRHA).</summary>
        internal const float ReferenceSharp = 2.25f;

        /// <summary>The heaviest base-game CE melee tool (mace head, 5.625 MPa).</summary>
        internal const float ReferenceBlunt = 5.625f;

        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "MeleePenetration",
            new[] { typeof(ThingWithComps), typeof(Pawn) },
            "melee weapons will rank with a dead penetration term under CE (raw damage-per-second only).");

        [HarmonyPrefix]
        public static bool Prefix(ThingWithComps weapon, ref float __result)
        {
            try
            {
                return PrefixInner(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE melee-penetration scoring failed; Simple "
                              + "Sidearms' own read is used instead. " + e, 0x43455314);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(ThingWithComps weapon, ref float __result)
        {
            List<Tool> tools = weapon?.def?.tools;
            // Vanilla-tool weapons (un-CE-patched mod weapons, tech-hediff items whose
            // tools live on the hediff) keep SS's own read — it is correct for them.
            if (tools == null || tools.Count == 0 || !(tools[0] is ToolCE))
            {
                return true;
            }
            float totalChance = 0f;
            foreach (Tool tool in tools)
            {
                totalChance += tool.chanceFactor;
            }
            if (totalChance <= 0f)
            {
                return true;
            }
            float sharp = 0f;
            float blunt = 0f;
            foreach (Tool tool in tools)
            {
                if (!(tool is ToolCE ce))
                {
                    continue;
                }
                float weight = tool.chanceFactor / totalChance;
                sharp += weight * ce.armorPenetrationSharp;
                blunt += weight * ce.armorPenetrationBlunt;
            }
            // Material and quality, via CE's own instance stat — the same factor CE's
            // melee-penetration readout applies.
            float instanceFactor = weapon.GetStatValue(CE_StatDefOf.MeleePenetrationFactor);
            __result = Mathf.Max(sharp / ReferenceSharp, blunt / ReferenceBlunt) * instanceFactor;
            return false;
        }
    }
}
