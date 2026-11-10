using Unity.Entities;
using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>
    /// ALL combat tuning (like Flight Config). Lives on the "Combat Config" object in the District
    /// subscene. Open the subscene to edit; values re-bake live in Play Mode.
    /// </summary>
    [DisallowMultipleComponent]
    public class CombatConfigAuthoring : MonoBehaviour
    {
        [Header("Power (health)")]
        public float MaxPower = 100f;
        [Tooltip("Seconds knocked out before respawning.")] public float RespawnDelay = 4f;

        [Header("Lock-on")]
        public float LockRange = 80f;
        [Tooltip("Lock breaks beyond this distance.")] public float LockBreakRange = 140f;
        [Tooltip("How far in front a target must be to be picked (dot product, -1..1).")] public float LockConeDot = 0.2f;

        [Header("Hit detection")]
        [Tooltip("Radius of the strike sphere (m).")] public float HitRange = 3.5f;
        [Tooltip("How far in front of the attacker the strike sphere sits (m).")] public float HitReachForward = 1.5f;
        [Tooltip("Attacks home toward the lock target within this distance (m).")] public float HomingRange = 14f;
        public float HomingMaxSpeed = 45f;
        [Tooltip("Where homing stops in front of the target (m).")] public float StrikeDistance = 1.8f;

        [Header("Light chain (3 hits, 3rd launches)")]
        public float LightDuration = 0.28f;
        [Tooltip("Seconds into the swing when the hit is checked.")] public float LightActiveTime = 0.10f;
        public float LightDamage = 5f;
        public float LightKnockback = 6f;
        [Tooltip("Short loss of control after hits 1-2 (s).")] public float LightStagger = 0.25f;
        public float LauncherDamage = 8f;
        public float LauncherSpeed = 45f;
        [Tooltip("0 = straight forward, 1 = 45 degrees up.")] public float LauncherUpBias = 0.9f;
        public float LauncherDuration = 1.2f;

        [Header("Heavy (hold to charge)")]
        public float HeavyMaxChargeTime = 0.8f;
        public float HeavyDuration = 0.35f;
        public float HeavyActiveTime = 0.12f;
        public float HeavyMinDamage = 10f, HeavyMaxDamage = 25f;
        public float HeavyMinSpeed = 30f, HeavyMaxSpeed = 110f;
        public float HeavyLaunchDuration = 1.2f;

        [Header("Finisher (hit a launched target)")]
        public float FinisherDamage = 15f;
        public float FinisherSpeed = 150f;
        public float FinisherDuration = 1.5f;

        [Header("Dash")]
        public float DashSpeed = 70f;
        public float DashDuration = 0.22f;
        public float DashCooldown = 0.35f;

        [Header("Guard (hold)")]
        [Range(0f, 1f)] public float GuardKnockbackScale = 0.2f;
        [Range(0f, 1f)] public float GuardDamageScale = 0.25f;
        [Tooltip("Guard works when the hit comes from in front (dot product).")] public float GuardFrontDot = 0.2f;

        [Header("Feel")]
        [Tooltip("Freeze on impact for attacker and victim (s).")] public float HitStopTime = 0.08f;
        [Tooltip("Gravity on launched (ballistic) bodies, m/s².")] public float LaunchGravity = 18f;
        [Tooltip("Air drag on launched bodies (per second).")] public float LaunchDrag = 0.25f;
        [Tooltip("Velocity damping while attacking (per second).")] public float AttackDamping = 6f;

        [Header("Breaking the city")]
        [Tooltip("A launched body breaks building chunks above this speed.")] public float BreakSpeed = 20f;
        [Tooltip("A flying player breaks chunks above this speed (boost).")] public float FlyingBreakSpeed = 150f;
        [Tooltip("Fraction of speed lost per chunk broken.")] [Range(0f, 1f)] public float ChunkSpeedLoss = 0.2f;
        [Tooltip("Damage taken per chunk smashed.")] public float ChunkImpactDamage = 2f;
        [Tooltip("Hitting unbreakable walls/ground faster than this hurts.")] public float WallImpactSpeed = 30f;
        public float WallImpactDamagePerMs = 0.15f;

        class Baker : Baker<CombatConfigAuthoring>
        {
            public override void Bake(CombatConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new CombatConfig
                {
                    MaxPower = a.MaxPower, RespawnDelay = a.RespawnDelay,
                    LockRange = a.LockRange, LockBreakRange = a.LockBreakRange, LockConeDot = a.LockConeDot,
                    HitRange = a.HitRange, HitReachForward = a.HitReachForward,
                    HomingRange = a.HomingRange, HomingMaxSpeed = a.HomingMaxSpeed, StrikeDistance = a.StrikeDistance,
                    LightDuration = a.LightDuration, LightActiveTime = a.LightActiveTime, LightDamage = a.LightDamage,
                    LightKnockback = a.LightKnockback, LightStagger = a.LightStagger,
                    LauncherDamage = a.LauncherDamage, LauncherSpeed = a.LauncherSpeed, LauncherUpBias = a.LauncherUpBias,
                    LauncherDuration = a.LauncherDuration,
                    HeavyMaxChargeTime = a.HeavyMaxChargeTime, HeavyDuration = a.HeavyDuration, HeavyActiveTime = a.HeavyActiveTime,
                    HeavyMinDamage = a.HeavyMinDamage, HeavyMaxDamage = a.HeavyMaxDamage,
                    HeavyMinSpeed = a.HeavyMinSpeed, HeavyMaxSpeed = a.HeavyMaxSpeed, HeavyLaunchDuration = a.HeavyLaunchDuration,
                    FinisherDamage = a.FinisherDamage, FinisherSpeed = a.FinisherSpeed, FinisherDuration = a.FinisherDuration,
                    DashSpeed = a.DashSpeed, DashDuration = a.DashDuration, DashCooldown = a.DashCooldown,
                    GuardKnockbackScale = a.GuardKnockbackScale, GuardDamageScale = a.GuardDamageScale, GuardFrontDot = a.GuardFrontDot,
                    HitStopTime = a.HitStopTime, LaunchGravity = a.LaunchGravity, LaunchDrag = a.LaunchDrag, AttackDamping = a.AttackDamping,
                    BreakSpeed = a.BreakSpeed, FlyingBreakSpeed = a.FlyingBreakSpeed, ChunkSpeedLoss = a.ChunkSpeedLoss,
                    ChunkImpactDamage = a.ChunkImpactDamage, WallImpactSpeed = a.WallImpactSpeed, WallImpactDamagePerMs = a.WallImpactDamagePerMs,
                });
            }
        }
    }
}
