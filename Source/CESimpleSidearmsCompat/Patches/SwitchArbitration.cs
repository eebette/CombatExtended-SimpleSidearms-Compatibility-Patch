using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Patches CE weapon replenishment to use SS item selection logic using a prefix on
    /// CE's CompInventory.TryFindViableWeapon.
    /// </summary>
    [HarmonyPatch(typeof(CompInventory), nameof(CompInventory.TryFindViableWeapon),
                  new[] { typeof(ThingWithComps), typeof(bool), typeof(Func<ThingWithComps, CompAmmoUser, bool>) },
                  new[] { ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal })]
    public static class CompInventory_TryFindViableWeapon_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(CompInventory), "TryFindViableWeapon",
            new[] { typeof(ThingWithComps).MakeByRefType(), typeof(bool), typeof(Func<ThingWithComps, CompAmmoUser, bool>) },
            "weapon replacement after a loss, consumption, or dry magazine will ignore Simple Sidearms preferences.");

        [HarmonyPrefix]
        public static bool Prefix(CompInventory __instance, ref ThingWithComps weapon, bool useAOE,
                                  Func<ThingWithComps, CompAmmoUser, bool> predicate, ref bool __result)
        {
            try
            {
                return PrefixInner(__instance, ref weapon, useAOE, predicate, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Switch arbitration failed; Combat Extended "
                              + "picks the replacement weapon on its own. " + e, 0x4345530D);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(CompInventory __instance, ref ThingWithComps weapon, bool useAOE,
                                        Func<ThingWithComps, CompAmmoUser, bool> predicate, ref bool __result)
        {
            // Specialized CE calls (AOE requests, predicated searches) pass through.
            if (useAOE || predicate != null)
            {
                return true;
            }
            Pawn pawn = __instance.parentPawn;
            if (pawn == null || !pawn.IsValidSidearmsCarrierRightNow())
            {
                return true;
            }
            // Return on any weapons tagged as NoSwitch
            if (pawn.equipment?.Primary?.def.weaponTags?.Contains("NoSwitch") ?? false)
            {
                return true;
            }

            ThingWithComps pick = WeaponAssingment_equipSpecificWeapon_DryRun.AskSS(pawn, out bool ssDecided);
            if (!ssDecided)
            {
                // Pawn's memory says "stay unarmed" never reaches an equip at all
                if (WantsToStayUnarmed(pawn))
                {
                    weapon = null;
                    __result = false;
                    return false;
                }
                return true; // no opinion — CE's own search
            }
            if (pick == null)
            {
                // SS deliberately chose "no weapon".
                weapon = null;
                __result = false;
                return false;
            }
            if (pick == pawn.equipment?.Primary)
            {
                // Nothing this search can act on.
                return true;
            }
            // CE must actually be able to use the pick.
            CompAmmoUser ammoUser = pick.TryGetComp<CompAmmoUser>();
            if (!EquipmentUtility.CanEquip(pick, pawn)
                || (ammoUser != null && !ammoUser.HasAndUsesAmmoOrMagazine)
                || (!useAOE && pick.def.IsAOEWeapon())
                || pick.def.IsIlluminationDevice())
            {
                return true;
            }
            // And the pick must live where CE's swap mechanics expect it.
            if (!__instance.container.Contains(pick))
            {
                return true;
            }
            weapon = pick;
            __result = true;
            return false;
        }

        /// <summary>
        /// Does pawn want to be unarmed?
        /// </summary>
        private static bool WantsToStayUnarmed(Pawn pawn)
        {
            return pawn.equipment?.Primary == null
                   && (CompSidearmMemory.GetMemoryCompForPawn(pawn, false)?.IsCurrentWeaponForced(true) ?? false);
        }
    }

    /// <summary>
    /// Intercepts SS's equipSpecificWeapon to check whether a gun is equippable per SS rules.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_DryRun
    {
        private static Pawn askingFor;
        private static ThingWithComps answer;
        private static bool answered;

        /// <summary>Whether the halting prefix below actually installed.</summary>
        private static bool installed;

        public static bool Prepare()
        {
            installed = PatchGuard.Require(typeof(WeaponAssingment), "equipSpecificWeapon",
                    new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) },
                    "asking Simple Sidearms which weapon it prefers finds no answer, so Combat Extended's own pick is used.")
                && SSEnums.Require("asking Simple Sidearms which weapon it prefers finds no answer, so Combat Extended's own pick is used.");
            return installed;
        }

        /// <summary>
        /// The weapon SS would equip right now.
        ///
        /// <paramref name="decided"/> separates SS's two silent modes:
        /// false means it never reached an equip at all (no opinion),
        /// true with a null return means it deliberately chose to leave the pawn unarmed.
        /// </summary>
        public static ThingWithComps AskSS(Pawn pawn, out bool decided)
        {
            if (!installed)
            {
                Log.ErrorOnce("[CE+SimpleSidearms] The dry-run blocker is not installed (upstream drift?) — "
                              + "refusing to ask Simple Sidearms for a preference; Combat Extended's own "
                              + "pick is used.", 0x43455317);
                decided = false;
                return null;
            }
            askingFor = pawn;
            answer = null;
            answered = false;
            try
            {
                WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, SSEnums.Combat);
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
