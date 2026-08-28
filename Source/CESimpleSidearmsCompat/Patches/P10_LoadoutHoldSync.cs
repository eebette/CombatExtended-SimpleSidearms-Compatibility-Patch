using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 10: CE loadout enforcement (JobGiver_UpdateLoadout → GetExcessThing /
    /// GetExcessEquipment) drops inventory items that aren't in the pawn's CE loadout or
    /// hold records — which includes SS-remembered sidearms, causing drop/retrieve churn.
    ///
    /// The exemption is answered where CE asks the question, and nothing is written back.
    /// CE's hold-tracker is shared state: HoldRecord has no owner field and
    /// Notify_HoldTrackerItem merges by ThingDef, so a record we created and one the
    /// player created with CE's own "hold N of these" command are the same object.
    /// Editing it from here corrupted player-set counts, fought CE's clear-forced-hold
    /// button, and — because CE deletes picked-up records whose def has left the
    /// inventory container — churned a create/delete cycle for equipped weapons.
    /// </summary>
    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetExcessThing),
                  new[] { typeof(Pawn), typeof(Thing), typeof(int) },
                  new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    public static class Utility_HoldTracker_GetExcessThing_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Utility_HoldTracker), "GetExcessThing",
            new[] { typeof(Pawn), typeof(Thing).MakeByRefType(), typeof(int).MakeByRefType() },
            "CE loadout enforcement will drop remembered sidearms from inventory (drop/retrieve churn).");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Thing dropThing, ref int dropCount, ref bool __result)
        {
            try
            {
                PostfixInner(pawn, ref dropThing, ref dropCount, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Remembered-sidearm drop exemption failed. " + e, 0x4345530E);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref Thing dropThing, ref int dropCount, ref bool __result)
        {
            if (!__result || dropThing == null || !dropThing.def.IsWeapon)
            {
                return;
            }
            if (CompatUtil.SSRemembers(pawn, dropThing))
            {
                __result = false;
                dropThing = null;
                dropCount = 0;
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
