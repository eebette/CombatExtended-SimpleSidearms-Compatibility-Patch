using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using SSCore = PeteTimesSix.SimpleSidearms.SimpleSidearms;

namespace CESSCompatTestStaging
{
    /// <summary>
    /// In-game benchmark for the weapon-scoring path, run with
    ///   -celoadsave=CETEST-2-selection -ceassert=cebench
    ///
    /// Combat Extended's convention is to benchmark inside RimWorld rather than in a
    /// desktop harness, and to express the result against frame time, so this measures the
    /// calls SS actually makes during a warmup tick, on a real loaded save, with real pawns
    /// and real carried weapons — then projects the per-tick cost at colony scale.
    ///
    /// Two arms in one process, so JIT state, the loaded save and the map are identical:
    ///   patched   — the compat patch's scoring active
    ///   unpatched — Harmony patches removed, i.e. stock Simple Sidearms scoring
    /// Comparing across two runs with different builds of the patch gives the third arm.
    /// </summary>
    public class CEBenchRunnerComponent : GameComponent
    {
        private const int WarmupIterations = 2000;
        private const int TimedIterations = 20000;
        private const int Rounds = 5;
        private const float FrameBudgetMs = 1000f / 60f;
        private const int ProjectedPawns = 20;

        /// <summary>
        /// The patch memoises derived stats for the tick that produced them. A tight loop
        /// inside one tick would therefore measure 19,999 warm-cache calls and one cold one,
        /// which is not how the game calls it — once per pawn per tick, always cold. Reset the
        /// memo before every timed iteration so both arms are measured on equal terms (it is
        /// a no-op for the stock arm, which has no cache).
        /// </summary>
        private static FieldInfo cacheTickField;

        private static void InvalidateScoreCache()
        {
            if (cacheTickField == null)
            {
                Type cache = GenTypes.GetTypeInAnyAssembly("CESimpleSidearmsCompat.Patches.ScoreCache");
                cacheTickField = cache?.GetField("tick", BindingFlags.NonPublic | BindingFlags.Static);
            }
            cacheTickField?.SetValue(null, -1);
        }

        private string scenario;
        private bool active;
        private bool done;
        private int startTick;

        private readonly List<string> results = new List<string>();

        public CEBenchRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("cebench"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                active = true;
                startTick = Find.TickManager.TicksGame;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                Log.Message("[CEBench] Loaded; settling before measuring.");
            });
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            // Let the map finish spawning and CE finish its first inventory passes before
            // timing anything.
            if (Find.TickManager.TicksGame - startTick < 120)
            {
                return;
            }
            done = true;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Log.Error("[CEBench] Failed: " + e);
                results.Add($"  \"crashed\": \"{Escape(e.ToString())}\"");
            }
            Write();
            Root.Shutdown();
        }

        private void Run()
        {
            // The staged pawn, not whichever colonist the quicktest map happened to spawn
            // first: the cost being measured scales with how many weapons are carried.
            Pawn pawn = Find.CurrentMap?.mapPawns?.FreeColonists?
                .Where(p => p.inventory != null)
                .OrderByDescending(p => p.GetCarriedWeapons(includeEquipped: true).Count(w => w.def.IsRangedWeapon))
                .FirstOrDefault();
            if (pawn == null)
            {
                throw new InvalidOperationException("No colonist on the map");
            }
            List<ThingWithComps> carried = pawn.GetCarriedWeapons(includeEquipped: true);
            List<ThingWithComps> ranged = carried.Where(w => w.def.IsRangedWeapon).ToList();
            if (ranged.Count < 2)
            {
                throw new InvalidOperationException(
                    $"{pawn.LabelShort} carries {ranged.Count} ranged weapons; the scoring path needs a choice to make");
            }
            // Nearest hostile: a target 60 cells out is beyond every weapon's range, so the
            // selection would short-circuit instead of scoring anything.
            Pawn hostile = Find.CurrentMap.mapPawns.AllPawnsSpawned
                .Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead)
                .OrderBy(p => p.Position.DistanceTo(pawn.Position))
                .FirstOrDefault();
            LocalTargetInfo target = hostile != null ? new LocalTargetInfo(hostile) : LocalTargetInfo.Invalid;
            float distance = hostile != null ? hostile.Position.DistanceTo(pawn.Position) : 20f;

            results.Add($"  \"pawn\": \"{Escape(pawn.LabelShort)}\"");
            results.Add($"  \"carriedWeapons\": {carried.Count}");
            results.Add($"  \"rangedWeapons\": {ranged.Count}");
            results.Add($"  \"targetDistance\": {distance:F1}");
            results.Add($"  \"timedIterations\": {TimedIterations}");
            results.Add($"  \"rounds\": {Rounds}");

            // The call SS makes once per tick per warming-up pawn (warmup auto-switch -> SS's
            // trySwapToMoreAccurateRangedWeapon -> findBestRangedWeapon).
            Func<int> selection = () =>
            {
                var pick = GettersFilters.findBestRangedWeapon(pawn, target, true, true, true);
                return pick.Item1 != null ? 1 : 0;
            };
            // The per-candidate scoring inside it.
            Func<int> scoring = () =>
            {
                int n = 0;
                foreach (ThingWithComps weapon in ranged)
                {
                    n += StatCalculator.RangedDPS(weapon, SSCore.Settings.SpeedSelectionBiasRanged, 1f, distance) > 0f ? 1 : 0;
                }
                return n;
            };

            // Decompose the overhead: which patched member costs what, measured the same way
            // in both arms.
            Func<int> speed = () =>
            {
                int n = 0;
                foreach (ThingWithComps weapon in ranged)
                {
                    n += StatCalculator.RangedSpeed(weapon) > 0f ? 1 : 0;
                }
                return n;
            };
            Func<int> classify = () =>
            {
                int n = 0;
                foreach (ThingWithComps weapon in ranged)
                {
                    n += GettersFilters.isEMPWeapon(weapon) ? 1 : 0;
                    n += GettersFilters.isDangerousWeapon(weapon) ? 1 : 0;
                }
                return n;
            };
            Func<int> canUse = () =>
            {
                int n = 0;
                foreach (ThingWithComps weapon in ranged)
                {
                    n += StatCalculator.canUseSidearmInstance(weapon, pawn, out string _) ? 1 : 0;
                }
                return n;
            };

            double patchedSelection = Measure(selection);
            double patchedScoring = Measure(scoring);
            Report("patched", patchedSelection, patchedScoring);
            results.Add($"  \"patchedSpeedUsPerWeaponSet\": {Measure(speed):F3}");
            results.Add($"  \"patchedClassifyUsPerWeaponSet\": {Measure(classify):F3}");
            results.Add($"  \"patchedCanUseUsPerWeaponSet\": {Measure(canUse):F3}");

            // Same process, same save: drop the patch's Harmony patches and measure stock SS.
            new Harmony("eebette.CESSCompatBench").UnpatchAll("eebette.CESimpleSidearmsCompat");
            Log.Message("[CEBench] Compat patches removed; measuring stock Simple Sidearms.");

            double stockSelection = Measure(selection);
            double stockScoring = Measure(scoring);
            Report("stock", stockSelection, stockScoring);
            results.Add($"  \"stockSpeedUsPerWeaponSet\": {Measure(speed):F3}");
            results.Add($"  \"stockClassifyUsPerWeaponSet\": {Measure(classify):F3}");
            results.Add($"  \"stockCanUseUsPerWeaponSet\": {Measure(canUse):F3}");

            double overhead = patchedSelection - stockSelection;
            results.Add($"  \"selectionOverheadUsPerCall\": {overhead:F3}");
            results.Add($"  \"selectionOverheadPctOfFrameAt{ProjectedPawns}Pawns\": "
                        + $"{(overhead * ProjectedPawns / 1000.0) / FrameBudgetMs * 100.0:F3}");
        }

        /// <summary>
        /// Best-of-N: the minimum is the least noisy estimate of the real cost — GC and
        /// background work can only add time.
        /// </summary>
        private static double Measure(Func<int> body)
        {
            int sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += body();
            }
            double best = double.MaxValue;
            for (int round = 0; round < Rounds; round++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < TimedIterations; i++)
                {
                    InvalidateScoreCache();
                    sink += body();
                }
                watch.Stop();
                double usPerCall = watch.Elapsed.TotalMilliseconds * 1000.0 / TimedIterations;
                if (usPerCall < best)
                {
                    best = usPerCall;
                }
            }
            if (sink < 0)
            {
                Log.Message("[CEBench] sink " + sink); // keep the loop from being optimised away
            }
            return best;
        }

        private void Report(string arm, double selectionUs, double scoringUs)
        {
            double perTickMsAtScale = selectionUs * ProjectedPawns / 1000.0;
            results.Add($"  \"{arm}SelectionUsPerCall\": {selectionUs:F3}");
            results.Add($"  \"{arm}ScoringUsPerWeaponSet\": {scoringUs:F3}");
            results.Add($"  \"{arm}MsPerTickAt{ProjectedPawns}Pawns\": {perTickMsAtScale:F3}");
            results.Add($"  \"{arm}PctOfFrameAt{ProjectedPawns}Pawns\": {perTickMsAtScale / FrameBudgetMs * 100.0:F3}");
            Log.Message($"[CEBench] {arm}: selection {selectionUs:F3} us/call, "
                        + $"{perTickMsAtScale:F3} ms/tick at {ProjectedPawns} pawns "
                        + $"({perTickMsAtScale / FrameBudgetMs * 100.0:F2}% of a 60fps frame)");
        }

        private void Write()
        {
            string folder = GenFilePaths.SaveDataFolderPath;
            string path = Path.Combine(folder, $"bench-results-{scenario}.json");
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scenario\": \"{Escape(scenario)}\",\n");
            sb.Append(string.Join(",\n", results));
            sb.Append("\n}\n");
            File.WriteAllText(path, sb.ToString());
            Log.Message("[CEBench] Wrote " + path);
        }

        private static string Escape(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") ?? "";
        }
    }

    [StaticConstructorOnStartup]
    public static class CEBenchBoot
    {
        static CEBenchBoot()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("cebench"))
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message($"[CEBench] Auto-loading save '{save}'.");
                    GameDataSaveLoader.LoadGame(save);
                });
            }
        }
    }
}
