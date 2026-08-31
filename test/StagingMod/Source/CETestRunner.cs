using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;
using SSCore = PeteTimesSix.SimpleSidearms.SimpleSidearms;

namespace CESSCompatTestStaging
{
    /// <summary>
    /// Acceptance harness for the CETEST saves (compat patch axes). Launch with:
    ///   -celoadsave=CETEST-1-pickup     -ceassert=cetest1
    ///   -celoadsave=CETEST-2-selection  -ceassert=cetest2
    ///   -celoadsave=CETEST-3-combat     -ceassert=cetest3
    ///   -celoadsave=CETEST-4-generation -ceassert=cetest4
    /// Same phase/check machinery as the Loadouts module's SupplyTestRunner; this
    /// runner owns scenarios prefixed "cetest" and ignores everything else (the
    /// Loadouts staging mod owns "supply" and does the same, so both mods can sit
    /// in one profile). Results: test-results-&lt;scenario&gt;.json in the save-data
    /// folder, then self-shutdown.
    /// Beyond TESTPLAN criteria, phases hunt under-the-surface bugs: hold-record
    /// duplication, CE capacity overruns, orphan ammo, generator idempotence.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CETestBoot
    {
        static CETestBoot()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("cetest"))
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message($"[CETest] Auto-loading save '{save}'.");
                    GameDataSaveLoader.LoadGame(save);
                });
            }
        }
    }

    public class CETestRunnerComponent : GameComponent
    {
        private class Check
        {
            public string name;
            public Func<(bool pass, string detail)> eval;
            public bool informational; // recorded, never fails the run
            // Must-not-happen. Re-evaluated on every poll instead of latching on first pass,
            // and a failure fails the phase immediately rather than waiting for the deadline.
            // Without this a negative check passes at tick 0 — before the thing it forbids
            // could have happened — and is never looked at again.
            public bool negative;
            // Something the phase needs to be TRUE before its real checks mean anything —
            // the pawn is carrying the weapon, the raiders are on the map, the magazine is
            // loaded. A phase whose precondition never holds is reported INVALID rather than
            // passed or failed: it did not test what it claims to, and that is a different
            // problem from the code being wrong.
            public bool precondition;
            public bool passed;
            public string lastDetail = "not evaluated";
        }

        private class Phase
        {
            public string label;
            // Establishes everything this phase depends on, so it inherits nothing from the
            // phases before it. Runs once, before mutate. Paired with precondition checks:
            // arrange makes it so, the preconditions prove it.
            public Action arrange;
            public Action mutate;
            public List<Check> checks = new List<Check>();
            public int deadlineTicks;
            // Phase cannot complete before this. The observation window a negative check has
            // to hold across, and the settle time for informational checks.
            public int minTicks;
            // Runs on every poll after the act, for phases whose scenario needs driving
            // rather than waiting.
            public Action poll;
            public bool failed;
            public bool invalid;   // a precondition never held; the phase proved nothing
            // mutate is deferred until every precondition holds. Firing it immediately after
            // arrange means firing it into a world that has not caught up.
            public bool mutated;
            public string diagnostic;  // an unexpected error or warning seen during it
            // Diagnostics THIS phase deliberately provokes (e.g. SS's equip-refusal
            // warnings). Scoped per phase on purpose: the same warning appearing in any
            // other phase is a finding, and a global allowlist entry would mask it.
            public string[] expectedDiagnostics;
        }

        /// <summary>
        /// Diagnostics we have accounted for and decided are not ours. Everything else — any
        /// Error from any mod, any Warning not listed here — fails the phase it appeared in.
        ///
        /// Errors from CE or Simple Sidearms count against us on purpose: this mod exists to
        /// make the two work together, and breaking one of them is the most consequential
        /// thing it can do. Each entry below has to be justified, not just observed.
        /// </summary>
        private static readonly string[] ExpectedDiagnostics =
        {
            // Simple Sidearms sweeps its own memory on load and says so. Not provoked by us:
            // it fires on a save this mod has never touched.
            "had a null weapon memory, removing",
            "had a missing def or malformed data, removing",
            // The harness's own lines — NOT a blanket prefix. The runner's "threw:" reports
            // fail their own phase directly at the catch sites, so listing them here loses
            // nothing; it only stops the report bleeding into the NEXT phase's scan.
            "[CETest] Phase ",
            "[CETest] poll for ",
            "[CETest] Mutation for phase ",
            "[CETest] Setup for phase ",
            "[CETest] Isolated run",
            "[CETest] Results written",
            "[CETest] Scenario complete",
            "[CETest] Loadouts module",
            "[CETestStaging]",
            // RimBridge logs startup telemetry at Warning level and its startup straddles
            // the log baseline. Development tool, not shipped, nothing here provokes it.
            "[RimBridge] STARTUP_TIMING",
        };

        // text -> repeats already accounted for. RimWorld merges identical log texts
        // into one LogMessage with a growing repeats count, so set-of-text dedupe made
        // the FIRST (expected) occurrence of a constant warning spend the text for the
        // whole run — an identical warning provoked by a later phase was invisible.
        private readonly Dictionary<string, int> seenDiagnostics = new Dictionary<string, int>();

        /// <summary>
        /// Everything already in the log when the scenario starts is somebody else's: mod
        /// metadata complaints, startup telemetry, whatever the profile's other mods say on
        /// their way up. Only what the run provokes can be attributed to it.
        /// </summary>
        private void BaselineDiagnostics()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                seenDiagnostics[msg.text ?? ""] = msg.repeats;
            }
            Log.Message($"[CETest] Diagnostics baselined at {seenDiagnostics.Count} pre-existing message(s).");
        }

        /// <summary>
        /// The baseline marks everything already logged as somebody else's — including
        /// this mod's own load-time failures (a Prepare refusal, a Bootstrap per-class
        /// error, the P07 transpiler's mismatch fallback), which happen during startup and
        /// would otherwise only surface through whatever behavioral phase happens to sit
        /// downstream. One sweep of the baselined messages for our own prefixes closes
        /// that hole.
        /// </summary>
        private static string StartupDiagnostic()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
                {
                    continue;
                }
                string text = msg.text ?? "";
                if (text.Contains("[CE+SimpleSidearms]")
                    || text.Contains("Loadouts module is ACTIVE")
                    || text.Contains("scenarios are contaminated"))
                {
                    return text.Split('\n')[0];
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the first unaccounted-for error or warning since the last call.
        /// Log.Messages is a capped queue, so this reads the whole of it every poll and
        /// remembers what it has already reported rather than tracking an index the queue
        /// can invalidate underneath it.
        /// </summary>
        private string NewDiagnostic(Phase phase)
        {
            foreach (LogMessage msg in Log.Messages)
            {
                if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
                {
                    continue;
                }
                string text = msg.text ?? "";
                // A grown repeats count is a NEW occurrence of a previously-seen text and
                // must be re-judged against the CURRENT phase's expectations.
                if (seenDiagnostics.TryGetValue(text, out int accounted) && msg.repeats <= accounted)
                {
                    continue;
                }
                seenDiagnostics[text] = msg.repeats;
                if (ExpectedDiagnostics.Any(e => text.Contains(e)))
                {
                    continue;
                }
                if (phase?.expectedDiagnostics != null
                    && phase.expectedDiagnostics.Any(e => text.Contains(e)))
                {
                    continue;
                }
                return $"{msg.type}: {text.Split('\n')[0]}";
            }
            return null;
        }

        private List<Phase> phases;
        private int isolatedPhase = -1;
        private int totalPhaseCount;
        private int phaseIndex = -1;
        private int phaseStartTick;
        private string scenario;
        private bool active;
        private bool done;

        public CETestRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("cetest"))
            {
                return;
            }
            // "cetest1:2" runs phase 2 and nothing else, in its own process against a freshly
            // loaded save. The sequenced run proves the phases work against accumulated
            // state; this proves each one stands on its own.
            int colon = scenario.IndexOf(':');
            if (colon > 0 && int.TryParse(scenario.Substring(colon + 1), out int only))
            {
                isolatedPhase = only;
                scenario = scenario.Substring(0, colon);
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    DisableLoadoutsModule();
                    DisableTacticsModule();
                    phases = BuildScenario(scenario);
                    // Every phase carries the state dump; forgetting to add it per-phase is
                    // exactly the kind of omission it exists to catch.
                    string nick = ScenarioPawn(scenario);
                    foreach (Phase ph in phases)
                    {
                        if (!ph.checks.Any(c => c.name == "state"))
                        {
                            ph.checks.Add(State(nick));
                        }
                    }
                    totalPhaseCount = phases.Count;
                    if (isolatedPhase >= 0)
                    {
                        phases = isolatedPhase < totalPhaseCount
                            ? new List<Phase> { phases[isolatedPhase] }
                            : new List<Phase>();
                        Log.Message($"[CETest] Isolated run: phase {isolatedPhase} of {totalPhaseCount}"
                                    + (phases.Count == 0 ? " — out of range." : $" ('{phases[0].label}')."));
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[CETest] Scenario build failed: " + e);
                    WriteResults(crashed: e.ToString());
                    Root.Shutdown();
                    return;
                }
                BaselineDiagnostics();
                active = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Log.Message($"[CETest] Scenario '{scenario}' started, {phases.Count} phases.");
                AdvancePhase();
            });
        }

        /// <summary>
        /// The Loadouts module shares this test profile; switch its derivations off
        /// (in-memory only — no WriteSettings) so CETEST scenarios exercise the compat
        /// patch alone.
        /// </summary>
        /// <summary>
        /// Same isolation for the Tactics module, which ships its behaviors ON by
        /// default: with it in the mod list, its findBest re-ranks and reload-abort
        /// component would run inside every CETEST scenario. All its behaviors are
        /// toggle-gated, so switching the toggles off in-memory is a full disable.
        /// </summary>
        private static void DisableTacticsModule()
        {
            try
            {
                bool tacticsActive = ModsConfig.IsActive("eebette.CESimpleSidearmsCompat.Tactics")
                    || Harmony.GetAllPatchedMethods().Any(m =>
                        Harmony.GetPatchInfo(m)?.Owners.Any(o => o.Contains("CESimpleSidearmsCompat.Tactics")) ?? false);
                Type mod = GenTypes.GetTypeInAnyAssembly("CESSCompatTactics.TacticsMod");
                object settings = mod?.GetProperty("Settings")?.GetValue(null);
                if (settings == null)
                {
                    if (tacticsActive)
                    {
                        Log.Error("[CETest] Tactics module is ACTIVE but its settings type was not "
                                  + "found (renamed?) — scenarios are contaminated by its features.");
                    }
                    return;
                }
                string[] toggles = { "reloadAbort", "forcedDryFallthrough", "ammoDepthTiebreak",
                                     "targetAwareAmmoScoring", "armorAwareMelee" };
                foreach (string name in toggles)
                {
                    FieldInfo field = settings.GetType().GetField(name);
                    if (field == null)
                    {
                        Log.Error($"[CETest] Tactics settings found but '{name}' is gone — cannot switch "
                                  + "that feature off; scenarios are contaminated by it.");
                        continue;
                    }
                    field.SetValue(settings, false);
                }
                Log.Message("[CETest] Tactics module switched off (in-memory) for this run.");
            }
            catch (Exception e)
            {
                Log.Error("[CETest] Could not disable Tactics module — scenarios may be contaminated: " + e.Message);
            }
        }

        private static void DisableLoadoutsModule()
        {
            try
            {
                // Two independent sentinels: the packageId (renameable) and the live
                // Harmony owner list (survives a packageId rename — its own rename would
                // still slip through, but both moving in one release is a smaller target).
                bool loadoutsActive = ModsConfig.IsActive("eebette.CESimpleSidearmsCompat.Loadouts")
                    || Harmony.GetAllPatchedMethods().Any(m =>
                        Harmony.GetPatchInfo(m)?.Owners.Any(o => o.Contains("CESimpleSidearmsCompat.Loadouts")) ?? false);
                Type mod = GenTypes.GetTypeInAnyAssembly("CESimpleSidearmsCompat.Loadouts.LoadoutsMod");
                object settings = mod?.GetProperty("Settings")?.GetValue(null);
                if (settings == null)
                {
                    if (loadoutsActive)
                    {
                        // The mod is in the list but the reflection missed — a silent return
                        // here means every CETEST scenario runs with the Loadouts projections
                        // still active, which is exactly the contamination this method exists
                        // to prevent. Fail loud so a rename breaks the suite, not the results.
                        Log.Error("[CETest] Loadouts module is ACTIVE but its settings type was not "
                                  + "found (renamed again?) — scenarios are contaminated by its patches.");
                    }
                    return;
                }
                FieldInfo field = settings.GetType().GetField("loadoutWeaponsAsSidearms");
                if (field == null)
                {
                    Log.Error("[CETest] Loadouts settings found but 'loadoutWeaponsAsSidearms' is gone — "
                              + "cannot switch the module off; scenarios are contaminated by its patches.");
                    return;
                }
                field.SetValue(settings, false);
                Log.Message("[CETest] Loadouts module switched off (in-memory) for this run.");
            }
            catch (Exception e)
            {
                Log.Error("[CETest] Could not disable Loadouts module — scenarios may be contaminated: " + e.Message);
            }
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            if (Find.TickManager.Paused || Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0)
            {
                return;
            }

            if (phases.Count == 0)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];

            // Any unaccounted-for error or warning, from this mod or from CE or SS, fails
            // the phase it appeared in. Checked first: a phase that provoked a red error has
            // not passed, whatever its assertions say.
            string diagnostic = NewDiagnostic(phase);
            if (diagnostic != null)
            {
                phase.failed = true;
                phase.diagnostic = diagnostic;
                Log.Warning($"[CETest] Phase '{phase.label}' FAILED on an unexpected diagnostic: {diagnostic}");
                AdvancePhase();
                return;
            }

            // Poll BEFORE the checks, so the state the checks evaluate includes the last
            // action the phase drove.
            if (phase.mutated)
            {
                try
                {
                    phase.poll?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[CETest] poll for '{phase.label}' threw: " + e);
                    // A phase whose driver is dead is not observing anything — failing it
                    // beats silently degrading its driven window to a passive one.
                    phase.failed = true;
                    AdvancePhase();
                    return;
                }
            }

            bool allPass = true;
            bool preconditionsHold = true;
            Check tripped = null;
            foreach (Check check in phase.checks)
            {
                // Nothing but a precondition may be evaluated before the act. Without this
                // an outcome check runs against the freshly-arranged world — where it is
                // often trivially true — and latches there.
                if (!phase.mutated && !check.precondition)
                {
                    continue;
                }
                // Informational checks re-evaluate until the phase ends (their last
                // observation is what gets reported) and never gate advancement. Negative
                // checks re-evaluate because a must-not-happen that passes now can still
                // fail later — latching them is what makes them vacuous.
                if (check.passed && !check.informational && !check.negative)
                {
                    continue;
                }
                try
                {
                    (bool pass, string detail) = check.eval();
                    check.lastDetail = detail;
                    check.passed = pass || check.informational;
                    if (!pass && !check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            preconditionsHold = false;
                        }
                        else if (check.negative)
                        {
                            tripped = check;
                        }
                    }
                }
                catch (Exception e)
                {
                    // Full ToString: a bare Message on an NRE says nothing about WHERE. And
                    // first-wins: RimWorld dedups repeated stacktraces into "Duplicate
                    // stacktrace" markers, so re-evaluations would overwrite the only copy
                    // of the real stack with the marker.
                    if (!(check.lastDetail?.StartsWith("EXCEPTION") ?? false))
                    {
                        check.lastDetail = "EXCEPTION: " + e;
                    }
                    if (!check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            // A throwing precondition means the world was never ready — the
                            // phase must report VOID (tested nothing), not FAIL (blaming the
                            // product for a broken setup).
                            preconditionsHold = false;
                        }
                    }
                }
            }

            // Preconditions hold and the act has not happened yet: this is the moment the
            // world is ready for it.
            if (preconditionsHold && !phase.mutated)
            {
                phase.mutated = true;
                phaseStartTick = tick;   // the observation window starts from the act
                try
                {
                    phase.mutate?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[CETest] Mutation for phase '{phase.label}' threw: " + e);
                    phase.failed = true;
                    AdvancePhase();
                }
                return;
            }
            // Nothing the phase asserts means anything until its act has happened.
            if (!phase.mutated)
            {
                if (tick - phaseStartTick > phase.deadlineTicks)
                {
                    phase.invalid = true;
                    Log.Warning($"[CETest] Phase '{phase.label}' INVALID — preconditions never held: "
                                + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                                         .Select(c => $"{c.name} ({c.lastDetail})")));
                    AdvancePhase();
                }
                return;
            }

            if (tripped != null && preconditionsHold)
            {
                phase.failed = true;
                Log.Warning($"[CETest] Phase '{phase.label}' FAILED: '{tripped.name}' must not happen "
                            + $"but did at tick {tick} — {tripped.lastDetail}");
                AdvancePhase();
                return;
            }
            if (tick - phaseStartTick < phase.minTicks)
            {
                return;
            }
            if (allPass)
            {
                Log.Message($"[CETest] Phase '{phase.label}' PASSED at tick {tick}.");
                AdvancePhase();
            }
            else if (tick - phaseStartTick > phase.deadlineTicks)
            {
                // A phase whose preconditions never held did not test what it claims to.
                // That is a broken test, not broken code, and conflating the two is how a
                // suite quietly stops meaning anything.
                phase.invalid = !preconditionsHold;
                phase.failed = !phase.invalid;
                string why = phase.invalid
                    ? "INVALID — preconditions never held: "
                      + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                               .Select(c => $"{c.name} ({c.lastDetail})"))
                    : $"FAILED (deadline {phase.deadlineTicks} ticks).";
                Log.Warning($"[CETest] Phase '{phase.label}' {why}");
                AdvancePhase();
            }
        }

        private void AdvancePhase()
        {
            phaseIndex++;
            if (phaseIndex >= phases.Count)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];
            phaseStartTick = Find.TickManager.TicksGame;
            try
            {
                // Arrange only. mutate waits for the preconditions to hold — see Phase.mutated.
                phase.arrange?.Invoke();
                if (!phase.checks.Any(c => c.precondition))
                {
                    phase.mutate?.Invoke();
                    phase.mutated = true;
                }
            }
            catch (Exception e)
            {
                Log.Error($"[CETest] Setup for phase '{phase.label}' threw: " + e);
                phase.failed = true;
                foreach (Check c in phase.checks)
                {
                    c.lastDetail = "mutation threw: " + e.Message;
                }
                AdvancePhase();
            }
        }

        private void Finish()
        {
            done = true;
            WriteResults();
            Log.Message("[CETest] Scenario complete; shutting down.");
            Root.Shutdown();
        }

        private void WriteResults(string crashed = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scenario\": \"{scenario}\",\n");
            sb.Append($"  \"phaseCount\": {totalPhaseCount},\n");
            if (isolatedPhase >= 0)
            {
                sb.Append($"  \"isolatedPhase\": {isolatedPhase},\n");
            }
            bool overall = crashed == null && phases != null && phases.All(p => !p.failed && !p.invalid);
            sb.Append($"  \"passed\": {(overall ? "true" : "false")},\n");
            if (crashed != null)
            {
                sb.Append($"  \"crashed\": \"{Escape(crashed)}\",\n");
            }
            sb.Append($"  \"ticks\": {(Find.TickManager?.TicksGame ?? 0)},\n");
            sb.Append("  \"phases\": [\n");
            if (phases != null)
            {
                for (int i = 0; i < phases.Count; i++)
                {
                    Phase p = phases[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"label\": \"{Escape(p.label)}\",\n");
                    sb.Append($"      \"passed\": {((!p.failed && !p.invalid) ? "true" : "false")},\n");
                    sb.Append($"      \"invalid\": {(p.invalid ? "true" : "false")},\n");
                    if (p.diagnostic != null)
                    {
                        sb.Append($"      \"diagnostic\": \"{Escape(p.diagnostic)}\",\n");
                    }
                    sb.Append($"      \"reached\": {(i <= phaseIndex ? "true" : "false")},\n");
                    sb.Append("      \"checks\": [\n");
                    for (int j = 0; j < p.checks.Count; j++)
                    {
                        Check c = p.checks[j];
                        sb.Append("        {");
                        sb.Append($"\"name\": \"{Escape(c.name)}\", ");
                        sb.Append($"\"passed\": {(c.passed ? "true" : "false")}, ");
                        sb.Append($"\"informational\": {(c.informational ? "true" : "false")}, ");
                        sb.Append($"\"precondition\": {(c.precondition ? "true" : "false")}, ");
                        sb.Append($"\"detail\": \"{Escape(c.lastDetail)}\"");
                        sb.Append("}");
                        sb.Append(j < p.checks.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append("      ]\n");
                    sb.Append(i < phases.Count - 1 ? "    },\n" : "    }\n");
                }
            }
            sb.Append("  ]\n}\n");
            string suffix = isolatedPhase >= 0 ? $"-iso-{isolatedPhase:D2}" : "";
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{scenario}{suffix}.json");
            File.WriteAllText(path, sb.ToString());
            Log.Message($"[CETest] Results written to {path}");
        }

        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        // ---- shared helpers -----------------------------------------------

        private static Pawn Colonist(string nick)
        {
            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned
                .FirstOrDefault(p => p.Name is NameTriple nt && nt.Nick == nick);
            if (pawn == null)
            {
                throw new InvalidOperationException("Colonist not found: " + nick);
            }
            return pawn;
        }

        private static ThingDef D(string defName) => DefDatabase<ThingDef>.GetNamed(defName);

        private static ThingWithComps Carried(Pawn pawn, ThingDef def)
        {
            if (pawn.equipment?.Primary?.def == def)
            {
                return pawn.equipment.Primary;
            }
            return pawn.inventory.innerContainer.OfType<ThingWithComps>().FirstOrDefault(t => t.def == def);
        }

        private static Check C(string name, Func<(bool, string)> eval, bool informational = false)
        {
            return new Check { name = name, eval = eval, informational = informational };
        }

        /// <summary>A must-not-happen check. Held across the whole phase, not just sampled once.</summary>
        private static Check N(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, negative = true };
        }

        /// <summary>
        /// Something that must be true for the phase to mean anything. If it never holds the
        /// phase reports INVALID — it did not test what it claims to, which is a different
        /// failure from the code being wrong and should not be reported as either.
        /// </summary>
        private static Check P(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, precondition = true };
        }

        /// <summary>
        /// The standing state dump every phase carries. Informational, re-evaluated on every
        /// poll while positive checks latch — so this reports live state where the checks
        /// beside it report history, and a disagreement between the two is how a check that
        /// latched on the wrong world gets caught.
        /// </summary>
        private static Check State(string nick)
        {
            return new Check
            {
                name = "state",
                informational = true,
                eval = () =>
                {
                    Pawn pawn = Colonist(nick);
                    CompSidearmMemory m = CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false);
                    string mem = m == null ? "-" : string.Join(",", m.RememberedWeapons.Select(pr => pr.thing?.defName));
                    string carried = string.Join(",", pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .Select(w => w.def.defName));
                    CompInventory inv = pawn.TryGetComp<CompInventory>();
                    return (true,
                        $"mem=[{mem}] carried=[{carried}] "
                        + $"ranged={m?.DefaultRangedWeapon?.thing?.defName ?? "-"} "
                        + $"melee={m?.PreferredMeleeWeapon?.thing?.defName ?? "-"} "
                        + $"forced={m?.ForcedWeapon?.thing?.defName ?? "-"} "
                        + $"primary={pawn.equipment?.Primary?.def?.defName ?? "-"} "
                        + $"bulk={inv?.currentBulk:F1}/{inv?.capacityBulk:F1} "
                        + $"job={pawn.CurJobDef?.defName ?? "-"}");
                },
            };
        }

        /// <summary>The colonist whose state each scenario's dump follows.</summary>
        private static string ScenarioPawn(string name)
        {
            switch (name)
            {
                case "cetest1": return "Bulky";
                case "cetest2": return "Picky";
                case "cetest3": return "Scopey";
                case "cetest4": return "Boomy";
                default: return "Bulky";
            }
        }

        /// <summary>
        /// Phase 0 of every scenario: the patch census. Reflection-derived from Harmony
        /// rather than hardcoded per class, so a Prepare that quietly returned false, a
        /// Bootstrap per-class failure, or a TargetMethods that resolved nothing shows up as
        /// a wrong count before any behavioral phase runs against a half-patched game.
        /// </summary>
        private static Phase PatchInventoryPhase()
        {
            return new Phase
            {
                label = "patch-inventory",
                deadlineTicks = 1200,
                checks =
                {
                    C("no-startup-errors-from-this-mod", () =>
                    {
                        string bad = StartupDiagnostic();
                        return (bad == null, bad ?? "startup log clean");
                    }),
                    C("all-compat-patches-applied", () =>
                    {
                        var mine = Harmony.GetAllPatchedMethods()
                            .Where(m =>
                            {
                                var info = Harmony.GetPatchInfo(m);
                                // Literal rather than Bootstrap.HarmonyId: the staging mod
                                // deliberately has no reference to the product assembly, and
                                // the census should key on the id as actually registered.
                                return info != null && info.Owners.Contains("eebette.CESimpleSidearmsCompat");
                            })
                            .ToList();
                        // 23 distinct methods today: P01 x3 (capacity gate, retrieval
                        // postfix+scope on one method, hasWeaponType), P02 x3, P03 x2,
                        // P04 x1, P05 x2 (one shared with P09's dry-run), P06 x1, P07 x1
                        // (SS's own warmup postfix, transpiled), P08 x2 (SelfConsume
                        // declarations), P09 x2, P10 x2, P11 x2, P12 x1 (MeleePenetration),
                        // P13 x2 (getMeleeDPSBiased, MeleeSpeed).
                        // ">=" so an upstream adding a third SelfConsume declaration cannot
                        // fail the census.
                        return (mine.Count >= 23,
                            $"methods patched by eebette.CESimpleSidearmsCompat={mine.Count} (want >= 23): "
                            + string.Join(", ", mine.Select(m => m.DeclaringType?.Name + "." + m.Name).OrderBy(n => n)));
                    }),
                }
            };
        }

        private static List<Pawn> Hostiles()
        {
            return Find.CurrentMap.mapPawns.AllPawnsSpawned
                .Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead && !p.Downed).ToList();
        }

        private List<Phase> BuildScenario(string name)
        {
            switch (name)
            {
                case "cetest1": return BuildCetest1();
                case "cetest2": return BuildCetest2();
                case "cetest3": return BuildCetest3();
                case "cetest4": return BuildCetest4();
                default: throw new InvalidOperationException("Unknown scenario: " + name);
            }
        }

        // -- CETEST-1: axes 1 (bulk pickup) + 10 (hold sync) ----------------

        private List<Phase> BuildCetest1()
        {
            Pawn bulky = Colonist("Bulky");
            ThingDef lmg = D("Gun_LMG");
            ThingDef revolver = D("Gun_Revolver");
            ThingDef pistol = D("Gun_Autopistol");

            int PistolHoldRecords() => bulky.GetHoldRecords()?.Count(r => r._def == pistol) ?? 0;

            return new List<Phase>
            {
                PatchInventoryPhase(),
                new Phase
                {
                    label = "pickup-legality-and-hold",
                    deadlineTicks = 3000,
                    minTicks = 600, // the hold-record negative has to hold across a window
                    checks =
                    {
                        P("pistol-carried-and-remembered", () =>
                        {
                            bool carried = Carried(bulky, pistol) != null;
                            bool remembered = CompSidearmMemory.GetMemoryCompForPawn(bulky)
                                .RememberedWeapons.Any(pr => pr.thing == pistol);
                            return (carried && remembered, $"carried={carried} remembered={remembered}");
                        }),
                        C("lmg-denied-by-bulk", () =>
                        {
                            bool ok = StatCalculator.CanPickupSidearmType(new ThingDefStuffDefPair(lmg, null), bulky, out string err);
                            return (!ok, $"canPickup={ok} err='{err}'");
                        }),
                        C("revolver-denial-not-bulk", () =>
                        {
                            // Pawn already holds rifle+pistol, so SS's own ranged-slot cap
                            // legitimately denies a third ranged weapon. Axis 1 only owns the
                            // BULK gate: the light revolver must never be rejected as too
                            // heavy — its denial reason must be SS's slot cap, not P01.
                            bool ok = StatCalculator.CanPickupSidearmType(new ThingDefStuffDefPair(revolver, null), bulky, out string err);
                            bool bulkDenial = err != null && err.Contains("heavy");
                            return (!bulkDenial, $"canPickup={ok} err='{err}' (must not be a bulk denial)");
                        }),
                        C("bulk-within-capacity", () =>
                        {
                            CompInventory inv = bulky.TryGetComp<CompInventory>();
                            return (inv.currentBulk <= inv.capacityBulk + 0.01f,
                                $"bulk {inv.currentBulk:F1}/{inv.capacityBulk:F1} weight {inv.currentWeight:F1}/{inv.capacityWeight:F1}");
                        }),
                        C("remembered-pistol-not-excess", () =>
                        {
                            bool excess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int _);
                            bool pistolTargeted = excess && dropThing?.def == pistol;
                            return (!pistolTargeted, $"excess={excess} dropThing={dropThing?.def?.defName ?? "none"}");
                        }),
                        N("no-hold-records-written", () =>
                        {
                            // The exemption is answered in the GetExcess* postfixes and nothing
                            // is written back: CE's hold-tracker is shared with the player's own
                            // "hold this" command, and editing it clobbered their records.
                            // Negative: a record appearing at ANY point in the window fails the
                            // phase — the old latched form could pass before one was written.
                            int n = PistolHoldRecords();
                            return (n == 0, $"pistol hold records={n} (want 0 — exemption is read-only)");
                        }),
                    }
                },
                new Phase
                {
                    label = "forget-releases-hold",
                    deadlineTicks = 4000,
                    minTicks = 600,
                    mutate = () =>
                    {
                        CompSidearmMemory.GetMemoryCompForPawn(bulky)
                            .ForgetSidearmMemory(new ThingDefStuffDefPair(pistol, null));
                    },
                    checks =
                    {
                        P("pistol-remembered-before-forget", () =>
                        {
                            bool remembered = CompSidearmMemory.GetMemoryCompForPawn(bulky)
                                .RememberedWeapons.Any(pr => pr.thing == pistol);
                            return (remembered, $"remembered={remembered}");
                        }),
                        N("still-no-hold-records", () =>
                        {
                            int n = PistolHoldRecords();
                            return (n == 0, $"pistol hold records={n}");
                        }),
                        C("pistol-now-excess", () =>
                        {
                            bool excess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int _);
                            return (excess && dropThing?.def == pistol,
                                $"excess={excess} dropThing={dropThing?.def?.defName ?? "none"}");
                        }),
                    }
                },
                new Phase
                {
                    label = "re-remember-idempotent",
                    deadlineTicks = 4000,
                    mutate = () =>
                    {
                        ThingWithComps pistolThing = Carried(bulky, pistol);
                        CompSidearmMemory mem = CompSidearmMemory.GetMemoryCompForPawn(bulky);
                        for (int i = 0; i < 3; i++)
                        {
                            mem.InformOfAddedSidearm(pistolThing);
                        }
                    },
                    checks =
                    {
                        P("pistol-carried", () =>
                            (Carried(bulky, pistol) != null, $"carried={Carried(bulky, pistol) != null}")),
                        C("exemption-survives-repeat-remembers", () =>
                        {
                            // Repeated remembers can grow SS's memory list (observably, no
                            // dedup fires); the exemption must stay a clean yes/no regardless,
                            // and must still not write anything into CE's tracker.
                            bool excess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int _);
                            bool pistolTargeted = excess && dropThing?.def == pistol;
                            int n = PistolHoldRecords();
                            return (!pistolTargeted && n == 0,
                                $"pistolTargeted={pistolTargeted} hold records={n} (want false/0)");
                        }),
                        C("ss-memory-dup-upstream-quirk", () =>
                        {
                            // SS's InformOfAddedSidearm has NO duplicate guard upstream (the
                            // dedup code is commented out in SS source) — repeated calls grow
                            // RememberedWeapons. Recorded here as an upstream quirk; the compat
                            // patch's own state (hold records, previous check) must still dedup.
                            int n = CompSidearmMemory.GetMemoryCompForPawn(bulky)
                                .RememberedWeapons.Count(p => p.thing == pistol);
                            return (n == 1, $"pistol memory entries after 3x remember={n} (SS-native, no upstream guard)");
                        }, informational: true),
                    }
                },
                new Phase
                {
                    // Issue #23: SS memory is per type, so the drop exemption used to make
                    // every spare copy of a remembered pair undroppable forever.
                    label = "spare-copies-of-a-remembered-pair-are-droppable",
                    deadlineTicks = 4000,
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("enforcement-converges-on-the-remembered-copy", () =>
                        {
                            int remaining = CarriedOfDef(bulky, pistol);
                            return (spareCount == 2 && remaining == 1 && !spareExcess,
                                $"pistolDrops={spareCount} pistolsLeft={remaining} excessAfter={spareExcess} "
                                + "(want: two pistol drops, one kept, no excess)");
                        }),
                    },
                    mutate = () =>
                    {
                        // Everything synchronous with the capture: Bulky's staged loadout is
                        // dropUndefined, so CE's own enforcement consumes the unshielded
                        // spares within a few ticks of their existence — polling after even
                        // one tick races the very behavior under test (observed: the spares
                        // were gone before the check's first evaluation).
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(bulky);
                        while (memory.RememberedWeapons.Count(pr => pr.thing == pistol) > 1)
                        {
                            memory.ForgetSidearmMemory(new ThingDefStuffDefPair(pistol, null));
                        }
                        if (!memory.RememberedWeapons.Any(pr => pr.thing == pistol))
                        {
                            memory.InformOfAddedSidearm(Carried(bulky, pistol));
                        }
                        while (bulky.inventory.innerContainer.Count(t => t.def == pistol)
                               + (bulky.equipment?.Primary?.def == pistol ? 1 : 0) < 3)
                        {
                            pawnInventoryAdd(bulky, pistol);
                        }
                        bulky.TryGetComp<CompInventory>().UpdateInventory();
                        // Unstackable weapons converge one instance per pass (CE's dropCount
                        // is capped by the named instance's stackCount of 1), so drive the
                        // convergence synchronously: each proposal is consumed and the scan
                        // re-asked. The shield must yield exactly two proposals — the two
                        // spares — and then silence with the remembered copy still carried.
                        // Enact CE's enforcement faithfully: consume EVERY proposal (the
                        // loadout is dropUndefined, so other uncovered staged items are
                        // legitimately excess too and can precede the pistols in scan
                        // order), counting the pistol drops. The shield must yield exactly
                        // two pistol proposals and leave the remembered copy carried.
                        spareCount = 0;
                        spareDef = pistol;
                        for (int guard = 0; guard < 12; guard++)
                        {
                            spareExcess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int n);
                            if (!spareExcess || dropThing == null)
                            {
                                break;
                            }
                            if (dropThing.def == pistol)
                            {
                                spareCount++;
                            }
                            dropThing.Destroy(DestroyMode.Vanish);
                            bulky.TryGetComp<CompInventory>().UpdateInventory();
                        }
                    }
                },
                new Phase
                {
                    label = "the-remembered-copy-stays-protected",
                    deadlineTicks = 4000,
                    arrange = () =>
                    {
                        // Trim back down to the single remembered copy.
                        foreach (Thing spare in bulky.inventory.innerContainer
                            .Where(t => t.def == pistol).Skip(1).ToList())
                        {
                            spare.Destroy(DestroyMode.Vanish);
                        }
                    },
                    checks =
                    {
                        P("exactly-one-pistol-carried", () =>
                            (CarriedOfDef(bulky, pistol) == 1, $"carried={CarriedOfDef(bulky, pistol)}")),
                        C("no-drop-proposed-for-the-kept-copy", () =>
                        {
                            bool excess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int _);
                            bool pistolTargeted = excess && dropThing?.def == pistol;
                            return (!pistolTargeted, $"excess={excess} dropThing={dropThing?.def?.defName ?? "none"}");
                        }),
                    }
                },
                new Phase
                {
                    // The multi-stuff wedge (adversarial round 3): CE counts excess per
                    // DEF, the old veto protected per PAIR — one remembered steel knife
                    // plus a plasteel pickup meant CE flagged one-too-many forever and the
                    // veto blocked the trim forever. The storage shield works at CE's own
                    // def level, so the unremembered material trims.
                    label = "a-second-material-of-a-remembered-def-still-trims",
                    deadlineTicks = 4000,
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("enforcement-keeps-exactly-one-knife", () =>
                            (stuffCount == 1 && !stuffExcess,
                             $"knivesLeft={stuffCount} survivorStuff={stuffDef?.defName ?? "none"} excessAfter={stuffExcess} "
                             + "(want: one knife survives, no excess — WHICH material survives is CE's "
                             + "instance naming, the acknowledged seam in P10's header)")),
                    },
                    mutate = () =>
                    {
                        // Steel copy (remembered), plasteel copy (the battlefield pickup) —
                        // setup and capture in one synchronous act, because the loadout is
                        // dropUndefined and CE eats the unshielded copy within ticks.
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(bulky);
                        while (memory.RememberedWeapons.Any(pr => pr.thing == D("MeleeWeapon_Knife")))
                        {
                            memory.ForgetSidearmMemory(memory.RememberedWeapons.First(pr => pr.thing == D("MeleeWeapon_Knife")));
                        }
                        foreach (Thing old in bulky.inventory.innerContainer.Where(t => t.def == D("MeleeWeapon_Knife")).ToList())
                        {
                            old.Destroy(DestroyMode.Vanish);
                        }
                        var steel = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Knife"), D("Steel"));
                        var plasteel = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Knife"), D("Plasteel"));
                        bulky.inventory.innerContainer.TryAdd(steel, canMergeWithExistingStacks: false);
                        bulky.inventory.innerContainer.TryAdd(plasteel, canMergeWithExistingStacks: false);
                        bulky.TryGetComp<CompInventory>().UpdateInventory();
                        memory.InformOfAddedSidearm(steel);
                        // Converge CE's enforcement synchronously (other uncovered staged
                        // items — loose ammo — legitimately precede the knife in scan
                        // order on a fresh save): consume every proposal, then judge the
                        // end state. The def-level shield must leave exactly one knife,
                        // and it must be the remembered STEEL one only because it was the
                        // last of its def standing — instance naming stays CE's (the
                        // acknowledged seam in P10's header) — so the plasteel copy is
                        // what the proposals consumed.
                        for (int guard = 0; guard < 20; guard++)
                        {
                            stuffExcess = Utility_HoldTracker.GetExcessThing(bulky, out Thing dropThing, out int _);
                            if (!stuffExcess || dropThing == null)
                            {
                                break;
                            }
                            dropThing.Destroy(DestroyMode.Vanish);
                            bulky.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        var survivors = bulky.inventory.innerContainer
                            .Where(t => t.def == D("MeleeWeapon_Knife")).ToList();
                        stuffCount = survivors.Count;
                        stuffDef = survivors.FirstOrDefault()?.Stuff;
                    }
                },
                new Phase
                {
                    // Issue #20: SS's retrieval loop returns on the first unsatisfied memory
                    // it can find an instance for. A capacity-refusal used to mean no
                    // retrieval at all this pass — and a permanent map-wide rescan.
                    label = "a-refused-heavy-memory-does-not-block-the-next",
                    deadlineTicks = 4000,
                    minTicks = 300, // window for the not-forgotten negative
                    // Everything happens in mutate, at a REAL tick: at tick 0 (isolated
                    // runs fire arrange straight from LoadedGame) CE's capacity stats read
                    // as zeroes, so a capacity-targeted fill silently no-ops and the fit
                    // predicates flip once the world starts ticking.
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("setup-heavy-gun-unfittable-knife-fittable", () =>
                            (setupBigWontFit && setupKnifeFits,
                             $"bigWontFit={setupBigWontFit} knifeFits={setupKnifeFits} {retrievalDebug}")),
                        C("first-pass-refuses-the-heavy-gun", () =>
                            (retrievalFirstPass == null,
                             $"job={retrievalFirstPass?.def?.defName ?? "null"} target={retrievalFirstPass?.targetA.Thing?.def?.defName ?? "-"}")),
                        C("second-pass-fetches-the-knife", () =>
                        {
                            bool knife = retrievalSecondPass?.targetA.Thing?.def == D("MeleeWeapon_Knife");
                            return (knife, $"job={retrievalSecondPass?.def?.defName ?? "null"} "
                                + $"target={retrievalSecondPass?.targetA.Thing?.def?.defName ?? "-"}");
                        }),
                        N("the-refused-memory-is-not-forgotten", () =>
                        {
                            bool still = CompSidearmMemory.GetMemoryCompForPawn(bulky)
                                .RememberedWeapons.Any(pr => pr.thing == D("Gun_Minigun"));
                            return (still, "minigun remembered=" + still);
                        }),
                    },
                    mutate = () =>
                    {
                        // Phase independence: the two-stuff phase leaves a carried steel
                        // knife AND its memory entry behind — the copy would satisfy the
                        // knife memory (nulling the second pass), and the leftover memory
                        // makes SS fetch the knife on the FIRST pass, before the loop ever
                        // reaches the heavy gun.
                        CompSidearmMemory cleanup = CompSidearmMemory.GetMemoryCompForPawn(bulky);
                        while (cleanup.RememberedWeapons.Any(pr => pr.thing == D("MeleeWeapon_Knife")))
                        {
                            cleanup.ForgetSidearmMemory(cleanup.RememberedWeapons.First(pr => pr.thing == D("MeleeWeapon_Knife")));
                        }
                        foreach (Thing carriedKnife in bulky.inventory.innerContainer
                            .Where(t => t.def == D("MeleeWeapon_Knife")).ToList())
                        {
                            carriedKnife.Destroy(DestroyMode.Vanish);
                        }
                        bulky.TryGetComp<CompInventory>().UpdateInventory();
                        SpawnNear(bulky, D("Gun_Minigun"), null);
                        SpawnNear(bulky, D("MeleeWeapon_Knife"), D("Steel"));
                        CompInventory inv = bulky.TryGetComp<CompInventory>();
                        inv.UpdateInventory();
                        ThingWithComps bigThing = bulky.Map.listerThings.ThingsOfDef(D("Gun_Minigun"))
                            .OfType<ThingWithComps>().First(t => t.Spawned);
                        ThingWithComps knifeThing = bulky.Map.listerThings.ThingsOfDef(D("MeleeWeapon_Knife"))
                            .OfType<ThingWithComps>().First(t => t.Spawned);
                        // Ballast against the REAL predicate — CanFitInInventory itself —
                        // until the heavy gun stops fitting; the knife needs a fraction of
                        // that room and keeps fitting.
                        for (int guard = 0; guard < 60 && inv.CanFitInInventory(bigThing, out int _); guard++)
                        {
                            Thing steel = ThingMaker.MakeThing(D("Steel"));
                            steel.stackCount = 10;
                            bulky.inventory.innerContainer.TryAdd(steel, canMergeWithExistingStacks: false);
                            inv.UpdateInventory();
                        }
                        setupBigWontFit = !inv.CanFitInInventory(bigThing, out int _);
                        setupKnifeFits = inv.CanFitInInventory(knifeThing, out int _);

                        // Unforbid on the RESOLVED instances: the flag set at spawn time
                        // did not stick (forensics showed the knife still forbidden, and
                        // SS's search skips forbidden things).
                        bigThing.SetForbidden(false, warnOnFail: false);
                        knifeThing.SetForbidden(false, warnOnFail: false);
                        // Memorise both and take the two passes synchronously — SS's own
                        // think tree cannot fetch the knife in a poll gap first.
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(bulky);
                        memory.InformOfAddedSidearm(bigThing);
                        memory.InformOfAddedSidearm(knifeThing);
                        retrievalFirstPass = JobGiver_RetrieveWeapon.TryGiveJobStatic(bulky, inCombat: false);
                        retrievalSecondPass = JobGiver_RetrieveWeapon.TryGiveJobStatic(bulky, inCombat: false);
                        // Forensics: a third pass with the heavy memory removed isolates the
                        // knife search from the skip machinery.
                        memory.ForgetSidearmMemory(new ThingDefStuffDefPair(D("Gun_Minigun"), null));
                        Verse.AI.Job third = JobGiver_RetrieveWeapon.TryGiveJobStatic(bulky, inCombat: false);
                        memory.InformOfAddedSidearm(bigThing);
                        retrievalDebug = $"third={third?.def?.defName ?? "null"}:{third?.targetA.Thing?.def?.defName ?? "-"} "
                            + $"knifeForbidden={knifeThing.IsForbidden(bulky)} reservable={bulky.CanReserve(knifeThing)}";
                    }
                },
                new Phase
                {
                    // Convergence-round Critical: CE's "Ad Hoc" loadouts generate their
                    // ammo slots by calling GetStorageByThingDef from inside GetSlotsFor —
                    // the method the drop shield patches. Enumerating GetSlotsFor in the
                    // postfix made that an unbounded mutual recursion: one checkbox was an
                    // uncatchable stack overflow. The phase's assertion is survival: with
                    // the pre-fix code the process dies inside this mutate and no result
                    // file is ever written.
                    label = "an-adhoc-loadout-does-not-recurse-the-shield",
                    deadlineTicks = 4000,
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("the-excess-scan-returns", () =>
                            (adHocProbeRan, $"probeRan={adHocProbeRan} excess={adHocExcessResult}")),
                    },
                    mutate = () =>
                    {
                        CombatExtended.Loadout loadout = Utility_Loadouts.GetLoadout(bulky);
                        bool wasAdHoc = loadout.adHoc;
                        int wasMags = loadout.adHocMags;
                        try
                        {
                            loadout.adHoc = true;
                            loadout.adHocMags = 2;
                            adHocExcessResult = Utility_HoldTracker.GetExcessThing(bulky, out Thing _, out int _);
                            adHocProbeRan = true;
                        }
                        finally
                        {
                            loadout.adHoc = wasAdHoc;
                            loadout.adHocMags = wasMags;
                        }
                    }
                },
            };
        }

        private bool adHocProbeRan;
        private bool adHocExcessResult;

        /// <summary>Instances of a def the pawn carries, equipped included.</summary>
        private static int CarriedOfDef(Pawn pawn, ThingDef def)
        {
            int n = pawn.inventory?.innerContainer?.Count(t => t.def == def) ?? 0;
            if (pawn.equipment?.Primary?.def == def)
            {
                n++;
            }
            return n;
        }

        /// <summary>Raw inventory insert — deliberately bypasses every pickup gate.</summary>
        private static void pawnInventoryAdd(Pawn pawn, ThingDef def)
        {
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, def.MadeFromStuff ? D("Steel") : null);
            pawn.inventory.innerContainer.TryAdd(thing, canMergeWithExistingStacks: false);
        }

        private static void SpawnNear(Pawn pawn, ThingDef def, ThingDef stuff)
        {
            Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? (stuff ?? D("Steel")) : null);
            CellFinder.TryFindRandomCellNear(pawn.Position, pawn.Map, 6,
                c => c.Standable(pawn.Map), out IntVec3 cell);
            GenPlace.TryPlaceThing(thing, cell, pawn.Map, ThingPlaceMode.Near);
            // SS's retrieval search skips forbidden things, and placed items can spawn
            // forbidden to the player faction.
            thing.SetForbidden(false, warnOnFail: false);
        }

        private ThingWithComps meleeMace;
        private ThingWithComps meleeGladius;

        // Captured synchronously inside the retrieval-skip phase.
        private bool spareExcess;
        private ThingDef spareDef;
        private int spareCount;
        private bool stuffExcess;
        private ThingDef stuffDef;
        private int stuffCount;

        private Verse.AI.Job retrievalFirstPass;
        private Verse.AI.Job retrievalSecondPass;
        private string retrievalDebug;
        private bool setupBigWontFit;
        private bool setupKnifeFits;

        // -- CETEST-2: axes 2 (CE DPS), 3/9 (ammo-aware selection), 11 (classification) --

        private List<Phase> BuildCetest2()
        {
            Pawn picky = Colonist("Picky");
            ThingDef rifleDef = D("Gun_AssaultRifle");
            ThingDef pistolDef = D("Gun_Autopistol");
            ThingDef revolverDef = D("Gun_Revolver");
            ThingDef grenadeDef = D("Weapon_GrenadeEMP");

            // The staged hostile exists so findBestRangedWeapon has a real target; its guns
            // are not part of any assertion. They used to be its own protection: under the
            // old invented hit proxy the raider's SS-side pick was poor enough that Picky
            // survived every run. With scoring on CE's real hit model the raider draws a
            // loaded gun and downed Picky at tick ~210 (t180 RunForCover, t210 downed,
            // rifle on the ground — tracked instance-by-instance). A target only needs to
            // stand there.
            void DisarmHostiles()
            {
                foreach (Pawn hostile in Hostiles())
                {
                    // Inventory first: destroying the primary makes CE re-arm from
                    // inventory synchronously (SwitchToNextViableWeapon on destroy), so an
                    // equipment-first sweep leaves the raider holding the gun it just
                    // pulled. Then keep clearing until the hand is actually empty.
                    for (int guard = 0; guard < 8; guard++)
                    {
                        foreach (ThingWithComps w in hostile.inventory?.innerContainer?
                                     .OfType<ThingWithComps>().Where(t => t.def.IsWeapon).ToList()
                                 ?? new List<ThingWithComps>())
                        {
                            w.Destroy(DestroyMode.Vanish);
                        }
                        if (hostile.equipment?.Primary == null)
                        {
                            break;
                        }
                        hostile.equipment.DestroyAllEquipment();
                    }
                }
            }

            return new List<Phase>
            {
                PatchInventoryPhase(),
                new Phase
                {
                    label = "scoring-and-classification",
                    deadlineTicks = 4000,
                    arrange = DisarmHostiles,
                    checks =
                    {
                        P("dry-revolver-has-no-ammo", () =>
                        {
                            var user = Carried(picky, revolverDef)?.TryGetComp<CompAmmoUser>();
                            return (user != null && !user.HasAmmoOrMagazine,
                                $"mag {user?.CurMagCount}/{user?.MagSize} hasAmmoOrMag={user?.HasAmmoOrMagazine}");
                        }),
                        P("loaded-guns-have-ammo", () =>
                        {
                            var rifle = Carried(picky, rifleDef)?.TryGetComp<CompAmmoUser>();
                            var pistolUser = Carried(picky, pistolDef)?.TryGetComp<CompAmmoUser>();
                            bool ok = rifle?.HasAmmoOrMagazine == true && pistolUser?.HasAmmoOrMagazine == true;
                            return (ok, $"rifle={rifle?.HasAmmoOrMagazine} pistol={pistolUser?.HasAmmoOrMagazine}");
                        }),
                        C("ce-dps-sane-rifle", () =>
                        {
                            float bias = SSCore.Settings.SpeedSelectionBiasRanged;
                            float rifleDps = StatCalculator.RangedDPS(Carried(picky, rifleDef), bias, 0f, 20f);
                            bool sane = rifleDps > 0f && !float.IsNaN(rifleDps) && !float.IsInfinity(rifleDps);
                            return (sane, $"rifle@20={rifleDps:F2}");
                        }),
                        C("ce-dps-sane-pistol", () =>
                        {
                            // Scored INSIDE the pistol's own range. The old form asked for
                            // dps at a flat 20 cells, which only ever worked through stock
                            // SS's squared range gate — an autopistol's real range is below
                            // 20, and the corrected gate (#22/V2) rightly returns -1 there.
                            ThingWithComps pi = Carried(picky, pistolDef);
                            float range = pi.GetComp<CompEquippable>().PrimaryVerb.verbProps.range;
                            float dist = Math.Max(2f, Math.Min(8f, range - 2f));
                            float bias = SSCore.Settings.SpeedSelectionBiasRanged;
                            float pistolDps = StatCalculator.RangedDPS(pi, bias, 0f, dist);
                            bool sane = pistolDps > 0f && !float.IsNaN(pistolDps) && !float.IsInfinity(pistolDps);
                            return (sane, $"pistol@{dist:F0}={pistolDps:F2} (range={range:F0})");
                        }),
                        C("out-of-range-scores-negative", () =>
                        {
                            // The corrected range gate (#22/V2): a plain cell distance beyond
                            // the weapon's range must be ineligible (-1). Stock SS squares
                            // the range on both sides, which kept a 30-cell rifle scoreable
                            // out to 900 cells.
                            ThingWithComps rifle = Carried(picky, rifleDef);
                            float range = rifle.GetComp<CompEquippable>().PrimaryVerb.verbProps.range;
                            float dps = StatCalculator.RangedDPS(rifle, SSCore.Settings.SpeedSelectionBiasRanged, 0f, range + 20f);
                            return (dps < 0f, $"range={range:F0} dps@range+20={dps:F2} (want -1)");
                        }),
                        C("rifle-beats-pistol-at-range", () =>
                        {
                            float bias = SSCore.Settings.SpeedSelectionBiasRanged;
                            float rifleDps = StatCalculator.RangedDPS(Carried(picky, rifleDef), bias, 0f, 30f);
                            float pistolDps = StatCalculator.RangedDPS(Carried(picky, pistolDef), bias, 0f, 30f);
                            return (rifleDps > pistolDps, $"rifle@30={rifleDps:F2} pistol@30={pistolDps:F2}");
                        }),
                        C("best-weapon-never-dry", () =>
                        {
                            Pawn target = Hostiles().FirstOrDefault(h => !(h.RaceProps?.IsMechanoid ?? false));
                            var (weapon, dps, _) = GettersFilters.findBestRangedWeapon(picky,
                                target != null ? new LocalTargetInfo(target) : (LocalTargetInfo?)null);
                            bool ok = weapon != null && weapon.def != revolverDef;
                            return (ok, $"best={weapon?.def?.defName ?? "null"} dps={dps:F2} target={(target != null ? "yes" : "no")}");
                        }),
                        C("emp-grenade-classified-emp", () =>
                        {
                            ThingWithComps grenade = Carried(picky, grenadeDef);
                            return (grenade != null && GettersFilters.isEMPWeapon(grenade),
                                $"grenade={(grenade != null)} isEMP={(grenade != null ? GettersFilters.isEMPWeapon(grenade).ToString() : "n/a")}");
                        }),
                        C("fmj-rifle-not-emp-not-dangerous", () =>
                        {
                            ThingWithComps rifle = Carried(picky, rifleDef);
                            bool emp = GettersFilters.isEMPWeapon(rifle);
                            bool danger = GettersFilters.isDangerousWeapon(rifle);
                            return (!emp && !danger, $"isEMP={emp} isDangerous={danger}");
                        }),
                    }
                },
                new Phase
                {
                    // Axis 12: SS's melee penetration term is dead under CE (vanilla stub =
                    // damage x 0.015, near-uniform); the patch feeds it CE's real per-tool
                    // penetration. A mace's head (5.625 MPa blunt) must outrank a gladius
                    // (~0.45 mmRHA sharp) by a wide margin — under the dead stub both sit
                    // within a whisker of each other, damage-proportional.
                    label = "ce-melee-penetration-signal",
                    deadlineTicks = 4000,
                    checks =
                    {
                        P("world-ticking-and-weapons-made", () =>
                        {
                            if (meleeMace == null)
                            {
                                meleeMace = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Mace"), D("Steel"));
                                meleeGladius = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Gladius"), D("Steel"));
                            }
                            return (Find.TickManager.TicksGame > 60 && meleeMace != null && meleeGladius != null,
                                $"tick={Find.TickManager.TicksGame}");
                        }),
                        C("mace-penetration-dwarfs-gladius", () =>
                        {
                            float mace = StatCalculator.MeleePenetration(meleeMace, picky);
                            float gladius = StatCalculator.MeleePenetration(meleeGladius, picky);
                            // Same stuff on both, so the material factor cancels; the dead
                            // vanilla stub gives a damage-proportional ~1:1 ratio.
                            return (mace > gladius * 1.8f && mace > 0.3f,
                                $"mace={mace:F3} gladius={gladius:F3} ratio={(gladius > 0 ? mace / gladius : -1):F2}");
                        }),
                    }
                },
                new Phase
                {
                    // Axis 13: vanilla's melee damage accessor multiplies each tool by the
                    // ATTACKER's part efficiency in the tool's linkedBodyPartsGroup — and CE
                    // groups are weapon anatomy (Blade, Point), which no human has. Blades
                    // therefore scored as their 1-damage handles in every SS melee rank
                    // (a club outranked a longsword everywhere). The patch feeds SS
                    // selection-weighted CE tool damage; a steel gladius's biased score
                    // must sit well above its handle-only reading.
                    label = "ce-melee-damage-signal",
                    deadlineTicks = 4000,
                    checks =
                    {
                        P("weapons-made", () =>
                        {
                            if (meleeMace == null)
                            {
                                meleeMace = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Mace"), D("Steel"));
                                meleeGladius = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Gladius"), D("Steel"));
                            }
                            return (meleeMace != null && meleeGladius != null, "made");
                        }),
                        C("gladius-scores-as-a-blade-not-a-handle", () =>
                        {
                            float bias = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SpeedSelectionBiasMelee;
                            float gladius = StatCalculator.getMeleeDPSBiased(meleeGladius, picky, bias, 0f);
                            float mace = StatCalculator.getMeleeDPSBiased(meleeMace, picky, bias, 0f);
                            // A handle-only gladius read sits near 1 dps; a real blade read
                            // lands the same order of magnitude as the mace.
                            return (gladius > 4f && gladius > mace * 0.35f,
                                $"gladius={gladius:F2} mace={mace:F2}");
                        }),
                        C("blade-speed-is-the-blade's", () =>
                        {
                            float speed = StatCalculator.MeleeSpeed(meleeGladius, picky);
                            // Handle-only read = the handle's own cooldown; the blade-weighted
                            // read must differ from a pure handle figure and stay plausible.
                            return (speed > 0.5f && speed < 3f, $"speed={speed:F2}");
                        }),
                    }
                },
                new Phase
                {
                    label = "dry-primary-switches-to-loaded",
                    deadlineTicks = 6000,
                    minTicks = 600, // the never-dry negative has to hold across a window
                    arrange = DisarmHostiles, // an isolated run skips the earlier disarm
                    mutate = () =>
                    {
                        // Drain the rifle completely: empty mag AND remove its caliber from
                        // inventory so CE cannot count it reloadable, then run SS's re-equip.
                        ThingWithComps rifle = Carried(picky, rifleDef);
                        CompAmmoUser user = rifle.TryGetComp<CompAmmoUser>();
                        user.CurMagCount = 0;
                        List<ThingDef> rifleAmmo = user.Props.ammoSet.ammoTypes.Select(l => (ThingDef)l.ammo).ToList();
                        foreach (Thing stack in picky.inventory.innerContainer.Where(t => rifleAmmo.Contains(t.def)).ToList())
                        {
                            stack.Destroy(DestroyMode.Vanish);
                        }
                        WeaponAssingment.equipBestWeaponFromInventoryByPreference(picky, DroppingModeEnum.Combat);
                    },
                    checks =
                    {
                        P("rifle-and-loaded-pistol-carried", () =>
                        {
                            bool rifleCarried = Carried(picky, rifleDef) != null;
                            var pistolUser = Carried(picky, pistolDef)?.TryGetComp<CompAmmoUser>();
                            bool pistolLoaded = pistolUser?.HasAmmoOrMagazine == true;
                            return (rifleCarried && pistolLoaded, $"rifle={rifleCarried} pistolLoaded={pistolLoaded}");
                        }),
                        C("primary-is-loaded-pistol", () =>
                        {
                            ThingDef primary = picky.equipment?.Primary?.def;
                            return (primary == pistolDef, $"primary={primary?.defName ?? "none"}");
                        }),
                        N("never-dry-revolver-or-fists", () =>
                        {
                            // Held across the window, not sampled once: the switch landing on
                            // the pistol first and the revolver later would have passed the
                            // old latched form.
                            ThingDef primary = picky.equipment?.Primary?.def;
                            return (primary != revolverDef, $"primary={primary?.defName ?? "FISTS"}");
                        }),
                    }
                },
            };
        }

        // -- CETEST-3: axes 6 (CQC), 7 (warmup swap), 5 (reload guard) ------

        // Captured synchronously inside the axis-9 queued-equip phase; a poll-based check
        // would race the job it is meant to observe.
        private ThingDef queuedFrom;
        private JobDef queuedJob;
        private ThingDef queuedTarget;
        private ThingDef queuedPrimaryImmediately;
        private bool queuedResult;

        private List<Phase> BuildCetest3()
        {
            Pawn fency = Colonist("Fency");
            Pawn scopey = Colonist("Scopey");
            ThingDef gladius = D("MeleeWeapon_Gladius");
            ThingDef sniper = D("Gun_SniperRifle");
            ThingDef shotgun = D("Gun_PumpShotgun");

            return new List<Phase>
            {
                PatchInventoryPhase(),
                new Phase
                {
                    label = "cqc-melee-draw",
                    deadlineTicks = 30000,
                    checks =
                    {
                        P("hostiles-present", () =>
                            (Hostiles().Count > 0, $"hostiles={Hostiles().Count}")),
                        P("fency-carries-gladius", () =>
                            (Carried(fency, gladius) != null, $"carried={Carried(fency, gladius) != null}")),
                        C("fency-draws-gladius", () =>
                        {
                            ThingDef primary = fency.equipment?.Primary?.def;
                            return (primary == gladius, $"primary={primary?.defName ?? "none"} raiders={Hostiles().Count}");
                        }),
                    }
                },
                new Phase
                {
                    label = "warmup-swap-to-shotgun",
                    deadlineTicks = 10000,
                    mutate = () =>
                    {
                        SSCore.Settings.RangedCombatAutoSwitch = true;
                        // (kept as a hard stop behind the precondition — a throw here is a
                        // broken arrange, not a product failure)
                        // 0.9 = swap allowed through the first 90% of the warmup — permissive
                        // arrange, but INSIDE the 0..1 range the setting means anything in
                        // (the old 5f made the gate a constant, i.e. asserted nothing about it).
                        SSCore.Settings.RangedCombatAutoSwitchMaxWarmup = 0.9f;
                        Pawn target = Hostiles().FirstOrDefault();
                        if (target == null)
                        {
                            throw new InvalidOperationException("No hostile left for warmup-swap phase");
                        }
                        // Move the target close to Scopey so short range favors the shotgun.
                        IntVec3 near = scopey.Position + new IntVec3(6, 0, 0);
                        near = near.ClampInsideMap(scopey.Map);
                        if (!near.Standable(scopey.Map))
                        {
                            CellFinder.TryFindRandomCellNear(scopey.Position, scopey.Map, 8,
                                c => c.Standable(scopey.Map), out near);
                        }
                        target.Position = near;
                        target.Notify_Teleported();
                        scopey.drafter.Drafted = true;
                        Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                        scopey.jobs.TryTakeOrderedJob(job);
                    },
                    checks =
                    {
                        P("hostile-available-and-shotgun-carried", () =>
                        {
                            bool hostile = Hostiles().Count > 0;
                            bool sg = Carried(scopey, shotgun) != null;
                            return (hostile && sg, $"hostiles={Hostiles().Count} shotgunCarried={sg}");
                        }),
                        C("scopey-swaps-to-shotgun", () =>
                        {
                            ThingDef primary = scopey.equipment?.Primary?.def;
                            return (primary == shotgun, $"primary={primary?.defName ?? "none"} job={scopey.CurJobDef?.defName}");
                        }),
                    }
                },
                new Phase
                {
                    label = "reload-guard",
                    deadlineTicks = 15000,
                    mutate = () =>
                    {
                        foreach (Pawn hostile in Hostiles())
                        {
                            hostile.Destroy(DestroyMode.Vanish);
                        }
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // Drain whatever Scopey now holds (shotgun after the swap phase, or
                        // sniper if the swap failed); spares from staging are in inventory.
                        ThingWithComps primary = scopey.equipment.Primary;
                        CompAmmoUser user = primary.TryGetComp<CompAmmoUser>();
                        user.CurMagCount = 0;
                        Job reload = user.TryMakeReloadJob();
                        if (reload == null)
                        {
                            throw new InvalidOperationException("TryMakeReloadJob returned null (no spare ammo?)");
                        }
                        scopey.jobs.StartJob(reload, JobCondition.InterruptForced);
                        // Axis 5 direct hit: while the reload job runs, fire SS's switch
                        // entry point — the patch must refuse to cancel the reload.
                        WeaponAssingment.equipBestWeaponFromInventoryByPreference(scopey, DroppingModeEnum.Combat);
                    },
                    checks =
                    {
                        P("spare-ammo-for-primary", () =>
                        {
                            ThingWithComps primary = scopey.equipment?.Primary;
                            CompAmmoUser user = primary?.TryGetComp<CompAmmoUser>();
                            bool spare = user != null && scopey.inventory.innerContainer.Any(t =>
                                user.Props.ammoSet.ammoTypes.Any(l => (ThingDef)l.ammo == t.def));
                            return (spare, $"primary={primary?.def?.defName ?? "none"} spareAmmo={spare}");
                        }),
                        C("reload-survives-ss-switch-call", () =>
                        {
                            // Passes once the reload finished with the same weapon still equipped.
                            ThingWithComps primary = scopey.equipment?.Primary;
                            CompAmmoUser user = primary?.TryGetComp<CompAmmoUser>();
                            bool full = user != null && user.CurMagCount == user.MagSize;
                            return (full, $"primary={primary?.def?.defName} mag={user?.CurMagCount}/{user?.MagSize} job={scopey.CurJobDef?.defName}");
                        }),
                        C("reload-job-observed", () =>
                        {
                            bool reloading = scopey.CurJobDef == CE_JobDefOf.ReloadWeapon;
                            return (reloading, $"job={scopey.CurJobDef?.defName}");
                        }, informational: true),
                    }
                },
                new Phase
                {
                    // Axis 9, stopJob:false path (CE's CompReload calls it that way when a
                    // pawn's gun is empty mid-cast). CE wants an interruptible
                    // EquipFromInventory job, not an instant swap — but it should be equipping
                    // SS's preferred weapon, not the first viable one in CE's own list order.
                    label = "queued-equip-uses-ss-preference",
                    deadlineTicks = 10000,
                    // On a fresh save (isolated run) the sniper IS the primary, and a
                    // preference equal to the current weapon is an answer this path cannot
                    // act on — the phase would test CE's fallback, not the arbitration. Put
                    // another weapon in hand first; mid-sequence this is a no-op.
                    arrange = () =>
                    {
                        if (scopey.equipment?.Primary?.def == sniper)
                        {
                            ThingWithComps other = scopey.inventory.innerContainer
                                .OfType<ThingWithComps>().FirstOrDefault(t => t.def == shotgun)
                                ?? scopey.inventory.innerContainer.OfType<ThingWithComps>()
                                    .FirstOrDefault(t => t.def.IsRangedWeapon);
                            if (other != null)
                            {
                                scopey.TryGetComp<CompInventory>().TrySwitchToWeapon(other);
                            }
                        }
                    },
                    mutate = () =>
                    {
                        scopey.jobs.StopAll();
                        ThingWithComps sniperThing = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().FirstOrDefault(t => t.def == sniper)
                            ?? (scopey.equipment.Primary?.def == sniper ? scopey.equipment.Primary : null);
                        if (sniperThing == null)
                        {
                            throw new InvalidOperationException("Scopey is not carrying the sniper");
                        }
                        // CE only offers weapons it considers firable, so make sure the one SS
                        // is about to prefer actually has rounds.
                        sniperThing.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(scopey);
                        memory.DefaultRangedWeapon = new ThingDefStuffDefPair(sniper, null);

                        queuedFrom = scopey.equipment.Primary?.def;
                        bool handled = scopey.TryGetComp<CompInventory>()
                            .SwitchToNextViableWeapon(useFists: true, useAOE: false, stopJob: false);
                        // Captured synchronously: the job runs within a few ticks, well inside
                        // the runner's 30-tick poll interval.
                        queuedResult = handled;
                        queuedJob = scopey.CurJobDef;
                        queuedTarget = (scopey.CurJob?.targetA.Thing)?.def;
                        queuedPrimaryImmediately = scopey.equipment.Primary?.def;
                    },
                    checks =
                    {
                        P("sniper-carried-not-primary", () =>
                        {
                            bool carried = Carried(scopey, sniper) != null;
                            bool notPrimary = scopey.equipment?.Primary?.def != sniper;
                            return (carried && notPrimary,
                                $"sniperCarried={carried} primary={scopey.equipment?.Primary?.def?.defName ?? "none"}");
                        }),
                        C("equip-was-queued-not-instant", () =>
                        {
                            bool queued = queuedJob == CE_JobDefOf.EquipFromInventory;
                            bool unchanged = queuedPrimaryImmediately == queuedFrom;
                            return (queued && unchanged,
                                $"handled={queuedResult} job={queuedJob?.defName ?? "none"} "
                                + $"primaryAtCall={queuedPrimaryImmediately?.defName ?? "none"} (was {queuedFrom?.defName ?? "none"})");
                        }),
                        C("queued-weapon-is-ss-preference", () =>
                            (queuedTarget == sniper, $"jobTarget={queuedTarget?.defName ?? "none"}")),
                        C("preference-actually-equipped", () =>
                        {
                            ThingDef primary = scopey.equipment?.Primary?.def;
                            return (primary == sniper, $"primary={primary?.defName ?? "none"} job={scopey.CurJobDef?.defName ?? "none"}");
                        }),
                    }
                },
                new Phase
                {
                    // The reload-guard refinement (from the Loadouts module's reviews): the
                    // explicit-switch prefix used to end the reload for EVERY call, refused
                    // ones included. A no-op switch (the weapon already in hand) must not
                    // cost the reload.
                    label = "a-no-op-switch-keeps-the-reload",
                    deadlineTicks = 15000,
                    expectedDiagnostics = new[] { "Attepmpted to equip already-equipped weapon" },
                    arrange = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        ThingWithComps primary = scopey.equipment.Primary;
                        CompAmmoUser user = primary.TryGetComp<CompAmmoUser>();
                        user.CurMagCount = 0;
                        Verse.AI.Job reload = user.TryMakeReloadJob();
                        if (reload != null)
                        {
                            scopey.jobs.StartJob(reload, JobCondition.InterruptForced);
                        }
                    },
                    checks =
                    {
                        P("a-reload-is-running", () =>
                            (scopey.CurJobDef == CE_JobDefOf.ReloadWeapon, $"job={scopey.CurJobDef?.defName ?? "none"}")),
                        C("reload-survives-the-no-op-switch", () =>
                            (reloadAfterNoopSwitch == CE_JobDefOf.ReloadWeapon,
                             $"jobAfterCall={reloadAfterNoopSwitch?.defName ?? "none"}")),
                        C("magazine-refills", () =>
                        {
                            CompAmmoUser user = scopey.equipment?.Primary?.TryGetComp<CompAmmoUser>();
                            bool full = user != null && user.CurMagCount == user.MagSize;
                            return (full, $"mag={user?.CurMagCount}/{user?.MagSize} job={scopey.CurJobDef?.defName}");
                        }),
                    },
                    mutate = () =>
                    {
                        // SS refuses this with its "already-equipped" warning (allowlisted —
                        // deliberately provoked); the guard must not have ended the reload.
                        WeaponAssingment.equipSpecificWeapon(scopey, scopey.equipment.Primary,
                            dropCurrent: false, intentionalDrop: false);
                        reloadAfterNoopSwitch = scopey.CurJobDef;
                    }
                },
                new Phase
                {
                    // The deeper half: a switch SS refuses AT EQUIP TIME (blocked weapon)
                    // still loses the prefix's EndCurrentJob — the postfix must restart the
                    // reload instead of leaving the magazine empty until something notices.
                    label = "a-blocked-switch-restarts-the-reload",
                    deadlineTicks = 15000,
                    expectedDiagnostics = new[] { "SS: Blocked equip of" },
                    arrange = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // A third gun straight into the pack, BIOCODED TO SOMEONE ELSE:
                        // SS's equip-time gate (canUseSidearmInstance) checks biocode and
                        // role locks, NOT carry limits — a mere over-limit weapon sails
                        // through it, which left this phase VOID on the first design.
                        if (!scopey.inventory.innerContainer.Any(t => t.def == D("Gun_Autopistol")))
                        {
                            var extra = (ThingWithComps)ThingMaker.MakeThing(D("Gun_Autopistol"));
                            extra.TryGetComp<CompBiocodable>()?.CodeFor(Colonist("Fency"));
                            scopey.inventory.innerContainer.TryAdd(extra, canMergeWithExistingStacks: false);
                        }
                        ThingWithComps primary = scopey.equipment.Primary;
                        CompAmmoUser user = primary.TryGetComp<CompAmmoUser>();
                        user.CurMagCount = 0;
                        Verse.AI.Job reload = user.TryMakeReloadJob();
                        if (reload != null)
                        {
                            scopey.jobs.StartJob(reload, JobCondition.InterruptForced);
                        }
                    },
                    checks =
                    {
                        P("reload-running-and-third-gun-blocked", () =>
                        {
                            bool reloading = scopey.CurJobDef == CE_JobDefOf.ReloadWeapon;
                            ThingWithComps third = scopey.inventory.innerContainer
                                .OfType<ThingWithComps>().FirstOrDefault(t => t.def == D("Gun_Autopistol"));
                            bool blocked = third != null
                                && !StatCalculator.canUseSidearmInstance(third, scopey, out string _)
                                && !SSCore.Settings.AllowBlockedWeaponUse;
                            return (reloading && blocked, $"job={scopey.CurJobDef?.defName ?? "none"} blocked={blocked}");
                        }),
                        C("switch-was-refused", () =>
                            (!blockedSwitchResult, $"equipSpecificWeapon returned {blockedSwitchResult}")),
                        C("reload-restarted-after-refusal", () =>
                            (reloadAfterBlockedSwitch == CE_JobDefOf.ReloadWeapon,
                             $"jobAfterCall={reloadAfterBlockedSwitch?.defName ?? "none"}")),
                    },
                    mutate = () =>
                    {
                        ThingWithComps third = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().First(t => t.def == D("Gun_Autopistol"));
                        blockedSwitchResult = WeaponAssingment.equipSpecificWeapon(scopey, third,
                            dropCurrent: false, intentionalDrop: false);
                        reloadAfterBlockedSwitch = scopey.CurJobDef;
                    }
                },
                new Phase
                {
                    // The reload guard keys on the job's own TargetB now: CE issues
                    // ReloadWeapon jobs for INVENTORY guns too (gear-tab reload, top-offs),
                    // and a backpack top-off does not conflict with equipping — the old
                    // guard killed it and "repaired" by reloading the equipped gun instead.
                    label = "an-inventory-top-off-survives-a-refused-switch",
                    deadlineTicks = 15000,
                    expectedDiagnostics = new[] { "SS: Blocked equip of" },
                    checks =
                    {
                        P("topping-off-the-backpack-gun", () =>
                        {
                            bool reloading = scopey.CurJobDef == CE_JobDefOf.ReloadWeapon;
                            bool inventoryGun = scopey.CurJob?.targetB.Thing is ThingWithComps g
                                && g != scopey.equipment?.Primary;
                            return (reloading && inventoryGun,
                                $"job={scopey.CurJobDef?.defName ?? "none"} targetB={(scopey.CurJob?.targetB.Thing)?.def?.defName ?? "-"}");
                        }),
                        C("the-top-off-is-untouched", () =>
                        {
                            bool stillIt = topOffJobAfter == CE_JobDefOf.ReloadWeapon
                                && topOffTargetAfter != null && topOffTargetAfter != scopey.equipment?.Primary;
                            return (stillIt, $"jobAfter={topOffJobAfter?.defName ?? "none"} "
                                + $"targetAfter={topOffTargetAfter?.def?.defName ?? "-"} (must be the backpack gun)");
                        }),
                    },
                    arrange = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // A drained backpack gun with spare ammo, and a blocked (biocoded)
                        // pistol to provoke the refusal.
                        ThingWithComps backpackGun = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().FirstOrDefault(t => t.def == shotgun && t != scopey.equipment?.Primary);
                        if (backpackGun == null)
                        {
                            backpackGun = (ThingWithComps)ThingMaker.MakeThing(shotgun);
                            scopey.inventory.innerContainer.TryAdd(backpackGun, canMergeWithExistingStacks: false);
                        }
                        CompAmmoUser user = backpackGun.TryGetComp<CompAmmoUser>();
                        user.ResetAmmoCount();
                        user.CurMagCount = 0;
                        var spare = ThingMaker.MakeThing((ThingDef)user.selectedAmmo);
                        spare.stackCount = user.MagSize * 2;
                        scopey.inventory.innerContainer.TryAdd(spare, canMergeWithExistingStacks: false);
                        scopey.TryGetComp<CompInventory>().UpdateInventory();
                        Verse.AI.Job reload = user.TryMakeReloadJob();
                        if (reload != null)
                        {
                            scopey.jobs.StartJob(reload, JobCondition.InterruptForced);
                        }
                        if (!scopey.inventory.innerContainer.Any(t => t.def == D("Gun_Autopistol")))
                        {
                            var blocked = (ThingWithComps)ThingMaker.MakeThing(D("Gun_Autopistol"));
                            blocked.TryGetComp<CompBiocodable>()?.CodeFor(Colonist("Fency"));
                            scopey.inventory.innerContainer.TryAdd(blocked, canMergeWithExistingStacks: false);
                        }
                    },
                    mutate = () =>
                    {
                        ThingWithComps blocked = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().First(t => t.def == D("Gun_Autopistol"));
                        WeaponAssingment.equipSpecificWeapon(scopey, blocked,
                            dropCurrent: false, intentionalDrop: false);
                        topOffJobAfter = scopey.CurJobDef;
                        topOffTargetAfter = scopey.CurJob?.targetB.Thing as ThingWithComps;
                    }
                },
                new Phase
                {
                    // Axis 9, rebuilt (adversarial round 3): CE's main dry-gun path
                    // (DoOutOfAmmoAction) searches for a replacement DIRECTLY — the old
                    // SwitchToNextViableWeapon-only seam never saw it, so CE equipped its
                    // first cached gun and SS was never asked. The pick must be SS's.
                    label = "a-dry-fire-arbitrates-to-ss-pick",
                    deadlineTicks = 8000,
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("dry-fire-forensics", () => (true, dryFireDebug ?? "-"), informational: true),
                        C("replacement-job-targets-the-ss-pick", () =>
                            (dryFireJob == CE_JobDefOf.EquipFromInventory && dryFireTarget == sniper,
                             $"job={dryFireJob?.defName ?? "none"} target={dryFireTarget?.defName ?? "-"} (want EquipFromInventory:{sniper.defName})")),
                    },
                    mutate = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // A loaded decoy FIRST in CE's cached list order, the SS-preferred
                        // sniper after it: CE's own search takes list order, so the old
                        // code deterministically picks the decoy.
                        foreach (ThingWithComps gun in scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().Where(t => t.def.IsRangedWeapon).ToList())
                        {
                            gun.Destroy(DestroyMode.Vanish);
                        }
                        var decoy = (ThingWithComps)ThingMaker.MakeThing(D("Gun_Autopistol"));
                        decoy.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                        scopey.inventory.innerContainer.TryAdd(decoy, canMergeWithExistingStacks: false);
                        var sniperThing = (ThingWithComps)ThingMaker.MakeThing(sniper);
                        sniperThing.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                        scopey.inventory.innerContainer.TryAdd(sniperThing, canMergeWithExistingStacks: false);
                        scopey.TryGetComp<CompInventory>().UpdateInventory();
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(scopey);
                        memory.InformOfAddedSidearm(decoy);
                        memory.InformOfAddedSidearm(sniperThing);
                        memory.DefaultRangedWeapon = new ThingDefStuffDefPair(sniper, null);

                        // The gun that runs dry must NOT share a pair with the SS
                        // preference: SS's DefaultRanged branch returns without equipping
                        // when the pawn already wields the preferred pair (pair-level
                        // identity cannot see that the wielded copy is the dry one), which
                        // reads as no-opinion — that wielded-dry-copy gap is the parked
                        // forced-dry family, not this phase's subject. Put a shotgun in
                        // hand and dry that.
                        ThingWithComps shotgunThing = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().FirstOrDefault(t => t.def == shotgun);
                        if (shotgunThing == null)
                        {
                            shotgunThing = (ThingWithComps)ThingMaker.MakeThing(shotgun);
                            scopey.inventory.innerContainer.TryAdd(shotgunThing, canMergeWithExistingStacks: false);
                            scopey.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        shotgunThing.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                        if (scopey.equipment.Primary != shotgunThing)
                        {
                            scopey.TryGetComp<CompInventory>().TrySwitchToWeapon(shotgunThing);
                        }
                        // Dry the equipped gun completely and let CE's own out-of-ammo
                        // action run — the real path a dry-firing gun takes.
                        ThingWithComps primary = scopey.equipment.Primary;
                        CompAmmoUser user = primary.TryGetComp<CompAmmoUser>();
                        user.CurMagCount = 0;
                        foreach (Thing stack in scopey.inventory.innerContainer
                            .Where(t => user.Props.ammoSet.ammoTypes.Any(l => (ThingDef)l.ammo == t.def)).ToList())
                        {
                            stack.Destroy(DestroyMode.Vanish);
                        }
                        user.DoOutOfAmmoAction();
                        dryFireJob = scopey.CurJobDef;
                        dryFireTarget = (scopey.CurJob?.targetA.Thing)?.def;
                        // Forensics: exercise the arbitration point directly and dump the
                        // state the preference tree keys on.
                        scopey.TryGetComp<CompInventory>().TryFindViableWeapon(out ThingWithComps directPick);
                        dryFireDebug = $"directPick={directPick?.def?.defName ?? "null"} "
                            + $"mode={memory.primaryWeaponMode} default={memory.DefaultRangedWeapon?.thing?.defName ?? "-"} "
                            + $"forced={memory.ForcedWeapon?.thing?.defName ?? "-"} primary={scopey.equipment?.Primary?.def?.defName ?? "-"} "
                            + $"sniperCarried={scopey.inventory.innerContainer.Any(t => t.def == sniper)} "
                            + $"valid={scopey.IsValidSidearmsCarrierRightNow()}";
                    }
                },
                new Phase
                {
                    // The disarm trap: the old re-entry read CE's fists branch (strip the
                    // primary, equip nothing, return true) as success whenever SS's pick
                    // was unusable. An unusable forced pick must hand the search back to
                    // CE unrestricted — pawn keeps a weapon.
                    label = "an-unusable-forced-pick-does-not-disarm",
                    deadlineTicks = 8000,
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("primary-survives-the-switch-call", () =>
                            (forcedDisarmPrimaryPresent,
                             $"primaryAtCall={forcedDisarmPrimaryPresent} job={forcedDisarmJob?.defName ?? "none"}")),
                        C("a-loaded-gun-gets-queued-instead", () =>
                            (forcedDisarmJob == CE_JobDefOf.EquipFromInventory,
                             $"job={forcedDisarmJob?.defName ?? "none"}")),
                    },
                    mutate = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // SS's forced pick: a completely dry sniper in the pack.
                        ThingWithComps sniperThing = scopey.inventory.innerContainer
                            .OfType<ThingWithComps>().FirstOrDefault(t => t.def == sniper);
                        if (sniperThing == null)
                        {
                            sniperThing = (ThingWithComps)ThingMaker.MakeThing(sniper);
                            scopey.inventory.innerContainer.TryAdd(sniperThing, canMergeWithExistingStacks: false);
                            scopey.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        CompAmmoUser sniperUser = sniperThing.TryGetComp<CompAmmoUser>();
                        sniperUser.CurMagCount = 0;
                        foreach (Thing stack in scopey.inventory.innerContainer
                            .Where(t => sniperUser.Props.ammoSet.ammoTypes.Any(l => (ThingDef)l.ammo == t.def)).ToList())
                        {
                            stack.Destroy(DestroyMode.Vanish);
                        }
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(scopey);
                        memory.InformOfAddedSidearm(sniperThing);
                        memory.ForcedWeapon = new ThingDefStuffDefPair(sniper, null);

                        scopey.TryGetComp<CompInventory>()
                            .SwitchToNextViableWeapon(useFists: true, useAOE: false, stopJob: false);
                        forcedDisarmPrimaryPresent = scopey.equipment.Primary != null;
                        forcedDisarmJob = scopey.CurJobDef;
                        memory.ForcedWeapon = null;
                    }
                },
                new Phase
                {
                    // SS's silence is two-shaped: an already-unarmed pawn whose memory says
                    // "stay unarmed" never reaches an equip in the preference tree, and the
                    // old job path read that as "no opinion" and let CE re-arm them.
                    label = "stay-unarmed-is-respected-on-the-job-path",
                    deadlineTicks = 8000,
                    minTicks = 300, // the negative holds across a window
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("no-equip-job-was-queued", () =>
                            (unarmedPathJob != CE_JobDefOf.EquipFromInventory,
                             $"jobAtCall={unarmedPathJob?.defName ?? "none"}")),
                        N("the-pawn-stays-unarmed", () =>
                            (scopey.equipment?.Primary == null,
                             $"primary={scopey.equipment?.Primary?.def?.defName ?? "none"}")),
                    },
                    mutate = () =>
                    {
                        scopey.drafter.Drafted = false;
                        scopey.jobs.StopAll();
                        // Unarm by hand and set the player's fists preference; a loaded gun
                        // stays in the pack as bait for the old re-arm path.
                        ThingWithComps primary = scopey.equipment.Primary;
                        if (primary != null)
                        {
                            scopey.equipment.TryTransferEquipmentToContainer(primary, scopey.inventory.innerContainer);
                        }
                        if (!scopey.inventory.innerContainer.OfType<ThingWithComps>()
                                .Any(t => t.def.IsRangedWeapon && (t.TryGetComp<CompAmmoUser>()?.HasAndUsesAmmoOrMagazine ?? true)))
                        {
                            var bait = (ThingWithComps)ThingMaker.MakeThing(D("Gun_Autopistol"));
                            bait.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                            scopey.inventory.innerContainer.TryAdd(bait, canMergeWithExistingStacks: false);
                        }
                        scopey.TryGetComp<CompInventory>().UpdateInventory();
                        CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(scopey);
                        memory.ForcedUnarmed = true;

                        scopey.TryGetComp<CompInventory>()
                            .SwitchToNextViableWeapon(useFists: true, useAOE: false, stopJob: false);
                        unarmedPathJob = scopey.CurJobDef;
                    }
                },
            };
        }

        private JobDef topOffJobAfter;
        private ThingWithComps topOffTargetAfter;

        // Captured synchronously inside the arbitration phases.
        private JobDef dryFireJob;
        private ThingDef dryFireTarget;
        private string dryFireDebug;
        private bool forcedDisarmPrimaryPresent;
        private JobDef forcedDisarmJob;
        private JobDef unarmedPathJob;

        // Captured synchronously inside the two reload-guard phases.
        private JobDef reloadAfterNoopSwitch;
        private JobDef reloadAfterBlockedSwitch;
        private bool blockedSwitchResult;

        // -- CETEST-4: axes 4 (NPC sidearm ammo) + 8 (one-use fallback) -----

        // Where Boomy stood before being parked out of the raiders' reach; default when
        // the parking phase never ran (isolated run of the one-use phase).
        private IntVec3 boomyHome = IntVec3.Invalid;

        private List<Phase> BuildCetest4()
        {
            Pawn boomy = Colonist("Boomy");
            ThingDef pistol = D("Gun_Autopistol");

            // The raider phases and the one-use phase share a map but must not share
            // actors: P04 loads the raiders' guns, and a loaded raider volley killed
            // Boomy twice while the earlier phases ran (dead=True forensics; isolated
            // runs — where the raiders die within a tick — never reproduced it).
            void ParkBoomy()
            {
                boomyHome = boomy.Position;
                IntVec3 corner = new IntVec3(5, 0, boomy.Map.Size.z - 6);
                if (!corner.Standable(boomy.Map))
                {
                    CellFinder.TryFindRandomCellNear(corner, boomy.Map, 8, c => c.Standable(boomy.Map), out corner);
                }
                boomy.Position = corner;
                boomy.Notify_Teleported();
                boomy.drafter.Drafted = true; // stand still; do not wander back into the fight
            }
            void UnparkBoomy()
            {
                foreach (Pawn hostile in Hostiles())
                {
                    hostile.Destroy(DestroyMode.Vanish);
                }
                if (boomyHome.IsValid)
                {
                    IntVec3 back = boomyHome;
                    if (!back.Standable(boomy.Map))
                    {
                        CellFinder.TryFindRandomCellNear(back, boomy.Map, 8, c => c.Standable(boomy.Map), out back);
                    }
                    boomy.Position = back;
                    boomy.Notify_Teleported();
                }
                boomy.drafter.Drafted = false;
                boomy.jobs.StopAll();
            }

            (bool ok, string detail) RaiderProvisioning()
            {
                var problems = new List<string>();
                int checkedRaiders = 0;
                foreach (Pawn raider in Hostiles().Where(h => !(h.RaceProps?.IsMechanoid ?? false)))
                {
                    checkedRaiders++;
                    var carriedWeapons = raider.inventory.innerContainer.OfType<ThingWithComps>()
                        .Where(t => t.def.IsWeapon).ToList();
                    if (raider.equipment?.Primary != null)
                    {
                        carriedWeapons.Add(raider.equipment.Primary);
                    }
                    var validAmmoDefs = new HashSet<ThingDef>();
                    foreach (ThingWithComps weapon in carriedWeapons)
                    {
                        CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
                        if (user == null || !user.UseAmmo)
                        {
                            continue;
                        }
                        foreach (var link in user.Props.ammoSet.ammoTypes)
                        {
                            validAmmoDefs.Add(link.ammo);
                        }
                        bool isSidearm = weapon != raider.equipment?.Primary;
                        if (isSidearm && user.CurMagCount <= 0)
                        {
                            problems.Add($"{raider.LabelShort}: sidearm {weapon.def.defName} mag {user.CurMagCount}/{user.MagSize}");
                        }
                        bool hasSpare = raider.inventory.innerContainer.Any(t =>
                            user.Props.ammoSet.ammoTypes.Any(l => (ThingDef)l.ammo == t.def));
                        if (isSidearm && !hasSpare)
                        {
                            problems.Add($"{raider.LabelShort}: no spare ammo for sidearm {weapon.def.defName}");
                        }
                    }
                    foreach (Thing stack in raider.inventory.innerContainer.Where(t => t.def is AmmoDef))
                    {
                        // CE injects loose thrown grenades (AmmoDefs that are themselves
                        // weapons) into raid inventories with no launcher — legitimate.
                        if (stack.def.IsWeapon)
                        {
                            continue;
                        }
                        if (!validAmmoDefs.Contains(stack.def))
                        {
                            problems.Add($"{raider.LabelShort}: ORPHAN ammo {stack.def.defName} x{stack.stackCount}");
                        }
                    }
                    CompInventory inv = raider.TryGetComp<CompInventory>();
                    if (inv != null && inv.currentBulk > inv.capacityBulk + 0.01f)
                    {
                        problems.Add($"{raider.LabelShort}: OVER BULK {inv.currentBulk:F1}/{inv.capacityBulk:F1}");
                    }
                    if (inv != null && inv.currentWeight > inv.capacityWeight + 0.01f)
                    {
                        problems.Add($"{raider.LabelShort}: OVER WEIGHT {inv.currentWeight:F1}/{inv.capacityWeight:F1}");
                    }
                }
                return (problems.Count == 0 && checkedRaiders > 0,
                    problems.Count == 0 ? $"{checkedRaiders} raiders clean" : string.Join(" | ", problems.Take(6)));
            }

            return new List<Phase>
            {
                PatchInventoryPhase(),
                new Phase
                {
                    label = "raider-ammo-provisioning",
                    deadlineTicks = 4000,
                    arrange = ParkBoomy,
                    checks =
                    {
                        P("raiders-present", () =>
                        {
                            int n = Hostiles().Count(h => !(h.RaceProps?.IsMechanoid ?? false));
                            return (n > 0, $"human raiders={n}");
                        }),
                        C("raiders-provisioned-no-orphans-no-overcap", () => RaiderProvisioning()),
                    }
                },
                new Phase
                {
                    label = "fresh-generation-provisions-ammo",
                    deadlineTicks = 4000,
                    mutate = () =>
                    {
                        // Strip FIRST: the staged raiders were provisioned when the save
                        // was created, and ForceRangedSidearm no-ops while a ranged
                        // ammo-using sidearm exists — the old phase regenerated nothing and
                        // passed with P04 disabled (caught by a scratch A/B). Destroying
                        // the sidearms and their ammo forces a live generator pass through
                        // the patch.
                        Pawn raider = Hostiles().FirstOrDefault(h => !(h.RaceProps?.IsMechanoid ?? false));
                        if (raider != null)
                        {
                            foreach (Thing t in raider.inventory.innerContainer
                                .Where(t => t.def.IsWeapon || t.def is CombatExtended.AmmoDef).ToList())
                            {
                                t.Destroy(DestroyMode.Vanish);
                            }
                            raider.TryGetComp<CompInventory>()?.UpdateInventory();
                            TestStagingComponent.ForceRangedSidearm(raider);
                        }
                    },
                    checks =
                    {
                        P("raider-present", () =>
                        {
                            int n = Hostiles().Count(h => !(h.RaceProps?.IsMechanoid ?? false));
                            return (n > 0, $"human raiders={n}");
                        }),
                        C("still-clean-after-regeneration", () => RaiderProvisioning()),
                    }
                },
                new Phase
                {
                    label = "one-use-fallback",
                    deadlineTicks = 25000,
                    // No live hostiles at all — a raider (even disarmed) charges into melee
                    // and kills the attack job; a still-armed one kills Boomy. Done in
                    // arrange so the world is clean before the preconditions gate the act.
                    arrange = UnparkBoomy,
                    mutate = () =>
                    {
                        boomy.drafter.Drafted = true;
                        SSCore.Settings.RangedCombatAutoSwitch = false;
                        // The staged TripleRocket exercised the same Verb_ShootCEOneUse
                        // consumption path but its FRAGMENTS reach far beyond the blast
                        // radius — it downed or killed the shooter at any in-range firing
                        // distance often enough to make the phase a coin flip. A smoke
                        // grenade is the same one-use verb with a harmless payload, so the
                        // phase tests the re-equip fallback instead of Boomy's luck. The
                        // rocket is removed so SS cannot pick it as the re-equip instead
                        // of the pistol.
                        foreach (ThingWithComps rocket in boomy.inventory.innerContainer
                            .OfType<ThingWithComps>().Where(t => t.def.defName.Contains("Rocket")).ToList())
                        {
                            rocket.Destroy(DestroyMode.Vanish);
                        }
                        if (boomy.equipment.Primary?.def.defName.Contains("Rocket") ?? false)
                        {
                            boomy.equipment.Primary.Destroy(DestroyMode.Vanish);
                        }
                        // Destroying the primary makes CE re-arm Boomy synchronously
                        // (SwitchToNextViableWeapon on destroy — through P09 and SS), so by
                        // here the pistol is usually already in hand. Route the grenade
                        // through CE's own switch API instead of AddEquipment, which errors
                        // on an occupied primary slot.
                        var launcher = (ThingWithComps)ThingMaker.MakeThing(D("CE_Weapon_GrenadeSmoke"));
                        boomy.inventory.innerContainer.TryAdd(launcher, canMergeWithExistingStacks: false);
                        boomy.TryGetComp<CompInventory>().TrySwitchToWeapon(launcher);
                        // As far as this launcher can actually shoot, so the blast cannot reach
                        // the shooter, but inside its range so a drafted AttackStatic fires
                        // instead of standing there unable to reach the cell. At a fixed 10
                        // cells Boomy was downed or killed by his own rocket; at a fixed 24 he
                        // was out of range and never fired. Both decided the phase on something
                        // other than the one-use fallback.
                        float launcherRange = launcher.GetComp<CompEquippable>()?.PrimaryVerb?.verbProps?.range ?? 12f;
                        int shotDistance = Math.Max(6, Math.Min(18, (int)launcherRange - 2));
                        // Clear line of sight is part of the distance contract: a rocket
                        // intercepted by a tree two cells out detonates next to the shooter,
                        // and a dead Boomy fails the phase on something other than the
                        // one-use fallback (observed once — dead=True, launcher consumed,
                        // nobody left to re-equip).
                        bool ShotCellOk(IntVec3 c) =>
                            c.Standable(boomy.Map)
                            && c.DistanceTo(boomy.Position) > shotDistance - 2f
                            && GenSight.LineOfSight(boomy.Position, c, boomy.Map, skipFirstCell: true);
                        IntVec3 targetCell = boomy.Position + new IntVec3(shotDistance, 0, 0);
                        targetCell = targetCell.ClampInsideMap(boomy.Map);
                        if (!ShotCellOk(targetCell))
                        {
                            CellFinder.TryFindRandomCellNear(boomy.Position, boomy.Map, shotDistance + 4,
                                ShotCellOk, out targetCell);
                        }
                        Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, new LocalTargetInfo(targetCell));
                        boomy.jobs.TryTakeOrderedJob(job);
                    },
                    checks =
                    {
                        P("boomy-carries-pistol", () =>
                        {
                            bool pistolCarried = Carried(boomy, pistol) != null;
                            return (pistolCarried, $"pistol={pistolCarried}");
                        }),
                        C("one-use-consumed-pistol-equipped", () =>
                        {
                            bool grenadeAnywhere =
                                boomy.equipment?.Primary?.def == D("CE_Weapon_GrenadeSmoke")
                                || boomy.inventory.innerContainer.Any(t => t.def == D("CE_Weapon_GrenadeSmoke"));
                            ThingDef primary = boomy.equipment?.Primary?.def;
                            return (!grenadeAnywhere && primary == pistol,
                                $"grenadePresent={grenadeAnywhere} primary={primary?.defName ?? "FISTS"}");
                        }),
                        C("boomy-health-forensics", () =>
                        {
                            bool pistolCarried = boomy.inventory.innerContainer.Any(t => t.def == pistol);
                            return (true, $"downed={boomy.Downed} dead={boomy.Dead} drafted={boomy.Drafted} pistolInInventory={pistolCarried} job={boomy.CurJobDef?.defName}");
                        }, informational: true),
                        C("one-use-not-in-inventory", () =>
                        {
                            bool present = boomy.inventory.innerContainer.Any(t => t.def == D("CE_Weapon_GrenadeSmoke"));
                            return (!present, $"grenade in inventory={present}");
                        }, informational: true),
                    }
                },
            };

        }
    }
}
