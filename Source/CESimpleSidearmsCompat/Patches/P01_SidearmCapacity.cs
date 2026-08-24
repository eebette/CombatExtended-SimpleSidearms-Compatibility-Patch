using System;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 1: SS decides what a pawn may carry as a sidearm without CE's bulk model.
    /// Weight is already CE-aware through CE's MassUtility.Capacity patch; bulk is not.
    ///
    /// SS has two doors onto that decision and they do not share a gate. The gizmo and the
    /// float menus ask StatCalculator.CanPickupSidearmType; JobGiver_RetrieveWeapon, which
    /// fetches remembered weapons on its own from the vanilla think tree, asks nothing at
    /// all — its pickup driver ends in a bare innerContainer.TryAdd. Both are patched here.
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.CanPickupSidearmType))]
    public static class StatCalculator_CanPickupSidearmType_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ThingDefStuffDefPair sidearmType, Pawn pawn, ref string errString, ref bool __result)
        {
            if (!__result || pawn == null || sidearmType.thing == null)
            {
                return;
            }
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null)
            {
                return;
            }
            float bulk = sidearmType.thing.GetStatValueAbstract(CE_StatDefOf.Bulk, sidearmType.stuff);
            if (bulk <= 0f)
            {
                return;
            }
            // currentBulk is CE's cached figure, kept fresh on every inventory change;
            // capacityBulk is a live CarryBulk stat read. Still far cheaper than the
            // GetAvailableBulk(true) full recount, which matters because SS calls this
            // inside a filter over every valid sidearm pair at pawn generation.
            if (bulk > inventory.capacityBulk - inventory.currentBulk)
            {
                errString = "SidearmPickupFail_NoFreeSpace".Translate();
                __result = false;
            }
        }
    }

    /// <summary>
    /// The second door: SS's own retrieval never consults CanPickupSidearmType, so without
    /// this a pawn walks up to 1000 cells for a weapon CE has no room for, and it then
    /// counts against everything else they carry.
    ///
    /// Known limitation, tracked separately: SS returns on the first unsatisfied memory it
    /// can find an instance for, so cancelling the job here also skips every memory behind
    /// it in the list, and the refused weapon is re-searched on each think pass.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_RetrieveWeapon), nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic))]
    public static class JobGiver_RetrieveWeapon_TryGiveJobStatic_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(JobGiver_RetrieveWeapon), "TryGiveJobStatic",
                                   new[] { typeof(Pawn), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE-SS Compat] JobGiver_RetrieveWeapon.TryGiveJobStatic not found — sidearm "
                      + "retrieval will not be capacity-checked. Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
            {
                return;
            }
            try
            {
                Thing target = __result.targetA.Thing;
                CompInventory inventory = pawn?.TryGetComp<CompInventory>();
                if (target == null || inventory == null)
                {
                    return;
                }
                if (!inventory.CanFitInInventory(target, out int _))
                {
                    __result = null;
                }
            }
            catch (Exception e)
            {
                // Reached from the think tree and from SS's AutoUndrafter every 100 ticks;
                // a throw here would be a flood, so leave SS's job untouched instead.
                Log.ErrorOnce("[CE-SS Compat] Capacity check on sidearm retrieval failed: " + e, 0x43455352);
            }
        }
    }
}
