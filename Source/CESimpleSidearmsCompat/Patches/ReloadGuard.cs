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
    /// Suppresses idle/optimisation preference weapon swaps during CE reload.
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
            return modeOverride == SSEnums.Melee || dropMode == SSEnums.UsedUp;
        }
    }

    /// <summary>
    /// Cleanly ends a specific equip during a CE reload.
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
            // Protects against a swap during a reload of the equipped-primary,
            // leaving the pawn holding a different gun than the one half-fed.
            var reloading = pawn.CurJob?.targetB.Thing as ThingWithComps;
            if (reloading == null || reloading != pawn.equipment?.Primary)
            {
                return;
            }
            // Don't try to equip the already-equipped weapon.
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
            // SS refused after the prefix ran: restart the reload.
            CompAmmoUser ammoUser = gun?.TryGetComp<CompAmmoUser>();
            Verse.AI.Job reload = ammoUser?.TryMakeReloadJob();
            if (reload != null && pawn.jobs != null && pawn.CurJob == null)
            {
                pawn.jobs.StartJob(reload, JobCondition.InterruptForced);
            }
        }

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
