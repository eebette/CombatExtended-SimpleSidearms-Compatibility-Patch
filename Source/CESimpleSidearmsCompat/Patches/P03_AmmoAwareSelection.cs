using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 3: SS's best-ranged-weapon selection is ammo-blind and can hand a pawn an empty
    /// gun. If the original pick has no usable ammo, ask SS the same question again with the
    /// dry weapons hidden — so SS's own filter chain picks the replacement, including the
    /// third-party rules it applies for other mods (VFE off-hand shields, Tacticowl
    /// dual-wield) and whatever it adds next. Re-deriving that chain here meant re-deriving
    /// it wrong: it silently missed both of those.
    /// </summary>
    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon),
                  new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class GettersFilters_findBestRangedWeapon_Patch
    {
        /// <summary>Non-null only for the duration of one re-run, for one pawn.</summary>
        internal static Pawn HidingDryWeaponsFor;

        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "findBestRangedWeapon",
            new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
            "a pawn can draw an empty gun while carrying a loaded one.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, LocalTargetInfo? target, bool skipManualUse, bool skipDangerous, bool skipEMP, bool includeEquipped,
                                   ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            try
            {
                PostfixInner(pawn, target, skipManualUse, skipDangerous, skipEMP, includeEquipped, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ammo-aware weapon re-pick failed; Simple "
                              + "Sidearms' original (possibly dry) pick stands. " + e, 0x43455305);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, LocalTargetInfo? target, bool skipManualUse, bool skipDangerous, bool skipEMP, bool includeEquipped,
                                         ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            if (HidingDryWeaponsFor != null)
            {
                return; // this call IS the re-run
            }
            if (__result.weapon == null || CompatUtil.WeaponHasAmmoFor(pawn, __result.weapon))
            {
                return;
            }

            HidingDryWeaponsFor = pawn;
            try
            {
                __result = GettersFilters.findBestRangedWeapon(pawn, target, skipManualUse, skipDangerous, skipEMP, includeEquipped);
            }
            finally
            {
                HidingDryWeaponsFor = null;
            }
        }
    }

    /// <summary>
    /// The one seam the re-run needs: while it is in flight, the pawn's carried-weapon list
    /// does not include guns with no usable ammo.
    /// </summary>
    [HarmonyPatch(typeof(Extensions), nameof(Extensions.GetCarriedWeapons),
                  new[] { typeof(Pawn), typeof(bool), typeof(bool) })]
    public static class Extensions_GetCarriedWeapons_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Extensions), "GetCarriedWeapons",
            new[] { typeof(Pawn), typeof(bool), typeof(bool) },
            "the ammo-aware re-pick cannot hide dry guns and is inert.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, List<ThingWithComps> __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Dry-gun filter failed during the ammo-aware "
                              + "re-pick. " + e, 0x43455306);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, List<ThingWithComps> __result)
        {
            if (__result == null || GettersFilters_findBestRangedWeapon_Patch.HidingDryWeaponsFor != pawn)
            {
                return;
            }
            __result.RemoveAll(w => w != null && w.def.IsRangedWeapon && !CompatUtil.WeaponHasAmmoFor(pawn, w));
        }
    }
}
