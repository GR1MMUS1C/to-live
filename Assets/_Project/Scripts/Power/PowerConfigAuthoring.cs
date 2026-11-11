using Unity.Entities;
using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// ALL power/transformation gameplay tuning: tier 2, surge fuel, the ritual, surge, burnout,
    /// beam and grab/throw. Lives on "Power Config" in the District subscene (like Combat Config).
    /// The LOOK (colours, halo) is on the "Power Look" component in Main.unity.
    /// </summary>
    [DisallowMultipleComponent]
    public class PowerConfigAuthoring : MonoBehaviour
    {
        [Header("Tier 2 (permanent baseline)")]
        [Tooltip("Lifetime fuel needed to unlock tier 2.")] public float Tier2FuelThreshold = 300f;
        public float Tier2PowerMultiplier = 1.5f, Tier2DamageMultiplier = 1.3f, Tier2KnockbackMultiplier = 1.25f, Tier2SpeedMultiplier = 1.15f;

        [Header("Surge fuel (placeholder source until crowds arrive)")]
        public float FuelPerDamageDealt = 1f;
        public float FuelPerDamageTaken = 0.6f;
        public float FuelPerChunkBroken = 2f;
        [Tooltip("Surge meter gained per fuel unit (1 = full meter).")] public float MeterPerFuel = 0.01f;

        [Header("Ritual (build-up)")]
        [Tooltip("Hold the ritual input this long to start.")] public float RitualHoldTime = 0.4f;
        public float RitualTime = 3f;
        [Tooltip("Fraction of damage taken during the ritual.")] [Range(0f, 1f)] public float RitualDamageTaken = 0.15f;
        [Tooltip("Chunks crack and break within this radius, growing with progress.")] public float RitualCrackRadius = 14f;
        public float RitualPushRadius = 14f;
        public float RitualPushSpeed = 12f;

        [Header("Eruption")]
        public float EruptionRadius = 30f;
        public float EruptionDamage = 20f;
        public float EruptionKnockback = 80f;
        public float EruptionBreakRadius = 20f;

        [Header("Surge (peak) and burnout (cost)")]
        public float SurgeTime = 20f;
        public float SurgeSpeedMultiplier = 1.6f, SurgeDamageMultiplier = 2f, SurgeKnockbackMultiplier = 1.8f, SurgeBeamMultiplier = 2f;
        [Tooltip("Damage taken is divided by this during the surge.")] public float SurgeDefense = 1.5f;
        public float BurnoutTime = 6f;
        public float BurnoutSpeedMultiplier = 0.7f, BurnoutDamageMultiplier = 0.6f;

        [Header("Beam (hold)")]
        public float BeamChargeTime = 0.35f;
        public float BeamMaxTime = 3f;
        public float BeamRange = 90f;
        public float BeamRadius = 1.2f;
        public float BeamDamagePerSecond = 20f;
        [Tooltip("Push speed applied to characters in the beam (m/s).")] public float BeamPush = 35f;
        [Tooltip("Energy (0..1) used per second of firing.")] public float BeamDrainPerSecond = 0.33f;
        public float BeamRegenPerSecond = 0.12f;
        [Tooltip("Energy needed to start a beam.")] public float BeamMinEnergy = 0.25f;

        [Header("Grab and throw")]
        public float GrabRange = 4f;
        public float MaxCarryTime = 3f;
        public float ThrowSpeed = 120f;
        public float ThrowDamage = 12f;
        public float ThrowDuration = 1.5f;
        [Tooltip("The victim can start breaking free after this many seconds.")] public float EscapeAfter = 0.8f;
        [Tooltip("Presses (light/dash) needed to break free.")] public float EscapeMashes = 6f;
        public float CarrySpeedScale = 0.75f;

        class Baker : Baker<PowerConfigAuthoring>
        {
            public override void Bake(PowerConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new PowerConfig
                {
                    Tier2FuelThreshold = a.Tier2FuelThreshold, Tier2Power = a.Tier2PowerMultiplier, Tier2Damage = a.Tier2DamageMultiplier,
                    Tier2Knockback = a.Tier2KnockbackMultiplier, Tier2Speed = a.Tier2SpeedMultiplier,
                    FuelPerDamageDealt = a.FuelPerDamageDealt, FuelPerDamageTaken = a.FuelPerDamageTaken, FuelPerChunk = a.FuelPerChunkBroken, MeterPerFuel = a.MeterPerFuel,
                    RitualHoldTime = a.RitualHoldTime, RitualTime = a.RitualTime, RitualDamageTaken = a.RitualDamageTaken,
                    RitualCrackRadius = a.RitualCrackRadius, RitualPushRadius = a.RitualPushRadius, RitualPushSpeed = a.RitualPushSpeed,
                    EruptionRadius = a.EruptionRadius, EruptionDamage = a.EruptionDamage, EruptionKnockback = a.EruptionKnockback, EruptionBreakRadius = a.EruptionBreakRadius,
                    SurgeTime = a.SurgeTime, SurgeSpeed = a.SurgeSpeedMultiplier, SurgeDamage = a.SurgeDamageMultiplier, SurgeKnockback = a.SurgeKnockbackMultiplier,
                    SurgeBeam = a.SurgeBeamMultiplier, SurgeDefense = a.SurgeDefense,
                    BurnoutTime = a.BurnoutTime, BurnoutSpeed = a.BurnoutSpeedMultiplier, BurnoutDamage = a.BurnoutDamageMultiplier,
                    BeamChargeTime = a.BeamChargeTime, BeamMaxTime = a.BeamMaxTime, BeamRange = a.BeamRange, BeamRadius = a.BeamRadius,
                    BeamDamagePerSecond = a.BeamDamagePerSecond, BeamPush = a.BeamPush, BeamDrainPerSecond = a.BeamDrainPerSecond,
                    BeamRegenPerSecond = a.BeamRegenPerSecond, BeamMinEnergy = a.BeamMinEnergy,
                    GrabRange = a.GrabRange, MaxCarryTime = a.MaxCarryTime, ThrowSpeed = a.ThrowSpeed, ThrowDamage = a.ThrowDamage,
                    ThrowDuration = a.ThrowDuration, EscapeAfter = a.EscapeAfter, EscapeMashes = a.EscapeMashes, CarrySpeedScale = a.CarrySpeedScale,
                });
            }
        }
    }
}
