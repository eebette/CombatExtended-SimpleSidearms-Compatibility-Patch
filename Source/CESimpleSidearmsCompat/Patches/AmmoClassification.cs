using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Re-evaluate SS's EMP/dangerous classification using the current CE projectile when the weapon has an ammo comp.
    /// </summary>
    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.isEMPWeapon),
                  new[] { typeof(ThingWithComps) })]
    public static class GettersFilters_isEMPWeapon_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "isEMPWeapon",
            new[] { typeof(ThingWithComps) },
            "EMP classification will read the verb's default projectile instead of the loaded ammo.");

        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, ref bool __result)
        {
            try
            {
                PostfixInner(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ammo-based EMP classification failed. " + e, 0x43455310);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ThingWithComps weapon, ref bool __result)
        {
            if (weapon?.TryGetComp<CompAmmoUser>() == null)
            {
                return;
            }
            ProjectileProperties projectile = CompatUtil.CurrentProjectile(weapon)?.projectile;
            if (projectile != null)
            {
                __result = projectile.damageDef == DamageDefOf.EMP;
            }
        }
    }

    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.isDangerousWeapon),
                  new[] { typeof(ThingWithComps) })]
    public static class GettersFilters_isDangerousWeapon_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "isDangerousWeapon",
            new[] { typeof(ThingWithComps) },
            "dangerous-weapon classification will read the verb's default projectile instead of the loaded ammo.");

        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, ref bool __result)
        {
            try
            {
                PostfixInner(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ammo-based dangerous classification failed. " + e, 0x43455311);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ThingWithComps weapon, ref bool __result)
        {
            if (__result || weapon?.TryGetComp<CompAmmoUser>() == null)
            {
                return;
            }
            ProjectileProperties projectile = CompatUtil.CurrentProjectile(weapon)?.projectile;
            if (projectile == null)
            {
                return;
            }
            // Incendiary or explosive CE ammo: keep it out of automatic swaps, same intent
            // as SS's vanilla "dangerous" filter.
            if (projectile.damageDef == DamageDefOf.Flame || projectile.explosionRadius > 0.1f)
            {
                __result = true;
            }
        }
    }
}
