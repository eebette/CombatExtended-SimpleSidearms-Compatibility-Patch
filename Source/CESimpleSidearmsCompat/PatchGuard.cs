using System;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsCompat
{
    /// <summary>
    /// Failure doctrine for every patch class in this assembly, in three layers:
    ///
    /// 1. Attribute pins. Every [HarmonyPatch] names its target's full parameter list, so an
    ///    upstream overload added later cannot make the attribute ambiguous — an ambiguous
    ///    match throws inside the patch processor and Bootstrap can only report it
    ///    generically.
    ///
    /// 2. Prepare guards. Each class re-resolves its pinned target here first; when the
    ///    member is gone the class skips itself with a named, player-readable consequence
    ///    instead of tripping the processor. Prepare runs per class, so one missing member
    ///    costs exactly its own feature.
    ///
    /// 3. Outer/inner method splits. Patch bodies reference CE and Simple Sidearms members
    ///    far beyond the patched method, and the JIT resolves those when the *body* first
    ///    compiles — a missing member throws before the first instruction runs, where no
    ///    try/catch inside the same method can see it. So each patch entry is a thin outer
    ///    method (signature limited to types the patch target itself already proved) that
    ///    calls the real body in a NoInlining inner method inside try/catch: upstream drift
    ///    surfaces as one named error, and the original keeps running vanilla.
    /// </summary>
    internal static class PatchGuard
    {
        internal const string LogPrefix = "[CE+SimpleSidearms] ";

        /// <summary>
        /// Shared by every Prepare: no target, no feature, named error, no throw. The
        /// parameter types are mandatory — AccessTools.Method with a null list rethrows on
        /// an ambiguous match, which is the processor abort these guards exist to prevent.
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
    /// SS enum values, re-resolved BY NAME at load. The C# compiler bakes enum members to
    /// integers from the reference assembly, so an upstream insertion mid-enum silently
    /// rewires every baked comparison and every value passed into SS — zero log lines,
    /// green census. Parsing the names against the loaded assembly's enum makes that drift
    /// either harmless (values follow the names) or loud (a vanished name fails the
    /// consuming classes' Prepare with its consequence).
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
