using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Rendering;
using UnityEngine;

namespace ToLive.Destruction
{
    /// <summary>
    /// SERVER ONLY. Turns this tick's break requests (and debug commands) into the replicated
    /// <see cref="BuildingState"/> mask, then runs the structural support check: any chunk that no
    /// longer has a path of intact chunks down to the ground collapses too. Because only the server
    /// decides, every client ends up with exactly the same set of broken chunks.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial struct BreakSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BuildingGraph>();
            state.RequireForUpdate<BuildingState>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
            var graph = SystemAPI.GetComponent<BuildingGraph>(graphEntity);
            var requests = SystemAPI.GetBuffer<BreakRequest>(graphEntity);
            var command = SystemAPI.GetComponent<BuildingCommand>(graphEntity);
            var bsRef = SystemAPI.GetSingletonRW<BuildingState>();
            var bs = bsRef.ValueRO;
            var net = SystemAPI.GetSingleton<NetworkTime>();
            var tick = net.ServerTick.IsValid ? net.ServerTick.TickIndexForValidTick : 0u;
            var count = Unity.Mathematics.math.min(graph.ChunkCount, BuildingState.MaxChunks);
            var changed = false;

            if (command.Rebuild != 0)
            {
                bs.Broken0 = bs.Broken1 = bs.Broken2 = bs.Broken3 = 0;
                bs.Generation++;
                requests.Clear();
                changed = true;
                Debug.Log("[ToLive] Building rebuilt");
            }
            if (command.DestroyAll != 0)
            {
                for (var i = 0; i < count; i++) bs.SetBroken(i);
                bs.LastImpactTick = tick;
                bs.LastImpactVelocity = default;
                changed = true;
                Debug.Log("[ToLive] Stress: whole building destroyed");
            }
            if (command.Rebuild != 0 || command.DestroyAll != 0)
                SystemAPI.SetComponent(graphEntity, new BuildingCommand());

            foreach (var r in requests)
            {
                if (r.Chunk < 0 || r.Chunk >= count || bs.IsBroken(r.Chunk)) continue;
                bs.SetBroken(r.Chunk);
                ToLive.Power.SurgeFuel.CreditChunk(ref state, r.Instigator);
                bs.LastImpactTick = tick;
                bs.LastImpactVelocity = r.Velocity;
                changed = true;
            }
            requests.Clear();

            if (changed && command.Rebuild == 0)
                CollapseUnsupported(ref bs, SystemAPI.GetBuffer<ChunkNode>(graphEntity), SystemAPI.GetBuffer<ChunkNeighbor>(graphEntity), count);

            if (changed) bsRef.ValueRW = bs;
        }

        /// <summary>Flood-fill from grounded intact chunks; everything not reached falls.</summary>
        static void CollapseUnsupported(ref BuildingState bs, DynamicBuffer<ChunkNode> nodes, DynamicBuffer<ChunkNeighbor> neighbors, int count)
        {
            var supported = new NativeArray<bool>(count, Allocator.Temp);
            var queue = new NativeQueue<int>(Allocator.Temp);
            for (var i = 0; i < count; i++)
            {
                if (nodes[i].Grounded != 0 && !bs.IsBroken(i))
                {
                    supported[i] = true;
                    queue.Enqueue(i);
                }
            }
            while (queue.TryDequeue(out var i))
            {
                var n = nodes[i];
                for (var k = 0; k < n.NeighborCount; k++)
                {
                    var j = neighbors[n.NeighborStart + k].Index;
                    if (j < 0 || j >= count || supported[j] || bs.IsBroken(j)) continue;
                    supported[j] = true;
                    queue.Enqueue(j);
                }
            }
            var collapsed = 0;
            for (var i = 0; i < count; i++)
            {
                if (supported[i] || bs.IsBroken(i)) continue;
                bs.SetBroken(i);
                collapsed++;
            }
            if (collapsed > 0) Debug.Log($"[ToLive] Structural collapse: {collapsed} unsupported chunks fell");
        }
    }

    /// <summary>
    /// SERVER AND CLIENT. Applies the replicated break mask to the local world: broken chunks lose
    /// their collider (so players fly through the hole) and stop rendering; "rebuild" restores them.
    /// On clients it also asks <see cref="DebrisSystem"/> to spawn debris for newly broken chunks
    /// (not for damage that already existed when this client joined).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ChunkSyncSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BuildingGraph>();
            state.RequireForUpdate<BuildingState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var bs = SystemAPI.GetSingleton<BuildingState>();
            var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
            var applied = SystemAPI.GetComponent<BuildingApplied>(graphEntity);
            if (applied.Initialized != 0 && applied.Generation == bs.Generation &&
                applied.A0 == bs.Broken0 && applied.A1 == bs.Broken1 && applied.A2 == bs.Broken2 && applied.A3 == bs.Broken3)
                return;

            var isClient = state.WorldUnmanaged.IsClient();
            var spawnDebris = isClient && applied.Initialized != 0 && applied.Generation == bs.Generation;

            var toHide = new NativeList<Entity>(Allocator.Temp);
            var toShow = new NativeList<Entity>(Allocator.Temp);
            var newlyBroken = new NativeList<int>(Allocator.Temp);
            foreach (var (chunk, entity) in SystemAPI.Query<RefRO<BuildingChunk>>().WithEntityAccess().WithOptions(EntityQueryOptions.IncludeDisabledEntities))
            {
                var i = chunk.ValueRO.Index;
                var broken = bs.IsBroken(i);
                var hidden = SystemAPI.HasComponent<ChunkHidden>(entity);
                if (broken && !hidden)
                {
                    toHide.Add(entity);
                    if (spawnDebris) newlyBroken.Add(i);
                }
                else if (!broken && hidden) toShow.Add(entity);
            }

            var em = state.EntityManager;
            foreach (var e in toHide)
            {
                var saved = em.HasComponent<PhysicsCollider>(e) ? em.GetComponentData<PhysicsCollider>(e).Value : default;
                em.RemoveComponent<PhysicsCollider>(e);
                em.AddComponentData(e, new ChunkHidden { SavedCollider = saved });
                em.AddComponent<DisableRendering>(e);
            }
            foreach (var e in toShow)
            {
                var saved = em.GetComponentData<ChunkHidden>(e).SavedCollider;
                if (saved.IsCreated) em.AddComponentData(e, new PhysicsCollider { Value = saved });
                em.RemoveComponent<ChunkHidden>(e);
                em.RemoveComponent<DisableRendering>(e);
            }
            if (newlyBroken.Length > 0)
            {
                var requests = em.GetBuffer<DebrisSpawnRequest>(graphEntity);
                foreach (var i in newlyBroken) requests.Add(new DebrisSpawnRequest { Chunk = i });
            }

            em.SetComponentData(graphEntity, new BuildingApplied
            {
                A0 = bs.Broken0, A1 = bs.Broken1, A2 = bs.Broken2, A3 = bs.Broken3,
                Generation = bs.Generation, Initialized = 1,
            });
        }
    }
}
