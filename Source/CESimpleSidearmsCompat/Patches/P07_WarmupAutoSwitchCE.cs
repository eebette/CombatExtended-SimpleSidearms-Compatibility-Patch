using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Intercepts;
using RimWorld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 7: SS's mid-combat "swap to a more accurate ranged weapon" only triggers for
    /// vanilla Verb_Shoot, so it is silently dead under CE (Verb_ShootCE is a sibling of
    /// Verb_Shoot, not a subclass). Instead of replicating SS's decision logic for CE verbs
    /// — the old shape here, and issue #22's V1 — SS's OWN postfix body is transpiled at
    /// exactly two points and then runs unmodified for both verb families:
    ///
    ///  1. Its `verb is Verb_Shoot` gate also accepts an equipment-sourced Verb_ShootCE
    ///     (equipment-sourced because Verb_ShootCE additionally backs ability and hediff
    ///     verbs, which can warm up with nothing equipped — and SS's swap scores against
    ///     the equipped weapon and dereferences it).
    ///
    ///  2. Its warmup-window denominator — SecondsToTicks(verbProps.warmupTime x
    ///     AimingDelayFactor) — is replaced with the window reconstructed from the stance
    ///     itself (elapsed + ticksLeft). For vanilla verbs that is the identical number,
    ///     because it is exactly how the stance was sized; for CE verbs it is the correct
    ///     one, where the static verbProps figure is wrong twice over (CE overrides the
    ///     WarmupTime that sized the stance, and Verb_ShootCE.RecalculateWarmupTicks
    ///     shrinks ticksLeft on every repeat shot at the same target).
    /// </summary>
    [HarmonyPatch(typeof(Stance_Warmup_StanceTick_Postfix), nameof(Stance_Warmup_StanceTick_Postfix.StanceTick),
                  new[] { typeof(Stance_Warmup) })]
    public static class Stance_Warmup_StanceTick_CE_Patch
    {
        // The CE-side probe is part of THIS class's Prepare although the patch target is
        // SS's method: the helpers below are called from IL injected into SS's postfix, so
        // there is no outer/inner split to catch their JIT — a vanished CE type would
        // otherwise throw inside the stance tick, per warming pawn, per tick, taking SS's
        // vanilla feature down with a stack blaming SS (adversarial round 3).
        public static bool Prepare() => PatchGuard.Require(typeof(Stance_Warmup_StanceTick_Postfix), "StanceTick",
                new[] { typeof(Stance_Warmup) },
                "mid-warmup switches to a more accurate ranged weapon stay dead under CE.")
            && PatchGuard.RequireType("CombatExtended.Verb_ShootCE",
                "mid-warmup switches to a more accurate ranged weapon stay dead under CE.");

        /// <summary>
        /// Stack-neutral stand-in for the original `isinst Verb_Shoot`: pushes the verb for
        /// an eligible one and null otherwise, so the branch that follows behaves
        /// identically. The CE reference lives in a guarded NoInlining inner so drift that
        /// slips past the Prepare probe degrades this to the vanilla-only gate with one
        /// named error instead of a per-tick exception flood inside the stance tick.
        /// </summary>
        public static Verb EligibleShootVerb(Verb verb)
        {
            if (verb is Verb_Shoot)
            {
                return verb;
            }
            try
            {
                return CEEligibleVerb(verb);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE verb gate failed; warmup auto-switch runs "
                              + "for vanilla verbs only. " + e, 0x43455315);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Verb CEEligibleVerb(Verb verb)
        {
            return verb is Verb_ShootCE && verb.EquipmentSource != null ? verb : null;
        }

        /// <summary>
        /// The stance's real warmup window. Falls back to SS's own figure if the stance
        /// fields ever stop adding up.
        /// </summary>
        public static int WarmupWindowTicks(int ssComputed, Stance_Warmup stance)
        {
            int elapsed = Find.TickManager.TicksGame - stance.startedTick;
            int window = elapsed + stance.ticksLeft;
            return window > 0 ? window : ssComputed;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo eligible = AccessTools.Method(typeof(Stance_Warmup_StanceTick_CE_Patch), nameof(EligibleShootVerb));
            MethodInfo windowFix = AccessTools.Method(typeof(Stance_Warmup_StanceTick_CE_Patch), nameof(WarmupWindowTicks));
            MethodInfo secondsToTicks = AccessTools.Method(typeof(GenTicks), nameof(GenTicks.SecondsToTicks));

            bool gateDone = false, windowDone = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (!gateDone && list[i].opcode == OpCodes.Isinst && list[i].operand as Type == typeof(Verb_Shoot))
                {
                    // In-place opcode/operand swap keeps any branch labels on the instruction.
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = eligible;
                    gateDone = true;
                    continue;
                }
                if (!windowDone && list[i].Calls(secondsToTicks))
                {
                    // SS's int is on the stack; push the stance and let WarmupWindowTicks
                    // decide. Net stack effect unchanged.
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, windowFix));
                    i += 2;
                    windowDone = true;
                }
            }

            if (!gateDone || !windowDone)
            {
                // SS reshaped the body; leave it untouched rather than half-transpiled.
                // Vanilla behavior is unaffected — only the CE extension goes dead.
                Log.Error(PatchGuard.LogPrefix + "Simple Sidearms' warmup postfix no longer matches "
                          + $"(gate found: {gateDone}, window found: {windowDone}) — mid-warmup "
                          + "switches to a more accurate ranged weapon stay dead under CE.");
                return instructions;
            }
            return list;
        }
    }
}
