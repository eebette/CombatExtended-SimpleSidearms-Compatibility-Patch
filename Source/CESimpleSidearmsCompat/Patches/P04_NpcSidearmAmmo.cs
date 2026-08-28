using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 4: SS-generated NPC sidearms spawn with empty magazines and no spare ammo under
    /// CE. After SS generates, load every ammo-using inventory weapon and stock spare
    /// magazines, respecting CE inventory capacity.
    /// </summary>
    [HarmonyPatch(typeof(PawnSidearmsGenerator), nameof(PawnSidearmsGenerator.TryGenerateSidearmFor),
                  new[] { typeof(Pawn), typeof(float), typeof(float), typeof(PawnGenerationRequest) })]
    public static class PawnSidearmsGenerator_TryGenerateSidearmFor_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(PawnSidearmsGenerator), "TryGenerateSidearmFor",
            new[] { typeof(Pawn), typeof(float), typeof(float), typeof(PawnGenerationRequest) },
            "NPC sidearms will spawn with empty magazines and no spare ammo.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, bool __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Loading generated NPC sidearms failed; they "
                              + "spawn with empty magazines. " + e, 0x43455307);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, bool __result)
        {
            if (!__result || pawn?.inventory?.innerContainer == null)
            {
                return;
            }
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null)
            {
                return;
            }
            // The pawnkind's CE ammo policy, if it has one: letting CE apply it keeps
            // SS-generated sidearms in the same ammo economy as the primary CE generated a
            // moment earlier — otherwise every one of them carries the default round and
            // faction AP/incendiary loadouts stop at the primary weapon. Kinds without the
            // extension get a bare instance, so the same CE code path provisions them too.
            LoadoutPropertiesExtension loadoutProps = pawn.kindDef?.GetModExtension<LoadoutPropertiesExtension>();
            LoadoutPropertiesExtension ammoPolicy = loadoutProps ?? BareAmmoPolicy;

            bool changed = false;
            foreach (ThingWithComps weapon in pawn.inventory.innerContainer.OfType<ThingWithComps>().Where(t => t.def.IsWeapon).ToList())
            {
                CompAmmoUser ammoUser = weapon.TryGetComp<CompAmmoUser>();
                if (ammoUser == null)
                {
                    continue;
                }
                // A gun generated with an empty magazine is unfireable whether or not the
                // ammo system is on; CE's own generator loads every primary, so a squad
                // would otherwise spawn with loaded primaries and empty sidearms.
                if (ammoUser.HasMagazine && ammoUser.CurMagCount <= 0)
                {
                    if (loadoutProps != null)
                    {
                        loadoutProps.LoadWeaponWithRandAmmo(weapon);
                    }
                    else
                    {
                        ammoUser.ResetAmmoCount();
                    }
                    changed = true;
                }
                // Spare ammo is what the ammo system gates.
                if (!Controller.settings.EnableAmmoSystem || !ammoUser.UseAmmo)
                {
                    continue;
                }
                AmmoDef ammoDef = ammoUser.CurrentAmmo ?? ammoUser.SelectedAmmo;
                if (ammoDef == null || inventory.AmmoCountOfDef(ammoDef) > 0)
                {
                    continue;
                }
                // CE's own provisioner (publicized): picks the round under the kind's ammo
                // policy, sizes the stack per magazine, fits it to inventory and trims to
                // whole magazines. Calling it is what keeps this identical to how CE stocks
                // the primary — and an upstream ask to make it public is on the issue-5
                // list so the publicizer is not load-bearing.
                ammoPolicy.TryGenerateAmmoFor(weapon, inventory, MagazineCountFor(pawn));
                changed = true;
            }
            if (changed)
            {
                inventory.UpdateInventory();
            }
        }

        /// <summary>Ammo policy for pawnkinds that ship no CE loadout extension at all.</summary>
        private static readonly LoadoutPropertiesExtension BareAmmoPolicy = new LoadoutPropertiesExtension();

        private static int MagazineCountFor(Pawn pawn)
        {
            // Prefer CE's own per-kind sidearm loadout config when present.
            SidearmOption option = pawn.kindDef?.GetModExtension<LoadoutPropertiesExtension>()?
                                   .sidearms?.FirstOrDefault(s => s.magazineCount.TrueMax > 0f);
            if (option != null)
            {
                int count = Mathf.RoundToInt(option.magazineCount.RandomInRange);
                if (count > 0)
                {
                    return count;
                }
            }
            return Rand.RangeInclusive(1, 3);
        }
    }
}
