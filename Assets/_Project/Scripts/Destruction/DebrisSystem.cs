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
        public byte Lifted;          // hovering around a ritual (cosmetic)
        public float3 Base;          // where it rested before being lifted
        public float3 Size;
    }

    /// <summary>Cosmetic ritual request for one frame (filled by the PowerPresenter on the client).</summary>
    public struct RitualDebrisFx
    {
        public int Caster;
        public float3 Center;
        public float Radius, Height, Progress;
        public bool Fling, SpawnRubble;
        public int RubbleCount;
        public UnityEngine.Color Tint;
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
        /// <summary>Ritual debris effects requested this frame (client cosmetic).</summary>
        public static readonly System.Collections.Generic.List<RitualDebrisFx> RitualFx = new System.Collections.Generic.List<RitualDebrisFx>();

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
            if (RitualFx.Count > 0 && cfg.Prefab != Entity.Null) ApplyRitualFx(cfg);
            RitualFx.Clear();
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
                    em.AddComponentData(e, new Debris { Order = m_Order++, Collider = collider, Size = size });
                }
            }
        }

        /// <summary>
        /// Ritual cosmetics: rubble tears up from the ground, settled pieces near the caster lift and
        /// hover higher as the build-up grows, and on the eruption everything lifted is flung outward.
        /// </summary>
        void ApplyRitualFx(in DebrisConfig cfg)
        {
            var em = EntityManager;
            var time = (float)SystemAPI.Time.ElapsedTime;
            foreach (var fx in RitualFx)
            {
                if (fx.SpawnRubble)
                {
                    for (var i = 0; i < fx.RubbleCount; i++)
                    {
                        var a = m_Random.NextFloat(0f, math.PI * 2f);
                        var r = m_Random.NextFloat(2.5f, fx.Radius);
                        var size = new float3(m_Random.NextFloat(0.3f, 1.4f), m_Random.NextFloat(0.2f, 0.8f), m_Random.NextFloat(0.3f, 1.4f));
                        var groundY = math.max(0f, fx.Center.y - 30f) <= 0f ? size.y * 0.5f : fx.Center.y - 1.5f;
                        var pos = new float3(fx.Center.x + math.cos(a) * r, groundY, fx.Center.z + math.sin(a) * r);
                        var e = em.Instantiate(cfg.Prefab);
                        em.SetComponentData(e, LocalTransform.FromPositionRotation(pos, quaternion.Euler(m_Random.NextFloat3(-0.3f, 0.3f))));
                        em.AddComponentData(e, new PostTransformMatrix { Value = float4x4.Scale(size) });
                        em.AddComponentData(e, new Debris { Order = m_Order++, Asleep = 1, Size = size });
                    }
                }

                var lift = fx.Height * math.smoothstep(0f, 1f, fx.Progress);
                foreach (var (debris, xf, e) in SystemAPI.Query<RefRW<Debris>, RefRW<LocalTransform>>().WithEntityAccess())
                {
                    var d = debris.ValueRO;
                    if (d.Asleep == 0) continue; // only settled pieces lift
                    var p = d.Lifted != 0 ? d.Base : xf.ValueRO.Position;
                    var flat = new float2(p.x - fx.Center.x, p.z - fx.Center.z);
                    var dist = math.length(flat);
                    if (dist > fx.Radius || p.y > fx.Center.y + 12f) continue;
                    if (d.Lifted == 0) { d.Lifted = 1; d.Base = p; debris.ValueRW = d; }

                    var seed = (d.Order % 97) * 0.37f;
                    var near = 1f - dist / fx.Radius;
                    var h = lift * (0.4f + 0.9f * near) * (0.7f + 0.3f * math.sin(seed * 5f));
                    var bob = math.sin(time * (1.5f + seed) + seed * 10f) * 0.25f * fx.Progress;
                    // slowly drift inward/around as the pressure builds
                    var swirl = quaternion.RotateY(fx.Progress * 0.35f * (0.5f + near));
                    var offset = math.mul(swirl, new float3(d.Base.x - fx.Center.x, 0, d.Base.z - fx.Center.z));
                    var t = xf.ValueRO;
                    t.Position = new float3(fx.Center.x, d.Base.y, fx.Center.z) + offset + new float3(0, h + bob, 0);
                    t.Rotation = math.mul(t.Rotation, quaternion.Euler(new float3(0.4f, 0.7f, 0.2f) * SystemAPI.Time.DeltaTime * fx.Progress * (1f + seed)));
                    xf.ValueRW = t;
                }

                if (fx.Fling)
                {
                    var toWake = new NativeList<Entity>(Allocator.Temp);
                    foreach (var (debris, e) in SystemAPI.Query<RefRO<Debris>>().WithEntityAccess())
                        if (debris.ValueRO.Lifted != 0) toWake.Add(e);
                    foreach (var e in toWake)
                    {
                        var d = em.GetComponentData<Debris>(e);
                        var pos = em.GetComponentData<LocalTransform>(e).Position;
                        var outward = math.normalizesafe(new float3(pos.x - fx.Center.x, 0, pos.z - fx.Center.z), new float3(1, 0, 0));
                        var velocity = outward * m_Random.NextFloat(22f, 45f) + new float3(0, m_Random.NextFloat(6f, 18f), 0);
                        var collider = BoxCollider.Create(new BoxGeometry
                        {
                            Center = float3.zero, Orientation = quaternion.identity, Size = d.Size,
                            BevelRadius = math.min(0.05f, math.cmin(d.Size) * 0.1f),
                        }, CollisionFilter.Default);
                        em.AddComponentData(e, new PhysicsCollider { Value = collider });
                        em.AddComponentData(e, PhysicsMass.CreateDynamic(collider.Value.MassProperties, math.max(1f, d.Size.x * d.Size.y * d.Size.z * cfg.Density)));
                        em.AddComponentData(e, new PhysicsVelocity { Linear = velocity, Angular = m_Random.NextFloat3(-6f, 6f) });
                        em.AddComponentData(e, new PhysicsDamping { Linear = 0.05f, Angular = 0.2f });
                        if (!em.HasComponent<PhysicsWorldIndex>(e)) em.AddSharedComponent(e, new PhysicsWorldIndex(1));
                        d.Asleep = 0; d.Lifted = 0; d.StillTime = 0f; d.Collider = collider;
                        em.SetComponentData(e, d);
                    }
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
