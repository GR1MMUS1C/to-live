using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace ToLive.Destruction
{
    /// <summary>
    /// The client-only physics world (index 1) for cosmetic debris, exactly as the Netcode docs
    /// describe for "client-only physics for effects". It shares the main world's static colliders,
    /// so debris lands on the ground, buildings and intact chunks, but nothing in it can affect
    /// players or the server.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class DebrisPhysicsSystemGroup : CustomPhysicsSystemGroup
    {
        public DebrisPhysicsSystemGroup() : base(1, true) { }
    }

    /// <summary>A live cosmetic debris piece.</summary>
    public struct Debris : IComponentData
    {
        public int Order;            // spawn order (oldest first for caps)
        public float StillTime;      // seconds spent resting
        public byte Asleep;
        public BlobAssetReference<Collider> Collider;
    }

    /// <summary>
    /// CLIENT ONLY. Spawns layer-2 debris for chunks that just broke, puts settled pieces to sleep
    /// (they stop simulating and become plain rendered boxes), and enforces the live/total caps.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ChunkSyncSystem))]
    public partial class DebrisSystem : SystemBase
    {
        /// <summary>Stats for the debug overlay (local client world).</summary>
        public static int LiveDebris, TotalDebris, BrokenChunks, ChunkCount;
        public static float3 BuildingCenter;
        /// <summary>Overrides the baked live cap when >= 0 (benchmark / debug).</summary>
        public static int LiveCapOverride = -1;
        /// <summary>Benchmark only: overrides the total cap, and multiplies pieces per chunk (simulates more destruction).</summary>
        public static int TotalCapOverride = -1;
        public static int SpawnMultiplier = 1;

        int m_Order;
        uint m_Generation;
        Random m_Random = new Random(1234);
        EntityQuery m_DebrisQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<DebrisConfig>();
            RequireForUpdate<BuildingGraph>();
            m_DebrisQuery = GetEntityQuery(ComponentType.ReadWrite<Debris>());
        }

        protected override void OnDestroy()
        {
            DestroyAll();
            LiveDebris = TotalDebris = 0;
        }

        protected override void OnUpdate()
        {
            var cfg = SystemAPI.GetSingleton<DebrisConfig>();
            var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
            var graph = SystemAPI.GetComponent<BuildingGraph>(graphEntity);
            var hasState = SystemAPI.TryGetSingleton<BuildingState>(out var bs);
            ChunkCount = graph.ChunkCount;
            BuildingCenter = graph.Center;
            var broken = hasState ? bs.CountBroken() : 0;
            if (broken != BrokenChunks)
                UnityEngine.Debug.Log($"[ToLive] {World.Name} sees {broken}/{graph.ChunkCount} broken chunks (generation {bs.Generation})");
            BrokenChunks = broken;

            if (hasState && bs.Generation != m_Generation)
            {
                DestroyAll(); // rebuild clears the debris
                m_Generation = bs.Generation;
            }

            // Spawn debris for newly broken chunks, spread over frames (MaxChunksPerFrame) so a
            // whole-building collapse doesn't cause one huge hitch.
            var requests = SystemAPI.GetBuffer<DebrisSpawnRequest>(graphEntity);
            if (requests.Length > 0 && cfg.Prefab != Entity.Null)
            {
                var take = math.min(requests.Length, math.max(1, cfg.MaxChunksPerFrame));
                var pending = new NativeArray<DebrisSpawnRequest>(take, Allocator.Temp);
                for (var i = 0; i < take; i++) pending[i] = requests[i];
                requests.RemoveRange(0, take);
                Spawn(pending, cfg, graph, hasState ? bs.LastImpactVelocity : float3.zero);
            }

            UpdateSleepAndCaps(cfg);
        }

        void Spawn(NativeArray<DebrisSpawnRequest> pending, in DebrisConfig cfg, in BuildingGraph graph, float3 impactVelocity)
        {
            // index -> chunk entity
            var map = new NativeHashMap<int, Entity>(128, Allocator.Temp);
            foreach (var (chunk, e) in SystemAPI.Query<RefRO<BuildingChunk>>().WithEntityAccess())
                map[chunk.ValueRO.Index] = e;

            var em = EntityManager;
            foreach (var req in pending)
            {
                if (!map.TryGetValue(req.Chunk, out var chunkEntity)) continue;
                var chunkCenter = em.GetComponentData<LocalToWorld>(chunkEntity).Position;
                var pieces = em.GetBuffer<ChunkPiece>(chunkEntity).ToNativeArray(Allocator.Temp);
                var outward = math.normalizesafe(new float3(chunkCenter.x - graph.Center.x, 0, chunkCenter.z - graph.Center.z));
                var mult = math.max(1, SpawnMultiplier);
                var shrink = 1f / math.pow(mult, 1f / 3f);
                for (var copy = 0; copy < mult; copy++)
                foreach (var piece in pieces)
                {
                    var size = math.max(piece.Size * shrink, 0.05f);
                    var pos = chunkCenter + piece.LocalCenter + (copy == 0 ? float3.zero : m_Random.NextFloat3(-0.5f, 0.5f) * piece.Size);
                    var e = em.Instantiate(cfg.Prefab);
                    em.SetComponentData(e, LocalTransform.FromPosition(pos));
                    em.AddComponentData(e, new PostTransformMatrix { Value = float4x4.Scale(size) });

                    var collider = BoxCollider.Create(new BoxGeometry
                    {
                        Center = float3.zero, Orientation = quaternion.identity, Size = size,
                        BevelRadius = math.min(0.05f, math.cmin(size) * 0.1f),
                    }, CollisionFilter.Default);
                    var mass = math.max(1f, size.x * size.y * size.z * cfg.Density);
                    var velocity = impactVelocity * cfg.ImpactTransfer
                                   + outward * m_Random.NextFloat(0.5f, 1f) * cfg.Scatter
                                   + m_Random.NextFloat3Direction() * cfg.Scatter * 0.5f;
                    em.AddComponentData(e, new PhysicsCollider { Value = collider });
                    em.AddComponentData(e, PhysicsMass.CreateDynamic(collider.Value.MassProperties, mass));
                    em.AddComponentData(e, new PhysicsVelocity { Linear = velocity, Angular = m_Random.NextFloat3(-3f, 3f) });
                    em.AddComponentData(e, new PhysicsDamping { Linear = 0.05f, Angular = 0.2f });
                    em.AddSharedComponent(e, new PhysicsWorldIndex(1));
                    em.AddComponentData(e, new Debris { Order = m_Order++, Collider = collider });
                }
            }
        }

        void UpdateSleepAndCaps(in DebrisConfig cfg)
        {
            var dt = SystemAPI.Time.DeltaTime;
            var cap = LiveCapOverride >= 0 ? LiveCapOverride : cfg.LiveDebrisCap;
            var toSleep = new NativeList<Entity>(Allocator.Temp);
            var awake = new NativeList<(int order, Entity e)>(Allocator.Temp);
            var all = new NativeList<(int order, Entity e)>(Allocator.Temp);

            foreach (var (debris, e) in SystemAPI.Query<RefRW<Debris>>().WithEntityAccess())
            {
                all.Add((debris.ValueRO.Order, e));
                if (debris.ValueRO.Asleep != 0) continue;
                var v = SystemAPI.GetComponent<PhysicsVelocity>(e);
                var resting = math.length(v.Linear) < cfg.SleepSpeed && math.length(v.Angular) < cfg.SleepSpeed * 2f;
                debris.ValueRW.StillTime = resting ? debris.ValueRO.StillTime + dt : 0f;
                if (debris.ValueRO.StillTime > cfg.SleepTime) toSleep.Add(e);
                else awake.Add((debris.ValueRO.Order, e));
            }

            // Over the live cap: freeze the oldest moving pieces.
            if (awake.Length > cap)
            {
                awake.Sort(new OrderComparer());
                for (var i = 0; i < awake.Length - cap; i++) toSleep.Add(awake[i].e);
            }
            foreach (var e in toSleep) Sleep(e);

            // Over the total cap: remove the oldest pieces altogether.
            var totalCap = TotalCapOverride >= 0 ? TotalCapOverride : cfg.MaxTotalDebris;
            if (all.Length > totalCap)
            {
                all.Sort(new OrderComparer());
                for (var i = 0; i < all.Length - totalCap; i++) DestroyPiece(all[i].e);
            }

            TotalDebris = m_DebrisQuery.CalculateEntityCount();
            LiveDebris = math.max(0, math.min(awake.Length, cap) - 0);
        }

        /// <summary>Stop simulating: drop the physics body, keep the rendered box where it lies.</summary>
        void Sleep(Entity e)
        {
            var em = EntityManager;
            var d = em.GetComponentData<Debris>(e);
            if (d.Asleep != 0) return;
            em.RemoveComponent<PhysicsVelocity>(e);
            em.RemoveComponent<PhysicsMass>(e);
            em.RemoveComponent<PhysicsDamping>(e);
            em.RemoveComponent<PhysicsCollider>(e);
            if (d.Collider.IsCreated) d.Collider.Dispose();
            d.Collider = default;
            d.Asleep = 1;
            em.SetComponentData(e, d);
        }

        void DestroyPiece(Entity e)
        {
            var d = EntityManager.GetComponentData<Debris>(e);
            if (d.Collider.IsCreated)
            {
                EntityManager.RemoveComponent<PhysicsCollider>(e);
                d.Collider.Dispose();
            }
            EntityManager.DestroyEntity(e);
        }

        void DestroyAll()
        {
            if (!m_DebrisQuery.IsEmptyIgnoreFilter)
            {
                using var entities = m_DebrisQuery.ToEntityArray(Allocator.Temp);
                foreach (var e in entities) DestroyPiece(e);
            }
            TotalDebris = LiveDebris = 0;
        }

        struct OrderComparer : System.Collections.Generic.IComparer<(int order, Entity e)>
        {
            public int Compare((int order, Entity e) a, (int order, Entity e) b) => a.order.CompareTo(b.order);
        }
    }
}
