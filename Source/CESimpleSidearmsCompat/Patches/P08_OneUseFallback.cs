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
    /// Axis 8: SS re-equips after a one-use weapon is consumed by hooking vanilla
    /// Verb_ShootOneUse.SelfConsume; CE's Verb_ShootCEOneUse is a separate class, so that
    /// hook never fires. CE natively re-equips a same-def weapon (and otherwise calls
    /// SwitchToNextViableWeapon, which axis 9 routes through SS preferences); this fallback
    /// covers the remaining case where the pawn ends up empty-handed.
    /// </summary>
    [HarmonyPatch]
    public static class Verb_ShootCEOneUse_SelfConsume_Patch
    {
        /// <summary>
        /// SelfConsume is private, so a subclass declaring its own shadows the base rather
        /// than overriding it — and Verb_ThrowGrenade does exactly that, which meant every
        /// thrown weapon slipped past a patch on the base declaration alone.
        ///
        /// Each type is checked on its own so one silently un-shadowed declaration cannot
        /// leave the others unpatched — and a miss is LOGGED, because an empty yield here
        /// used to skip that verb type without a word.
        /// </summary>
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
                    // The Static subclass inherits rather than shadows today; its own
                    // declaration appearing would be new, its absence is normal.
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
            if (pawn.equipment.Primary != null || !pawn.IsValidSidearmsCarrierRightNow())
            {
                return;
            }
            WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, DroppingModeEnum.UsedUp);
        }
    }
}
