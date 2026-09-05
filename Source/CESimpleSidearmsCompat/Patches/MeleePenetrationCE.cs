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
    /// Patches SS's melee-penetration input to CE's own armor-penetration values.
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
            // Skip vanilla-tool weapons.
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
            // Take the better of the two normalized modes.
            // Factor = CE's own instance stat.
            float instanceFactor = weapon.GetStatValue(CE_StatDefOf.MeleePenetrationFactor);
            __result = Mathf.Max(sharp / ReferenceSharp, blunt / ReferenceBlunt) * instanceFactor;
            return false;
        }
    }
}
