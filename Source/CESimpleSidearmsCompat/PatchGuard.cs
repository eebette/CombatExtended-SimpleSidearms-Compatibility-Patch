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
    }
}
