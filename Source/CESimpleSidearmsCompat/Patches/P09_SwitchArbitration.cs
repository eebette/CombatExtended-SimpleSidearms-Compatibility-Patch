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
    /// Axis 9: when CE replaces a lost, consumed, or dry weapon, Simple Sidearms should
    /// choose the replacement; CE keeps everything else — the job it queues, the mote, the
    /// stow mechanics, the fists fallback.
    ///
    /// One choke point delivers that: every CE replacement path — the out-of-ammo action's
    /// direct search, SwitchToNextViableWeapon's search (weapon destroyed, one-use
    /// consumed), and the flare-gun swap-away — funnels through the public
    /// CompInventory.TryFindViableWeapon. The prefix below substitutes SS's answer as the
    /// weapon CE "found", and CE's own caller then does with it exactly what it would have
    /// done with its own pick. When SS has no usable answer, CE's search runs untouched.
    ///
    /// History note (adversarial round 3): the previous shape patched only
    /// SwitchToNextViableWeapon and re-entered CE's search restricted to SS's pick. That
    /// missed CE's main dry-gun path entirely (DoOutOfAmmoAction searches directly), and
    /// the re-entry was built on two traps — CE's fists branch strips the pawn and reports
    /// success, and TryFindViableWeapon's predicate parameter is broken as shipped
    /// (operator precedence ignores it for any loaded ammo-comp gun, and dereferences null
    /// for ammo-comp-less ones; CE itself never passes a predicate, so the bug is latent
    /// upstream — reported). Substituting at the search instead of re-entering removes
    /// every one of those paths.
    ///
    /// Deliberate semantics change that came with the move: an instant CE-initiated swap
    /// now equips SS's pick through CE's stow mechanics rather than SS's own equip (with
    /// its fumble-drop rolls). The switch is CE's event; CE's mechanics own it. SS's
    /// memory stays correct either way — its equip hooks observe the equipment change
    /// itself.
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
            // Specialized CE calls (AOE requests, predicated searches) pass through — they
            // are CE asking a narrower question than "what should this pawn hold".
            if (useAOE || predicate != null)
            {
                return true;
            }
            Pawn pawn = __instance.parentPawn;
            if (pawn == null || !pawn.IsValidSidearmsCarrierRightNow())
            {
                return true;
            }
            // Kept although both current CE callers pre-check the tag themselves: this
            // prefix must hold for any future caller too, and reading a weaponTag has one
            // shape — convergent use of CE's published extension point, not a
            // transcription (#22/V8 ruling).
            if (pawn.equipment?.Primary?.def.weaponTags?.Contains("NoSwitch") ?? false)
            {
                return true;
            }

            ThingWithComps pick = WeaponAssingment_equipSpecificWeapon_DryRun.AskSS(pawn, out bool ssDecided);
            if (!ssDecided)
            {
                // SS's silence has two shapes (see AskSS): an already-unarmed pawn whose
                // memory says "stay unarmed" never reaches an equip at all, and letting
                // CE's search run would re-arm a pawn the player set to fists.
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
                // Nothing this search can act on — CE's own search skips the primary too.
                return true;
            }
            // CE must actually be able to use the pick: the same public gates its own
            // search applies. An unusable pick (a dry gun the player forced, a biocoded
            // weapon) hands the search back to CE unrestricted — its fists fallback then
            // only fires when NOTHING is usable, instead of stripping the pawn because
            // SS's first choice was.
            CompAmmoUser ammoUser = pick.TryGetComp<CompAmmoUser>();
            if (!EquipmentUtility.CanEquip(pick, pawn)
                || (ammoUser != null && !ammoUser.HasAndUsesAmmoOrMagazine))
            {
                return true;
            }
            weapon = pick;
            __result = true;
            return false;
        }

        /// <summary>
        /// Forced-unarmed, forced-unarmed-while-drafted and preferred-unarmed all leave
        /// Primary null on success, so an empty hand is an answer rather than a failure.
        /// Ask SS instead of inferring it from the equipment pointer, or CE re-arms a pawn
        /// the player set to fists.
        /// </summary>
        private static bool WantsToStayUnarmed(Pawn pawn)
        {
            return pawn.equipment?.Primary == null
                   && (CompSidearmMemory.GetMemoryCompForPawn(pawn, false)?.IsCurrentWeaponForced(true) ?? false);
        }
    }

    /// <summary>
    /// Every branch of SS's preference tree — forced weapon, forced-while-drafted, default
    /// ranged, preferred melee, unarmed, best-by-DPS — ends at this one method, and each
    /// returns as soon as it succeeds.
    ///
    /// CONTRACT (relied on beyond this file): AskSS observes without acting. While it is on
    /// the stack, nothing is equipped, dropped, forgotten, or remembered — SS's preference
    /// tree is halted at the exact decision point and its answer extracted. Any change that
    /// lets a side effect escape the dry run breaks every caller that treats "ask SS" as a
    /// pure question, and the suite's arbitration phases with it.
    ///
    /// Composition note: halting via a false-returning prefix also skips any
    /// later-registered prefix on equipSpecificWeapon (Harmony semantics), so the dry run
    /// reflects SS as patched by everything that loaded BEFORE this mod. Consumers loading
    /// after must not rely on their own equipSpecificWeapon prefixes firing during
    /// arbitration — their filters on the picker functions (which run inside the dry run)
    /// are the composing surface.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_DryRun
    {
        private static Pawn askingFor;
        private static ThingWithComps answer;
        private static bool answered;

        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipSpecificWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) },
            "asking Simple Sidearms which weapon it prefers finds no answer, so Combat Extended's own pick is used.");

        /// <summary>
        /// The weapon SS would equip right now. <paramref name="decided"/> separates SS's two
        /// kinds of silence: false means it never reached an equip at all (no opinion), true
        /// with a null return means it deliberately chose to leave the pawn unarmed. Nothing
        /// is equipped, dropped, or remembered.
        /// </summary>
        public static ThingWithComps AskSS(Pawn pawn, out bool decided)
        {
            askingFor = pawn;
            answer = null;
            answered = false;
            try
            {
                WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, DroppingModeEnum.Combat);
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
