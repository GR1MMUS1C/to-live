using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Player;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>
    /// Server only: records each target's position every tick into a small ring buffer, so hit
    /// checks can be rewound to what the attacker saw.
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(FlightSystem))]
    public partial struct PositionHistorySystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state) => state.RequireForUpdate<NetworkTime>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            foreach (var (historyRef, xf) in SystemAPI.Query<DynamicBuffer<PositionHistory>, RefRO<LocalTransform>>())
            {
                var history = historyRef;
                if (history.Length != 32) history.ResizeUninitialized(32);
                history[(int)(tick % 32)] = new PositionHistory { Tick = tick, Position = xf.ValueRO.Position };
            }
        }
    }

    /// <summary>
    /// Server only: respawns knocked-out characters after the delay (placeholder for the later
    /// "drained and expelled" rule). Players go back to a spawn point, the dummy to its home.
    /// Also spawns the training dummy once the district is loaded.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CombatServerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
            state.RequireForUpdate<CombatConfig>();
            state.RequireForUpdate<PlayerSpawner>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var cfg = SystemAPI.GetSingleton<CombatConfig>();
            var spawnerEntity = SystemAPI.GetSingletonEntity<PlayerSpawner>();
            var spawner = SystemAPI.GetComponent<PlayerSpawner>(spawnerEntity);
            var points = SystemAPI.GetBuffer<PlayerSpawnPoint>(spawnerEntity).ToNativeArray(Allocator.Temp);

            // Spawn the training dummy once.
            using (var dummies = SystemAPI.QueryBuilder().WithAll<TrainingDummy>().Build().ToEntityArray(Allocator.Temp))
            {
                if (dummies.Length == 0 && spawner.DummyPrefab != Entity.Null)
                {
                    var d = state.EntityManager.Instantiate(spawner.DummyPrefab);
                    state.EntityManager.SetComponentData(d, LocalTransform.FromPositionRotation(spawner.DummyHome, quaternion.RotateY(math.radians(spawner.DummyHomeYaw))));
                    state.EntityManager.SetComponentData(d, new TrainingDummy { Home = spawner.DummyHome, HomeYaw = spawner.DummyHomeYaw });
                    state.EntityManager.SetComponentData(d, new FlightState { Yaw = spawner.DummyHomeYaw });
                    state.EntityManager.SetComponentData(d, new CombatState { Power = cfg.MaxPower });
                    Debug.Log($"[ToLive] Training dummy spawned at {spawner.DummyHome}");
                }
            }

            // Respawn knocked-out characters.
            foreach (var (combat, flight, xf, entity) in SystemAPI.Query<RefRW<CombatState>, RefRW<FlightState>, RefRW<LocalTransform>>().WithEntityAccess())
            {
                var c = combat.ValueRO;
                if (c.Action != CombatAction.KnockedOut || CombatSystem.Before(tick, c.ActionEndTick)) continue;

                float3 pos; float yaw;
                if (SystemAPI.HasComponent<TrainingDummy>(entity))
                {
                    var home = SystemAPI.GetComponent<TrainingDummy>(entity);
                    pos = home.Home; yaw = home.HomeYaw;
                }
                else
                {
                    var id = SystemAPI.HasComponent<GhostOwner>(entity) ? SystemAPI.GetComponent<GhostOwner>(entity).NetworkId : 1;
                    pos = points.Length > 0 ? points[(math.max(id, 1) - 1) % points.Length].Position : new float3(0, 60, 0);
                    yaw = math.degrees(math.atan2(-pos.x, -pos.z));
                }
                var maxPower = cfg.MaxPower;
                if (SystemAPI.TryGetSingleton<ToLive.Power.PowerConfig>(out var pcfg) && SystemAPI.HasComponent<ToLive.Power.PowerState>(entity))
                    maxPower = pcfg.MaxPower(cfg.MaxPower, SystemAPI.GetComponent<ToLive.Power.PowerState>(entity));
                ResetCharacter(ref combat.ValueRW, ref flight.ValueRW, ref xf.ValueRW, pos, yaw, maxPower);
            }
        }

        public static void ResetCharacter(ref CombatState c, ref FlightState f, ref LocalTransform xf, float3 pos, float yaw, float power)
        {
            c = new CombatState { Power = power, LockTarget = c.LockTarget, DashReadyTick = c.DashReadyTick };
            f = new FlightState { Yaw = yaw };
            xf = LocalTransform.FromPositionRotation(pos, quaternion.RotateY(math.radians(yaw)));
        }
    }

    /// <summary>Server only: handles debug RPCs (reset dummy + rebuild building, destroy building).</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct DebugCommandServerSystem : ISystem
    {
        /// <summary>Debug: fill the meter, toggle tier, slide alignment (sender's character) or make the dummy surge.</summary>
        void ApplyPowerDebug(ref SystemState state, byte command, int senderId)
        {
            var hasCfg = SystemAPI.TryGetSingleton<ToLive.Power.PowerConfig>(out var cfg);
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var senderAlignment = 0f;
            byte senderTier = 1;
            foreach (var (owner, power) in SystemAPI.Query<RefRO<GhostOwner>, RefRW<ToLive.Power.PowerState>>())
            {
                if (owner.ValueRO.NetworkId != senderId) continue;
                var p = power.ValueRO;
                switch (command)
                {
                    case DebugCommandRequest.FillSurgeMeter: p.Meter = 1f; if (p.Phase == ToLive.Power.SurgePhase.Burnout || p.Phase == ToLive.Power.SurgePhase.Surge) p.Phase = ToLive.Power.SurgePhase.None; break;
                    case DebugCommandRequest.ToggleTier: p.Tier = (byte)(p.Tier >= 2 ? 1 : 2); break;
                    case DebugCommandRequest.AlignmentDown: p.Alignment = math.max(-1f, p.Alignment - 0.25f); break;
                    case DebugCommandRequest.AlignmentUp: p.Alignment = math.min(1f, p.Alignment + 0.25f); break;
                }
                power.ValueRW = p;
                senderAlignment = p.Alignment;
                senderTier = p.Tier;
            }

            if (command == DebugCommandRequest.BenchRitualSetup && SystemAPI.TryGetSingleton<BuildingGraph>(out var graph))
            {
                foreach (var (owner, power, xf, flight) in SystemAPI.Query<RefRO<GhostOwner>, RefRW<ToLive.Power.PowerState>, RefRW<LocalTransform>, RefRW<FlightState>>())
                {
                    if (owner.ValueRO.NetworkId != senderId) continue;
                    var pos = new float3(graph.Center.x, 3f, graph.Center.z + 15f);
                    xf.ValueRW = LocalTransform.FromPositionRotation(pos, quaternion.RotateY(math.radians(180f)));
                    flight.ValueRW = new FlightState { Yaw = 180f };
                    var p = power.ValueRO;
                    p.Meter = 1f; p.Phase = ToLive.Power.SurgePhase.None;
                    power.ValueRW = p;
                }
            }

            if (command == DebugCommandRequest.DummyRitual && hasCfg)
            {
                foreach (var (power, _) in SystemAPI.Query<RefRW<ToLive.Power.PowerState>, RefRO<TrainingDummy>>())
                {
                    var p = power.ValueRO;
                    if (p.Phase != ToLive.Power.SurgePhase.None) continue;
                    p.Alignment = senderAlignment;
                    p.Tier = senderTier;
                    p.Meter = 1f;
                    p.Phase = ToLive.Power.SurgePhase.Ritual;
                    p.PhaseStartTick = tick;
                    p.PhaseEndTick = tick + CombatSystem.Ticks(cfg.RitualTime, 60);
                    p.Erupted = 0;
                    power.ValueRW = p;
                }
            }
        }

        /// <summary>NetworkId of the client world running in this process (the host's own player), or -1.</summary>
        static int LocalClientNetworkId()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated) return -1;
            using var q = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
            return q.CalculateEntityCount() == 1 ? q.GetSingleton<NetworkId>().Value : -1;
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var maxPower = SystemAPI.TryGetSingleton<CombatConfig>(out var cfg) ? cfg.MaxPower : 100f;
            var hostId = LocalClientNetworkId();
            foreach (var (cmd, rpc, rpcEntity) in SystemAPI.Query<RefRO<DebugCommandRequest>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                var command = cmd.ValueRO.Command;
                ecb.DestroyEntity(rpcEntity);

                // Only the hosting player (the client running in this same process) may use debug
                // commands. The sender is identified by the RPC's SourceConnection -> NetworkId.
                var source = rpc.ValueRO.SourceConnection;
                var senderId = SystemAPI.HasComponent<NetworkId>(source) ? SystemAPI.GetComponent<NetworkId>(source).Value : -1;
                if (hostId < 0 || senderId != hostId)
                {
                    Debug.LogWarning($"[ToLive] Ignored debug command {command} from connection {senderId} (only the host may use debug commands)");
                    continue;
                }

                if (command == DebugCommandRequest.ResetDummyAndBuilding)
                {
                    foreach (var (dummy, combat, flight, xf) in SystemAPI.Query<RefRO<TrainingDummy>, RefRW<CombatState>, RefRW<FlightState>, RefRW<LocalTransform>>())
                                CombatServerSystem.ResetCharacter(ref combat.ValueRW, ref flight.ValueRW, ref xf.ValueRW, dummy.ValueRO.Home, dummy.ValueRO.HomeYaw, maxPower);
                }

                if (command == DebugCommandRequest.ToggleDummyGuard)
                    foreach (var dummy in SystemAPI.Query<RefRW<TrainingDummy>>())
                        dummy.ValueRW.Guard = (byte)(dummy.ValueRO.Guard == 0 ? 1 : 0);

                if (command >= DebugCommandRequest.FillSurgeMeter && command <= DebugCommandRequest.BenchRitualSetup)
                    ApplyPowerDebug(ref state, command, senderId);

                if (SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graph))
                {
                    var bc = SystemAPI.GetComponent<BuildingCommand>(graph);
                    if (command == DebugCommandRequest.ResetDummyAndBuilding) bc.Rebuild = 1;
                    if (command == DebugCommandRequest.DestroyBuilding) bc.DestroyAll = 1;
                    SystemAPI.SetComponent(graph, bc);
                }
                Debug.Log($"[ToLive] Debug command {command} received");
            }
            ecb.Playback(state.EntityManager);
        }
    }
}
