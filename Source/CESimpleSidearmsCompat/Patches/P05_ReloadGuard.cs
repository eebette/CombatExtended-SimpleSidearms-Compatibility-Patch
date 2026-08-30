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
                "Simple Sidearms' automatic swaps can cancel a Combat Extended reload mid-way.")
            && SSEnums.Require("Simple Sidearms' automatic swaps can cancel a Combat Extended reload mid-way.");

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
            // (Name-resolved values — see SSEnums.)
            return modeOverride == SSEnums.Melee || dropMode == SSEnums.UsedUp;
        }
    }

    /// <summary>
    /// The explicit-switch half: a specific equip during a CE reload ends the reload
    /// cleanly first, so the swap does not leave a reload job driving a gun that is no
    /// longer in hand. Found via the Loadouts module's reviews and fixed here where the
    /// guard lives: the old prefix ended the reload for EVERY call — including ones SS was
    /// about to refuse (already-equipped no-ops, its equip-time blocked-weapon check) — so
    /// a refused switch silently cost the pawn their reload. The no-op case is skipped up
    /// front; every deeper refusal is repaired after the fact by restarting the reload the
    /// prefix ended.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_Patch
    {
        /// <summary>The pawn whose reload the prefix ended for the call in flight, and the
        /// gun that reload was feeding.</summary>
        private static Pawn endedReloadFor;
        private static ThingWithComps endedReloadGun;

        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipSpecificWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) },
            "explicit weapon switches during a reload will not end the reload job cleanly first.");

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, ThingWithComps weapon)
        {
            try
            {
                PrefixInner(pawn, weapon);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload hand-off on explicit switches failed. " + e, 0x43455309);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PrefixInner(Pawn pawn, ThingWithComps weapon)
        {
            if (pawn?.CurJobDef != CE_JobDefOf.ReloadWeapon)
            {
                return;
            }
            // The gun the job is actually feeding is its TargetB — CE issues ReloadWeapon
            // jobs for INVENTORY guns too (gear-tab reload, JobGiver_CheckReload top-offs),
            // and a backpack top-off does not conflict with equipping at all: the gun
            // stays where the driver expects it. Only a reload of the equipped primary is
            // orphaned by a swap.
            var reloading = pawn.CurJob?.targetB.Thing as ThingWithComps;
            if (reloading == null || reloading != pawn.equipment?.Primary)
            {
                return;
            }
            // Equipping the already-equipped weapon is a no-op SS refuses immediately —
            // nothing is about to conflict with the reload, so keep it running.
            if (weapon != null && weapon == reloading)
            {
                return;
            }
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
            endedReloadFor = pawn;
            endedReloadGun = reloading;
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, bool __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload restart after a refused switch failed. " + e, 0x43455313);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, bool __result)
        {
            if (endedReloadFor != pawn)
            {
                return;
            }
            ThingWithComps gun = endedReloadGun;
            endedReloadFor = null;
            endedReloadGun = null;
            // The equip went through — the reload was ended for a reason and the new
            // weapon is not the one it was feeding.
            if (__result)
            {
                return;
            }
            // SS refused after the prefix ran (blocked weapon, invalid carrier): the pawn
            // still holds the half-reloaded gun and lost the job for nothing. Restart the
            // reload of THAT gun, not whatever is in hand now.
            CompAmmoUser ammoUser = gun?.TryGetComp<CompAmmoUser>();
            Verse.AI.Job reload = ammoUser?.TryMakeReloadJob();
            if (reload != null && pawn.jobs != null && pawn.CurJob == null)
            {
                pawn.jobs.StartJob(reload, JobCondition.InterruptForced);
            }
        }

        // A finalizer, not just the postfix: postfixes do not run when the original (or a
        // later prefix) throws, and a stranded flag both loses the repair and arms a
        // spurious reload on an unrelated later call for the same pawn. Same reasoning as
        // the retrieval scope bracket in P01.
        [HarmonyFinalizer]
        public static void Finalizer(Pawn pawn)
        {
            if (endedReloadFor == pawn)
            {
                endedReloadFor = null;
                endedReloadGun = null;
            }
        }
    }
}
