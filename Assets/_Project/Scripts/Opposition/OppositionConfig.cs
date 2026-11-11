using Unity.Entities;

namespace ToLive.Opposition
{
    /// <summary>Per unit type numbers (police, gunship, anti-god).</summary>
    public struct UnitStats
    {
        public float Health;
        public float Speed;           // m/s
        public float Accel;           // m/s²
        public float PreferredRange;  // keeps this distance from its target
        public float HeightAbove;     // ... and this height above it
        public float FireRange;
        public float FireInterval;    // s between shots
        public float Damage;
        public float Strafe;          // strafe speed while holding range
        public int WreckPieces;       // cosmetic debris when destroyed
    }

    /// <summary>
    /// ALL heat and response-force tuning. Baked from <see cref="OppositionConfigAuthoring"/> on
    /// "Opposition Config" in the District subscene (like Combat Config).
    /// </summary>
    public struct OppositionConfig : IComponentData
    {
        public Entity PolicePrefab, GunshipPrefab, AntiGodPrefab, RocketPrefab, StatePrefab;

        // heat
        public float HeatPerChunk, HeatPerCollapsedChunk, HeatPerUnitDamage, HeatPerEruption, HeatPerCivilianDeath, TrustedHeatScale;
        public float Level1, Level2, Level3, MaxHeat;
        public float QuietTime, DecayPerSecond;
        public float BlameQuietTime, BlameDecayPerSecond;

        // spawning
        public float WaveInterval;
        public int PolicePerWave, GunshipsPerWave;
        public int MaxPolice, MaxGunships, MaxAntiGod, MaxUnits;
        public float SpawnDistance, SpawnHeightMin, SpawnHeightMax, DistrictHalfSize;

        public UnitStats Police, Gunship, AntiGod;

        // police tracers
        public float TracerAccuracy, TracerFlinch;
        // gunship rockets
        public float RocketSpeed, RocketRadius, RocketKnockback, RocketBreakRadius, RocketLifetime;
        // anti-god beam
        public float BeamChargeTime, BeamAimLockTime, BeamFireTime, BeamCooldown, BeamRadius, BeamTrackRate, BeamKnockback, SuppressTime;

        // launched bodies hitting other bodies (a thrown unit is a projectile)
        public float BodyImpactSpeed, BodyImpactRadius, BodyImpactDamagePerSpeed, BodyImpactLaunchScale;

        public UnitStats Stats(byte kind) => kind switch { UnitKind.Gunship => Gunship, UnitKind.AntiGod => AntiGod, _ => Police };

        public byte LevelFor(float heat) => heat >= Level3 ? (byte)3 : heat >= Level2 ? (byte)2 : heat >= Level1 ? (byte)1 : (byte)0;

        public float ThresholdFor(int level) => level switch { 1 => Level1, 2 => Level2, 3 => Level3, _ => 0f };
    }
}
