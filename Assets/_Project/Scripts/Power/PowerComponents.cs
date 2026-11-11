using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Power
{
    public static class SurgePhase
    {
        public const byte None = 0;
        public const byte Ritual = 1;   // build-up: locked in place, the world reacts
        public const byte Surge = 2;    // peak: big multipliers, meter drains
        public const byte Burnout = 3;  // cost: weaker, can't re-surge

        public static string Name(byte p) => p switch { Ritual => "RITUAL", Surge => "SURGE", Burnout => "BURNOUT", _ => "" };
    }

    /// <summary>
    /// Per-character power state: tier (permanent baseline), alignment (trust ↔ dominance, drives the
    /// look), the surge meter and the surge phase, plus beam energy. Predicted for the owner so
    /// starting the ritual or the beam feels instant; the server is authoritative for everything
    /// (fuel, phase, hits). Unreal analogy: GAS attributes + an ability state, replicated.
    /// </summary>
    public struct PowerState : IComponentData
    {
        [GhostField] public byte Tier;                              // 1 or 2
        [GhostField(Quantization = 100)] public float Alignment;    // -1 trust .. +1 dominance
        [GhostField(Quantization = 1000)] public float Meter;       // surge meter 0..1
        [GhostField(Quantization = 10)] public float Fuel;          // lifetime fuel (tier unlock)
        [GhostField] public byte Phase;
        [GhostField] public uint PhaseStartTick;
        [GhostField] public uint PhaseEndTick;
        [GhostField] public byte RitualHoldTicks;                   // how long the ritual input has been held
        [GhostField(Quantization = 100)] public float BeamEnergy;   // 0..1
        [GhostField] public byte Erupted;                           // eruption already happened this ritual
        [GhostField] public uint SuppressedUntilTick;               // anti-god beam: no surge meter gain until this tick
    }

    /// <summary>Beam and grab state (predicted, like CombatState).</summary>
    public struct MoveState : IComponentData
    {
        [GhostField] public byte Beam;          // 0 off, 1 charging, 2 firing
        [GhostField] public uint BeamStartTick;
        [GhostField] public Entity Carrying;    // grabbed victim (holder side)
        [GhostField] public Entity GrabbedBy;   // holder (victim side)
        [GhostField] public uint GrabStartTick;
        [GhostField] public byte Mash;          // victim's break-free presses
        [GhostField] public byte Throw;         // holder asked to throw (server launches the victim)
    }

    /// <summary>
    /// THE SURGE FUEL SOURCE (placeholder, milestone 4 replaces the producers).
    /// Anything that should fill a character's surge meter appends one of these to the
    /// <see cref="SurgeFuelQueue"/> singleton on the server; <c>SurgeFuelSystem</c> turns it into
    /// meter + fuel. Today: damage dealt, damage taken, chunks broken. Later: the crowd.
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct SurgeFuelEvent : IBufferElementData
    {
        public Entity Target;
        public float Amount;   // in "fuel units" (see PowerConfig)
    }

    /// <summary>Tag on the singleton entity holding the <see cref="SurgeFuelEvent"/> buffer (server).</summary>
    public struct SurgeFuelQueue : IComponentData { }

    /// <summary>Server-only: who last hit this body, so chunks it smashes credit the attacker.</summary>
    public struct LastAttacker : IComponentData
    {
        public Entity Value;
    }

    /// <summary>Multipliers for one character right now (tier × phase).</summary>
    public struct PowerMods
    {
        public float Speed, Damage, Knockback, Beam, Defense;
        public static PowerMods One => new PowerMods { Speed = 1, Damage = 1, Knockback = 1, Beam = 1, Defense = 1 };
    }
}
