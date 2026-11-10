using ToLive.Player;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Net
{
    /// <summary>RPC the client sends once connected: "I'm ready, start streaming and give me a player".</summary>
    public struct GoInGameRequest : IRpcCommand { }

    /// <summary>
    /// Client side: as soon as the connection has a NetworkId (the handshake finished) and the
    /// gameplay subscene has loaded (PlayerSpawner exists), mark the connection in-game and tell the server.
    /// Pattern taken from the Netcode for Entities "Networked cube" sample.
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct GoInGameClientSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlayerSpawner>();
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkId>().WithNone<NetworkStreamInGame>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess().WithNone<NetworkStreamInGame>())
            {
                ecb.AddComponent<NetworkStreamInGame>(entity);
                var req = ecb.CreateEntity();
                ecb.AddComponent<GoInGameRequest>(req);
                ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
            }
            ecb.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// Server side: on GoInGameRequest, spawn that player's ghost (like Unreal's GameMode
    /// SpawnDefaultPawnFor + Possess). The ghost is owned by the connection's NetworkId, which makes
    /// it predicted on the owner's machine and interpolated on everyone else's.
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct GoInGameServerSystem : ISystem
    {
        ComponentLookup<NetworkId> m_NetworkIds;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlayerSpawner>();
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<GoInGameRequest, ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
            m_NetworkIds = state.GetComponentLookup<NetworkId>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            var spawnerEntity = SystemAPI.GetSingletonEntity<PlayerSpawner>();
            var prefab = SystemAPI.GetComponent<PlayerSpawner>(spawnerEntity).PlayerPrefab;
            var spawnPoints = SystemAPI.GetBuffer<PlayerSpawnPoint>(spawnerEntity);
            m_NetworkIds.Update(ref state);

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (request, requestEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>().WithAll<GoInGameRequest>().WithEntityAccess())
            {
                var connection = request.ValueRO.SourceConnection;
                ecb.AddComponent<NetworkStreamInGame>(connection);
                var networkId = m_NetworkIds[connection].Value;

                var spawn = spawnPoints.Length > 0 ? spawnPoints[(networkId - 1) % spawnPoints.Length].Position : new float3(0, 60, 0);
                // Face the middle of the district.
                var toCenter = new float3(-spawn.x, 0, -spawn.z);
                var yawDeg = math.lengthsq(toCenter) > 0.01f ? math.degrees(math.atan2(toCenter.x, toCenter.z)) : 0f;

                var player = ecb.Instantiate(prefab);
                ecb.SetComponent(player, new GhostOwner { NetworkId = networkId });
                ecb.SetComponent(player, LocalTransform.FromPositionRotation(spawn, quaternion.EulerZXY(0, math.radians(yawDeg), 0)));
                ecb.SetComponent(player, new FlightState { Yaw = yawDeg, Gear = FlightGear.Hover });
                var maxPower = SystemAPI.TryGetSingleton<ToLive.Combat.CombatConfig>(out var combatCfg) ? combatCfg.MaxPower : 100f;
                ecb.SetComponent(player, new ToLive.Combat.CombatState { Power = maxPower });
                // Destroy the player automatically when this connection drops.
                ecb.AppendToBuffer(connection, new LinkedEntityGroup { Value = player });
                ecb.DestroyEntity(requestEntity);

                Debug.Log($"[ToLive] Player {networkId} joined, spawning at {spawn}");
            }
            ecb.Playback(state.EntityManager);
        }
    }
}
