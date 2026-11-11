using ToLive.Combat;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>
    /// SERVER: who holds the district and how. Applies the <see cref="DistrictSource"/> queue to each
    /// god's trust and fear, runs the "protecting" producers (staying near a crowd without harming it,
    /// destroying army units that threaten people, defeating the rival in front of a crowd), decays
    /// standing, derives the district meter, the leader and each god's alignment, pays surge fuel from
    /// the watching crowd, and writes the cause feed. Unreal analogy: a GameState-owned "faction
    /// standing" component with server-only producers.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenSimSystem))]
    public partial struct DistrictSystem : ISystem
    {
        NativeHashMap<Entity, uint> m_LastHarm;          // god -> tick they last killed a civilian
        NativeHashSet<Entity> m_CreditedUnits;           // destroyed units already credited
        NativeHashMap<long, float2> m_Feed;              // (god, cause) -> (summed value, count) for the next feed line
        NativeList<Entity> m_FeedGods;
        byte m_SeenRivalDowns;
        uint m_NextProtectTick;

        public void OnCreate(ref SystemState state)
        {
            m_LastHarm = new NativeHashMap<Entity, uint>(4, Allocator.Persistent);
            m_CreditedUnits = new NativeHashSet<Entity>(16, Allocator.Persistent);
            m_Feed = new NativeHashMap<long, float2>(16, Allocator.Persistent);
            m_FeedGods = new NativeList<Entity>(4, Allocator.Persistent);
            state.RequireForUpdate<DistrictConfig>();
            state.RequireForUpdate<DistrictState>();
            state.RequireForUpdate<CrowdServer>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnDestroy(ref SystemState state)
        {
            m_LastHarm.Dispose(); m_CreditedUnits.Dispose(); m_Feed.Dispose(); m_FeedGods.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<DistrictConfig>();
            var rate = Damage.TickRate(em);
            var dt = SystemAPI.Time.DeltaTime;
            var districtEntity = SystemAPI.GetSingletonEntity<DistrictState>();
            var serverEntity = SystemAPI.GetSingletonEntity<CrowdServer>();
            var server = em.GetComponentData<CrowdServer>(serverEntity);
            var citizens = em.GetBuffer<CitizenSim>(serverEntity, true).ToNativeArray(Allocator.Temp);

            // ---- protecting producers ----
            if (CombatSystem.Before(m_NextProtectTick, tick + 1))
            {
                m_NextProtectTick = tick + CombatSystem.Ticks(1f, rate);
                // near a crowd without harming it (and not boosting through it)
                foreach (var (kind, flight, xf, combat, e) in SystemAPI.Query<RefRO<CharacterKind>, RefRO<FlightState>, RefRO<LocalTransform>, RefRO<CombatState>>().WithEntityAccess())
                {
                    if (combat.ValueRO.Action == CombatAction.KnockedOut || flight.ValueRO.Gear == FlightGear.Boost) continue;
                    if (m_LastHarm.TryGetValue(e, out var harm) && tick - harm < CombatSystem.Ticks(cfg.HarmCooldown, rate)) continue;
                    var near = CountNear(citizens, xf.ValueRO.Position, cfg.NearCrowdRadius);
                    if (near < cfg.NearCrowdMin) continue;
                    DistrictSource.Add(em, e, cfg.TrustPerSecondNearCrowd * math.min(1f, near / 20f), 0f, Cause.ProtectedCrowd);
                }
            }
            // destroyed army units that threatened people
            foreach (var (unit, attacker, xf, e) in SystemAPI.Query<RefRO<ResponseUnit>, RefRO<LastAttacker>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (unit.ValueRO.Mode != UnitMode.Dead || m_CreditedUnits.Contains(e)) continue;
                m_CreditedUnits.Add(e);
                var god = HeatSource.Responsible(em, attacker.ValueRO.Value);
                if (!HeatSource.IsGod(em, god)) continue;
                if (CountNear(citizens, xf.ValueRO.Position, cfg.UnitThreatRadius) < 3) continue;
                DistrictSource.Add(em, god, cfg.TrustPerUnitDestroyed, 0f, Cause.UnitDestroyed);
            }
            // the rival defeated in front of a crowd
            if (SystemAPI.TryGetSingleton<HeatState>(out var heat) && heat.RivalDowns != m_SeenRivalDowns)
            {
                m_SeenRivalDowns = heat.RivalDowns;
                if (heat.Rival != Entity.Null && em.Exists(heat.Rival))
                {
                    var god = HeatSource.Responsible(em, em.GetComponentData<LastAttacker>(heat.Rival).Value);
                    var watchers = CountNear(citizens, em.GetComponentData<LocalTransform>(heat.Rival).Position, cfg.WatchRadius);
                    if (HeatSource.IsGod(em, god) && god != heat.Rival && watchers >= cfg.WatchersNeeded)
                        DistrictSource.Add(em, god, cfg.TrustPerRivalDefeated, 0f, Cause.RivalDefeated);
                }
            }

            // ---- apply the district source queue ----
            var queue = em.GetBuffer<DistrictEvent>(serverEntity);
            var events = queue.ToNativeArray(Allocator.Temp);
            queue.Clear();
            var standings = em.GetBuffer<GodStanding>(districtEntity);
            foreach (var ev in events)
            {
                var idx = FindOrAdd(standings, ev.God, em);
                if (idx < 0) continue;
                var s = standings[idx];
                s.Trust = math.max(0f, s.Trust + ev.Trust);
                s.Fear = math.max(0f, s.Fear + ev.Fear);
                standings[idx] = s;
                if (ev.Cause == Cause.CiviliansKilled)
                {
                    m_LastHarm[ev.God] = tick;
                    if (cfg.CrowdFuel) SurgeFuel.Add(em, ev.God, cfg.FuelPerKillFear * ev.Count); // short-term spike of fear
                    standings = em.GetBuffer<GodStanding>(districtEntity);
                }
                Feed(ev.God, ev.Cause, ev.Cause == Cause.CiviliansKilled || ev.Cause == Cause.Destruction ? ev.Count : math.max(ev.Trust, ev.Fear));
            }

            // ---- decay, drop gods that left ----
            standings = em.GetBuffer<GodStanding>(districtEntity);
            for (var i = standings.Length - 1; i >= 0; i--)
            {
                var s = standings[i];
                if (!em.Exists(s.God)) { standings.RemoveAt(i); continue; }
                var k = math.exp(-cfg.StandingDecayPerSecond * dt);
                s.Trust *= k; s.Fear *= k;
                standings[i] = s;
            }

            // ---- meter, leader, alignment ----
            var d = em.GetComponentData<DistrictState>(districtEntity);
            float totalTrust = 0f, totalFear = 0f, best = 0f;
            d.Leader = Entity.Null; d.LeaderAlignment = 0f; d.Hold = 0f;
            foreach (var s in standings)
            {
                totalTrust += s.Trust; totalFear += s.Fear;
                var sum = s.Trust + s.Fear;
                if (sum > best)
                {
                    best = sum; d.Leader = s.God;
                    d.LeaderAlignment = (s.Fear - s.Trust) / (sum + 1f);
                    d.Hold = sum / (sum + cfg.StandingScale);
                }
            }
            d.Meter = (totalFear - totalTrust) / (totalFear + totalTrust + 10f);
            foreach (var s in standings)
            {
                if (!em.HasComponent<PowerState>(s.God)) continue;
                var p = em.GetComponentData<PowerState>(s.God);
                var target = math.clamp((s.Fear - s.Trust) / math.max(1f, cfg.AlignmentScale), -1f, 1f);
                p.Alignment = Approach(p.Alignment, target, 0.25f * dt); // driven by what they did
                em.SetComponentData(s.God, p);
            }

            // ---- surge fuel from the watching crowd ----
            if (cfg.CrowdFuel && CombatSystem.Before(server.NextFuelTick, tick + 1))
            {
                server.NextFuelTick = tick + CombatSystem.Ticks(cfg.FuelInterval, rate);
                using var gods = SystemAPI.QueryBuilder().WithAll<CharacterKind, PowerState, LocalTransform>().Build().ToEntityArray(Allocator.Temp);
                foreach (var g in gods)
                {
                    var watchers = CountNear(citizens, em.GetComponentData<LocalTransform>(g).Position, cfg.WatchRadius);
                    if (watchers == 0) continue;
                    var sum = 0f;
                    foreach (var s in em.GetBuffer<GodStanding>(districtEntity, true)) if (s.God == g) sum = s.Trust + s.Fear;
                    var factor = cfg.AweFactor + (1f - cfg.AweFactor) * sum / (sum + cfg.StandingScale);
                    SurgeFuel.Add(em, g, watchers * cfg.FuelPerWatcherPerSecond * cfg.FuelInterval * factor);
                }
            }

            // ---- cause feed (summed over a short window, one line per god and cause) ----
            if (CombatSystem.Before(server.NextFeedTick, tick + 1))
            {
                server.NextFeedTick = tick + CombatSystem.Ticks(cfg.CauseFeedInterval, rate);
                var feed = em.GetBuffer<CauseEntry>(districtEntity);
                using var keys = m_Feed.GetKeyArray(Allocator.Temp);
                foreach (var key in keys)
                {
                    var v = m_Feed[key];
                    var god = m_FeedGods[(int)(key >> 8)];
                    d.CauseSeq++;
                    if (feed.Length >= 8) feed.RemoveAt(0);
                    feed.Add(new CauseEntry { Seq = d.CauseSeq, God = god, Kind = (byte)(key & 0xFF), Value = (short)math.clamp(math.round(v.x), -9999, 9999) });
                }
                m_Feed.Clear();
                m_FeedGods.Clear();
            }

            em.SetComponentData(districtEntity, d);
            em.SetComponentData(serverEntity, server);
        }

        void Feed(Entity god, byte cause, float value)
        {
            var gi = m_FeedGods.IndexOf(god);
            if (gi < 0) { gi = m_FeedGods.Length; m_FeedGods.Add(god); }
            var key = ((long)gi << 8) | cause;
            m_Feed.TryGetValue(key, out var v);
            m_Feed[key] = new float2(v.x + value, v.y + 1);
        }

        static int FindOrAdd(DynamicBuffer<GodStanding> standings, Entity god, EntityManager em)
        {
            if (god == Entity.Null || !em.Exists(god)) return -1;
            for (var i = 0; i < standings.Length; i++) if (standings[i].God == god) return i;
            standings.Add(new GodStanding { God = god });
            return standings.Length - 1;
        }

        public static int CountNear(NativeArray<CitizenSim> citizens, float3 pos, float radius)
        {
            var n = 0; var r2 = radius * radius;
            for (var i = 0; i < citizens.Length; i++)
                if (citizens[i].Alive != 0 && math.distancesq(citizens[i].Position, pos) <= r2) n++;
            return n;
        }

        static float Approach(float v, float target, float step) => v < target ? math.min(target, v + step) : math.max(target, v - step);
    }
}
