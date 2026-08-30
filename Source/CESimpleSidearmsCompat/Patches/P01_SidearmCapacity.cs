using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 1: SS decides what a pawn may carry as a sidearm without CE's bulk model.
    /// Weight is already CE-aware through CE's MassUtility.Capacity patch; bulk is not.
    ///
    /// SS has two doors onto that decision and they do not share a gate. The gizmo and the
    /// float menus ask StatCalculator.CanPickupSidearmType; JobGiver_RetrieveWeapon, which
    /// fetches remembered weapons on its own from the vanilla think tree, asks nothing at
    /// all — its pickup driver ends in a bare innerContainer.TryAdd. Both are patched here.
    /// </summary>
    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.CanPickupSidearmType),
                  new[] { typeof(ThingDefStuffDefPair), typeof(Pawn), typeof(string) },
                  new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class StatCalculator_CanPickupSidearmType_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "CanPickupSidearmType",
            new[] { typeof(ThingDefStuffDefPair), typeof(Pawn), typeof(string).MakeByRefType() },
            "sidearm pickup will ignore Combat Extended's bulk capacity.");

        [HarmonyPostfix]
        public static void Postfix(ThingDefStuffDefPair sidearmType, Pawn pawn, ref string errString, ref bool __result)
        {
            try
            {
                PostfixInner(sidearmType, pawn, ref errString, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Bulk capacity check failed; sidearm pickup "
                              + "falls back to weight-only limits. " + e, 0x43455301);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ThingDefStuffDefPair sidearmType, Pawn pawn, ref string errString, ref bool __result)
        {
            if (!__result || pawn == null || sidearmType.thing == null)
            {
                return;
            }
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null)
            {
                return;
            }
            float bulk = sidearmType.thing.GetStatValueAbstract(CE_StatDefOf.Bulk, sidearmType.stuff);
            if (bulk <= 0f)
            {
                return;
            }
            // GetAvailableBulk(false) reads CE's cached figure without the full recount,
            // which matters because SS calls this inside a filter over every valid sidearm
            // pair at pawn generation.
            if (bulk > inventory.GetAvailableBulk(false))
            {
                errString = "SidearmPickupFail_NoFreeSpace".Translate();
                __result = false;
            }
        }
    }

    /// <summary>
    /// The second door: SS's own retrieval never consults CanPickupSidearmType, so without
    /// this a pawn walks up to 1000 cells for a weapon CE has no room for, and it then
    /// counts against everything else they carry.
    ///
    /// SS's loop returns on the first unsatisfied memory it can find an instance for, so a
    /// cancellation here used to mean "no retrieval at all this pass" — one heavy memory
    /// starved every sidearm behind it in the list, and the refused pair was re-searched
    /// map-wide on every think pass forever (issue #20). The refusal is therefore recorded,
    /// and RetrievalRefusals' hasWeaponType hook makes SS's own loop walk PAST the refused
    /// pair on later passes — no SS logic re-derived, and the scan stops re-running until
    /// the pawn actually frees up room.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_RetrieveWeapon), nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic),
                  new[] { typeof(Pawn), typeof(bool) })]
    public static class JobGiver_RetrieveWeapon_TryGiveJobStatic_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(JobGiver_RetrieveWeapon), "TryGiveJobStatic",
            new[] { typeof(Pawn), typeof(bool) },
            "sidearm retrieval will not be capacity-checked.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
            {
                return;
            }
            try
            {
                PostfixInner(pawn, ref __result);
            }
            catch (Exception e)
            {
                // Reached from the think tree and from SS's AutoUndrafter every 100 ticks;
                // a throw here would be a flood, so leave SS's job untouched instead.
                Log.ErrorOnce(PatchGuard.LogPrefix + "Capacity check on sidearm retrieval failed: " + e, 0x43455352);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref Job __result)
        {
            Thing target = __result.targetA.Thing;
            CompInventory inventory = pawn?.TryGetComp<CompInventory>();
            if (target == null || inventory == null)
            {
                return;
            }
            if (!inventory.CanFitInInventory(target, out int _))
            {
                __result = null;
                RetrievalRefusals.Record(pawn, inventory, target);
            }
        }
    }

    /// <summary>
    /// The memory of "CE had no room for that pair", so a refusal costs one search instead
    /// of a permanent one-per-think-pass rescan, and so the memories behind it in SS's list
    /// still get their turn. An entry dies the moment the pawn has more free bulk or weight
    /// than when it was recorded (something was dropped, used up, or the pawn got stronger),
    /// and after an in-game hour regardless, as a backstop for capacity changes free space
    /// cannot see.
    /// </summary>
    internal static class RetrievalRefusals
    {
        private struct Refusal
        {
            public float freeBulk;
            public float freeWeight;
            public int tick;
        }

        private const int ExpiryTicks = 2500;

        private static readonly Dictionary<(int pawn, ThingDefStuffDefPair pair), Refusal> refusals
            = new Dictionary<(int, ThingDefStuffDefPair), Refusal>();

        /// <summary>One message per pawn and pair per session — the alert SS keeps lit has no
        /// other explanation the player could find.</summary>
        private static readonly HashSet<(int pawn, ThingDefStuffDefPair pair)> messaged
            = new HashSet<(int, ThingDefStuffDefPair)>();

        /// <summary>Non-null while SS's retrieval job giver is running for this pawn.</summary>
        internal static Pawn RetrievingFor;

        /// <summary>
        /// These statics outlive the loaded game: without this, loading an earlier save
        /// time-travels TicksGame backwards (a negative age makes the expiry backstop
        /// unreachable), thingIDNumbers recur across saves and colonies, and the
        /// once-per-session message set stays spent — a pawn could silently never
        /// retrieve again with the one breadcrumb suppressed.
        /// </summary>
        private static int gameStamp;

        private static void EnsureGame()
        {
            int stamp = Current.Game?.GetHashCode() ?? 0;
            if (stamp == gameStamp)
            {
                return;
            }
            gameStamp = stamp;
            refusals.Clear();
            messaged.Clear();
            RetrievingFor = null;
        }

        internal static void Record(Pawn pawn, CompInventory inventory, Thing target)
        {
            EnsureGame();
            var key = (pawn.thingIDNumber, new ThingDefStuffDefPair(target.def, target.Stuff));
            refusals[key] = new Refusal
            {
                freeBulk = inventory.GetAvailableBulk(false),
                freeWeight = inventory.GetAvailableWeight(false),
                tick = Find.TickManager.TicksGame,
            };
            if (pawn.Faction == Faction.OfPlayer && messaged.Add(key))
            {
                Messages.Message($"{pawn.LabelShort} can't fetch {target.LabelNoCount}: "
                                 + "no room in inventory.", pawn, MessageTypeDefOf.NeutralEvent);
            }
        }

        internal static bool StandsFor(Pawn pawn, ThingDefStuffDefPair pair)
        {
            EnsureGame();
            var key = (pawn.thingIDNumber, pair);
            if (!refusals.TryGetValue(key, out Refusal refusal))
            {
                return false;
            }
            // Negative age counts as expired: it means the clock moved backwards under
            // the entry (a same-session load the game stamp somehow missed).
            int age = Find.TickManager.TicksGame - refusal.tick;
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null
                || age < 0 || age > ExpiryTicks
                || inventory.GetAvailableBulk(false) > refusal.freeBulk + 0.01f
                || inventory.GetAvailableWeight(false) > refusal.freeWeight + 0.01f)
            {
                refusals.Remove(key);
                return false;
            }
            return true;
        }
    }

    /// <summary>Brackets SS's retrieval pass, so the hasWeaponType hook below acts only there.</summary>
    [HarmonyPatch(typeof(JobGiver_RetrieveWeapon), nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic),
                  new[] { typeof(Pawn), typeof(bool) })]
    public static class JobGiver_RetrieveWeapon_Scope_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(JobGiver_RetrieveWeapon), "TryGiveJobStatic",
            new[] { typeof(Pawn), typeof(bool) },
            "a capacity-refused sidearm memory will block every memory behind it and rescan the map each think pass.");

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn) => RetrievalRefusals.RetrievingFor = pawn;

        // A finalizer rather than a postfix: it runs even when the original throws, and a
        // leaked scope would turn the retrieval-only hook into a global one.
        [HarmonyFinalizer]
        public static void Finalizer() => RetrievalRefusals.RetrievingFor = null;
    }

    /// <summary>
    /// Inside the retrieval pass only, a pair CE refused reports as satisfied — SS's own
    /// loop then walks past it to the next memory, exactly as if the pawn already carried
    /// it. hasWeaponType has three other callers (the missing-sidearm alert, two preference
    /// branches); the scope guard keeps them all on the truth.
    /// </summary>
    [HarmonyPatch(typeof(Extensions), nameof(Extensions.hasWeaponType),
                  new[] { typeof(Pawn), typeof(ThingDefStuffDefPair), typeof(int) })]
    public static class Extensions_hasWeaponType_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Extensions), "hasWeaponType",
            new[] { typeof(Pawn), typeof(ThingDefStuffDefPair), typeof(int) },
            "a capacity-refused sidearm memory will block every memory behind it in the retrieval list.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ThingDefStuffDefPair weapon, ref bool __result)
        {
            try
            {
                PostfixInner(pawn, weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Retrieval refusal check failed. " + e, 0x43455312);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ThingDefStuffDefPair weapon, ref bool __result)
        {
            if (__result || pawn == null || RetrievalRefusals.RetrievingFor != pawn)
            {
                return;
            }
            if (RetrievalRefusals.StandsFor(pawn, weapon))
            {
                __result = true;
            }
        }
    }
}
