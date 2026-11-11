using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// THE SURGE FUEL SOURCE. Producers call these helpers (server only); <see cref="SurgeFuelSystem"/>
    /// applies the queued events. Milestone 4 swaps the producers (crowd trust / fear) without
    /// touching the rest of the surge code.
    /// </summary>
    public static class SurgeFuel
    {
        public static void Add(EntityManager em, Entity target, float amount)
        {
            if (target == Entity.Null || amount <= 0f) return;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<SurgeFuelQueue>());
            if (q.CalculateEntityCount() != 1) return;
            em.GetBuffer<SurgeFuelEvent>(q.GetSingletonEntity()).Add(new SurgeFuelEvent { Target = target, Amount = amount });
        }

        /// <summary>Fuel for a hit, at the configured rates (same as melee): attacker gets dealt, victim gets taken.</summary>
        public static void AddDamage(EntityManager em, Entity attacker, Entity victim, float damage)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<PowerConfig>());
            if (q.CalculateEntityCount() != 1) return;
            var cfg = q.GetSingleton<PowerConfig>();
            Add(em, attacker, damage * cfg.FuelPerDamageDealt);
            Add(em, victim, damage * cfg.FuelPerDamageTaken);
        }

        /// <summary>A chunk broke: credit whoever launched the body that smashed it (or the body itself).</summary>
        public static void CreditChunk(ref SystemState state, Entity instigator)
        {
            var em = state.EntityManager;
            if (instigator == Entity.Null || !em.Exists(instigator)) return;
            var credit = instigator;
            if (em.HasComponent<LastAttacker>(instigator))
            {
                var la = em.GetComponentData<LastAttacker>(instigator).Value;
                if (la != Entity.Null && em.Exists(la) &&
                    em.HasComponent<CombatState>(instigator) && em.GetComponentData<CombatState>(instigator).Action == CombatAction.Launched)
                    credit = la;
            }
            var cfgQ = em.CreateEntityQuery(ComponentType.ReadOnly<PowerConfig>());
            var perChunk = cfgQ.CalculateEntityCount() == 1 ? cfgQ.GetSingleton<PowerConfig>().FuelPerChunk : 2f;
            Add(em, credit, perChunk);
        }
    }

    /// <summary>Server: turns fuel events into surge meter and lifetime fuel; unlocks tier 2.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BreakSystem))]
    public partial struct SurgeFuelSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var e = state.EntityManager.CreateEntity(typeof(SurgeFuelQueue));
            state.EntityManager.AddBuffer<SurgeFuelEvent>(e);
            state.EntityManager.SetName(e, "SurgeFuelQueue");
            state.RequireForUpdate<PowerConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var cfg = SystemAPI.GetSingleton<PowerConfig>();
            var queue = SystemAPI.GetSingletonBuffer<SurgeFuelEvent>();
            if (queue.Length == 0) return;
            var events = queue.ToNativeArray(Allocator.Temp);
            queue.Clear();
            var powers = SystemAPI.GetComponentLookup<PowerState>();
            var combats = SystemAPI.GetComponentLookup<CombatState>();
            var baseMax = SystemAPI.TryGetSingleton<CombatConfig>(out var cc) ? cc.MaxPower : 100f;
            foreach (var ev in events)
            {
                if (!powers.HasComponent(ev.Target)) continue;
                var p = powers[ev.Target];
                p.Fuel += ev.Amount;
                if (p.Phase == SurgePhase.None) p.Meter = math.saturate(p.Meter + ev.Amount * cfg.MeterPerFuel);
                if (p.Tier < 2 && p.Fuel >= cfg.Tier2FuelThreshold)
                {
                    p.Tier = 2;
                    if (combats.HasComponent(ev.Target))
                    {
                        var c = combats[ev.Target];
                        c.Power += baseMax * (cfg.Tier2Power - 1f); // grow into the new max
                        combats[ev.Target] = c;
                    }
                    Debug.Log($"[ToLive] Tier 2 unlocked for {ev.Target}");
                }
                powers[ev.Target] = p;
            }
        }
    }

    /// <summary>
    /// Server: what the ritual does to the WORLD. During the build-up, chunks crack and break in a
    /// growing radius and nearby characters are pushed back. On the first tick of the surge, the
    /// eruption: a big shockwave that damages and knocks back characters and breaks chunks in a
    /// larger radius. Server-decided, so everyone sees the same result.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(BreakSystem))]
    public partial struct RitualServerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PowerConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var cfg = SystemAPI.GetSingleton<PowerConfig>();
            var combatCfg = SystemAPI.TryGetSingleton<CombatConfig>(out var cc) ? cc : default;
            var dt = SystemAPI.Time.DeltaTime;

            var casters = new NativeList<(Entity e, float3 pos, PowerState p)>(Allocator.Temp);
            foreach (var (power, xf, e) in SystemAPI.Query<RefRO<PowerState>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                var p = power.ValueRO;
                if (p.Phase == SurgePhase.Ritual || (p.Phase == SurgePhase.Surge && p.Erupted == 0))
                    casters.Add((e, xf.ValueRO.Position, p));
            }
            if (casters.Length == 0) return;

            var hasGraph = SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graphEntity);
            foreach (var (caster, pos, p) in casters)
            {
                if (p.Phase == SurgePhase.Ritual)
                {
                    var progress = math.saturate((tick - p.PhaseStartTick) / math.max(1f, p.PhaseEndTick - p.PhaseStartTick));
                    // cracks: break chunks inside a radius that grows with the build-up (eased in)
                    if (hasGraph) BreakInRadius(ref state, graphEntity, pos, cfg.RitualCrackRadius * progress * progress, caster, new float3(0, 4, 0));
                    // pressure: push other characters back
                    PushCharacters(ref state, caster, pos, cfg.RitualPushRadius, cfg.RitualPushSpeed * progress, 0f, tick, false, combatCfg, dt);
                }
                else
                {
                    // ERUPTION (first surge tick on the server)
                    if (hasGraph) BreakInRadius(ref state, graphEntity, pos, cfg.EruptionBreakRadius, caster, new float3(0, 25, 0));
                    var mods = cfg.Mods(p);
                    PushCharacters(ref state, caster, pos, cfg.EruptionRadius, cfg.EruptionKnockback * mods.Knockback, cfg.EruptionDamage * mods.Damage, tick, true, combatCfg, dt);
                    var pw = SystemAPI.GetComponentRW<PowerState>(caster);
                    pw.ValueRW.Erupted = 1;
                    Debug.Log($"[ToLive] Eruption by {caster} at {pos}");
                }
            }
        }

        static void BreakInRadius(ref SystemState state, Entity graphEntity, float3 center, float radius, Entity instigator, float3 velocity)
        {
            if (radius <= 0.5f) return;
            var em = state.EntityManager;
            var nodes = em.GetBuffer<ChunkNode>(graphEntity, true);
            var requests = em.GetBuffer<BreakRequest>(graphEntity);
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                // distance from the centre to the chunk's box
                var d = math.length(math.max(math.abs(center - n.Center) - n.Size * 0.5f, 0f));
                if (d > radius) continue;
                var outward = math.normalizesafe(n.Center - center) * 12f;
                requests.Add(new BreakRequest { Chunk = i, Velocity = velocity + outward, Instigator = instigator });
            }
        }

        static void PushCharacters(ref SystemState state, Entity caster, float3 center, float radius, float speed, float damage,
            uint tick, bool launch, in CombatConfig combatCfg, float dt)
        {
            var em = state.EntityManager;
            var rate = Damage.TickRate(em);
            using var q = em.CreateEntityQuery(ComponentType.ReadWrite<CombatState>(), ComponentType.ReadWrite<FlightState>(), ComponentType.ReadOnly<LocalTransform>());
            using var es = q.ToEntityArray(Allocator.Temp);
            var cfgQ = em.CreateEntityQuery(ComponentType.ReadOnly<PowerConfig>());
            var hasPowerCfg = cfgQ.CalculateEntityCount() == 1;
            var powerCfg = hasPowerCfg ? cfgQ.GetSingleton<PowerConfig>() : default;
            foreach (var e in es)
            {
                if (e == caster) continue;
                var pos = em.GetComponentData<LocalTransform>(e).Position;
                var to = pos - center;
                var d = math.length(to);
                if (d > radius) continue;
                var falloff = 1f - d / radius;
                var dir = math.normalizesafe(to + new float3(0, launch ? 0.4f * d : 0f, 0), new float3(0, 1, 0));
                var c = em.GetComponentData<CombatState>(e);
                var f = em.GetComponentData<FlightState>(e);
                if (c.Action == CombatAction.KnockedOut) continue;
                if (launch)
                {
                    var defense = hasPowerCfg && em.HasComponent<PowerState>(e) ? powerCfg.Mods(em.GetComponentData<PowerState>(e)).Defense : 1f;
                    var dealt = damage * falloff / defense;
                    // the shockwave launches everyone, also a held victim (GrabServerSystem then releases it)
                    c.Action = CombatAction.Launched;
                    c.Ballistic = 1;
                    c.ActionStartTick = tick;
                    c.ActionEndTick = tick + CombatSystem.Ticks(1.2f, rate);
                    c.LastHitTick = tick;
                    c.LastHitStrength = speed * falloff;
                    f.Velocity = dir * speed * (0.5f + 0.5f * falloff);
                    Damage.Apply(ref c, dealt, tick, rate, combatCfg.RespawnDelay); // knocks out at zero
                    if (em.HasComponent<LastAttacker>(e)) em.SetComponentData(e, new LastAttacker { Value = caster });
                    SurgeFuel.AddDamage(em, caster, e, dealt);
                }
                else
                {
                    f.Velocity += dir * speed * falloff * dt * 8f; // steady pressure
                }
                em.SetComponentData(e, c);
                em.SetComponentData(e, f);
            }
        }
    }

    /// <summary>
    /// Server: the beam. Each tick a firing character sweeps a sphere along its aim: every building
    /// chunk it passes breaks (same break pipeline), it stops at the first unbreakable surface, and
    /// characters near the beam take damage and are pushed along it.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(FlightSystem))]
    public partial struct BeamServerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PowerConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var cfg = SystemAPI.GetSingleton<PowerConfig>();
            var dt = SystemAPI.Time.DeltaTime;
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            if (!SystemAPI.TryGetSingleton<PhysicsWorldSingleton>(out var physics)) return;
            var hasGraph = SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graphEntity);
            var chunks = SystemAPI.GetComponentLookup<BuildingChunk>(true);
            var inputs = SystemAPI.GetComponentLookup<PlayerInput>(true);

            var beams = new NativeList<(Entity e, float3 origin, float3 dir, PowerMods mods)>(Allocator.Temp);
            foreach (var (move, power, flight, xf, e) in SystemAPI.Query<RefRO<MoveState>, RefRO<PowerState>, RefRO<FlightState>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (move.ValueRO.Beam != 2) continue;
                var input = inputs.HasComponent(e) ? inputs[e] : new PlayerInput { LookYaw = flight.ValueRO.Yaw, LookPitch = flight.ValueRO.Pitch };
                var dir = math.mul(quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f))), new float3(0, 0, 1));
                beams.Add((e, xf.ValueRO.Position + new float3(0, 0.6f, 0), dir, cfg.Mods(power.ValueRO)));
            }
            if (beams.Length == 0) return;

            var hits = new NativeList<ColliderCastHit>(64, Allocator.Temp);
            var fuel = new NativeList<(Entity e, float amount)>(Allocator.Temp);
            var rate = Damage.TickRate(state.EntityManager);
            var respawnDelay = Damage.RespawnDelay(state.EntityManager);
            foreach (var (caster, origin, dir, mods) in beams)
            {
                // length: up to the first unbreakable hit; chunks along the way break
                var length = cfg.BeamRange;
                hits.Clear();
                if (physics.SphereCastAll(origin + dir * 1.5f, cfg.BeamRadius, dir, cfg.BeamRange, ref hits, CollisionFilter.Default))
                {
                    var stop = float.MaxValue;
                    foreach (var h in hits)
                        if (!chunks.HasComponent(h.Entity)) stop = math.min(stop, h.Fraction * cfg.BeamRange);
                    if (stop < float.MaxValue) length = stop + 1.5f;
                    if (hasGraph)
                    {
                        var requests = SystemAPI.GetBuffer<BreakRequest>(graphEntity);
                        foreach (var h in hits)
                            if (chunks.HasComponent(h.Entity) && h.Fraction * cfg.BeamRange <= length)
                                requests.Add(new BreakRequest { Chunk = chunks[h.Entity].Index, Velocity = dir * 30f, Instigator = caster });
                    }
                }

                // characters near the beam line
                foreach (var (combat, flight, xf, power, e) in SystemAPI.Query<RefRW<CombatState>, RefRW<FlightState>, RefRO<LocalTransform>, RefRO<PowerState>>().WithEntityAccess())
                {
                    if (e == caster || combat.ValueRO.Action == CombatAction.KnockedOut) continue;
                    var to = xf.ValueRO.Position - origin;
                    var along = math.dot(to, dir);
                    if (along < 0f || along > length + cfg.BeamRadius * 2f) continue; // the beam's width reaches past where it stops
                    if (math.length(to - dir * along) > cfg.BeamRadius * 1.8f) continue;
                    var defense = cfg.Mods(power.ValueRO).Defense;
                    var dmg = cfg.BeamDamagePerSecond * mods.Beam * dt / defense;
                    var c = combat.ValueRO;
                    if (tick % 6 == 0) { c.LastHitTick = tick; c.LastHitStrength = 15f; }
                    Damage.Apply(ref c, dmg, tick, rate, respawnDelay); // shared path: knocks out at zero
                    combat.ValueRW = c;
                    flight.ValueRW.Velocity = math.lerp(flight.ValueRO.Velocity, dir * cfg.BeamPush * mods.Knockback, 0.25f);
                    fuel.Add((caster, dmg * cfg.FuelPerDamageDealt)); // configured rates, same as melee
                    fuel.Add((e, dmg * cfg.FuelPerDamageTaken));
                }
            }
            foreach (var (e, amount) in fuel) SurgeFuel.Add(state.EntityManager, e, amount);
        }
    }

    /// <summary>
    /// Server: the victim side of grab-and-throw. Holds the victim in front of the holder, lets the
    /// victim mash free, and performs the throw (the victim becomes a launched body that smashes
    /// through chunks like any launch).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(FlightSystem))]
    public partial struct GrabServerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PowerConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var cfg = SystemAPI.GetSingleton<PowerConfig>();
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadWrite<MoveState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<FlightState>());
            using var holders = q.ToEntityArray(Allocator.Temp);
            foreach (var holder in holders)
            {
                var m = em.GetComponentData<MoveState>(holder);
                var victim = m.Carrying;
                if (victim == Entity.Null) continue;
                if (!em.Exists(victim) || !em.HasComponent<CombatState>(victim)) { m.Carrying = Entity.Null; m.Throw = 0; em.SetComponentData(holder, m); continue; }

                var hc = em.GetComponentData<CombatState>(holder);
                var vc = em.GetComponentData<CombatState>(victim);
                var vm = em.GetComponentData<MoveState>(victim);
                var vf = em.GetComponentData<FlightState>(victim);
                var hx = em.GetComponentData<LocalTransform>(holder);
                var hf = em.GetComponentData<FlightState>(holder);

                var rate = Damage.TickRate(em);
                var respawnDelay = Damage.RespawnDelay(em);
                var holding = vm.GrabbedBy == holder;

                if (!holding)
                {
                    // start holding, but never grab a knocked-out / already-held victim, and a
                    // knocked-out holder can't grab
                    if (vc.Action == CombatAction.KnockedOut || vc.Action == CombatAction.Grabbed || hc.Action == CombatAction.KnockedOut)
                    {
                        m.Carrying = Entity.Null; m.Throw = 0;
                        em.SetComponentData(holder, m);
                        continue;
                    }
                    vc.Action = CombatAction.Grabbed; vc.ActionStartTick = tick; vc.LockTarget = Entity.Null;
                    vm.GrabbedBy = holder; vm.GrabStartTick = tick; vm.Mash = 0; vm.Beam = 0;
                }
                else if (vc.Action != CombatAction.Grabbed)
                {
                    // Re-checked every hold update: the victim left the hold through another path
                    // (knocked out, launched by an eruption, ...). Release it and keep its state:
                    // never overwrite KnockedOut with Launched/Grabbed.
                    vm.GrabbedBy = Entity.Null; vm.Mash = 0;
                    m.Carrying = Entity.Null; m.Throw = 0;
                    em.SetComponentData(victim, vm);
                    em.SetComponentData(holder, m);
                    continue;
                }

                var facing = math.mul(quaternion.RotateY(math.radians(hf.Yaw)), new float3(0, 0, 1));
                var holdPos = hx.Position + facing * 2.2f + new float3(0, 0.4f, 0);
                var released = false;

                if (hc.Action == CombatAction.KnockedOut)
                {
                    // holder knocked out: drop the victim (a short stagger), no throw
                    vc.Action = CombatAction.Launched; vc.Ballistic = 2;
                    vc.ActionStartTick = tick; vc.ActionEndTick = tick + CombatSystem.Ticks(0.3f, rate);
                    vf.Velocity = new float3(0, 4f, 0);
                    released = true;
                    Debug.Log($"[ToLive] {holder} was knocked out and dropped {victim}");
                }
                else if (m.Throw != 0)
                {
                    // THROW in the holder's aim direction
                    var hp = em.HasComponent<PowerState>(holder) ? em.GetComponentData<PowerState>(holder) : default;
                    var vp = em.HasComponent<PowerState>(victim) ? em.GetComponentData<PowerState>(victim) : default;
                    var mods = cfg.Mods(hp);
                    var input = em.HasComponent<PlayerInput>(holder) ? em.GetComponentData<PlayerInput>(holder) : new PlayerInput { LookYaw = hf.Yaw, LookPitch = hf.Pitch };
                    var aim = math.mul(quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f))), new float3(0, 0, 1));
                    vc.Action = CombatAction.Launched; vc.Ballistic = 0;
                    vc.ActionStartTick = tick; vc.ActionEndTick = tick + CombatSystem.Ticks(cfg.ThrowDuration, rate);
                    vc.LastHitTick = tick; vc.LastHitStrength = cfg.ThrowSpeed;
                    vf.Velocity = aim * cfg.ThrowSpeed * mods.Knockback;
                    var dmg = cfg.ThrowDamage * mods.Damage / cfg.Mods(vp).Defense;
                    // shared damage path: at zero the victim is knocked out but keeps flying (Ballistic = 1)
                    if (Damage.Apply(ref vc, dmg, tick, rate, respawnDelay)) Debug.Log($"[ToLive] Throw knocked out {victim}");
                    em.SetComponentData(victim, new LocalTransform { Position = holdPos + aim * 1.5f, Rotation = em.GetComponentData<LocalTransform>(victim).Rotation, Scale = 1f });
                    if (em.HasComponent<LastAttacker>(victim)) em.SetComponentData(victim, new LastAttacker { Value = holder });
                    SurgeFuel.AddDamage(em, holder, victim, dmg); // configured rates
                    released = true;
                    Debug.Log($"[ToLive] Throw by {holder}");
                }
                else if (vm.Mash >= cfg.EscapeMashes && tick - vm.GrabStartTick >= CombatSystem.Ticks(cfg.EscapeAfter, rate))
                {
                    // victim broke free
                    vc.Action = CombatAction.Launched; vc.Ballistic = 2;
                    vc.ActionStartTick = tick; vc.ActionEndTick = tick + CombatSystem.Ticks(0.3f, rate);
                    vf.Velocity = facing * 15f + new float3(0, 6, 0);
                    released = true;
                    Debug.Log($"[ToLive] {victim} broke free");
                }
                else
                {
                    // keep holding the victim in front of the holder
                    vf.Velocity = float3.zero;
                    em.SetComponentData(victim, new LocalTransform { Position = holdPos, Rotation = em.GetComponentData<LocalTransform>(victim).Rotation, Scale = 1f });
                }

                if (released)
                {
                    vm.GrabbedBy = Entity.Null; vm.Mash = 0;
                    m.Carrying = Entity.Null; m.Throw = 0;
                }
                em.SetComponentData(victim, vc);
                em.SetComponentData(victim, vm);
                em.SetComponentData(victim, vf);
                em.SetComponentData(holder, m);
            }
        }
    }
}
