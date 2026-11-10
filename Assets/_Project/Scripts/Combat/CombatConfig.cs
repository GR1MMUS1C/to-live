using Unity.Entities;

namespace ToLive.Combat
{
    /// <summary>Baked copy of <see cref="CombatConfigAuthoring"/>. Times are in seconds, speeds in m/s.</summary>
    public struct CombatConfig : IComponentData
    {
        public float MaxPower, RespawnDelay;
        public float LockRange, LockBreakRange, LockConeDot;
        public float HitRange, HitReachForward;
        public float HomingRange, HomingMaxSpeed, StrikeDistance;

        public float LightDuration, LightActiveTime, LightDamage, LightKnockback, LightStagger;
        public float LauncherDamage, LauncherSpeed, LauncherUpBias, LauncherDuration;

        public float HeavyMaxChargeTime, HeavyDuration, HeavyActiveTime;
        public float HeavyMinDamage, HeavyMaxDamage, HeavyMinSpeed, HeavyMaxSpeed, HeavyLaunchDuration;

        public float FinisherDamage, FinisherSpeed, FinisherDuration;

        public float DashSpeed, DashDuration, DashCooldown;

        public float GuardKnockbackScale, GuardDamageScale, GuardFrontDot;

        public float HitStopTime;
        public float LaunchGravity, LaunchDrag, AttackDamping;

        public float BreakSpeed, FlyingBreakSpeed, ChunkSpeedLoss, ChunkImpactDamage;
        public float WallImpactSpeed, WallImpactDamagePerMs;
    }
}
