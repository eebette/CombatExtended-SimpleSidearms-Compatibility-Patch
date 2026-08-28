using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Intercepts;
using PeteTimesSix.SimpleSidearms.Utilities;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using RimWorld;
using Verse;
using Verse.AI;
using SSCore = PeteTimesSix.SimpleSidearms.SimpleSidearms;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 7: SS's mid-combat "swap to a more accurate ranged weapon" only triggers for
    /// vanilla Verb_Shoot, so it is silently dead under CE (Verb_ShootCE). Replicates SS's
    /// Stance_Warmup postfix for CE shoot verbs, reusing SS's own helpers and settings.
    /// </summary>
    [HarmonyPatch(typeof(Stance_Warmup), nameof(Stance_Warmup.StanceTick), new Type[0])]
    public static class Stance_Warmup_StanceTick_CE_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Stance_Warmup), "StanceTick", new Type[0],
            "mid-warmup switches to a more accurate ranged weapon stay dead under CE.");

        [HarmonyPostfix]
        public static void Postfix(Stance_Warmup __instance)
        {
            try
            {
                PostfixInner(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Warmup auto-switch failed; mid-combat weapon "
                              + "upgrades are off. " + e, 0x4345530B);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Stance_Warmup __instance)
        {
            if (!SSCore.Settings.RangedCombatAutoSwitch)
            {
                return;
            }
            if (!(__instance.verb is Verb_ShootCE))
            {
                return; // vanilla verbs are handled by SS's own patch
            }
            Pawn pawn = __instance.stanceTracker.pawn;
            if (Stance_Warmup_StanceTick_Postfix.IsHunting(pawn))
            {
                return;
            }
            if (!pawn.IsValidSidearmsCarrierRightNow())
            {
                return;
            }
            if (pawn.equipment?.Primary == null)
            {
                // trySwapToMoreAccurateRangedWeapon scores against the equipped weapon and
                // dereferences it without a guard. Verb_ShootCE also covers ability and
                // hediff verbs, which can warm up with nothing equipped.
                return;
            }

            // Reconstruct this shot's warmup window from the stance itself. verbProps.warmupTime
            // is the wrong denominator twice over: CE overrides the virtual WarmupTime that sized
            // the stance (ammo and equipment modifiers), and Verb_ShootCE.RecalculateWarmupTicks
            // shrinks ticksLeft on every repeat shot at the same target (FasterRepeatShots, on by
            // default) — which used to put every shot after the first below the threshold forever.
            int elapsedTicks = Find.TickManager.TicksGame - __instance.startedTick;
            int windowTicks = elapsedTicks + __instance.ticksLeft;
            if (windowTicks <= 0 || __instance.ticksLeft / (float)windowTicks < 1f - SSCore.Settings.RangedCombatAutoSwitchMaxWarmup)
            {
                return;
            }

            LocalTargetInfo target = __instance.focusTarg;
            bool empGood = target.Pawn?.RaceProps.IsMechanoid ?? false;

            var jobData = Stance_Warmup_StanceTick_Postfix.AttackJobDataStore.FromJob(pawn.CurJob);

            bool skipManualUse = true;
            bool skipDangerous = pawn.IsColonistPlayerControlled && SSCore.Settings.SkipDangerousWeapons;
            bool skipEMP = (pawn.IsColonistPlayerControlled && SSCore.Settings.SkipEMPWeapons) || !empGood;

            bool swapped = WeaponAssingment.trySwapToMoreAccurateRangedWeapon(
                pawn, target, MiscUtils.shouldDrop(pawn, DroppingModeEnum.Combat, false), skipManualUse, skipDangerous, skipEMP);

            if (swapped && jobData.HasValue)
            {
                Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                jobData.Value.ApplyToJob(job);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, false);
            }
        }
    }
}
