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
    /// SS's DPS ranking use CE's stat model while preserving SS's speed-bias semantics.
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
        private static readonly Dictionary<int, float> shooters = new Dictionary<int, float>();

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
            shooters.Clear();
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

        /// <summary>One shooter scores every weapon they carry — resolve their accuracy once.</summary>
        internal static float ShootingAccuracyOf(Pawn pawn)
        {
            EnsureTick();
            if (shooters.TryGetValue(pawn.thingIDNumber, out float cached))
            {
                return cached;
            }
            float accuracy = Mathf.Min(pawn.GetStatValue(StatDefOf.ShootingAccuracyPawn),
                                       StatCalculator_RangedDPS_Patch.MaxShootingAccuracy);
            shooters[pawn.thingIDNumber] = accuracy;
            return accuracy;
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
        /// <summary>CE caps the shooting-accuracy term here (Verb_LaunchProjectileCE.ShootingAccuracy).</summary>
        internal const float MaxShootingAccuracy = 4.5f;

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
            // Mirrors SS's own (quirky, squared-vs-unsquared) range gate so relative ordering
            // stays consistent with what SS's callers expect.
            if (atkProps.range * atkProps.range < distance || atkProps.minRange * atkProps.minRange > distance)
            {
                __result = -1f;
                return false;
            }
            __result = CEDps(weapon, ammoUser, atkProps, speedBias, averageSpeed) * CEHitFactor(weapon, distance);
            return false;
        }

        /// <summary>
        /// Distance-dependent hit proxy from CE's accuracy stats, converted to lateral miss
        /// distance at range. Not CE's real ballistics — just enough distance falloff that SS
        /// ranks a shotgun above a sniper up close and the reverse at range, mirroring the
        /// role vanilla hit-chance plays in SS's formula.
        ///
        /// Sway is deliberately NOT summed into the spread as if it were degrees: CE's own
        /// SwayAmplitude is (4.5 - shooting accuracy) x SwayFactor, so the raw factor is a
        /// multiplier, not an angle. Adding it directly let sway account for ~90% of the term
        /// on a typical gun and made both the weapon's real spread and the shooter's skill
        /// nearly irrelevant to the ranking.
        /// </summary>
        internal static float CEHitFactor(ThingWithComps weapon, float distance)
        {
            ScoreCache.Accuracy stats = ScoreCache.AccuracyOf(weapon);
            float spreadDegrees = stats.spread;
            float swayFactor = stats.sway;
            Pawn carrier = CompatUtil.CarrierOf(weapon);
            float shootingAccuracy = carrier != null
                ? ScoreCache.ShootingAccuracyOf(carrier)
                : MaxShootingAccuracy; // unknown shooter: score the weapon on its own spread
            float angularErrorDegrees = spreadDegrees + Mathf.Max(0f, MaxShootingAccuracy - shootingAccuracy) * swayFactor;
            float lateralMissCells = distance * angularErrorDegrees * 0.01745f;
            return Mathf.Clamp01(0.4f / Mathf.Max(0.04f, lateralMissCells));
        }

        internal static float CEDps(ThingWithComps weapon, CompAmmoUser ammoUser, VerbProperties atkProps, float speedBias, float averageSpeed)
        {
            ThingDef projectile = CompatUtil.CurrentProjectile(weapon, ammoUser) ?? atkProps.defaultProjectile;
            float damage = projectile?.projectile?.GetDamageAmount(weapon) ?? 0f;
            int pellets = (projectile?.projectile as ProjectilePropertiesCE)?.pelletCount ?? 1;
            damage *= Math.Max(1, pellets);
            float burst = Math.Max(1, atkProps.burstShotCount);
            float speed = StatCalculator.RangedSpeed(weapon); // includes our reload amortization

            // Same speed-bias adjustment SS applies in its vanilla formulas.
            float diffFromAverage = (speed - averageSpeed) * (speedBias - 1f);
            speed += diffFromAverage;
            if (speed <= 0f)
            {
                return 0f;
            }
            // Flat damage-per-cycle; both variants multiply in CEHitFactor.
            return damage * burst / speed;
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
                       * StatCalculator_RangedDPS_Patch.CEHitFactor(weapon, StatCalculator_RangedDPS_Patch.NoTargetReferenceDistance);
            return false;
        }
    }
}
