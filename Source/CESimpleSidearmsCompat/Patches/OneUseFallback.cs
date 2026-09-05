using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Asks SS to equip a sidearm if the pawn is left empty-handed after a CE one-use/thrown weapon is spent.
    /// </summary>
    [HarmonyPatch]
    public static class Verb_ShootCEOneUse_SelfConsume_Patch
    {
        public static bool Prepare() => SSEnums.Require(
            "pawns will stay empty-handed after throwing or consuming a one-use CE weapon.");

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            foreach (Type type in new[] { typeof(Verb_ShootCEOneUse), typeof(Verb_ThrowGrenade), typeof(Verb_ShootCEOneUseStatic) })
            {
                MethodBase method = AccessTools.DeclaredMethod(type, "SelfConsume");
                if (method != null)
                {
                    found.Add(method);
                }
                else if (type != typeof(Verb_ShootCEOneUseStatic))
                {
                    Log.Warning(PatchGuard.LogPrefix + type.Name + ".SelfConsume not found — pawns "
                                + "using that verb will stay empty-handed after a one-use weapon. "
                                + "Combat Extended probably reshaped it.");
                }
            }
            if (found.Count == 0)
            {
                Log.Error(PatchGuard.LogPrefix + "No SelfConsume declarations found at all — pawns "
                          + "will stay empty-handed after throwing or consuming one-use CE weapons.");
            }
            return found;
        }

        [HarmonyPostfix]
        public static void Postfix(Verb_ShootCEOneUse __instance)
        {
            try
            {
                PostfixInner(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "One-use re-equip fallback failed. " + e, 0x4345530C);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Verb_ShootCEOneUse __instance)
        {
            Pawn pawn = __instance.ShooterPawn;
            if (pawn == null || pawn.Dead || pawn.equipment == null)
            {
                return;
            }
            // Opportunistic attacks restore the pawn's weapon when the job ends, so
            // re-equipping now would just swap it out and back.
            if (pawn.jobs?.curJob?.def == CE_JobDefOf.OpportunisticAttack)
            {
                return;
            }
            if (pawn.equipment.Primary != null || !pawn.IsValidSidearmsCarrierRightNow())
            {
                return;
            }
            WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, SSEnums.UsedUp);
        }
    }
}
