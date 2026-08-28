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
            // The pawnkind's CE ammo policy, if it has one: forced category, then a weighted
            // roll over the faction's categories, then generateAllowChance. Letting CE apply
            // it is what keeps SS-generated sidearms in the same ammo economy as the primary
            // CE generated a moment earlier — otherwise every one of them carries the default
            // round and faction AP/incendiary loadouts stop at the primary weapon.
            LoadoutPropertiesExtension loadoutProps = pawn.kindDef?.GetModExtension<LoadoutPropertiesExtension>();

            bool changed = false;
            foreach (ThingWithComps weapon in pawn.inventory.innerContainer.OfType<ThingWithComps>().Where(t => t.def.IsWeapon).ToList())
            {
                CompAmmoUser ammoUser = weapon.TryGetComp<CompAmmoUser>();
                if (ammoUser == null)
                {
                    continue;
                }
                // An empty magazine is not conditional on the ammo system: CompAmmoUser.Initialize
                // skips loading when !UseAmmo and leaves the gun unfireable either way. CE's own
                // generator fills it regardless (LoadWeaponWithRandAmmo's !UseAmmo branch), so a
                // squad would otherwise spawn with loaded primaries and empty sidearms.
                if (ammoUser.HasMagazine && ammoUser.CurMagCount <= 0)
                {
                    if (loadoutProps != null)
                    {
                        // Picks the ammo AND fills the magazine, and handles !UseAmmo itself.
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
                int magazines = MagazineCountFor(pawn);
                // MagSizeOverride is CE's "rounds per magazine for generation" knob — one-shot
                // launchers set it because their MagSize is 1.
                int perMagazine = Math.Max(1, ammoUser.MagSizeOverride > 0 ? ammoUser.MagSizeOverride
                                            : ammoUser.HasMagazine ? ammoUser.MagSize : 10);
                Thing ammo = ThingMaker.MakeThing(ammoDef);
                ammo.stackCount = magazines * perMagazine;
                if (inventory.CanFitInInventory(ammo, out int fitCount) && fitCount > 0)
                {
                    if (fitCount < ammo.stackCount)
                    {
                        // Whole magazines only, as CE's own TryGenerateAmmoFor does.
                        ammo.stackCount = fitCount - (fitCount % perMagazine);
                    }
                    if (ammo.stackCount > 0)
                    {
                        inventory.container.TryAdd(ammo, true);
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                inventory.UpdateInventory();
            }
        }

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
