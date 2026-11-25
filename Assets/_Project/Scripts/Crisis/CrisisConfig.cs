using Unity.Entities;

namespace ToLive.Crisis
{
    /// <summary>ALL crisis, ownership, rebuild/scar and defeat tuning (baked from <see cref="CrisisConfigAuthoring"/>).</summary>
    public struct CrisisConfig : IComponentData
    {
        // director
        public float FirstCrisisDelay, CrisisInterval, OutcomeShowTime;
        // disaster (failing tower)
        public float DisasterTime, CrackInterval, WarnTime, CatchRadius, EvacRadius;
        public int ChunksToHold, DominateChunks;
        // attack
        public float AttackTime, AttackSpawnDistance, AttackFireInterval;
        public int AttackPolice, AttackGunships;
        public float CollateralLimit;
        // uprising
        public float UprisingTime, UprisingRadius, StandDownTime, StandDownRadius, UprisingFearMin, UprisingErosionPerSecond;
        public int CrushKills;
        // outcomes
        public float TrustOnProtect, FearOnDominate, TrustLostOnFail, FearLostOnFail, TrustPerHold;
        // ownership
        public float HoldThreshold, HoldMargin, HoldTierFuelPerSecond;
        // rebuild / scar
        public float RebuildInterval, BodyClearTime, ReturnInterval;
        public float FortifyInterval, TowerRange, TowerDamage, TowerFireInterval;
        public int MaxFortify;
        public int DominatedWaveBonus;
        // defeat
        public float DefeatLoss, WinnerShare, LockoutTime, ExpelDistance;
    }
}
