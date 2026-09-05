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
    /// Patches SS's weapon swap logic to work with CE.
    /// SS's OWN postfix body is transpiled at two points:
    ///
    ///  1. Its `verb is Verb_Shoot` gate now accepts an equipment-sourced Verb_ShootCE.
    ///
    ///  2. Its warmup-window denominator is replaced with the window reconstructed from the
    ///     Stance_Warmup being ticked.
    /// </summary>
    [HarmonyPatch(typeof(Stance_Warmup_StanceTick_Postfix), nameof(Stance_Warmup_StanceTick_Postfix.StanceTick),
                  new[] { typeof(Stance_Warmup) })]
    public static class Stance_Warmup_StanceTick_CE_Patch
    {

        /// <summary>Gates the transpiler on SS's StanceTick and CE's Verb_ShootCE being present.</summary>
        public static bool Prepare()
        {
            UpstreamFingerprint.Verify(typeof(Stance_Warmup_StanceTick_Postfix), "StanceTick",
                UpstreamFingerprint.StanceTickHash,
                "the two IL anchors this transpiler edits around");
            return PatchGuard.Require(typeof(Stance_Warmup_StanceTick_Postfix), "StanceTick",
                new[] { typeof(Stance_Warmup) },
                "mid-warmup switches to a more accurate ranged weapon stay dead under CE.")
            && PatchGuard.RequireType("CombatExtended.Verb_ShootCE",
                "mid-warmup switches to a more accurate ranged weapon stay dead under CE.");
        }

        /// <summary>
        /// CE-friendly stand-in for the original `isinst Verb_Shoot`.
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

        /// <summary>Reconstructs the warmup window from the stance with fallback to original SS.</summary>
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
                    // SS's window int is already on the stack; push the stance and call
                    // WarmupWindowTicks(ssInt, stance).
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, windowFix));
                    i += 2;
                    windowDone = true;
                }
            }

            if (!gateDone || !windowDone)
            {
                // SS reshaped the body; leave it untouched rather than half-transpiled.
                Log.Error(PatchGuard.LogPrefix + "Simple Sidearms' warmup postfix no longer matches "
                          + $"(gate found: {gateDone}, window found: {windowDone}) — mid-warmup "
                          + "switches to a more accurate ranged weapon stay dead under CE.");
                return instructions;
            }
            return list;
        }
    }
}
