using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>
    /// SERVER: the response force's brains and weapons. Simple, readable behaviour per unit: approach
    /// from the edge, hold a preferred range and height from its target, strafe, fire on a cadence,
    /// retreat when heat drops, fall as a wreck when destroyed. Hits are decided here (server
    /// authoritative); clients only draw tracers, rockets and the beam from replicated state.
    /// Runs after the prediction group (which is OrderFirst), so the movement request
    /// (<see cref="UnitDrive"/>) is used by the flight model on the next tick. Unreal analogy: AIControllers + server-side weapon traces.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))] // the prediction group is OrderFirst: the drive is used next tick
    public partial struct UnitAISystem : ISystem
    {
        struct God
        {
            public Entity Entity;
            public float3 Position, Velocity;
            public float Blame;
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<OppositionConfig>();
            state.RequireForUpdate<CombatConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<OppositionConfig>();
            var combatCfg = SystemAPI.GetSingleton<CombatConfig>();
            var hasPowerCfg = SystemAPI.TryGetSingleton<PowerConfig>(out var powerCfg);
            var rate = Damage.TickRate(em);
            var dt = SystemAPI.Time.DeltaTime;
            var level = SystemAPI.TryGetSingleton<HeatState>(out var heat) ? heat.Level : (byte)0;
            var hasPhysics = SystemAPI.TryGetSingleton<PhysicsWorldSingleton>(out var physics);
            state.CompleteDependency(); // physics queries on the main thread
            var ctx = new HitContext { Tick = tick, Rate = rate, RespawnDelay = combatCfg.RespawnDelay, Combat = combatCfg, HasPower = hasPowerCfg, Power = powerCfg, SuppressTicks = CombatSystem.Ticks(cfg.SuppressTime, rate) };

            // Gods the army can shoot (players and the rival, not the dummy).
            var gods = new NativeList<God>(4, Allocator.Temp);
            foreach (var (kind, combat, xf, flight, e) in SystemAPI.Query<RefRO<ToLive.Opposition.CharacterKind>, RefRO<CombatState>, RefRO<LocalTransform>, RefRO<FlightState>>().WithEntityAccess())
            {
                if (combat.ValueRO.Action == CombatAction.KnockedOut) continue;
                var blame = SystemAPI.HasComponent<HeatBlame>(e) ? SystemAPI.GetComponent<HeatBlame>(e).Amount : 0f;
                gods.Add(new God { Entity = e, Position = xf.ValueRO.Position, Velocity = flight.ValueRO.Velocity, Blame = blame });
            }

            var anyBlamed = false; // includes knocked-out gods: the force waits for them to come back
            foreach (var blame in SystemAPI.Query<RefRO<HeatBlame>>()) if (blame.ValueRO.Amount > 0f) anyBlamed = true;

            var destroy = new NativeList<Entity>(4, Allocator.Temp);
            using var units = SystemAPI.QueryBuilder().WithAll<ResponseUnit, UnitBrain, UnitDrive>().Build().ToEntityArray(Allocator.Temp);
            foreach (var e in units)
            {
                var u = em.GetComponentData<ResponseUnit>(e);
                var brain = em.GetComponentData<UnitBrain>(e);
                var c = em.GetComponentData<CombatState>(e);
                var f = em.GetComponentData<FlightState>(e);
                var pos = em.GetComponentData<LocalTransform>(e).Position;
                var stats = cfg.Stats(u.Kind);
                var random = Unity.Mathematics.Random.CreateFromIndex(brain.Seed ^ (tick * 2654435761u));

                // ---- destroyed: a falling wreck, removed when it lands ----
                if (c.Action == CombatAction.KnockedOut)
                {
                    if (u.Mode != UnitMode.Dead)
                    {
                        u.Mode = UnitMode.Dead; u.BeamPhase = AntiGodPhase.Idle; brain.ModeTick = tick;
                        Debug.Log($"[ToLive] {UnitKind.Name(u.Kind)} destroyed");
                    }
                    var fallen = tick - brain.ModeTick;
                    if ((fallen > CombatSystem.Ticks(0.5f, rate) && (pos.y < 2.5f || math.length(f.Velocity) < 2f)) || fallen > CombatSystem.Ticks(5f, rate))
                        destroy.Add(e);
                    em.SetComponentData(e, u); em.SetComponentData(e, brain);
                    continue;
                }

                var busy = c.Action == CombatAction.Launched || c.Action == CombatAction.Grabbed;

                // ---- target: the nearest god that raised heat, else (on alert) the nearest god close by ----
                var tgt = default(God);
                var target = u.Mode == UnitMode.Retreat ? Entity.Null : PickTarget(gods, pos, stats.FireRange * 1.5f, out tgt);
                u.Target = target;

                // heat dropped below this unit's level: leave, unless it is still fighting a god who raised heat
                if (brain.Pinned == 0 && level < u.Kind && !anyBlamed && u.Mode != UnitMode.Retreat) { u.Mode = UnitMode.Retreat; brain.ModeTick = tick; target = Entity.Null; u.Target = Entity.Null; }

                var drive = new UnitDrive { Accel = stats.Accel, FaceYaw = f.Yaw };
                if (u.Mode == UnitMode.Retreat)
                {
                    var outward = math.normalizesafe(new float3(pos.x, 0, pos.z), new float3(1, 0, 0));
                    drive.Wish = outward * stats.Speed * 1.5f + new float3(0, 3f, 0);
                    drive.FaceYaw = math.degrees(math.atan2(outward.x, outward.z));
                    if (math.max(math.abs(pos.x), math.abs(pos.z)) > cfg.DistrictHalfSize) destroy.Add(e);
                }
                else if (target != Entity.Null)
                {
                    var to = tgt.Position - pos;
                    var dist = math.length(to);
                    var away = math.normalizesafe(new float3(pos.x - tgt.Position.x, 0, pos.z - tgt.Position.z), new float3(0, 0, 1));
                    var desired = tgt.Position + away * stats.PreferredRange + new float3(0, stats.HeightAbove, 0);
                    var tangent = math.cross(new float3(0, 1, 0), away) * brain.StrafeSign;
                    var seek = desired - pos;
                    var seekLen = math.length(seek);
                    drive.Wish = (seekLen > 0.01f ? seek / seekLen * math.min(stats.Speed, seekLen * 0.8f) : float3.zero) + tangent * stats.Strafe;
                    drive.FaceYaw = math.degrees(math.atan2(to.x, to.z));
                    u.Mode = dist < stats.PreferredRange * 1.6f ? UnitMode.Engage : UnitMode.Approach;
                    if (random.NextFloat() < dt / 4f) brain.StrafeSign = -brain.StrafeSign; // change sides every few seconds

                    if (!busy) Fire(ref state, ref u, ref brain, ref random, e, pos, tgt, dist, stats, cfg, ctx, hasPhysics, physics, dt);
                }
                else
                {
                    // nobody to shoot: circle the district centre
                    var a = (tick / (float)rate) * 0.05f + (brain.Seed % 628) * 0.01f;
                    var desired = new float3(math.cos(a) * 140f, 70f + u.Kind * 15f, math.sin(a) * 140f);
                    drive.Wish = math.normalizesafe(desired - pos) * stats.Speed * 0.6f;
                    drive.FaceYaw = math.degrees(math.atan2(drive.Wish.x, drive.Wish.z));
                    u.Mode = UnitMode.Approach;
                }
                if (target == Entity.Null || busy)
                {
                    if (u.BeamPhase == AntiGodPhase.Charging || u.BeamPhase == AntiGodPhase.Firing)
                    { u.BeamPhase = AntiGodPhase.Cooldown; u.PhaseEndTick = tick + CombatSystem.Ticks(1.5f, rate); }
                    else if (u.BeamPhase == AntiGodPhase.Cooldown && !CombatSystem.Before(tick, u.PhaseEndTick)) u.BeamPhase = AntiGodPhase.Idle;
                }

                // stay in the district, off the ground
                if (u.Mode != UnitMode.Retreat)
                {
                    if (math.abs(pos.x) > cfg.DistrictHalfSize) drive.Wish.x = -math.sign(pos.x) * stats.Speed;
                    if (math.abs(pos.z) > cfg.DistrictHalfSize) drive.Wish.z = -math.sign(pos.z) * stats.Speed;
                }
                if (pos.y < 10f) drive.Wish.y = math.max(drive.Wish.y, 6f);
                if (pos.y > 260f) drive.Wish.y = math.min(drive.Wish.y, -4f);

                em.SetComponentData(e, drive);
                em.SetComponentData(e, u);
                em.SetComponentData(e, brain);
            }
            foreach (var e in destroy) em.DestroyEntity(e);

            UpdateRockets(ref state, cfg, ctx, hasPhysics, physics, dt);
        }

        static Entity PickTarget(NativeList<God> gods, float3 pos, float alertRange, out God target)
        {
            target = default;
            var best = Entity.Null; var bestScore = float.MaxValue;
            foreach (var g in gods)
            {
                var d = math.distance(g.Position, pos);
                if (g.Blame <= 0f && d > alertRange) continue;
                var score = d - (g.Blame > 0f ? 10000f : 0f); // blamed gods first
                if (score < bestScore) { bestScore = score; best = g.Entity; target = g; }
            }
            return best;
        }

        struct HitContext
        {
            public uint Tick;
            public int Rate;
            public float RespawnDelay;
            public CombatConfig Combat;
            public bool HasPower;
            public PowerConfig Power;
            public uint SuppressTicks;
        }

        // ------------------------------------------------------------------ weapons

        static void Fire(ref SystemState state, ref ResponseUnit u, ref UnitBrain brain, ref Unity.Mathematics.Random random, Entity self, float3 pos,
            in God tgt, float dist, in UnitStats stats, in OppositionConfig cfg, in HitContext ctx, bool hasPhysics, PhysicsWorldSingleton physics, float dt)
        {
            var em = state.EntityManager;
            var tick = ctx.Tick;
            var dir = math.normalizesafe(tgt.Position - pos);
            switch (u.Kind)
            {
                case UnitKind.Police:
                {
                    if (dist > stats.FireRange || CombatSystem.Before(tick, brain.NextFireTick)) return;
                    brain.NextFireTick = tick + CombatSystem.Ticks(stats.FireInterval * random.NextFloat(0.8f, 1.25f), ctx.Rate);
                    var muzzle = pos + dir * 1.2f;
                    if (Blocked(hasPhysics, physics, muzzle, tgt.Position, out var block))
                    {
                        // no line of sight: the shot hits the wall
                        u.ShotTick = tick; u.ShotEnd = block; u.ShotHit = 0;
                        return;
                    }
                    var chance = cfg.TracerAccuracy * math.lerp(1f, 0.5f, math.saturate(dist / stats.FireRange));
                    if (math.length(tgt.Velocity) > 60f) chance *= 0.5f; // fast fliers are hard to track
                    var hit = random.NextFloat() < chance;
                    u.ShotTick = tick; u.ShotHit = (byte)(hit ? 1 : 0);
                    u.ShotEnd = hit ? tgt.Position : tgt.Position + random.NextFloat3Direction() * random.NextFloat(1.5f, 4f);
                    if (hit) HitCharacter(em, self, tgt.Entity, stats.Damage, dir, cfg.TracerFlinch, false, 0f, ctx, false);
                    ToLive.District.CrowdHazards.Scare(em, new float3(u.ShotEnd.x, 0f, u.ShotEnd.z), 15f); // gunfire scares people
                    return;
                }
                case UnitKind.Gunship:
                {
                    if (dist > stats.FireRange || CombatSystem.Before(tick, brain.NextFireTick) || cfg.RocketPrefab == Entity.Null) return;
                    // lead the target a little (half of its velocity over the flight time): dodgeable
                    var flight = dist / math.max(1f, cfg.RocketSpeed);
                    var aimAt = tgt.Position + tgt.Velocity * flight * 0.5f;
                    var aim = math.normalizesafe(aimAt - pos, dir);
                    var muzzle = pos + aim * 3f + new float3(0, -0.8f, 0);
                    if (Blocked(hasPhysics, physics, muzzle, tgt.Position, out _)) { brain.NextFireTick = tick + CombatSystem.Ticks(0.5f, ctx.Rate); return; }
                    brain.NextFireTick = tick + CombatSystem.Ticks(stats.FireInterval * random.NextFloat(0.85f, 1.2f), ctx.Rate);
                    var r = em.Instantiate(cfg.RocketPrefab);
                    em.SetComponentData(r, LocalTransform.FromPositionRotation(muzzle, quaternion.LookRotationSafe(aim, new float3(0, 1, 0))));
                    em.SetComponentData(r, new Rocket { Velocity = aim * cfg.RocketSpeed, Owner = self, SpawnTick = tick });
                    u.ShotTick = tick; u.ShotEnd = muzzle; u.ShotHit = 0;
                    return;
                }
                case UnitKind.AntiGod:
                {
                    var muzzle = pos + brain.AimDir * 5f;
                    switch (u.BeamPhase)
                    {
                        case AntiGodPhase.Cooldown:
                            if (!CombatSystem.Before(tick, u.PhaseEndTick)) u.BeamPhase = AntiGodPhase.Idle;
                            break;
                        case AntiGodPhase.Idle:
                            if (dist <= stats.FireRange)
                            {
                                u.BeamPhase = AntiGodPhase.Charging;
                                u.PhaseEndTick = tick + CombatSystem.Ticks(cfg.BeamChargeTime, ctx.Rate);
                                brain.AimDir = dir; // starts on the target, then tracks it with lag (move to dodge)
                                Debug.Log($"[ToLive] Anti-god weapon charging at {tgt.Entity}");
                            }
                            break;
                        case AntiGodPhase.Charging:
                        {
                            var lockTick = u.PhaseEndTick - CombatSystem.Ticks(cfg.BeamAimLockTime, ctx.Rate);
                            if (CombatSystem.Before(tick, lockTick))
                                brain.AimDir = RotateTowards(brain.AimDir, dir, math.radians(cfg.BeamTrackRate) * dt);
                            u.AimPoint = BeamEnd(hasPhysics, physics, muzzle, brain.AimDir, stats.FireRange);
                            if (!CombatSystem.Before(tick, u.PhaseEndTick))
                            {
                                u.BeamPhase = AntiGodPhase.Firing;
                                u.PhaseEndTick = tick + CombatSystem.Ticks(cfg.BeamFireTime, ctx.Rate);
                                Debug.Log($"[ToLive] Anti-god weapon FIRES");
                            }
                            break;
                        }
                        case AntiGodPhase.Firing:
                        {
                            var end = BeamEnd(hasPhysics, physics, muzzle, brain.AimDir, stats.FireRange);
                            u.AimPoint = end;
                            // damage spread over the firing time: fully inside the beam = stats.Damage in total
                            var perTick = stats.Damage / math.max(1f, cfg.BeamFireTime * ctx.Rate);
                            using var bodies = em.CreateEntityQuery(ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>()).ToEntityArray(Allocator.Temp);
                            foreach (var b in bodies)
                            {
                                if (b == self) continue;
                                var bp = em.GetComponentData<LocalTransform>(b).Position;
                                if (SegmentDistance(bp, muzzle, end) > cfg.BeamRadius + 0.6f) continue;
                                brain.BeamDealt += HitCharacter(em, self, b, perTick, brain.AimDir, cfg.BeamKnockback, true, 0.5f, ctx, true);
                            }
                            if (!CombatSystem.Before(tick, u.PhaseEndTick))
                            {
                                u.BeamPhase = AntiGodPhase.Cooldown;
                                u.PhaseEndTick = tick + CombatSystem.Ticks(cfg.BeamCooldown, ctx.Rate);
                                Debug.Log($"[ToLive] Anti-god beam ended: {brain.BeamDealt:0.0} damage dealt, beam end {end}");
                                brain.BeamDealt = 0f;
                            }
                            break;
                        }
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// One hit on a character through the shared damage path: guard and power defence apply,
        /// optional launch (rockets, beam), surge suppression (anti-god beam).
        /// </summary>
        static float HitCharacter(EntityManager em, Entity attacker, Entity victim, float damage, float3 dir, float push,
            bool launch, float launchTime, in HitContext ctx, bool suppress)
        {
            if (!em.Exists(victim) || !em.HasComponent<CombatState>(victim)) return 0f;
            var c = em.GetComponentData<CombatState>(victim);
            if (c.Action == CombatAction.KnockedOut) return 0f;
            var f = em.GetComponentData<FlightState>(victim);
            var hasP = em.HasComponent<PowerState>(victim);
            var p = hasP ? em.GetComponentData<PowerState>(victim) : default;
            var inRitual = p.Phase == SurgePhase.Ritual;

            var guarded = false;
            if (c.Guarding != 0)
            {
                var facing = math.mul(quaternion.RotateY(math.radians(f.Yaw)), new float3(0, 0, 1));
                guarded = math.dot(facing, -math.normalizesafe(new float3(dir.x, 0, dir.z))) > ctx.Combat.GuardFrontDot;
            }
            if (guarded) { damage *= ctx.Combat.GuardDamageScale; push *= ctx.Combat.GuardKnockbackScale; launch = false; }
            if (ctx.HasPower) damage /= ctx.Power.Mods(p).Defense;

            c.LastHitTick = ctx.Tick;
            c.LastHitStrength = math.max(push, 4f);
            if (!inRitual)
            {
                if (launch && c.Action != CombatAction.Grabbed)
                {
                    c.Action = CombatAction.Launched;
                    c.Ballistic = 0;
                    c.ActionStartTick = ctx.Tick;
                    c.ActionEndTick = ctx.Tick + CombatSystem.Ticks(launchTime, ctx.Rate);
                    c.ComboStep = 0; c.QueuedLight = 0; c.Charge = 0f;
                    f.Velocity = dir * push;
                }
                else f.Velocity += dir * push; // flinch
            }
            Damage.Apply(ref c, damage, ctx.Tick, ctx.Rate, ctx.RespawnDelay);
            em.SetComponentData(victim, c);
            em.SetComponentData(victim, f);
            if (suppress && hasP)
            {
                p.SuppressedUntilTick = ctx.Tick + ctx.SuppressTicks;
                em.SetComponentData(victim, p);
            }
            if (em.HasComponent<LastAttacker>(victim)) em.SetComponentData(victim, new LastAttacker { Value = attacker });
            SurgeFuel.AddDamage(em, attacker, victim, damage);
            return damage;
        }

        // ------------------------------------------------------------------ rockets

        static void UpdateRockets(ref SystemState state, in OppositionConfig cfg, in HitContext ctx, bool hasPhysics, PhysicsWorldSingleton physics, float dt)
        {
            var em = state.EntityManager;
            using var rockets = em.CreateEntityQuery(ComponentType.ReadWrite<Rocket>(), ComponentType.ReadWrite<LocalTransform>()).ToEntityArray(Allocator.Temp);
            if (rockets.Length == 0) return;
            using var bodies = em.CreateEntityQuery(ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>()).ToEntityArray(Allocator.Temp);
            var explode = new NativeList<(Entity rocket, float3 at)>(Allocator.Temp);
            foreach (var r in rockets)
            {
                var rk = em.GetComponentData<Rocket>(r);
                var xf = em.GetComponentData<LocalTransform>(r);
                var from = xf.Position;
                var to = from + rk.Velocity * dt;
                var at = to;
                var hit = !CombatSystem.Before(ctx.Tick, rk.SpawnTick + CombatSystem.Ticks(cfg.RocketLifetime, ctx.Rate));
                if (!hit && Blocked(hasPhysics, physics, from, to, out var wall)) { hit = true; at = wall; }
                if (!hit)
                    foreach (var b in bodies)
                    {
                        if (b == rk.Owner) continue;
                        var bp = em.GetComponentData<LocalTransform>(b).Position;
                        if (SegmentDistance(bp, from, to) < 1.6f) { hit = true; at = bp; break; }
                    }
                if (hit) explode.Add((r, at));
                else em.SetComponentData(r, LocalTransform.FromPositionRotation(to, xf.Rotation));
            }

            using var graphQ = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingGraph>());
            var hasGraph = graphQ.CalculateEntityCount() == 1;
            var graph = hasGraph ? graphQ.GetSingletonEntity() : Entity.Null;
            foreach (var (r, at) in explode)
            {
                var rk = em.GetComponentData<Rocket>(r);
                var stats = cfg.Gunship;
                var hitCount = 0; var dealt = 0f;
                foreach (var b in bodies)
                {
                    if (!em.Exists(b) || b == rk.Owner) continue;
                    var bp = em.GetComponentData<LocalTransform>(b).Position;
                    var d = math.distance(bp, at);
                    if (d > cfg.RocketRadius) continue;
                    var falloff = 1f - d / cfg.RocketRadius;
                    var dir = math.normalizesafe(bp - at + new float3(0, 0.6f, 0), math.normalizesafe(rk.Velocity));
                    var dmg = HitCharacter(em, rk.Owner, b, stats.Damage * (0.35f + 0.65f * falloff), dir, cfg.RocketKnockback * (0.4f + 0.6f * falloff), true, 0.7f, ctx, false);
                    if (dmg > 0f) { hitCount++; dealt += dmg; }
                }
                Debug.Log($"[ToLive] Rocket exploded at {at}: {hitCount} bodies hit, {dealt:0.0} damage");
                {
                    using var dq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.District.DistrictConfig>());
                    if (dq.CalculateEntityCount() == 1) // people under the blast die, more flee
                        ToLive.District.CrowdHazards.Kill(em, at, dq.GetSingleton<ToLive.District.DistrictConfig>().RocketKillRadius, cfg.RocketRadius * 4f, rk.Owner, ToLive.District.Cause.CiviliansKilled);
                }
                if (hasGraph && cfg.RocketBreakRadius > 0.5f)
                    RitualServerSystem.BreakInRadius(ref state, graph, at, cfg.RocketBreakRadius, rk.Owner, math.normalizesafe(rk.Velocity) * 15f);
                em.DestroyEntity(r);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Static world (city, intact chunks) between a and b? Returns the blocking point.</summary>
        static bool Blocked(bool hasPhysics, PhysicsWorldSingleton physics, float3 a, float3 b, out float3 point)
        {
            point = b;
            if (!hasPhysics || math.distancesq(a, b) < 0.01f) return false;
            var input = new RaycastInput { Start = a, End = b, Filter = CollisionFilter.Default };
            if (!physics.CollisionWorld.CastRay(input, out var hit)) return false;
            point = hit.Position;
            return true;
        }

        static float3 BeamEnd(bool hasPhysics, PhysicsWorldSingleton physics, float3 from, float3 dir, float range)
        {
            var to = from + dir * range;
            return Blocked(hasPhysics, physics, from, to, out var p) ? p : to;
        }

        static float SegmentDistance(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var t = math.saturate(math.dot(p - a, ab) / math.max(1e-4f, math.lengthsq(ab)));
            return math.distance(p, a + ab * t);
        }

        static float3 RotateTowards(float3 from, float3 to, float maxRadians)
        {
            from = math.normalizesafe(from, to);
            var angle = math.acos(math.clamp(math.dot(from, to), -1f, 1f));
            if (angle <= maxRadians || angle < 1e-4f) return to;
            var axis = math.normalizesafe(math.cross(from, to), new float3(0, 1, 0));
            return math.normalizesafe(math.mul(quaternion.AxisAngle(axis, maxRadians), from), to);
        }
    }
}
