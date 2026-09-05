using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Intercepts;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Re-attaches SS's OWN postfix from vanilla's attack hook to CE's.
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
            Verb_MeleeAttack_TryCastShot_PostFix.TryCastShot(__instance);
        }
    }
}
