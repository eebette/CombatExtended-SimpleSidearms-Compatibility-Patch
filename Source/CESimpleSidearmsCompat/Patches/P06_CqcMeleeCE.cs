using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 6: SS's CQC reaction (victim auto-draws a melee weapon when melee-attacked) hooks
    /// vanilla Verb_MeleeAttack.TryCastShot. CE's Verb_MeleeAttackCE overrides that method,
    /// so the SS hook never fires. Mirror SS's postfix on the CE override.
    /// </summary>
    [HarmonyPatch(typeof(Verb_MeleeAttackCE), "TryCastShot", new Type[0])]
    public static class Verb_MeleeAttackCE_TryCastShot_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Verb_MeleeAttackCE), "TryCastShot", new Type[0],
            "pawns will not auto-draw a melee weapon when melee-attacked (SS's own hook never fires on CE melee).");

        [HarmonyPostfix]
        public static void Postfix(Verb_MeleeAttackCE __instance)
        {
            try
            {
                PostfixInner(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CQC melee-draw reaction failed. " + e, 0x4345530A);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Verb_MeleeAttackCE __instance)
        {
            Thing targetThing = __instance.CurrentTarget.Thing;
            Pawn caster = __instance.CasterPawn;
            if (caster == null || !(targetThing is Pawn target))
            {
                return;
            }
            if (target.Dead || !target.RaceProps.Humanlike || target.equipment == null)
            {
                return;
            }
            WeaponAssingment.doCQC(target, caster);
        }
    }
}
