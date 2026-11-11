using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>Benchmark-only "option A": one interpolated ghost per citizen (position + mood).</summary>
    public struct CitizenProbe : IComponentData
    {
        [GhostField] public byte Mood;
        public float3 Target;
        public int Home;
    }

    /// <summary>
    /// SERVER, benchmark only: spawns N citizens as individual ghosts walking along their streets, so
    /// the bandwidth of "every citizen is a ghost" can be measured against the compact crowd state
    /// the game uses (docs/PERF.md, milestone 4A). Not used in normal play.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CitizenGhostProbeSystem : ISystem
    {
        public static int Pending = -1;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<DistrictConfig>();
            state.RequireForUpdate<CrowdLayout>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            if (Pending >= 0)
            {
                using (var old = SystemAPI.QueryBuilder().WithAll<CitizenProbe>().Build().ToEntityArray(Allocator.Temp))
                    em.DestroyEntity(old);
                var prefab = SystemAPI.GetSingleton<DistrictConfig>().ProbePrefab;
                var homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true).ToNativeArray(Allocator.Temp);
                var n = math.min(Pending, homes.Length);
                for (var i = 0; i < n && prefab != Entity.Null; i++)
                {
                    var e = em.Instantiate(prefab);
                    em.SetComponentData(e, LocalTransform.FromPosition(homes[i].Position));
                    em.SetComponentData(e, new CitizenProbe { Home = i, Target = homes[i].Position });
                }
                Debug.Log($"[ToLive] Citizen ghost probe: {n} citizen ghosts");
                Pending = -1;
            }

            var dt = SystemAPI.Time.DeltaTime;
            var layout = SystemAPI.GetSingletonBuffer<CitizenHome>(true);
            var frame = (uint)UnityEngine.Time.frameCount;
            foreach (var (probe, xf) in SystemAPI.Query<RefRW<CitizenProbe>, RefRW<LocalTransform>>())
            {
                var p = probe.ValueRO;
                var home = layout[p.Home];
                if (math.distancesq(xf.ValueRO.Position.xz, p.Target.xz) < 0.5f)
                {
                    var rnd = Unity.Mathematics.Random.CreateFromIndex((uint)p.Home * 31u + frame);
                    p.Target = home.Position + home.Street * rnd.NextFloat(-18f, 18f);
                    p.Mood = (byte)rnd.NextInt(0, 4);
                }
                var d = p.Target - xf.ValueRO.Position; d.y = 0f;
                var len = math.length(d);
                if (len > 1e-3f) xf.ValueRW.Position += d / len * math.min(len, 1.4f * dt);
                probe.ValueRW = p;
            }
        }
    }
}
