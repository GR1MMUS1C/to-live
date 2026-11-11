using Unity.Entities;
using Unity.Mathematics;

namespace ToLive.Power
{
    /// <summary>Baked copy of <see cref="PowerConfigAuthoring"/>. Seconds, metres, m/s.</summary>
    public struct PowerConfig : IComponentData
    {
        // Tier 2
        public float Tier2FuelThreshold, Tier2Power, Tier2Damage, Tier2Knockback, Tier2Speed;
        // Fuel sources (meter fraction per unit)
        public float FuelPerDamageDealt, FuelPerDamageTaken, FuelPerChunk, MeterPerFuel;
        // Ritual
        public float RitualHoldTime, RitualTime, RitualDamageTaken, RitualCrackRadius, RitualPushRadius, RitualPushSpeed;
        public float EruptionRadius, EruptionDamage, EruptionKnockback, EruptionBreakRadius;
        // Surge / burnout
        public float SurgeTime, SurgeSpeed, SurgeDamage, SurgeKnockback, SurgeBeam, SurgeDefense;
        public float BurnoutTime, BurnoutSpeed, BurnoutDamage;
        // Beam
        public float BeamChargeTime, BeamMaxTime, BeamRange, BeamRadius, BeamDamagePerSecond, BeamPush, BeamDrainPerSecond, BeamRegenPerSecond, BeamMinEnergy;
        // Grab / throw
        public float GrabRange, MaxCarryTime, ThrowSpeed, ThrowDamage, ThrowDuration, EscapeAfter, EscapeMashes, CarrySpeedScale;

        public PowerMods Mods(in PowerState p)
        {
            var m = PowerMods.One;
            if (p.Tier >= 2)
            {
                m.Damage *= Tier2Damage; m.Knockback *= Tier2Knockback; m.Speed *= Tier2Speed;
            }
            if (p.Phase == SurgePhase.Surge)
            {
                m.Speed *= SurgeSpeed; m.Damage *= SurgeDamage; m.Knockback *= SurgeKnockback; m.Beam *= SurgeBeam; m.Defense *= SurgeDefense;
            }
            else if (p.Phase == SurgePhase.Burnout)
            {
                m.Speed *= BurnoutSpeed; m.Damage *= BurnoutDamage; m.Knockback *= BurnoutDamage;
            }
            else if (p.Phase == SurgePhase.Ritual)
            {
                m.Defense = math.max(1f, 1f / math.max(0.01f, RitualDamageTaken));
            }
            return m;
        }

        public float MaxPower(float basePower, in PowerState p) => p.Tier >= 2 ? basePower * Tier2Power : basePower;
    }
}
