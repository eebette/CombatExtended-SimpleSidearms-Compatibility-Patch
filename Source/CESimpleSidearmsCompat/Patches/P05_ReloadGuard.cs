using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using Verse;
using Verse.AI;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 5: SS's automatic weapon swapping can fire mid CE reload, cancelling the reload
    /// job and wasting the attempt. Idle/optimisation preference swaps are suppressed during
    /// a reload; explicit/specific swaps end the reload cleanly first.
    ///
    /// Swaps the pawn did not choose the timing of are NOT suppressed. SS routes its
    /// close-quarters response through the same method (doCQC → tryCQCWeaponSwapToMelee),
    /// and reads a false return as "no weapon drawn" — which also skips the retaliation
    /// job, so blanket suppression left a reloading pawn standing there being stabbed.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByPreference),
                  new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) })]
    public static class WeaponAssingment_equipBestByPreference_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipBestWeaponFromInventoryByPreference",
            new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) },
            "Simple Sidearms' automatic swaps can cancel a Combat Extended reload mid-way.");

        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, DroppingModeEnum dropMode, PrimaryWeaponMode? modeOverride)
        {
            try
            {
                return PrefixInner(pawn, dropMode, modeOverride);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload guard on preference swaps failed; swaps "
                              + "run unguarded. " + e, 0x43455308);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(Pawn pawn, DroppingModeEnum dropMode, PrimaryWeaponMode? modeOverride)
        {
            if (pawn?.CurJobDef != CE_JobDefOf.ReloadWeapon)
            {
                return true;
            }
            // Melee override: doCQC (attacked in melee) and chooseOptimalMeleeForAttack
            // (ordered to melee). UsedUp: the weapon is already gone, so there is no
            // reload worth protecting. Everything else waits for the reload to finish.
            return modeOverride == PrimaryWeaponMode.Melee || dropMode == DroppingModeEnum.UsedUp;
        }
    }

    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipSpecificWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) },
            "explicit weapon switches during a reload will not end the reload job cleanly first.");

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn)
        {
            try
            {
                PrefixInner(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload hand-off on explicit switches failed. " + e, 0x43455309);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PrefixInner(Pawn pawn)
        {
            if (pawn?.CurJobDef == CE_JobDefOf.ReloadWeapon)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
            }
        }
    }
}
