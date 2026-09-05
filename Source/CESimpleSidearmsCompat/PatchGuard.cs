using System;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsCompat
{
    /// <summary>
    /// Failure doctrine for every patch class here — so a broken assumption turns a feature off,
    /// never crashes:
    ///
    /// 1. Attribute pins — an exact target signature; a moved/renamed target won't bind.
    /// 2. Prepare guards (Require/RequireType) — confirm the target + depended-on types exist,
    ///    else log the gameplay consequence and skip the class.
    /// 3. Thin-outer/NoInlining-inner split — the outer try/catch keeps a throw out of the game
    ///    (Log.ErrorOnce) and falls back to upstream behavior.
    /// </summary>
    internal static class PatchGuard
    {
        internal const string LogPrefix = "[CE+SimpleSidearms] ";

        /// <summary>
        /// Shared by every Prepare: check if target method exists.
        /// </summary>
        internal static bool Require(Type type, string method, Type[] args, string consequence)
        {
            if (AccessTools.Method(type, method, args) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}{type.Name}.{method} not found — {consequence} "
                      + "The mod that declares it probably moved it.");
            return false;
        }

        /// <summary>Prepare guard for a TYPE a patch body depends on beyond its target.</summary>
        internal static bool RequireType(string fullName, string consequence)
        {
            if (AccessTools.TypeByName(fullName) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}{fullName} not found — {consequence} "
                      + "The mod that declares it probably moved it.");
            return false;
        }
    }

    /// <summary>
    /// Hashes an upstream method's IL so a shape change we depend on beyond what our anchors see
    /// (a reshaped body a transpiler would mis-edit) becomes a loud re-verify error at load, not
    /// silent wrong behavior. To bake a hash: set it to "" — Verify then logs the computed value
    /// ("bake me") — run once, paste it in.
    /// </summary>
    internal static class UpstreamFingerprint
    {
        // Baked against SS v1.6 — re-harvest on upstream updates.
        internal const string StanceTickHash = "93dde9eafa5069a4";

        // Baked against SS v1.6 — re-harvest on upstream updates.
        internal const string MeleeDpsBiasedHash = "9eb4ccaa82b9c104";

        internal static void Verify(Type type, string method, string expected, string protects)
        {
            try
            {
                var mb = AccessTools.Method(type, method);
                if (mb == null)
                {
                    Log.Error($"{PatchGuard.LogPrefix}{type.Name}.{method} not found — {protects} "
                              + "cannot be verified against upstream.");
                    return;
                }
                ulong hash = 14695981039346656037UL; // FNV-1a
                foreach (var instruction in HarmonyLib.PatchProcessor.GetOriginalInstructions(mb))
                {
                    // Invariant formatting.
                    string token = instruction.opcode.Name
                        + (instruction.operand is IFormattable formattable
                            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                            : instruction.operand?.ToString() ?? "");
                    foreach (char c in token)
                    {
                        hash = (hash ^ c) * 1099511628211UL;
                    }
                }
                string computed = hash.ToString("x16");
                if (string.IsNullOrEmpty(expected))
                {
                    Log.Message($"{PatchGuard.LogPrefix}FINGERPRINT {type.Name}.{method} = {computed} (bake me)");
                    return;
                }
                if (computed != expected)
                {
                    Log.Error($"{PatchGuard.LogPrefix}{type.Name}.{method} changed shape upstream "
                              + $"(fingerprint {computed}, expected {expected}) — re-verify {protects}.");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{PatchGuard.LogPrefix}Fingerprint check for {type.Name}.{method} failed to run: " + e.Message);
            }
        }
    }

    /// <summary>
    /// SS enum values.
    /// </summary>
    internal static class SSEnums
    {
        internal static readonly bool Resolved;
        internal static readonly PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum Combat;
        internal static readonly PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum UsedUp;
        internal static readonly PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode Melee;

        static SSEnums()
        {
            Resolved = TryResolve("Combat", out Combat)
                       & TryResolve("UsedUp", out UsedUp)
                       & TryResolve("Melee", out Melee);
        }

        internal static bool Require(string consequence)
        {
            if (Resolved)
            {
                return true;
            }
            Log.Error(PatchGuard.LogPrefix + "A Simple Sidearms enum member could not be resolved "
                      + $"by name — {consequence}");
            return false;
        }

        private static bool TryResolve<T>(string name, out T value) where T : struct
        {
            if (Enum.TryParse(name, out value))
            {
                return true;
            }
            Log.Error($"{PatchGuard.LogPrefix}Simple Sidearms enum {typeof(T).Name}.{name} no longer exists.");
            return false;
        }
    }
}
