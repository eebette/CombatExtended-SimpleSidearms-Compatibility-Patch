using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 9: CE's CompInventory.SwitchToNextViableWeapon (weapon destroyed, one-use
    /// consumed, grenade thrown, empty gun mid-cast) picks a replacement by CE's own
    /// heuristic, ignoring SS preferences and remembered sidearms. For SS-managed pawns, SS
    /// chooses; CE's logic (incl. fists) is the fallback when SS has no opinion. Specialized
    /// CE calls (AOE requests, predicated searches) pass through.
    ///
    /// The split follows the seam between the two mods: SS owns which weapon, CE owns what
    /// the swap costs. CE says which it wants through stopJob — true means swap now, false
    /// means queue an interruptible CE_JobDefOf.EquipFromInventory job. Equipping SS's pick
    /// directly would answer both questions and silently make CE's slow path instant, so
    /// when CE asks for the job, SS is consulted without letting it equip (see
    /// WeaponAssingment_equipSpecificWeapon_DryRun) and its answer is handed back to CE as a
    /// candidate filter — CE's own job branch does the rest.
    /// </summary>
    [HarmonyPatch(typeof(CompInventory), nameof(CompInventory.SwitchToNextViableWeapon),
                  new[] { typeof(bool), typeof(bool), typeof(bool), typeof(Func<ThingWithComps, CompAmmoUser, bool>) })]
    public static class CompInventory_SwitchToNextViableWeapon_Patch
    {
        private static bool inSSEquip;

        public static bool Prepare() => PatchGuard.Require(typeof(CompInventory), "SwitchToNextViableWeapon",
            new[] { typeof(bool), typeof(bool), typeof(bool), typeof(Func<ThingWithComps, CompAmmoUser, bool>) },
            "weapon switching after a loss or one-use consumption will ignore Simple Sidearms preferences.");

        [HarmonyPrefix]
        public static bool Prefix(CompInventory __instance, bool useFists, bool useAOE, bool stopJob,
                                  Func<ThingWithComps, CompAmmoUser, bool> predicate, ref bool __result)
        {
            try
            {
                return PrefixInner(__instance, useFists, useAOE, stopJob, predicate, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Switch arbitration failed; Combat Extended "
                              + "picks the replacement weapon on its own. " + e, 0x4345530D);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(CompInventory __instance, bool useFists, bool useAOE, bool stopJob,
                                        Func<ThingWithComps, CompAmmoUser, bool> predicate, ref bool __result)
        {
            if (inSSEquip || useAOE || predicate != null)
            {
                return true;
            }
            Pawn pawn = __instance.parentPawn;
            if (pawn == null || !pawn.IsValidSidearmsCarrierRightNow())
            {
                return true;
            }
            // Kept although CE's original opens with the same test (#22/V8 proposed deleting
            // it): this prefix does NOT fall through to the original on the arbitration
            // paths below, so without this guard SS would happily swap away a NoSwitch
            // weapon (persona weapons and the like) that CE refuses to. Reading a weaponTag
            // has exactly one shape and the tag is CE's published extension point — this is
            // convergent use of a public identifier, not a transcription.
            if (pawn.equipment?.Primary?.def.weaponTags?.Contains("NoSwitch") ?? false)
            {
                return true;
            }

            return stopJob
                ? SwitchNow(pawn, ref __result)
                : SwitchViaJob(__instance, pawn, useFists, useAOE, ref __result);
        }

        /// <summary>
        /// CE wanted an immediate swap, which is also how SS equips — let SS do the whole
        /// thing.
        /// </summary>
        private static bool SwitchNow(Pawn pawn, ref bool __result)
        {
            ThingWithComps before = pawn.equipment?.Primary;
            inSSEquip = true;
            try
            {
                // Blocked during CE reload jobs by the axis-5 guard, which then lets CE's
                // own picker run — intended interplay.
                WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, DroppingModeEnum.Combat);
            }
            finally
            {
                inSSEquip = false;
            }
            ThingWithComps after = pawn.equipment?.Primary;
            if (after != null && after != before)
            {
                __result = true;
                return false; // SS handled the switch
            }
            return WantsToStayUnarmed(pawn) ? Handled(ref __result) : true;
        }

        /// <summary>
        /// CE wanted to queue an equip job. Ask SS which weapon without letting it equip,
        /// then re-enter CE restricted to that weapon so CE's own stopJob branch runs.
        /// </summary>
        private static bool SwitchViaJob(CompInventory inventory, Pawn pawn, bool useFists, bool useAOE, ref bool __result)
        {
            ThingWithComps pick = WeaponAssingment_equipSpecificWeapon_DryRun.AskSS(pawn, out bool ssDecided);
            if (!ssDecided || pick == pawn.equipment?.Primary)
            {
                return true; // SS had no opinion, or none this call can act on — CE's turn
            }
            if (pick == null)
            {
                return Handled(ref __result); // SS's answer is "no weapon" — do not re-arm
            }

            inSSEquip = true;
            try
            {
                __result = inventory.SwitchToNextViableWeapon(useFists, useAOE, stopJob: false,
                                                              predicate: (weapon, _) => weapon == pick);
            }
            finally
            {
                inSSEquip = false;
            }
            // CE could not use SS's pick (its viability search is narrower than SS's) —
            // nothing was equipped, so hand the decision back rather than leaving the pawn
            // holding an empty gun.
            return !__result;
        }

        /// <summary>
        /// Forced-unarmed, forced-unarmed-while-drafted and preferred-unarmed all leave
        /// Primary null on success, so an empty hand is an answer rather than a failure. Ask
        /// SS instead of inferring it from the equipment pointer, or CE re-arms a pawn the
        /// player set to fists.
        /// </summary>
        private static bool WantsToStayUnarmed(Pawn pawn)
        {
            return pawn.equipment?.Primary == null
                   && (CompSidearmMemory.GetMemoryCompForPawn(pawn, false)?.IsCurrentWeaponForced(true) ?? false);
        }

        private static bool Handled(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// CONTRACT (relied on beyond this file): AskSS observes without acting. While it is on
    /// the stack, nothing is equipped, dropped, forgotten, or remembered — SS's preference
    /// tree is halted at the exact decision point and its answer extracted. Any change that
    /// lets a side effect escape the dry run breaks every caller that treats "ask SS" as a
    /// pure question, and the suite's arbitration phases with it.
    ///
    /// Every branch of SS's preference tree — forced weapon, forced-while-drafted, default
    /// ranged, preferred melee, unarmed, best-by-DPS — ends at this one method, and each
    /// returns as soon as it succeeds. Reporting success without equipping therefore stops
    /// the tree exactly at its decision and yields the weapon SS would have equipped,
    /// without this mod re-deriving any of that ordering (the type-resolving entry point
    /// even applies SS's own highest-market-value tie-break before it gets here).
    ///
    /// Only active inside AskSS, for one pawn, for the duration of one synchronous call.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_DryRun
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipSpecificWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) },
            "asking Simple Sidearms which weapon it prefers finds no answer, so Combat Extended's own pick is used.");

        private static Pawn askingFor;
        private static ThingWithComps answer;
        private static bool answered;

        /// <summary>
        /// The weapon SS would equip right now. <paramref name="decided"/> separates SS's two
        /// kinds of silence: false means it never reached an equip at all (no opinion), true
        /// with a null return means it deliberately chose to leave the pawn unarmed. Nothing
        /// is equipped, dropped, or remembered.
        /// </summary>
        public static ThingWithComps AskSS(Pawn pawn, out bool decided)
        {
            askingFor = pawn;
            answer = null;
            answered = false;
            try
            {
                WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, DroppingModeEnum.Combat);
            }
            catch (Exception e)
            {
                Log.Error($"[CE+SimpleSidearms] Simple Sidearms threw while being asked for a weapon preference; "
                          + $"Combat Extended's own choice will be used instead. {e}");
                answer = null;
                answered = false;
            }
            finally
            {
                askingFor = null;
            }
            decided = answered;
            return answer;
        }

        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ThingWithComps weapon, ref bool __result)
        {
            if (askingFor == null || pawn != askingFor)
            {
                return true;
            }
            answer = weapon; // null is a real answer: SS's "go unarmed" branches pass null
            answered = true;
            __result = true; // "equipped" — stops SS at the branch it decided on
            return false;
        }
    }
}
