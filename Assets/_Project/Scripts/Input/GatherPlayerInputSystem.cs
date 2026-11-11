using ToLive.CameraRig;
using ToLive.Player;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Input
{
    /// <summary>
    /// Client-only: copies this frame's device input + camera direction into the local player's
    /// PlayerInput component. Netcode then stamps it with the current tick, sends it to the server
    /// and keeps it in the input buffer for re-prediction (the "Support Auto Command Target" path).
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class GatherPlayerInputSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
        }

        protected override void OnUpdate()
        {
            var blocked = GameplayInputGate.Blocked; // e.g. menu open
            foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>().WithAll<GhostOwnerIsLocal>())
            {
                input.ValueRW = new PlayerInput
                {
                    Move = blocked ? float2.zero : (float2)InputReader.Move,
                    Vertical = blocked ? 0f : InputReader.Vertical,
                    Thrust = blocked ? 0f : InputReader.Thrust,
                    Boost = (byte)(!blocked && InputReader.Boost ? 1 : 0),
                    LookYaw = FlightCameraRig.Yaw,
                    LookPitch = FlightCameraRig.Pitch,
                    HeavyHeld = (byte)(!blocked && InputReader.HeavyHeld ? 1 : 0),
                    GuardHeld = (byte)(!blocked && InputReader.GuardHeld ? 1 : 0),
                    BeamHeld = (byte)(!blocked && InputReader.BeamHeld ? 1 : 0),
                    RitualHeld = (byte)(!blocked && InputReader.RitualHeld ? 1 : 0),
                };
                if (!blocked)
                {
                    if (InputReader.LightPresses > 0) input.ValueRW.Light.Set();
                    if (InputReader.DashPresses > 0) input.ValueRW.Dash.Set();
                    if (InputReader.LockOnPresses > 0) input.ValueRW.LockOn.Set();
                    if (InputReader.GrabPresses > 0) input.ValueRW.Grab.Set();
                }
            }
            // presses have been handed to Netcode (InputEvent) - consume them
            InputReader.LightPresses = InputReader.DashPresses = InputReader.LockOnPresses = InputReader.GrabPresses = 0;
        }
    }

    /// <summary>Set by the UI so gameplay ignores input while a menu is open.</summary>
    public static class GameplayInputGate
    {
        public static bool Blocked;
    }
}
