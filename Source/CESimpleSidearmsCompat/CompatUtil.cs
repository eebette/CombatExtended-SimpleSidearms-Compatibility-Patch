using System.Linq;
using CombatExtended;
using PeteTimesSix.SimpleSidearms;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat
{
    /// <summary>
    /// Shared CE-aware queries about a weapon: whether it follows CE's model, whether it can
    /// fire, what it would fire, and who carries it.
    ///
    /// This is the only type in this assembly the suite modules may bind to.
    /// </summary>
    public static class CompatUtil
    {
        /// <summary>A weapon whose stats follow CE's model (patched verb and/or ammo comp).</summary>
        public static bool IsCEGun(ThingWithComps weapon, out CompAmmoUser ammoUser)
        {
            ammoUser = weapon?.TryGetComp<CompAmmoUser>();
            if (ammoUser != null)
            {
                return true;
            }
            return weapon?.def.Verbs?.FirstOrDefault() is VerbPropertiesCE;
        }

        /// <summary>
        /// True when the weapon can actually fire: no CE ammo comp, ammo system disabled,
        /// rounds in the magazine, or compatible ammo in the carrier's inventory.
        /// </summary>
        public static bool WeaponHasAmmoFor(Pawn carrier, ThingWithComps weapon)
        {
            CompAmmoUser ammoUser = weapon?.TryGetComp<CompAmmoUser>();
            if (ammoUser == null || !ammoUser.UseAmmo)
            {
                return true;
            }
            if (ammoUser.CurMagCount > 0)
            {
                return true;
            }
            CompInventory inventory = carrier?.TryGetComp<CompInventory>();
            if (inventory == null)
            {
                // No carrier context; fall back to CE's own holder-based check.
                return ammoUser.HasAmmoOrMagazine;
            }
            // CurAmmoSet, not Props.ammoSet: CompVariableAmmoUser overrides it with the
            // player-selected set, which is the one the pawn's rounds belong to.
            var ammoTypes = ammoUser.CurAmmoSet?.ammoTypes;
            if (ammoTypes == null)
            {
                return ammoUser.HasAmmoOrMagazine;
            }
            return ammoTypes.Any(link => link?.ammo != null && inventory.AmmoCountOfDef(link.ammo) > 0);
        }

        /// <summary>Projectile the weapon would currently fire (loaded/selected CE ammo, else verb default).</summary>
        public static ThingDef CurrentProjectile(ThingWithComps weapon)
        {
            return CurrentProjectile(weapon, weapon?.TryGetComp<CompAmmoUser>());
        }

        /// <summary>Projectile the weapon would currently fire, given its already-resolved ammo comp.</summary>
        public static ThingDef CurrentProjectile(ThingWithComps weapon, CompAmmoUser ammoUser)
        {
            if (ammoUser != null)
            {
                // An empty magazine leaves CurrentAmmo pointing at the spent round while
                // SelectedAmmo is what the next reload chambers. Avoid classifying a gun by
                // its last round.
                if (ammoUser.HasMagazine && ammoUser.CurMagCount <= 0 && ammoUser.SelectedAmmoProjectile != null)
                {
                    return ammoUser.SelectedAmmoProjectile;
                }
                return ammoUser.CurAmmoProjectile;
            }
            return weapon?.def.Verbs?.FirstOrDefault()?.defaultProjectile;
        }

        /// <summary>The pawn carrying this weapon (equipped or in inventory), if any.</summary>
        public static Pawn CarrierOf(Thing weapon)
        {
            switch (weapon?.ParentHolder)
            {
                case Pawn_EquipmentTracker equipment:
                    return equipment.pawn;
                case Pawn_InventoryTracker inventory:
                    return inventory.pawn;
                default:
                    return null;
            }
        }

        /// <summary>Does Simple Sidearms remember this weapon (def + stuff) for this pawn?</summary>
        public static bool SSRemembers(Pawn pawn, Thing weapon)
        {
            if (pawn == null || weapon == null)
            {
                return false;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null)
            {
                return false;
            }
            ThingDefStuffDefPair pair = new ThingDefStuffDefPair(weapon.def, weapon.Stuff);
            return memory.RememberedWeapons.Contains(pair);
        }
    }
}
