using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 10: CE loadout enforcement (JobGiver_UpdateLoadout → GetExcessThing) drops
    /// inventory items that aren't in the pawn's CE loadout or hold records — which
    /// includes SS-remembered sidearms, causing drop/retrieve churn.
    ///
    /// The exemption is a SUBTRACTION IN CE'S OWN ARITHMETIC, not a veto on its verdict:
    /// GetStorageByThingDef is the per-def stock count every excess decision starts from,
    /// and the postfix below shields the remembered copies a loadout row does not already
    /// cover. CE then computes zero excess for protected weapons all by itself — its scan
    /// walks past them to the genuinely excess cargo behind them, its own dropCount comes
    /// out pre-trimmed to the unprotected surplus, and nothing is written back to its
    /// shared hold-tracker state. Adversarial round 3 killed the old verdict-level veto
    /// three ways at once: it compared def-level CE arithmetic against pair-level SS
    /// memory (two materials of one remembered def wedged the trim open forever), it let
    /// whole stacks through untrimmed, and — because CE's scan returns its FIRST find —
    /// vetoing that find made CE believe the pawn had no excess at all.
    ///
    /// Count semantics: protected = max(remembered, loadout rows), achieved by shielding
    /// only remembered-beyond-rows (CE's own slot subtraction supplies the rest). SS
    /// memory is a per-pair multiset; the shield sums it per def, because def is the
    /// resolution CE enforces at. One acknowledged seam: which INSTANCE CE names for a
    /// legitimate drop is its own choice, so with mixed materials of one def it may drop
    /// the remembered-material copy and keep the other — def-level counts cannot steer
    /// instance naming.
    ///
    /// GetStorageByThingDef's other two consumers stay coherent under the shield: the
    /// pickup side tops a pawn back up to max(rows, remembered) — the same doctrine, and
    /// what SS's own retrieval would do anyway — and the adHoc ammo path reads ammo defs,
    /// which the weapons-only guard leaves untouched (a remembered thrown-grenade def is
    /// both, and shielding it just stops remembered grenades counting as loose ammo
    /// stock).
    /// </summary>
    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetStorageByThingDef),
                  new[] { typeof(Pawn) })]
    public static class Utility_HoldTracker_GetStorageByThingDef_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Utility_HoldTracker), "GetStorageByThingDef",
            new[] { typeof(Pawn) },
            "CE loadout enforcement will drop remembered sidearms from inventory (drop/retrieve churn).");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Remembered-sidearm shield failed; CE counts "
                              + "remembered weapons as excess. " + e, 0x4345530E);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            if (pawn == null || __result == null || __result.Count == 0)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false);
            if (memory?.RememberedWeapons == null || memory.RememberedWeapons.Count == 0)
            {
                return;
            }
            // Loadout rows already shield their count via CE's own slot subtraction;
            // shielding the full remembered count on top would protect remembered + rows
            // instead of max(remembered, rows). Specific rows only — a generic row that
            // happens to cover a weapon def makes CE subtract more, i.e. errs toward
            // keeping a remembered weapon, never toward dropping one.
            Dictionary<ThingDef, int> rowCounts = null;
            Loadout loadout = pawn.GetLoadout();
            if (loadout != null && !loadout.defaultLoadout)
            {
                rowCounts = loadout.GetSlotsFor(pawn)
                    .Where(slot => slot.thingDef != null && slot.thingDef.IsWeapon)
                    .GroupBy(slot => slot.thingDef)
                    .ToDictionary(g => g.Key, g => g.Sum(slot => slot.count));
            }
            foreach (ThingDef def in __result.Keys.Where(d => d.IsWeapon).ToList())
            {
                int remembered = memory.RememberedWeapons.Count(pair => pair.thing == def);
                if (remembered == 0)
                {
                    continue;
                }
                int rows = 0;
                rowCounts?.TryGetValue(def, out rows);
                int shield = remembered - rows;
                if (shield <= 0)
                {
                    continue;
                }
                __result[def].value -= shield;
                if (__result[def].value <= 0)
                {
                    __result.Remove(def);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetExcessEquipment),
                  new[] { typeof(Pawn), typeof(ThingWithComps) },
                  new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class Utility_HoldTracker_GetExcessEquipment_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Utility_HoldTracker), "GetExcessEquipment",
            new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType() },
            "CE loadout enforcement will strip remembered equipped weapons.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            try
            {
                PostfixInner(pawn, ref dropEquipment, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Remembered-equipment drop exemption failed. " + e, 0x4345530F);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            if (!__result || dropEquipment == null)
            {
                return;
            }
            // Deliberately not count-aware, unlike the inventory-side shield above:
            // whatever the counts, the equipped copy is the instance SS wants in the
            // rotation, and the inventory side trims the spares — protecting the one in
            // hand is what makes the two converge on "carry exactly what was asked"
            // instead of CE stripping the primary while duplicates sit in the backpack.
            if (CompatUtil.SSRemembers(pawn, dropEquipment))
            {
                __result = false;
                dropEquipment = null;
            }
        }
    }
}
