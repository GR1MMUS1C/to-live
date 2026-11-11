using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Combat
{
    public static class CombatAction
    {
        public const byte None = 0;
        public const byte Light = 1;        // light chain step 1..3 (ComboStep)
        public const byte HeavyCharge = 2;  // holding heavy
        public const byte HeavyStrike = 3;  // heavy released
        public const byte Dash = 4;
        public const byte Launched = 5;     // knocked back / launched: reduced control
        public const byte KnockedOut = 6;
        public const byte Grabbed = 7;      // held by another character (can mash to break free)

        public static string Name(byte a) => a switch
        {
            Light => "LIGHT", HeavyCharge => "CHARGING", HeavyStrike => "HEAVY", Dash => "DASH",
            Launched => "LAUNCHED", KnockedOut => "KNOCKED OUT", Grabbed => "GRABBED", _ => ""
        };
    }

    /// <summary>
    /// Predicted combat state machine of a character (players and the training dummy).
    /// Like Unreal's GAS ability state + attributes, but as plain replicated data: every field
    /// the combat systems read from the previous tick is a [GhostField] so client prediction can
    /// roll back and re-simulate. Timers are stored as tick numbers, so re-simulating a tick
    /// always gives the same answer.
    /// </summary>
    public struct CombatState : IComponentData
    {
        [GhostField(Quantization = 10)] public float Power;      // health ("power from people")
        [GhostField] public byte Action;
        [GhostField] public byte ComboStep;                      // 1..3 during the light chain
        [GhostField] public byte QueuedLight;                    // next light pressed during this one
        [GhostField] public byte HitDone;                        // current attack already resolved
        [GhostField] public byte Guarding;
        [GhostField] public byte Ballistic;                      // launched with gravity (launcher) vs straight (finisher)
        [GhostField] public uint ActionStartTick;
        [GhostField] public uint ActionEndTick;                  // launched / KO / dash end
        [GhostField] public uint HitStopUntilTick;
        [GhostField] public uint DashReadyTick;
        [GhostField(Quantization = 100)] public float Charge;    // heavy charge 0..1
        [GhostField] public Entity LockTarget;                   // soft lock-on (ghost entity reference)
        [GhostField] public uint LastHitTick;                    // when we were last hit (drives hit FX)
        [GhostField(Quantization = 10)] public float LastHitStrength;
    }

    /// <summary>Tag: this entity can be locked on to and hit.</summary>
    public struct CombatTarget : IComponentData { }

    /// <summary>The server-simulated training dummy and where it lives.</summary>
    public struct TrainingDummy : IComponentData
    {
        public float3 Home;
        public float HomeYaw;
        public byte Guard; // debug: dummy holds guard (toggle with F6)
    }

    /// <summary>
    /// Server-only position history (ring buffer, one entry per tick) used to rewind targets to
    /// what the attacker saw on their screen ("server-side rewind" / lag compensation).
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    [InternalBufferCapacity(0)]
    public struct PositionHistory : IBufferElementData
    {
        public uint Tick;
        public float3 Position;
    }

    /// <summary>Debug commands sent from a client to the server.</summary>
    public struct DebugCommandRequest : IRpcCommand
    {
        public byte Command;
        public byte Arg;                        // optional argument (e.g. unit count for BenchUnits)
        public const byte ResetDummyAndBuilding = 1;
        public const byte DestroyBuilding = 2;
        public const byte ToggleDummyGuard = 3;
        public const byte FillSurgeMeter = 4;   // sender's character
        public const byte ToggleTier = 5;
        public const byte AlignmentDown = 6;    // toward trust
        public const byte AlignmentUp = 7;      // toward dominance
        public const byte DummyRitual = 8;      // the dummy performs the ritual (copies sender's alignment)
        public const byte BenchRitualSetup = 9; // benchmark: put the sender next to the building with a full meter
        // Milestone 3B: heat, response force, rival
        public const byte HeatUp = 10;
        public const byte HeatDown = 11;
        public const byte ClearUnits = 12;
        public const byte SpawnEachUnit = 13;
        public const byte RivalToggle = 14;
        public const byte RivalDifficulty = 15;
        public const byte BenchUnits = 16;      // benchmark: spawn Arg response units near the sender and blame the humans
    }
}
