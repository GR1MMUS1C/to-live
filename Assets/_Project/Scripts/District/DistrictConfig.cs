using Unity.Entities;

namespace ToLive.District
{
    /// <summary>
    /// ALL crowd / district tuning, baked from <see cref="DistrictConfigAuthoring"/> on "District Config"
    /// in the District subscene (like a Data Asset read by the district systems).
    /// </summary>
    public struct DistrictConfig : IComponentData
    {
        public Entity StatePrefab, BodyPrefab, CitizenPrefab, ProbePrefab;

        // crowd
        public int CitizenCount;
        public float WalkSpeed, RunSpeed, WanderRange, ThinkInterval;
        public float GatherRadius, GatherDistance, FearRadius, FleeTime, CowerRadius;
        public float BodyKillRadius, FastBodySpeed, SupersonicKillAltitude, SupersonicScareRadius;
        public float ChunkKillRadius, EruptionKillFraction, RocketKillRadius;

        // standing (the district source rates)
        public float TrustPerSecondNearCrowd, NearCrowdRadius, NearCrowdMin, HarmCooldown;
        public float TrustPerUnitDestroyed, UnitThreatRadius;
        public float TrustPerRivalDefeated, WatchRadius, WatchersNeeded;
        public float TrustLostPerDeath, FearPerDeath;
        public float TrustLostPerChunk, FearPerChunk;
        public float TrustLostEruption, FearPerEruption;
        public float TrustLostRecklessPass, FearPerRecklessPass, RecklessPassCooldown;
        public float StandingDecayPerSecond;
        public float AlignmentScale;

        // fuel (replaces the placeholder producers)
        public bool CrowdFuel;
        public float FuelPerWatcherPerSecond, AweFactor, StandingScale, FuelPerKillFear, FuelInterval;

        public float CauseFeedInterval;
    }
}
