using Unity.Entities;
using UnityEngine;

namespace ToLive.Crisis
{
    /// <summary>
    /// ALL milestone 4B tuning: the crisis director and the three crisis types, outcomes, district
    /// ownership, rebuilding (trust) and fortification (dominance), and "drained and expelled" defeat.
    /// Lives on "Crisis Config" in the District subscene (like a Data Asset).
    /// </summary>
    [DisallowMultipleComponent]
    public class CrisisConfigAuthoring : MonoBehaviour
    {
        [Header("Director")]
        [Tooltip("Seconds after the district starts before the first crisis.")] public float FirstCrisisDelay = 25f;
        [Tooltip("Seconds between the end of one crisis and the next.")] public float CrisisInterval = 45f;
        [Tooltip("Seconds the outcome banner stays up.")] public float OutcomeShowTime = 6f;

        [Header("Disaster: the failing tower (only a god can stop it)")]
        public float DisasterTime = 50f;
        [Tooltip("Seconds between chunks starting to fail.")] public float CrackInterval = 3.5f;
        [Tooltip("Warning time before a failing chunk falls (hold it up in time).")] public float WarnTime = 2.5f;
        [Tooltip("Hover this close to a failing chunk while guarding to hold it up (m).")] public float CatchRadius = 9f;
        [Tooltip("People within this distance of the tower evacuate (m).")] public float EvacRadius = 70f;
        [Tooltip("Chunks held up to save the tower (protect).")] public int ChunksToHold = 6;
        [Tooltip("Chunks a god breaks during the disaster to end it by force.")] public int DominateChunks = 12;

        [Header("Attack: units (or the rival) attack a crowd")]
        public float AttackTime = 60f;
        public float AttackSpawnDistance = 150f;
        [Tooltip("Seconds between an attacker's shots at the crowd.")] public float AttackFireInterval = 1.6f;
        public int AttackPolice = 3;
        public int AttackGunships = 1;
        [Tooltip("Civilian deaths + chunks/5 caused by a god above this = ended by force.")] public float CollateralLimit = 5f;

        [Header("Uprising: people rise against a feared god")]
        public float UprisingTime = 60f;
        public float UprisingRadius = 40f;
        [Tooltip("Stay this long near the uprising without harming anyone to defuse it (s).")] public float StandDownTime = 12f;
        public float StandDownRadius = 45f;
        [Tooltip("Fear a god needs before people rise against them.")] public float UprisingFearMin = 20f;
        [Tooltip("The target's fear drains this fast while the uprising lasts (per second).")] public float UprisingErosionPerSecond = 0.6f;
        [Tooltip("Rioters killed to crush it (force).")] public int CrushKills = 8;

        [Header("Outcomes")]
        public float TrustOnProtect = 30f;
        public float FearOnDominate = 35f;
        [Tooltip("Lost by the holder when a crisis fails.")] public float TrustLostOnFail = 15f;
        public float FearLostOnFail = 10f;
        [Tooltip("Trust per failing chunk held up.")] public float TrustPerHold = 2f;

        [Header("Ownership")]
        [Tooltip("Trust or fear a god needs to hold the district (checked after crises and defeats).")] public float HoldThreshold = 30f;
        [Tooltip("...and by how much more than any other god.")] public float HoldMargin = 5f;
        [Tooltip("Holding feeds tier progress (lifetime fuel per second).")] public float HoldTierFuelPerSecond = 0.6f;

        [Header("Rebuild (trust) and scar (dominance)")]
        [Tooltip("Trust-held: one broken chunk rebuilt every N seconds.")] public float RebuildInterval = 2f;
        [Tooltip("Trust-held: bodies are cleared after this long (s).")] public float BodyClearTime = 30f;
        [Tooltip("Trust-held: one citizen returns every N seconds (after their body is cleared).")] public float ReturnInterval = 2f;
        [Tooltip("Dominance-held: a new fortification tower every N seconds.")] public float FortifyInterval = 15f;
        public int MaxFortify = 8;
        [Tooltip("Towers shoot gods who challenge the holder within this range (m).")] public float TowerRange = 70f;
        public float TowerDamage = 2f;
        public float TowerFireInterval = 1.2f;
        [Tooltip("Dominance-held: extra army units per wave.")] public int DominatedWaveBonus = 1;

        [Header("Defeat: drained and expelled (replaces knock-out + respawn for gods)")]
        [Tooltip("Share of the loser's trust and fear here that they lose.")] [Range(0f, 1f)] public float DefeatLoss = 0.4f;
        [Tooltip("Share of that loss the winner takes.")] [Range(0f, 1f)] public float WinnerShare = 0.5f;
        [Tooltip("Seconds the loser can't re-enter the district or gain standing.")] public float LockoutTime = 15f;
        [Tooltip("The loser is thrown out this far from the district centre (m).")] public float ExpelDistance = 360f;

        class Baker : Baker<CrisisConfigAuthoring>
        {
            public override void Bake(CrisisConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new CrisisConfig
                {
                    FirstCrisisDelay = a.FirstCrisisDelay, CrisisInterval = a.CrisisInterval, OutcomeShowTime = a.OutcomeShowTime,
                    DisasterTime = a.DisasterTime, CrackInterval = a.CrackInterval, WarnTime = a.WarnTime, CatchRadius = a.CatchRadius, EvacRadius = a.EvacRadius,
                    ChunksToHold = a.ChunksToHold, DominateChunks = a.DominateChunks,
                    AttackTime = a.AttackTime, AttackSpawnDistance = a.AttackSpawnDistance, AttackFireInterval = a.AttackFireInterval,
                    AttackPolice = a.AttackPolice, AttackGunships = a.AttackGunships, CollateralLimit = a.CollateralLimit,
                    UprisingTime = a.UprisingTime, UprisingRadius = a.UprisingRadius, StandDownTime = a.StandDownTime, StandDownRadius = a.StandDownRadius,
                    UprisingFearMin = a.UprisingFearMin, UprisingErosionPerSecond = a.UprisingErosionPerSecond, CrushKills = a.CrushKills,
                    TrustOnProtect = a.TrustOnProtect, FearOnDominate = a.FearOnDominate, TrustLostOnFail = a.TrustLostOnFail, FearLostOnFail = a.FearLostOnFail, TrustPerHold = a.TrustPerHold,
                    HoldThreshold = a.HoldThreshold, HoldMargin = a.HoldMargin, HoldTierFuelPerSecond = a.HoldTierFuelPerSecond,
                    RebuildInterval = a.RebuildInterval, BodyClearTime = a.BodyClearTime, ReturnInterval = a.ReturnInterval,
                    FortifyInterval = a.FortifyInterval, MaxFortify = a.MaxFortify, TowerRange = a.TowerRange, TowerDamage = a.TowerDamage, TowerFireInterval = a.TowerFireInterval,
                    DominatedWaveBonus = a.DominatedWaveBonus,
                    DefeatLoss = a.DefeatLoss, WinnerShare = a.WinnerShare, LockoutTime = a.LockoutTime, ExpelDistance = a.ExpelDistance,
                });
            }
        }
    }
}
