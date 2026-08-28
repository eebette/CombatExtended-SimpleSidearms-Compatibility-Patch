using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
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
            if (!CompatUtil.SSRemembers(pawn, dropThing))
            {
                return;
            }
            // Count-aware (issue #23): SS memory is per type, so an unconditional veto made
            // every spare copy of a remembered pair undroppable — a Knife x1 loadout row
            // plus two battlefield pickups meant three knives forever. The exemption
            // protects as many instances as SS's memory (a multiset — one entry per copy)
            // or the CE loadout row asks for, whichever is more, and lets CE trim the rest.
            int wanted = ProtectedCount(pawn, dropThing);
            if (CarriedCount(pawn, dropThing) <= wanted)
            {
                __result = false;
                dropThing = null;
                dropCount = 0;
            }
        }

        private static int ProtectedCount(Pawn pawn, Thing thing)
        {
            var pair = new ThingDefStuffDefPair(thing.def, thing.Stuff);
            int remembered = CompSidearmMemory.GetMemoryCompForPawn(pawn, false)?
                .RememberedWeapons?.Count(p => p == pair) ?? 0;
            // CE loadout slots carry no stuff, so a def-wide row conservatively covers
            // every stuff variant — the same def-level matching CE's own tracker uses.
            int inLoadout = pawn.GetLoadout()?.Slots?
                .Where(slot => slot.thingDef == thing.def)
                .Sum(slot => slot.count) ?? 0;
            return Math.Max(remembered, inLoadout);
        }

        private static int CarriedCount(Pawn pawn, Thing thing)
        {
            var pair = new ThingDefStuffDefPair(thing.def, thing.Stuff);
            int count = pawn.inventory?.innerContainer?
                .Where(t => t.def == pair.thing && t.Stuff == pair.stuff)
                .Sum(t => t.stackCount) ?? 0;
            ThingWithComps primary = pawn.equipment?.Primary;
            if (primary != null && primary.def == pair.thing && primary.Stuff == pair.stuff)
            {
                count++;
            }
            return count;
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
            // Deliberately NOT count-aware, unlike the inventory exemption above: whatever
            // the counts, the equipped copy is the instance SS wants in the rotation, and
            // the inventory side already trims the spares — protecting the one in hand is
            // what makes the two converge on "carry exactly what was asked" instead of CE
            // stripping the primary while duplicates sit in the backpack.
            if (CompatUtil.SSRemembers(pawn, dropEquipment))
            {
                __result = false;
                dropEquipment = null;
            }
        }
    }
}
