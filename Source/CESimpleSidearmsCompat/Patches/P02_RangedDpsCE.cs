using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Patches
{
    /// <summary>
    /// Axis 2: SS scores ranged weapons with vanilla verb stats, which are meaningless on CE
    /// weapons (zeroed accuracy, ammo-driven damage, reload downtime). These patches make
    /// SS's DPS ranking use CE's stat model; the speed bias the player sets still applies,
    /// on this module's own curve (see CEDps).
    /// </summary>
    /// <summary>
    /// Scoring runs once per carried weapon, per warming-up pawn, per tick — and SS asks for
    /// the same four stats every time. Measured in-game on a four-weapon colonist, the
    /// patched scoring path cost 4.2 us per weapon against stock Simple Sidearms' 0.84 us,
    /// which at twenty pawns in a firefight is ~3.3% of a 60fps frame (test/run-bench.sh).
    /// RimWorld's own cacheStaleAfterTicks did not move that: the repeated cost is the stat
    /// dispatch itself, not the evaluation behind it.
    ///
    /// So the values are memoised for the tick that produced them. Everything cached here is
    /// derived, read-only, and cannot change within a tick — weapon quality, attachments and
    /// damage are all fixed for the frame, and the shooter's accuracy with them.
    /// </summary>
    internal static class ScoreCache
    {
        internal struct Accuracy
        {
            public float spread;
            public float sway;
        }

        internal struct Reload
        {
            public float time;
            public int magSize;
        }

        private static int tick = -1;
        private static readonly Dictionary<int, Accuracy> accuracyStats = new Dictionary<int, Accuracy>();
        private static readonly Dictionary<int, Reload> reloadStats = new Dictionary<int, Reload>();
        private static readonly Dictionary<long, float> hitFactors = new Dictionary<long, float>();

        private static void EnsureTick()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now == tick)
            {
                return;
            }
            tick = now;
            accuracyStats.Clear();
            reloadStats.Clear();
            hitFactors.Clear();
        }

        /// <summary>
        /// Split by consumer rather than cached as one record: the reload amortization runs
        /// on its own for weapons the hit factor is never asked about, and filling in stats
        /// that caller will not read costs more than it saves.
        /// </summary>
        internal static Accuracy AccuracyOf(ThingWithComps weapon)
        {
            EnsureTick();
            if (accuracyStats.TryGetValue(weapon.thingIDNumber, out Accuracy cached))
            {
                return cached;
            }
            var stats = new Accuracy
            {
                spread = weapon.GetStatValue(CE_StatDefOf.ShotSpread),
                sway = weapon.GetStatValue(CE_StatDefOf.SwayFactor),
            };
            accuracyStats[weapon.thingIDNumber] = stats;
            return stats;
        }

        internal static Reload ReloadOf(ThingWithComps weapon, CompAmmoUser ammoUser)
        {
            EnsureTick();
            if (reloadStats.TryGetValue(weapon.thingIDNumber, out Reload cached))
            {
                return cached;
            }
            var stats = new Reload
            {
                time = weapon.GetStatValue(CE_StatDefOf.ReloadTime),
                magSize = ammoUser?.MagSize ?? 0,
            };
            reloadStats[weapon.thingIDNumber] = stats;
            return stats;
        }

        /// <summary>
        /// One warming-up pawn scores every carried weapon against the same target, so the
        /// (weapon, distance) hit factor repeats within the tick — and the CE hit model
        /// behind it is the costliest part of the score.
        /// </summary>
        internal static float HitFactorOf(ThingWithComps weapon, float distance, Func<float> compute)
        {
            EnsureTick();
            long key = ((long)weapon.thingIDNumber << 32) | (uint)BitConverter.SingleToInt32Bits(distance);
            if (hitFactors.TryGetValue(key, out float cached))
            {
                return cached;
            }
            float value = compute();
            hitFactors[key] = value;
            return value;
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.RangedSpeed),
                  new[] { typeof(ThingWithComps) })]
    public static class StatCalculator_RangedSpeed_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "RangedSpeed",
            new[] { typeof(ThingWithComps) },
            "reload downtime will not count against a weapon's rating.");

        // Fold reload downtime into the cycle time so slow-reloading weapons rank lower.
        // Also feeds SS's AverageSpeedRanged, keeping the bias baseline consistent.
        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, ref float __result)
        {
            try
            {
                PostfixInner(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload-time scoring failed; weapon speed "
                              + "ratings fall back to Simple Sidearms' own figure. " + e, 0x43455302);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ThingWithComps weapon, ref float __result)
        {
            CompAmmoUser ammoUser = weapon?.TryGetComp<CompAmmoUser>();
            if (ammoUser == null)
            {
                return;
            }
            ScoreCache.Reload stats = ScoreCache.ReloadOf(weapon, ammoUser);
            int magSize = stats.magSize;
            if (magSize <= 0)
            {
                return;
            }
            float reloadTime = stats.time;
            if (reloadTime <= 0f)
            {
                return;
            }
            // Live verb props, matching CEDps and SS's own RangedSpeed — def.Verbs[0] is the
            // static value and disagrees on weapons CE swaps verbs for (under-barrel launchers).
            float burst = Math.Max(1, weapon.GetComp<CompEquippable>()?.PrimaryVerb?.verbProps?.burstShotCount ?? 1);
            float burstsPerMag = Math.Max(1f, magSize / burst);
            __result += reloadTime / burstsPerMag;
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.RangedDPS),
                  new[] { typeof(ThingWithComps), typeof(float), typeof(float), typeof(float) })]
    public static class StatCalculator_RangedDPS_Patch
    {
        /// <summary>
        /// Stand-in range for the distance-free scoring path, which SS uses when no target is
        /// known. SS averaged the weapon's short/medium/long accuracy stats there; this plays
        /// the same role for CE weapons, which have those stats stripped.
        /// </summary>
        internal const float NoTargetReferenceDistance = 20f;

        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "RangedDPS",
            new[] { typeof(ThingWithComps), typeof(float), typeof(float), typeof(float) },
            "Simple Sidearms will rank CE guns by their vanilla stats, which are mostly zeroes.");

        [HarmonyPrefix]
        public static bool Prefix(ThingWithComps weapon, float speedBias, float averageSpeed, float distance, ref float __result)
        {
            try
            {
                return PrefixInner(weapon, speedBias, averageSpeed, distance, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE-model DPS scoring failed; Simple Sidearms' "
                              + "vanilla formula is used instead. " + e, 0x43455303);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(ThingWithComps weapon, float speedBias, float averageSpeed, float distance, ref float __result)
        {
            if (!CompatUtil.IsCEGun(weapon, out CompAmmoUser ammoUser))
            {
                return true;
            }
            VerbProperties atkProps = weapon.GetComp<CompEquippable>()?.PrimaryVerb?.verbProps;
            if (atkProps == null)
            {
                __result = 0f;
                return false;
            }
            // Vanilla range semantics: the caller passes a plain cell distance
            // (findBestRangedWeapon uses DistanceTo), so that is what the weapon's range is
            // compared against. Stock SS squares the range on both sides of this gate, which
            // lets a 30-cell gun stay scoreable out to 900 — CE-scored weapons use the
            // corrected gate, so ordering can differ from stock SS at extreme range.
            if (atkProps.range < distance || atkProps.minRange > distance)
            {
                __result = -1f;
                return false;
            }
            __result = CEDps(weapon, ammoUser, atkProps, speedBias, averageSpeed) * CEHitFactor(weapon, ammoUser, distance);
            return false;
        }

        /// <summary>
        /// The chance-to-connect term of the score, asked of CE's own public hit model
        /// (CE_Math.CalculateHitPercent — the function behind CE's estimated-hit-chance
        /// readout) instead of a curve invented here. SS gives this path a distance and
        /// nothing else, so the terms a real shot would bring are this module's own
        /// documented stand-ins:
        ///
        ///  - the target is a reference human silhouette (0.5 x 1.75 cells) — there is no
        ///    target object to measure;
        ///  - sway enters as the weapon's SwayFactor read directly as degrees — the
        ///    shooter's skill term scales every candidate's sway equally, and this is a
        ///    ranking, not a shot simulation;
        ///  - visibility, target lead and firing angle are identical for every candidate
        ///    and passed as zero (which also makes the projectile-speed and gravity terms
        ///    inert — they only shape the drop correction that hangs off visibility).
        /// </summary>
        internal static float CEHitFactor(ThingWithComps weapon, CompAmmoUser ammoUser, float distance)
        {
            return ScoreCache.HitFactorOf(weapon, distance, () =>
            {
                ScoreCache.Accuracy stats = ScoreCache.AccuracyOf(weapon);
                float dist = Mathf.Max(1f, distance);
                float shotSpeed = Mathf.Max(1f,
                    CompatUtil.CurrentProjectile(weapon, ammoUser)?.projectile?.speed ?? 0f);
                // offset = the aim height on the target's [0, h] span. CE's own readout
                // passes size.y / 2 (ShiftVecReport.cs:98 — center of the exposed span);
                // 0 means "aim at the feet" and mathematically caps the vertical term at
                // 0.5 however accurate the gun.
                return Mathf.Clamp01(CE_Math.CalculateHitPercent(
                    dist, ReferenceTargetWidth, ReferenceTargetHeight, offset: ReferenceTargetHeight / 2f,
                    shotSpeed: shotSpeed, shotAngle: 0f,
                    swayDegrees: stats.sway, spreadDegrees: stats.spread,
                    visibilityShift: 0f, gravity: CE_Utility.GravityConst));
            });
        }

        /// <summary>Reference silhouette a hypothetical shot is scored against — roughly a standing human.</summary>
        internal const float ReferenceTargetWidth = 0.5f;
        internal const float ReferenceTargetHeight = 1.75f;

        internal static float CEDps(ThingWithComps weapon, CompAmmoUser ammoUser, VerbProperties atkProps, float speedBias, float averageSpeed)
        {
            ThingDef projectile = CompatUtil.CurrentProjectile(weapon, ammoUser) ?? atkProps.defaultProjectile;
            float damage = projectile?.projectile?.GetDamageAmount(weapon) ?? 0f;
            int pellets = (projectile?.projectile as ProjectilePropertiesCE)?.pelletCount ?? 1;
            damage *= Math.Max(1, pellets);
            float burst = Math.Max(1, atkProps.burstShotCount);
            float speed = StatCalculator.RangedSpeed(weapon); // includes our reload amortization
            if (speed <= 0f)
            {
                return 0f;
            }
            // This module's own speed-bias curve: the raw rate is scaled by how the
            // weapon's pace compares to the carried average, raised to the bias the player
            // set. Bias 1 (the default) is exactly neutral; above it, slower-than-average
            // weapons fall off multiplicatively. CE weapons rank on this curve, not on
            // stock SS's — SS was asked to expose its own adjustment (issue #22 / V3).
            float paceFactor = averageSpeed > 0f
                ? Mathf.Pow(averageSpeed / speed, speedBias - 1f)
                : 1f;
            return damage * burst / speed * paceFactor;
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.RangedDPSAverage),
                  new[] { typeof(ThingWithComps), typeof(float), typeof(float) })]
    public static class StatCalculator_RangedDPSAverage_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "RangedDPSAverage",
            new[] { typeof(ThingWithComps), typeof(float), typeof(float) },
            "Simple Sidearms' no-target weapon ranking will use vanilla stats for CE guns.");

        [HarmonyPrefix]
        public static bool Prefix(ThingWithComps weapon, float speedBias, float averageSpeed, ref float __result)
        {
            try
            {
                return PrefixInner(weapon, speedBias, averageSpeed, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "CE-model average-DPS scoring failed; Simple "
                              + "Sidearms' vanilla formula is used instead. " + e, 0x43455304);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PrefixInner(ThingWithComps weapon, float speedBias, float averageSpeed, ref float __result)
        {
            if (!CompatUtil.IsCEGun(weapon, out CompAmmoUser ammoUser))
            {
                return true;
            }
            VerbProperties atkProps = weapon.GetComp<CompEquippable>()?.PrimaryVerb?.verbProps;
            if (atkProps == null)
            {
                __result = 0f;
                return false;
            }
            // SS's own no-target formula ends by weighting damage with the weapon's accuracy
            // stats, which for a CE gun resolve to the vanilla AccuracyBase fallback — i.e.
            // purely the quality factor. Dropping that term made an awful gun score identical
            // to a masterwork one, leaving carry order to decide. CE keeps quality (and
            // attachments, and damaged parts) in ShotSpread, so scoring the hit proxy at a
            // fixed reference range restores the signal without leaving CE's own model.
            __result = StatCalculator_RangedDPS_Patch.CEDps(weapon, ammoUser, atkProps, speedBias, averageSpeed)
                       * StatCalculator_RangedDPS_Patch.CEHitFactor(weapon, ammoUser, StatCalculator_RangedDPS_Patch.NoTargetReferenceDistance);
            return false;
        }
    }
}
