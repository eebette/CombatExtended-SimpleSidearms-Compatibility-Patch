using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Exempts SS-remembered sidearms from CE's loadout-drop (JobGiver_UpdateLoadout →
    /// GetExcessThing) by subtracting the remembered-beyond-loadout count from CE's own per-def
    /// stock (GetStorageByThingDef).
    /// </summary>
    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetStorageByThingDef),
                  new[] { typeof(Pawn) })]
    public static class Utility_HoldTracker_GetStorageByThingDef_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Utility_HoldTracker), "GetStorageByThingDef",
            new[] { typeof(Pawn) },
            "CE loadout enforcement will drop remembered sidearms from inventory (drop/retrieve churn).");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Remembered-sidearm shield failed; CE counts "
                              + "remembered weapons as excess. " + e, 0x4345530E);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            if (pawn == null || __result == null || __result.Count == 0)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false);
            if (memory?.RememberedWeapons == null || memory.RememberedWeapons.Count == 0)
            {
                return;
            }
            // Subtract the pawn's remembered sidearms from the "excess" count so CE won't drop
            // them.
            //
            // Use loadout.Slots, not GetSlotsFor(): on an "Ad Hoc" loadout GetSlotsFor() calls this
            // method, which calls GetSlotsFor() again — an endless loop that hard-crashed the game
            // when the "Ad Hoc" box was ticked.
            List<HoldRecord> holdRecords = LoadoutManager.GetHoldRecords(pawn);
            Dictionary<ThingDef, int> rowCounts = null;
            Loadout loadout = pawn.GetLoadout();
            if (loadout != null && !loadout.defaultLoadout)
            {
                rowCounts = loadout.Slots
                    .Where(slot => slot.thingDef != null && slot.thingDef.IsWeapon)
                    .GroupBy(slot => slot.thingDef)
                    .ToDictionary(g => g.Key, g => g.Sum(slot => slot.count));
                // Count the equipped primary.
                if (loadout.adHoc && (pawn.Faction?.IsPlayer ?? false)
                    && pawn.equipment?.Primary != null)
                {
                    ThingDef primaryDef = pawn.equipment.Primary.def;
                    if (!rowCounts.ContainsKey(primaryDef))
                    {
                        rowCounts[primaryDef] = 1;
                    }
                }
            }
            foreach (ThingDef def in __result.Keys.Where(d => d.IsWeapon).ToList())
            {
                int remembered = memory.RememberedWeapons.Count(pair => pair.thing == def);
                if (remembered == 0)
                {
                    continue;
                }
                int rows = 0;
                rowCounts?.TryGetValue(def, out rows);
                // Subtract the held count so we shield only the extra remembered weapon (since it appears in both).
                int held = holdRecords?.FirstOrDefault(r => r.thingDef == def)?.count ?? 0;
                int shield = remembered - rows - held;
                if (shield <= 0)
                {
                    continue;
                }
                __result[def].value -= shield;
                if (__result[def].value <= 0)
                {
                    __result.Remove(def);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetExcessEquipment),
                  new[] { typeof(Pawn), typeof(ThingWithComps) },
                  new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class Utility_HoldTracker_GetExcessEquipment_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Utility_HoldTracker), "GetExcessEquipment",
            new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType() },
            "CE loadout enforcement will strip remembered equipped weapons.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            try
            {
                PostfixInner(pawn, ref dropEquipment, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Remembered-equipment drop exemption failed. " + e, 0x4345530F);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            if (!__result || dropEquipment == null)
            {
                return;
            }
            if (CompatUtil.SSRemembers(pawn, dropEquipment))
            {
                __result = false;
                dropEquipment = null;
            }
        }
    }
}
