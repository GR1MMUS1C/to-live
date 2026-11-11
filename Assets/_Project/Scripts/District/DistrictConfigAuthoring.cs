using Unity.Entities;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>
    /// ALL district and crowd tuning: how many citizens, how they behave, every trust/fear source
    /// rate and how the crowd pays surge fuel. Lives on "District Config" in the District subscene.
    /// </summary>
    [DisallowMultipleComponent]
    public class DistrictConfigAuthoring : MonoBehaviour
    {
        [Header("Prefabs (made by Tools > To Live)")]
        [Tooltip("Replicated district state ghost.")] public GameObject StatePrefab;
        [Tooltip("Static ghost left where a citizen died.")] public GameObject BodyPrefab;
        [Tooltip("Client-only citizen render prefab (not networked).")] public GameObject CitizenPrefab;
        [Tooltip("Benchmark only: one ghost per citizen (option A in docs/PERF.md).")] public GameObject ProbePrefab;

        [Header("Crowd (count set from the measured benchmark, see docs/PERF.md)")]
        [Tooltip("Citizens in the district (max = baked homes).")] public int CitizenCount = 640;
        public float WalkSpeed = 1.4f;
        public float RunSpeed = 6f;
        [Tooltip("How far citizens wander along their street from home (m).")] public float WanderRange = 18f;
        [Tooltip("Seconds between a citizen's decisions.")] public float ThinkInterval = 0.5f;

        [Header("Crowd reactions")]
        [Tooltip("Citizens gather around a god they trust within this distance (m).")] public float GatherRadius = 45f;
        [Tooltip("They stop this far from the god and cheer (m).")] public float GatherDistance = 9f;
        [Tooltip("Citizens flee a god they fear within this distance (m).")] public float FearRadius = 35f;
        [Tooltip("Seconds a scared citizen keeps fleeing.")] public float FleeTime = 6f;
        [Tooltip("Too close to violence to run: they cower (m).")] public float CowerRadius = 5f;

        [Header("Ways citizens die (server decided)")]
        [Tooltip("A launched / fast body kills citizens within this distance (m).")] public float BodyKillRadius = 2f;
        [Tooltip("Bodies faster than this kill on contact (m/s).")] public float FastBodySpeed = 30f;
        [Tooltip("A supersonic pass lower than this above people kills them (m).")] public float SupersonicKillAltitude = 8f;
        [Tooltip("A supersonic pass within this distance scares people and costs trust (m).")] public float SupersonicScareRadius = 40f;
        [Tooltip("A breaking chunk kills citizens within this distance of it (m).")] public float ChunkKillRadius = 6f;
        [Tooltip("Fraction of the eruption radius that kills (the rest only scares).")] [Range(0f, 1f)] public float EruptionKillFraction = 0.5f;
        [Tooltip("Rocket blasts kill within this distance (m).")] public float RocketKillRadius = 5f;

        [Header("Trust sources (protecting)")]
        [Tooltip("Trust per second while near a crowd without harming it.")] public float TrustPerSecondNearCrowd = 0.6f;
        [Tooltip("'Near a crowd' distance (m).")] public float NearCrowdRadius = 35f;
        [Tooltip("Citizens that make a crowd.")] public float NearCrowdMin = 5f;
        [Tooltip("No 'near crowd' trust for this long after harming citizens (s).")] public float HarmCooldown = 15f;
        public float TrustPerUnitDestroyed = 6f;
        [Tooltip("A unit counts as threatening people when this close to living citizens (m).")] public float UnitThreatRadius = 90f;
        public float TrustPerRivalDefeated = 15f;
        [Tooltip("Citizens within this distance are watching (m).")] public float WatchRadius = 70f;
        [Tooltip("Watchers needed for the rival knock-out to count.")] public float WatchersNeeded = 5f;

        [Header("Fear sources (dominating)")]
        public float TrustLostPerDeath = 2f;
        public float FearPerDeath = 3f;
        public float TrustLostPerChunk = 0.1f;
        public float FearPerChunk = 0.25f;
        public float TrustLostEruption = 5f;
        public float FearPerEruption = 10f;
        [Tooltip("Reckless supersonic pass over people.")] public float TrustLostRecklessPass = 3f;
        public float FearPerRecklessPass = 1f;
        public float RecklessPassCooldown = 3f;
        [Tooltip("Trust and fear fade toward zero this fast (per second).")] public float StandingDecayPerSecond = 0.005f;
        [Tooltip("Standing difference that maps to full trust / full dominance alignment.")] public float AlignmentScale = 40f;

        [Header("Surge fuel from the crowd (replaces the placeholder producers)")]
        [Tooltip("Off = milestone 3 placeholder fuel (damage, chunks).")] public bool CrowdFuel = true;
        [Tooltip("Fuel per watching citizen per second, at full standing.")] public float FuelPerWatcherPerSecond = 0.05f;
        [Tooltip("Share of fuel watchers pay a god they have no feelings about yet.")] [Range(0f, 1f)] public float AweFactor = 0.25f;
        [Tooltip("Trust + fear at which standing pays half of full fuel.")] public float StandingScale = 30f;
        [Tooltip("Fear fuel spike per civilian killed (short-term).")] public float FuelPerKillFear = 2f;
        [Tooltip("Seconds between crowd fuel payouts.")] public float FuelInterval = 0.5f;

        [Header("HUD")]
        [Tooltip("Seconds standing changes are summed into one cause feed line.")] public float CauseFeedInterval = 1.5f;

        class Baker : Baker<DistrictConfigAuthoring>
        {
            public override void Bake(DistrictConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new DistrictConfig
                {
                    StatePrefab = a.StatePrefab != null ? GetEntity(a.StatePrefab, TransformUsageFlags.None) : Entity.Null,
                    BodyPrefab = a.BodyPrefab != null ? GetEntity(a.BodyPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    CitizenPrefab = a.CitizenPrefab != null ? GetEntity(a.CitizenPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    ProbePrefab = a.ProbePrefab != null ? GetEntity(a.ProbePrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    CitizenCount = a.CitizenCount, WalkSpeed = a.WalkSpeed, RunSpeed = a.RunSpeed, WanderRange = a.WanderRange, ThinkInterval = a.ThinkInterval,
                    GatherRadius = a.GatherRadius, GatherDistance = a.GatherDistance, FearRadius = a.FearRadius, FleeTime = a.FleeTime, CowerRadius = a.CowerRadius,
                    BodyKillRadius = a.BodyKillRadius, FastBodySpeed = a.FastBodySpeed, SupersonicKillAltitude = a.SupersonicKillAltitude, SupersonicScareRadius = a.SupersonicScareRadius,
                    ChunkKillRadius = a.ChunkKillRadius, EruptionKillFraction = a.EruptionKillFraction, RocketKillRadius = a.RocketKillRadius,
                    TrustPerSecondNearCrowd = a.TrustPerSecondNearCrowd, NearCrowdRadius = a.NearCrowdRadius, NearCrowdMin = a.NearCrowdMin, HarmCooldown = a.HarmCooldown,
                    TrustPerUnitDestroyed = a.TrustPerUnitDestroyed, UnitThreatRadius = a.UnitThreatRadius,
                    TrustPerRivalDefeated = a.TrustPerRivalDefeated, WatchRadius = a.WatchRadius, WatchersNeeded = a.WatchersNeeded,
                    TrustLostPerDeath = a.TrustLostPerDeath, FearPerDeath = a.FearPerDeath,
                    TrustLostPerChunk = a.TrustLostPerChunk, FearPerChunk = a.FearPerChunk,
                    TrustLostEruption = a.TrustLostEruption, FearPerEruption = a.FearPerEruption,
                    TrustLostRecklessPass = a.TrustLostRecklessPass, FearPerRecklessPass = a.FearPerRecklessPass, RecklessPassCooldown = a.RecklessPassCooldown,
                    StandingDecayPerSecond = a.StandingDecayPerSecond, AlignmentScale = a.AlignmentScale,
                    CrowdFuel = a.CrowdFuel, FuelPerWatcherPerSecond = a.FuelPerWatcherPerSecond, AweFactor = a.AweFactor,
                    StandingScale = a.StandingScale, FuelPerKillFear = a.FuelPerKillFear, FuelInterval = a.FuelInterval,
                    CauseFeedInterval = a.CauseFeedInterval,
                });
            }
        }
    }
}
