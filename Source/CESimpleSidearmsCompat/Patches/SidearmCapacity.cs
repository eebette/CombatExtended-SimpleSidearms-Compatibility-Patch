using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Patches SS pickup check logic to respect CE weight/bulk system by postfixing SS's
    /// StatCalculator.CanPickupSidearmType with a CE bulk-capacity check.
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
            // GetAvailableBulk(false) reads CE's cached figure without the full recount.
            if (bulk > inventory.GetAvailableBulk(false))
            {
                errString = "SidearmPickupFail_NoFreeSpace".Translate();
                __result = false;
            }
        }
    }

    /// <summary>
    /// Blocks SS's auto-retrieval from fetching a sidearm CE can't fit.
    /// </summary>
    [HarmonyPatch]
    public static class JobGiver_RetrieveWeapon_FitValidator_Patch
    {
        // Patches SS's the per-candidate fetch-ability test to also honor CE's criteria.
        private static MethodBase target;
        private static FieldInfo pawnField;

        public static bool Prepare()
        {
            if (target != null && pawnField != null)
            {
                return true;
            }
            MethodInfo foundMethod = null;
            FieldInfo foundField = null;
            int matches = 0;
            foreach (Type nested in typeof(JobGiver_RetrieveWeapon).GetNestedTypes(AccessTools.all))
            {
                foreach (MethodInfo m in nested.GetMethods(AccessTools.all))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.IsStatic || m.ReturnType != typeof(bool) || ps.Length != 1
                        || ps[0].ParameterType != typeof(Thing) || !m.Name.Contains("TryGiveJobStatic"))
                    {
                        continue;
                    }
                    // The closure must capture exactly one pawn.
                    FieldInfo pawnFld = null;
                    int pawnFieldCount = 0;
                    foreach (FieldInfo f in nested.GetFields(AccessTools.all))
                    {
                        if (f.FieldType == typeof(Pawn))
                        {
                            pawnFld = f;
                            pawnFieldCount++;
                        }
                    }
                    if (pawnFieldCount != 1)
                    {
                        continue;
                    }
                    matches++;
                    foundMethod = m;
                    foundField = pawnFld;
                }
            }
            if (matches == 1)
            {
                target = foundMethod;
                pawnField = foundField;
                return true;
            }
            Log.Error(PatchGuard.LogPrefix + "Simple Sidearms' retrieval reachability validator was "
                      + (matches == 0 ? "not found" : "ambiguous (" + matches + " candidates)")
                      + " — auto-retrieval will not be capacity-checked (a pawn may walk for a sidearm "
                      + "Combat Extended cannot fit).");
            return false;
        }

        public static MethodBase TargetMethod() => target;

        [HarmonyPostfix]
        // __0 (positional) rather than a named param: the target is a compiler-generated
        // lambda whose parameter name is incidental and could change between SS builds.
        public static void Postfix(Thing __0, ref bool __result, object __instance)
        {
            if (!__result)
            {
                return; // already rejected as forbidden or unreservable
            }
            try
            {
                PostfixInner(__0, ref __result, __instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Retrieval fit-validator failed; Simple Sidearms "
                              + "may fetch a sidearm Combat Extended cannot fit. " + e, 0x43455313);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Thing t, ref bool __result, object __instance)
        {
            if (t == null || !(pawnField.GetValue(__instance) is Pawn pawn))
            {
                return;
            }
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory != null && !inventory.CanFitInInventory(t, out int _))
            {
                __result = false;
            }
        }
    }
}
