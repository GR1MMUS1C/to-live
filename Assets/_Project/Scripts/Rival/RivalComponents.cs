using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Rival
{
    public static class RivalAct
    {
        public const byte Approach = 0;   // fly in
        public const byte Circle = 1;     // hold range, strafe, wait for an opening
        public const byte Chain = 2;      // dash in + light chain
        public const byte Heavy = 3;      // charged heavy (punish after a successful guard)
        public const byte Guard = 4;      // reactive guard
        public const byte Finisher = 5;   // chase a launched target and drive it (into the building)
        public const byte Beam = 6;       // beam at range
        public const byte Carry = 7;      // grabbed: carry, then throw (into the building)
        public const byte Disengage = 8;  // low power: back off
        public const byte Ritual = 9;     // surge ritual

        public static string Name(byte a) => a switch
        {
            Circle => "circle", Chain => "chain", Heavy => "heavy", Guard => "guard", Finisher => "finisher",
            Beam => "beam", Carry => "carry", Disengage => "disengage", Ritual => "ritual", _ => "approach",
        };
    }

    /// <summary>One difficulty preset (all rival behaviour weights).</summary>
    public struct RivalPreset
    {
        public float ReactionTime;     // s before it acts on what it sees
        public float GuardChance;      // chance to guard an incoming attack it noticed
        public float Aggression;       // chance per decision to go in for a chain
        public float ThinkInterval;    // s between decisions
        public float BeamChance;
        public float FinisherChance;
        public float GrabChance;
        public float AimNoise;         // degrees of aim error
    }

    /// <summary>Baked from <c>RivalConfigAuthoring</c> ("Rival Config" in the District subscene).</summary>
    public struct RivalConfig : IComponentData
    {
        public RivalPreset Easy, Normal, Hard;
        public float CircleRange, DashInRange, EngageRange;
        public float DisengagePower, DisengageTime, RitualPowerThreshold;
        public float BeamMinRange, BeamMaxRange, BeamHoldTime, BeamCooldown;
        public float CarryTime, HeavyChargeTime;
        public float RespawnDelay, Alignment;
        public int SpawnPoint;

        public RivalPreset Preset(byte difficulty) => difficulty switch { 0 => Easy, 2 => Hard, _ => Normal };
    }

    /// <summary>
    /// SERVER ONLY: the rival's AI memory. It lives on every character (same prefab as players) but
    /// is only used when <c>CharacterKind == Rival</c>. Like an AIController's blackboard.
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct RivalBrain : IComponentData
    {
        public byte Act;
        public uint ActTick;
        public uint NextThinkTick;
        public Entity Target;
        public float StrafeSign;
        public uint Seed;
        // reactive guard
        public uint SeenAttackTick;     // target's ActionStartTick we already judged
        public uint GuardFromTick;      // guard starts after the reaction delay
        public uint GuardUntilTick;
        public byte GuardSucceeded;     // we blocked something: punish with a heavy
        // chain / presses
        public uint NextPressTick;
        public byte Presses;
        public uint LockPressTick;
        public float2 AimNoise;         // degrees (yaw, pitch), re-rolled on each decision
        public uint NextBeamTick;
        public uint GrabPressTick;
        public byte ChainGrab;          // this chain will try a grab when the target is staggered
    }
}
