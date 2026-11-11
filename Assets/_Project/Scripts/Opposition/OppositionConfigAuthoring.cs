using Unity.Entities;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>
    /// ALL heat and response-force tuning: how heat rises and decays, wave spawning and caps, and the
    /// three unit types. Lives on "Opposition Config" in the District subscene. Like a Data Asset the
    /// AI director reads.
    /// </summary>
    [DisallowMultipleComponent]
    public class OppositionConfigAuthoring : MonoBehaviour
    {
        [System.Serializable]
        public class Unit
        {
            public float Health = 30f;
            public float Speed = 20f;
            public float Acceleration = 30f;
            [Tooltip("Keeps this distance from its target.")] public float PreferredRange = 35f;
            [Tooltip("... and this height above it.")] public float HeightAbove = 8f;
            public float FireRange = 80f;
            [Tooltip("Seconds between shots.")] public float FireInterval = 0.7f;
            public float Damage = 1.5f;
            [Tooltip("Sideways speed while holding range.")] public float Strafe = 6f;
            [Tooltip("Cosmetic wreck pieces when destroyed (under the debris cap).")] public int WreckPieces = 6;

            public UnitStats Bake() => new UnitStats
            {
                Health = Health, Speed = Speed, Accel = Acceleration, PreferredRange = PreferredRange, HeightAbove = HeightAbove,
                FireRange = FireRange, FireInterval = FireInterval, Damage = Damage, Strafe = Strafe, WreckPieces = WreckPieces,
            };
        }

        [Header("Prefabs (made by Tools > To Live)")]
        public GameObject PolicePrefab;
        public GameObject GunshipPrefab;
        public GameObject AntiGodPrefab;
        public GameObject RocketPrefab;
        [Tooltip("The replicated heat / rival state ghost.")] public GameObject StatePrefab;

        [Header("Heat sources (placeholder for milestone 4 dominance)")]
        public float HeatPerChunkBroken = 1.5f;
        [Tooltip("Heat per point of damage dealt to a response unit.")] public float HeatPerUnitDamage = 0.4f;
        public float HeatPerEruption = 25f;

        [Header("Heat levels")]
        [Tooltip("Heat needed for level 1 (police).")] public float Level1 = 10f;
        [Tooltip("Heat needed for level 2 (military).")] public float Level2 = 40f;
        [Tooltip("Heat needed for level 3 (anti-god weapon).")] public float Level3 = 90f;
        public float MaxHeat = 120f;
        [Tooltip("Seconds without new heat before it starts to decay.")] public float QuietTime = 15f;
        public float DecayPerSecond = 2f;
        [Tooltip("Seconds before a god's blame starts to fade (units target blamed gods).")] public float BlameQuietTime = 20f;
        public float BlameDecayPerSecond = 2f;

        [Header("Waves and caps (defaults set from the measured benchmark, see docs/PERF.md)")]
        public float WaveInterval = 8f;
        public int PolicePerWave = 2;
        public int GunshipsPerWave = 1;
        public int MaxPolice = 6;
        public int MaxGunships = 3;
        public int MaxAntiGod = 1;
        public int MaxUnits = 10;
        [Tooltip("Units spawn this far from the district centre.")] public float SpawnDistance = 250f;
        public float SpawnHeightMin = 60f, SpawnHeightMax = 120f;
        [Tooltip("Half the district size (units never wander further).")] public float DistrictHalfSize = 320f;

        [Header("Level 1: police drones (tracers)")]
        public Unit Police = new Unit { Health = 30f, Speed = 28f, Acceleration = 35f, PreferredRange = 35f, HeightAbove = 8f, FireRange = 80f, FireInterval = 0.9f, Damage = 0.8f, Strafe = 7f, WreckPieces = 6 };
        [Tooltip("Hit chance at point blank (falls off to half at fire range).")] [Range(0f, 1f)] public float TracerAccuracy = 0.5f;
        [Tooltip("Tiny push on a tracer hit (m/s).")] public float TracerFlinch = 2f;

        [Header("Level 2: gunships (rockets)")]
        public Unit Gunship = new Unit { Health = 120f, Speed = 20f, Acceleration = 18f, PreferredRange = 70f, HeightAbove = 20f, FireRange = 130f, FireInterval = 3f, Damage = 12f, Strafe = 5f, WreckPieces = 14 };
        public float RocketSpeed = 40f;
        public float RocketRadius = 7f;
        [Tooltip("Launch speed at the centre of the blast (m/s).")] public float RocketKnockback = 35f;
        [Tooltip("Building chunks within this radius of the blast break.")] public float RocketBreakRadius = 4f;
        public float RocketLifetime = 6f;

        [Header("Level 3: anti-god weapon (telegraphed beam)")]
        public Unit AntiGod = new Unit { Health = 400f, Speed = 12f, Acceleration = 8f, PreferredRange = 110f, HeightAbove = 45f, FireRange = 260f, FireInterval = 0f, Damage = 40f, Strafe = 2f, WreckPieces = 30 };
        [Tooltip("Build-up before the beam fires (aim line visible).")] public float BeamChargeTime = 2.5f;
        [Tooltip("The aim stops tracking this long before it fires (your window to dodge).")] public float BeamAimLockTime = 0.6f;
        public float BeamFireTime = 0.5f;
        public float BeamCooldown = 5f;
        public float BeamRadius = 2.5f;
        [Tooltip("How fast the aim follows its target while charging (degrees per second).")] public float BeamTrackRate = 25f;
        public float BeamKnockback = 40f;
        [Tooltip("Surge meter gain is suppressed this long after being hit.")] public float SuppressTime = 6f;

        [Header("Launched bodies hitting bodies (a thrown unit is a projectile)")]
        public float BodyImpactSpeed = 35f;
        public float BodyImpactRadius = 3f;
        [Tooltip("Damage to both bodies per m/s of impact speed.")] public float BodyImpactDamagePerSpeed = 0.15f;
        [Tooltip("The hit body is launched at this fraction of the impact velocity.")] public float BodyImpactLaunchScale = 0.6f;

        class Baker : Baker<OppositionConfigAuthoring>
        {
            public override void Bake(OppositionConfigAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new OppositionConfig
                {
                    PolicePrefab = a.PolicePrefab != null ? GetEntity(a.PolicePrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    GunshipPrefab = a.GunshipPrefab != null ? GetEntity(a.GunshipPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    AntiGodPrefab = a.AntiGodPrefab != null ? GetEntity(a.AntiGodPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    RocketPrefab = a.RocketPrefab != null ? GetEntity(a.RocketPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    StatePrefab = a.StatePrefab != null ? GetEntity(a.StatePrefab, TransformUsageFlags.None) : Entity.Null,
                    HeatPerChunk = a.HeatPerChunkBroken, HeatPerUnitDamage = a.HeatPerUnitDamage, HeatPerEruption = a.HeatPerEruption,
                    Level1 = a.Level1, Level2 = a.Level2, Level3 = a.Level3, MaxHeat = a.MaxHeat,
                    QuietTime = a.QuietTime, DecayPerSecond = a.DecayPerSecond,
                    BlameQuietTime = a.BlameQuietTime, BlameDecayPerSecond = a.BlameDecayPerSecond,
                    WaveInterval = a.WaveInterval, PolicePerWave = a.PolicePerWave, GunshipsPerWave = a.GunshipsPerWave,
                    MaxPolice = a.MaxPolice, MaxGunships = a.MaxGunships, MaxAntiGod = a.MaxAntiGod, MaxUnits = a.MaxUnits,
                    SpawnDistance = a.SpawnDistance, SpawnHeightMin = a.SpawnHeightMin, SpawnHeightMax = a.SpawnHeightMax, DistrictHalfSize = a.DistrictHalfSize,
                    Police = a.Police.Bake(), Gunship = a.Gunship.Bake(), AntiGod = a.AntiGod.Bake(),
                    TracerAccuracy = a.TracerAccuracy, TracerFlinch = a.TracerFlinch,
                    RocketSpeed = a.RocketSpeed, RocketRadius = a.RocketRadius, RocketKnockback = a.RocketKnockback,
                    RocketBreakRadius = a.RocketBreakRadius, RocketLifetime = a.RocketLifetime,
                    BeamChargeTime = a.BeamChargeTime, BeamAimLockTime = a.BeamAimLockTime, BeamFireTime = a.BeamFireTime,
                    BeamCooldown = a.BeamCooldown, BeamRadius = a.BeamRadius, BeamTrackRate = a.BeamTrackRate,
                    BeamKnockback = a.BeamKnockback, SuppressTime = a.SuppressTime,
                    BodyImpactSpeed = a.BodyImpactSpeed, BodyImpactRadius = a.BodyImpactRadius,
                    BodyImpactDamagePerSpeed = a.BodyImpactDamagePerSpeed, BodyImpactLaunchScale = a.BodyImpactLaunchScale,
                });
            }
        }
    }
}
