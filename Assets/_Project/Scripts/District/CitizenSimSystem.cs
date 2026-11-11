using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>
    /// SERVER: the crowd. Citizens are not ghosts: they are plain data in a buffer on a server singleton
    /// (hundreds of entities would cost far more bandwidth, see docs/PERF.md). The server decides
    /// everything players must agree on: who is alive, where they died (body ghosts), each citizen's
    /// mood (2 bits) and what the crowd pays out. Clients run look-alike local agents from that state.
    /// Unreal analogy: a server-side Mass/crowd simulation with only its results replicated.
    ///
    /// Each tick: scan for hazards (fast bodies, supersonic passes, beams, the anti-god beam, combat
    /// hits), resolve the hazard queue (deaths and scares), decide moods on a staggered cadence,
    /// move citizens along their streets, and publish the compact state on the district ghost.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BreakSystem))]
    public partial struct CitizenSimSystem : ISystem
    {
        struct God
        {
            public Entity Entity;
            public float3 Position;
            public float Trust, Fear;
            public bool Down;
        }

        NativeHashMap<Entity, uint> m_LastPass; // reckless-pass cooldown per god

        public void OnCreate(ref SystemState state)
        {
            var e = state.EntityManager.CreateEntity(typeof(CrowdServer));
            state.EntityManager.AddBuffer<CitizenSim>(e);
            state.EntityManager.AddBuffer<CrowdHazard>(e);
            state.EntityManager.AddBuffer<DistrictEvent>(e);
            state.EntityManager.SetComponentData(e, new CrowdServer { Seed = 0x5EEDu });
            state.EntityManager.SetName(e, "CrowdServer");
            m_LastPass = new NativeHashMap<Entity, uint>(4, Allocator.Persistent);
            state.RequireForUpdate<DistrictConfig>();
            state.RequireForUpdate<CrowdLayout>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnDestroy(ref SystemState state) { if (m_LastPass.IsCreated) m_LastPass.Dispose(); }

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
            var serverEntity = SystemAPI.GetSingletonEntity<CrowdServer>();
            var homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true);

            // ---- the district ghost (spawned once) and the citizens ----
            if (!SystemAPI.TryGetSingletonEntity<DistrictState>(out var districtEntity))
            {
                if (cfg.StatePrefab == Entity.Null) return;
                districtEntity = em.Instantiate(cfg.StatePrefab);
                ResetCrowd(em, serverEntity, districtEntity, cfg.CitizenCount, homes.ToNativeArray(Allocator.Temp), tick);
                Debug.Log($"[ToLive] District spawned with {em.GetComponentData<DistrictState>(districtEntity).Population} citizens");
                return;
            }
            var server = em.GetComponentData<CrowdServer>(serverEntity);
            homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true);
            if (server.PendingReset > 0)
            {
                ResetCrowd(em, serverEntity, districtEntity, server.PendingReset, homes.ToNativeArray(Allocator.Temp), tick);
                server = em.GetComponentData<CrowdServer>(serverEntity);
                server.PendingReset = 0;
                em.SetComponentData(serverEntity, server);
                Debug.Log($"[ToLive] Crowd reset to {em.GetComponentData<DistrictState>(districtEntity).Population} citizens");
            }

            homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true);
            var district = em.GetComponentData<DistrictState>(districtEntity);
            var standings = em.GetBuffer<GodStanding>(districtEntity, true).ToNativeArray(Allocator.Temp);

            // ---- gods (players and the rival) with their standing here ----
            var gods = new NativeList<God>(4, Allocator.Temp);
            foreach (var (kind, combat, xf, e) in SystemAPI.Query<RefRO<CharacterKind>, RefRO<CombatState>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                var g = new God { Entity = e, Position = xf.ValueRO.Position, Down = combat.ValueRO.Action == CombatAction.KnockedOut };
                foreach (var s in standings) if (s.God == e) { g.Trust = s.Trust; g.Fear = s.Fear; }
                gods.Add(g);
            }

            // ---- hazard scan: things that are dangerous just by moving ----
            var hazards = em.GetBuffer<CrowdHazard>(serverEntity);
            var hasPhysics = SystemAPI.TryGetSingleton<PhysicsWorldSingleton>(out var physics);
            state.CompleteDependency();
            var hasPowerCfg = SystemAPI.TryGetSingleton<PowerConfig>(out var powerCfg);
            var pending = new NativeList<CrowdHazard>(8, Allocator.Temp);
            var recklessGods = new NativeList<float4>(2, Allocator.Temp); // xyz pos, w = god index
            foreach (var (combat, flight, xf, e) in SystemAPI.Query<RefRO<CombatState>, RefRO<FlightState>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                var c = combat.ValueRO; var f = flight.ValueRO; var p = xf.ValueRO.Position;
                var speed = math.length(f.Velocity);
                var launched = c.Action == CombatAction.Launched || c.Action == CombatAction.KnockedOut;
                // a launched body or a god flying fast through people at street level kills them
                if (speed >= cfg.FastBodySpeed && p.y < 4f && (launched || SystemAPI.HasComponent<CharacterKind>(e)))
                    pending.Add(new CrowdHazard { Kind = HazardKind.Kill, Position = p, Radius = cfg.BodyKillRadius, ScareRadius = 20f, Responsible = HeatSource.Responsible(em, e), Source = Cause.CiviliansKilled });
                // supersonic passes: deadly very low, reckless (trust cost) when low over people
                if (f.Supersonic != 0 && SystemAPI.HasComponent<CharacterKind>(e) && p.y < cfg.SupersonicScareRadius)
                {
                    if (p.y < cfg.SupersonicKillAltitude)
                        pending.Add(new CrowdHazard { Kind = HazardKind.Kill, Position = new float3(p.x, 0, p.z), Radius = 6f, ScareRadius = cfg.SupersonicScareRadius, Responsible = e, Source = Cause.CiviliansKilled });
                    else pending.Add(new CrowdHazard { Kind = HazardKind.Scare, Position = new float3(p.x, 0, p.z), ScareRadius = cfg.SupersonicScareRadius });
                    recklessGods.Add(new float4(p, e.Index));
                }
                // violence (any hit this tick) scares people nearby
                if (c.LastHitTick == tick && p.y < 30f)
                    pending.Add(new CrowdHazard { Kind = HazardKind.Scare, Position = p, ScareRadius = 25f });
            }
            // a ritual building up: people near it freeze (the 4A fix: crowds can't simply outrun an eruption)
            if (hasPowerCfg)
                foreach (var (power, xf) in SystemAPI.Query<RefRO<PowerState>, RefRO<LocalTransform>>())
                    if (power.ValueRO.Phase == SurgePhase.Ritual)
                        pending.Add(new CrowdHazard { Kind = HazardKind.Pin, Position = new float3(xf.ValueRO.Position.x, 0f, xf.ValueRO.Position.z), ScareRadius = powerCfg.EruptionRadius * 0.8f });
            // player / rival beams
            if (hasPowerCfg)
                foreach (var (move, flight, xf, e) in SystemAPI.Query<RefRO<MoveState>, RefRO<FlightState>, RefRO<LocalTransform>>().WithEntityAccess())
                {
                    if (move.ValueRO.Beam != 2) continue;
                    var input = SystemAPI.HasComponent<PlayerInput>(e) ? SystemAPI.GetComponent<PlayerInput>(e) : new PlayerInput { LookYaw = flight.ValueRO.Yaw, LookPitch = flight.ValueRO.Pitch };
                    var dir = math.mul(quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f))), new float3(0, 0, 1));
                    var from = xf.ValueRO.Position + new float3(0, 0.6f, 0);
                    var to = from + dir * powerCfg.BeamRange;
                    if (hasPhysics && physics.CastRay(new RaycastInput { Start = from + dir * 1.5f, End = to, Filter = CollisionFilter.Default }, out var hit)) to = hit.Position;
                    pending.Add(new CrowdHazard { Kind = HazardKind.Segment, Position = from, End = to, Radius = powerCfg.BeamRadius + 0.6f, ScareRadius = powerCfg.BeamRadius + 15f, Responsible = e, Source = Cause.CiviliansKilled });
                }
            // the anti-god weapon's beam
            if (SystemAPI.TryGetSingleton<OppositionConfig>(out var oppCfg))
                foreach (var (unit, xf, e) in SystemAPI.Query<RefRO<ResponseUnit>, RefRO<LocalTransform>>().WithEntityAccess())
                {
                    var u = unit.ValueRO;
                    if (u.BeamPhase != AntiGodPhase.Firing) continue;
                    var from = xf.ValueRO.Position;
                    pending.Add(new CrowdHazard { Kind = HazardKind.Segment, Position = from, End = u.AimPoint, Radius = oppCfg.BeamRadius + 0.6f, ScareRadius = oppCfg.BeamRadius + 20f, Responsible = e, Source = Cause.CiviliansKilled });
                }
            hazards = em.GetBuffer<CrowdHazard>(serverEntity);
            hazards.AddRange(pending.AsArray());
            var all = hazards.ToNativeArray(Allocator.Temp);
            hazards.Clear();

            // ---- resolve hazards: deaths and scares ----
            var citizens = em.GetBuffer<CitizenSim>(serverEntity);
            var deaths = new NativeList<(Entity who, ushort index, float3 pos)>(Allocator.Temp);
            var fleeTicks = CombatSystem.Ticks(cfg.FleeTime, rate);
            var eruptionCrowd = new NativeList<Entity>(Allocator.Temp);
            var rioterDeaths = new NativeList<Entity>(Allocator.Temp);
            foreach (var h in all)
            {
                var scaredAny = false;
                for (var i = 0; i < citizens.Length; i++)
                {
                    var c = citizens[i];
                    if (c.Alive == 0) continue;
                    var d = h.Kind == HazardKind.Segment ? SegmentDistance(c.Position + new float3(0, 0.9f, 0), h.Position, h.End) : math.distance(c.Position, h.Position);
                    if (h.Kind == HazardKind.Pin)
                    {
                        // a ritual building up nearby: too terrified to run (the eruption will catch them)
                        if (d <= h.ScareRadius) { c.Mood = Mood.Cower; c.MoodUntilTick = tick + CombatSystem.Ticks(1f, rate); }
                    }
                    else if (h.Kind != HazardKind.Scare && d <= h.Radius)
                    {
                        c.Alive = 0;
                        deaths.Add((h.Responsible, (ushort)i, c.Position));
                        if (c.Riot != 0) rioterDeaths.Add(h.Responsible);
                    }
                    else if (d <= h.ScareRadius && c.Riot == 0) // rioters stand their ground
                    {
                        scaredAny = true;
                        var near = h.Kind == HazardKind.Segment ? h.End : h.Position;
                        c.Mood = d <= cfg.CowerRadius + h.Radius ? Mood.Cower : Mood.Flee;
                        c.FleeFrom = h.Kind == HazardKind.Segment ? ClosestOnSegment(c.Position, h.Position, h.End) : near;
                        c.MoodUntilTick = tick + fleeTicks;
                    }
                    citizens[i] = c;
                }
                if (h.Kind != HazardKind.Pin && (h.Kind != HazardKind.Scare || h.ScareRadius > 0f)) PushThreat(ref district, h.Kind == HazardKind.Segment ? h.End : h.Position, tick);
                if (h.Source == Cause.EruptionNearCrowd && scaredAny && h.Responsible != Entity.Null) eruptionCrowd.Add(h.Responsible);
            }
            foreach (var who in eruptionCrowd) DistrictSource.Add(em, who, -cfg.TrustLostEruption, cfg.FearPerEruption, Cause.EruptionNearCrowd);
            foreach (var who in rioterDeaths) ToLive.Crisis.CrisisTallies.Record(em, HeatSource.Responsible(em, who), rioters: 1);

            // reckless supersonic passes over people (trust cost, rate-limited per god)
            foreach (var rg in recklessGods)
            {
                var godEntity = Entity.Null;
                foreach (var g in gods) if (g.Entity.Index == (int)rg.w) godEntity = g.Entity;
                if (godEntity == Entity.Null) continue;
                if (m_LastPass.TryGetValue(godEntity, out var last) && tick - last < CombatSystem.Ticks(cfg.RecklessPassCooldown, rate)) continue;
                var below = 0;
                for (var i = 0; i < citizens.Length; i++)
                    if (citizens[i].Alive != 0 && math.distance(citizens[i].Position.xz, rg.xz) < cfg.SupersonicScareRadius) below++;
                if (below < cfg.NearCrowdMin) continue;
                m_LastPass[godEntity] = tick;
                DistrictSource.Add(em, godEntity, -cfg.TrustLostRecklessPass, cfg.FearPerRecklessPass, Cause.RecklessPass);
            }

            // deaths: bodies (static ghosts), blame (trust/fear/heat/fuel through the district source)
            if (deaths.Length > 0)
            {
                var byWho = new NativeHashMap<Entity, int>(4, Allocator.Temp);
                foreach (var (who, index, pos) in deaths)
                {
                    byWho.TryGetValue(who, out var n);
                    byWho[who] = n + 1;
                    if (cfg.BodyPrefab != Entity.Null)
                    {
                        var body = em.Instantiate(cfg.BodyPrefab);
                        var yaw = (index * 137.5f) % 360f;
                        em.SetComponentData(body, LocalTransform.FromPositionRotation(pos, quaternion.RotateY(math.radians(yaw))));
                        em.SetComponentData(body, new CitizenBody { Index = index, DeathTick = tick });
                    }
                }
                citizens = em.GetBuffer<CitizenSim>(serverEntity);
                foreach (var kv in byWho)
                {
                    // a crisis counts everyone it costs, not only deaths a god caused (the army's too)
                    if (kv.Key != Entity.Null && !HeatSource.IsGod(em, HeatSource.Responsible(em, kv.Key)))
                        ToLive.Crisis.CrisisTallies.Record(em, kv.Key, deaths: kv.Value);
                    DistrictSource.CiviliansKilled(em, kv.Key, kv.Value);
                    Debug.Log($"[ToLive] {kv.Value} civilians killed (responsible {kv.Key})");
                }
            }

            // ---- moods (staggered decisions) and movement ----
            citizens = em.GetBuffer<CitizenSim>(serverEntity);
            homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true); // re-fetch after structural changes
            var thinkTicks = math.max(1u, CombatSystem.Ticks(cfg.ThinkInterval, rate));
            var alive = 0;
            for (var i = 0; i < citizens.Length; i++)
            {
                var c = citizens[i];
                if (c.Alive == 0) continue;
                alive++;
                var home = homes[i];
                var rnd = Unity.Mathematics.Random.CreateFromIndex((uint)i * 7919u + tick);

                if (tick % thinkTicks == (uint)i % thinkTicks)
                {
                    var scared = (c.Mood == Mood.Flee || c.Mood == Mood.Cower) && CombatSystem.Before(tick, c.MoodUntilTick);
                    if (c.Riot != 0) { if (!scared) { c.Mood = Mood.Gather; c.Focus = Entity.Null; } }
                    else if (!scared)
                    {
                        c.Mood = Mood.Wander; c.Focus = Entity.Null;
                        var bestGather = float.MaxValue;
                        foreach (var g in gods)
                        {
                            if (g.Down) continue;
                            var d = math.distance(g.Position, c.Position);
                            // a god they fear: run (fear outweighs trust)
                            if (g.Fear > 5f && g.Fear > g.Trust * 1.2f && d < cfg.FearRadius)
                            {
                                c.Mood = Mood.Flee; c.FleeFrom = g.Position; c.MoodUntilTick = tick + fleeTicks / 2;
                                break;
                            }
                            // a god they trust: gather and cheer
                            if (g.Trust > 3f && g.Trust > g.Fear && d < cfg.GatherRadius && d < bestGather)
                            { bestGather = d; c.Mood = Mood.Gather; c.Focus = g.Entity; }
                        }
                    }
                }

                var walk = cfg.WalkSpeed;
                switch (c.Mood)
                {
                    case Mood.Wander:
                    {
                        if (math.distancesq(c.Position.xz, c.Target.xz) < 0.5f || math.lengthsq(c.Target) < 0.01f)
                            c.Target = home.Position + home.Street * rnd.NextFloat(-cfg.WanderRange, cfg.WanderRange);
                        c.Position = MoveTo(c.Position, c.Target, walk * dt);
                        break;
                    }
                    case Mood.Gather:
                    {
                        var focus = float3.zero; var found = false;
                        if (c.Riot != 0) { focus = c.RiotPoint; found = true; }
                        foreach (var g in gods) if (g.Entity == c.Focus) { focus = g.Position; found = true; }
                        if (!found) { c.Mood = Mood.Wander; break; }
                        var angle = i * 2.39996f;
                        var spot = new float3(focus.x + math.cos(angle) * cfg.GatherDistance, home.Position.y, focus.z + math.sin(angle) * cfg.GatherDistance);
                        // stay in the street: approach along the street axis, never more than 40 m from home
                        var along = math.dot(spot - home.Position, home.Street);
                        var target = home.Position + home.Street * math.clamp(along, -40f, 40f);
                        c.Position = MoveTo(c.Position, target, walk * 1.6f * dt);
                        break;
                    }
                    case Mood.Flee:
                    {
                        var away = math.dot(c.Position - c.FleeFrom, home.Street);
                        var dir = away >= 0f ? home.Street : -home.Street;
                        var next = c.Position + dir * cfg.RunSpeed * dt;
                        if (math.abs(math.dot(next - home.Position, home.Street)) < 70f) c.Position = next;
                        if (!CombatSystem.Before(tick, c.MoodUntilTick)) c.Mood = Mood.Wander;
                        break;
                    }
                    case Mood.Cower:
                        if (!CombatSystem.Before(tick, c.MoodUntilTick)) c.Mood = Mood.Flee;
                        break;
                }
                c.Position.y = home.Position.y;
                citizens[i] = c;
            }

            // ---- publish the compact state ----
            var aliveBits = em.GetBuffer<CitizenAliveBits>(districtEntity);
            var moodBits = em.GetBuffer<CitizenMoodBits>(districtEntity);
            citizens = em.GetBuffer<CitizenSim>(serverEntity);
            for (var w = 0; w < aliveBits.Length; w++)
            {
                uint bits = 0;
                for (var b = 0; b < 32; b++) { var i = w * 32 + b; if (i < citizens.Length && citizens[i].Alive != 0) bits |= 1u << b; }
                if (aliveBits[w].Bits != bits) aliveBits[w] = new CitizenAliveBits { Bits = bits };
            }
            for (var w = 0; w < moodBits.Length; w++)
            {
                uint bits = 0;
                for (var b = 0; b < 16; b++) { var i = w * 16 + b; if (i < citizens.Length && citizens[i].Alive != 0) bits |= (uint)(citizens[i].Mood & 3) << (b * 2); }
                if (moodBits[w].Bits != bits) moodBits[w] = new CitizenMoodBits { Bits = bits };
            }
            district = em.GetComponentData<DistrictState>(districtEntity).WithThreats(district);
            district.Alive = (ushort)alive;
            em.SetComponentData(districtEntity, district);
        }

        /// <summary>(Re)creates the crowd: N citizens at their homes, all alive; removes bodies.</summary>
        public static void ResetCrowd(EntityManager em, Entity serverEntity, Entity districtEntity, int count, NativeArray<CitizenHome> homes, uint tick)
        {
            count = math.clamp(count, 0, homes.Length);
            using (var bq = em.CreateEntityQuery(ComponentType.ReadOnly<CitizenBody>()))
                em.DestroyEntity(bq);
            var citizens = em.GetBuffer<CitizenSim>(serverEntity);
            citizens.Clear();
            for (var i = 0; i < count; i++)
                citizens.Add(new CitizenSim { Position = homes[i].Position, Target = homes[i].Position, Alive = 1, NextThinkTick = tick + (uint)(i % 30) });
            var aliveBits = em.GetBuffer<CitizenAliveBits>(districtEntity);
            aliveBits.Clear();
            for (var w = 0; w < (count + 31) / 32; w++) aliveBits.Add(new CitizenAliveBits { Bits = 0 });
            var moodBits = em.GetBuffer<CitizenMoodBits>(districtEntity);
            moodBits.Clear();
            for (var w = 0; w < (count + 15) / 16; w++) moodBits.Add(new CitizenMoodBits { Bits = 0 });
            var d = em.GetComponentData<DistrictState>(districtEntity);
            d.Population = (ushort)count; d.Alive = (ushort)count;
            em.SetComponentData(districtEntity, d);
        }

        static void PushThreat(ref DistrictState d, float3 pos, uint tick)
        {
            d.Threat3 = d.Threat2; d.ThreatTick3 = d.ThreatTick2;
            d.Threat2 = d.Threat1; d.ThreatTick2 = d.ThreatTick1;
            d.Threat1 = d.Threat0; d.ThreatTick1 = d.ThreatTick0;
            d.Threat0 = pos; d.ThreatTick0 = tick;
        }

        static float3 MoveTo(float3 from, float3 to, float step)
        {
            var d = to - from; d.y = 0;
            var len = math.length(d);
            return len <= step || len < 1e-4f ? new float3(to.x, from.y, to.z) : from + d / len * step;
        }

        static float SegmentDistance(float3 p, float3 a, float3 b) => math.distance(p, ClosestOnSegment(p, a, b));

        static float3 ClosestOnSegment(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var t = math.saturate(math.dot(p - a, ab) / math.max(1e-4f, math.lengthsq(ab)));
            return a + ab * t;
        }
    }

    public static class DistrictStateExtensions
    {
        /// <summary>Copy the threat ring from <paramref name="src"/> (the working copy) onto this state.</summary>
        public static DistrictState WithThreats(this DistrictState d, in DistrictState src)
        {
            d.Threat0 = src.Threat0; d.Threat1 = src.Threat1; d.Threat2 = src.Threat2; d.Threat3 = src.Threat3;
            d.ThreatTick0 = src.ThreatTick0; d.ThreatTick1 = src.ThreatTick1; d.ThreatTick2 = src.ThreatTick2; d.ThreatTick3 = src.ThreatTick3;
            return d;
        }
    }
}
