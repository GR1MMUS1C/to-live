using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Player
{
    public static class FlightGear
    {
        public const byte Hover = 0;
        public const byte Cruise = 1;
        public const byte Boost = 2;

        public static string Name(byte gear) => gear switch { Cruise => "CRUISE", Boost => "BOOST", _ => "HOVER" };
    }

    /// <summary>
    /// The player's input for one simulation tick. IInputComponentData is Netcode's version of
    /// Unreal's "server RPC with client inputs": it's sent to the server every tick and buffered,
    /// so the server (and the client during re-prediction) replay exactly the same inputs.
    /// The camera look direction is part of the input so movement "relative to the camera" is
    /// computed identically on client and server.
    /// </summary>
    public struct PlayerInput : IInputComponentData
    {
        public float2 Move;      // left stick / WASD, x = right, y = forward
        public float Vertical;   // +1 ascend, -1 descend
        public float Thrust;     // 0..1 (right trigger / Left Shift)
        public byte Boost;       // 1 while boost held
        public float LookYaw;    // camera yaw in degrees
        public float LookPitch;  // camera pitch in degrees (positive = looking down)
    }

    /// <summary>
    /// Predicted flight state. Every field the simulation reads from the previous tick must be a
    /// [GhostField] so the client can roll back to the server's value and re-simulate
    /// (comparable to Unreal's CharacterMovement saved moves + replicated movement state).
    /// </summary>
    public struct FlightState : IComponentData
    {
        [GhostField(Quantization = 100)] public float3 Velocity; // m/s, world space
        [GhostField(Quantization = 100)] public float Yaw;       // facing, degrees
        [GhostField(Quantization = 100)] public float Pitch;     // facing, degrees
        [GhostField(Quantization = 100)] public float Bank;      // roll for banking, degrees
        [GhostField] public byte Gear;
        /// <summary>1 while faster than the speed of sound. Hook for the future shockwave feature.</summary>
        [GhostField] public byte Supersonic;
    }

    /// <summary>Client-only bookkeeping for the supersonic placeholder effect (not networked).</summary>
    public struct SupersonicFxState : IComponentData
    {
        public byte WasSupersonic;
    }

    /// <summary>Points at the child entity that renders the body, so we can tint it per player.</summary>
    public struct PlayerVisual : IComponentData
    {
        public Entity Body;
        public Entity Pivot;
        public byte ColorApplied;
        public float Lean; // current cosmetic lean, degrees
    }

    /// <summary>Singleton in the gameplay subscene: which prefab to spawn for each player.</summary>
    public struct PlayerSpawner : IComponentData
    {
        public Entity PlayerPrefab;
    }

    public struct PlayerSpawnPoint : IBufferElementData
    {
        public float3 Position;
    }
}
