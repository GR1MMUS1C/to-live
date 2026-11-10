using System.Text;
using ToLive.CameraRig;
using ToLive.Input;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Developer helper for automated play-mode checks (used from the Unity CLI with `script eval`).
    /// Not used by the game. It can fake gameplay input and report every player's state per world.
    /// </summary>
    public static class DevProbe
    {
        /// <summary>One line per player per world: id, position, gear, speed, supersonic.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<GhostOwner>());
                using var entities = q.ToEntityArray(Allocator.Temp);
                using var conns = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                sb.Append($"{world.Name}: connections={conns.CalculateEntityCount()} players={entities.Length}");
                if (world.IsClient())
                {
                    using var ack = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>());
                    if (ack.CalculateEntityCount() == 1) sb.Append($" rtt={ack.GetSingleton<NetworkSnapshotAck>().EstimatedRTT:0}ms");
                }
                sb.AppendLine();
                foreach (var e in entities)
                {
                    var f = em.GetComponentData<FlightState>(e);
                    var t = em.GetComponentData<LocalTransform>(e);
                    var owner = em.GetComponentData<GhostOwner>(e);
                    var local = em.HasComponent<GhostOwnerIsLocal>(e) && em.IsComponentEnabled<GhostOwnerIsLocal>(e);
                    var predicted = em.HasComponent<PredictedGhost>(e);
                    sb.AppendLine($"  player {owner.NetworkId}{(local ? " (local)" : "")}{(predicted ? " predicted" : " interpolated")} pos={Fmt(t.Position)} gear={FlightGear.Name(f.Gear)} speed={math.length(f.Velocity):0.0} yaw={f.Yaw:0} pitch={f.Pitch:0} bank={f.Bank:0} supersonic={f.Supersonic}");
                }
            }
            sb.AppendLine($"camera: yaw={FlightCameraRig.Yaw:0} pitch={FlightCameraRig.Pitch:0} hasLocal={FlightCameraRig.HasLocalPlayer} speed={FlightCameraRig.LocalSpeed:0.0}");
            var cam = Camera.main;
            if (cam != null) sb.AppendLine($"main camera pos={cam.transform.position} fov={cam.fieldOfView:0.0}");
            return sb.ToString();
        }

        /// <summary>Overrides device input until <see cref="ReleaseInput"/> is called.</summary>
        public static string FakeInput(float moveX, float moveY, float vertical, float thrust, bool boost, float yaw, float pitch)
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = false; // stop it overwriting the statics
            InputReader.Move = new Vector2(moveX, moveY);
            InputReader.Vertical = vertical;
            InputReader.Thrust = thrust;
            InputReader.Boost = boost;
            InputReader.LookStick = Vector2.zero;
            InputReader.LookDelta = Vector2.zero;
            FlightCameraRig.Yaw = yaw;
            FlightCameraRig.Pitch = pitch;
            GameplayInputGate.Blocked = false;
            return "input faked";
        }

        public static string ReleaseInput()
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = true;
            return "input released";
        }

        /// <summary>
        /// Adds a second, full client world in this same process and connects it to the local host,
        /// so replication can be checked without a second Editor. (Both clients read the same
        /// keyboard/gamepad input, so this is only for automated checks.)
        /// </summary>
        public static string AddSecondClient()
        {
            var ui = Object.FindAnyObjectByType<ToLive.UI.GameUI>();
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld2");
            Unity.Scenes.SceneSystem.LoadSceneAsync(client.Unmanaged, ui.GameplayScene.SceneGUID);
            var connect = client.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
            client.EntityManager.SetComponentData(connect, new NetworkStreamRequestConnect
            {
                Endpoint = Unity.Networking.Transport.NetworkEndpoint.LoopbackIpv4.WithPort(ToLive.Net.NetSession.DefaultPort)
            });
            return "second client connecting";
        }

        static string Fmt(float3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
    }
}
