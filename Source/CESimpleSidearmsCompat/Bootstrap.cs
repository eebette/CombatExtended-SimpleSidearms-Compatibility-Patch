using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended.Compatibility;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsCompat
{
    public static class Bootstrap
    {
        public const string HarmonyId = "eebette.CESimpleSidearmsCompat";
        private const string LogPrefix = "[CE+SimpleSidearms] ";
        private static bool patched;

        private static bool DependenciesPresent =>
            ModsConfig.IsActive("CETeam.CombatExtended")
            && ModsConfig.IsActive("PeteTimesSix.SimpleSidearms");

        /// <summary>
        /// Applies the patch classes one at a time. Matches CE's own convention.
        /// </summary>
        public static void EnsurePatched()
        {
            if (patched)
            {
                return;
            }
            patched = true;

            if (!DependenciesPresent)
            {
                Log.Warning(LogPrefix + "Combat Extended or Simple Sidearms is not active; compatibility patches skipped.");
                return;
            }

            int applied = 0;
            var failures = new List<string>();
            try
            {
                var harmony = new Harmony(HarmonyId);
                foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Bootstrap).Assembly))
                {
                    try
                    {
                        List<MethodInfo> methods = harmony.CreateClassProcessor(type).Patch();
                        if (methods != null && methods.Count > 0)
                        {
                            applied++;
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Add(type.Name);
                        Log.Error($"{LogPrefix}Patch class {type.Name} could not be applied — that one fix is inactive, the others still work. This usually means CE or Simple Sidearms changed a patched member. {e}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}Patching aborted: {e}");
                return;
            }

            if (failures.Count > 0)
            {
                Log.Warning($"{LogPrefix}Installed {applied} patch class(es); {failures.Count} failed ({string.Join(", ", failures)}).");
            }
            else
            {
                Log.Message($"{LogPrefix}Compatibility patches installed ({applied} patch classes).");
            }
        }
    }

    // Primary entry point: discovered and installed by CE's own compatibility scanner.
    public class CECompatPatch : IPatch
    {
        public bool CanInstall()
        {
            return ModsConfig.IsActive("PeteTimesSix.SimpleSidearms");
        }

        public void Install()
        {
            Bootstrap.EnsurePatched();
        }
    }

    // Fallback in case CE's scanner changes behavior; EnsurePatched is idempotent.
    [StaticConstructorOnStartup]
    public static class BootstrapFallback
    {
        static BootstrapFallback()
        {
            Bootstrap.EnsurePatched();
        }
    }
}
