using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Player;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace ToLive.Flight
{
    /// <summary>
    /// The flight model. It runs inside the PredictedSimulationSystemGroup, so the server runs it
    /// once per tick for every player, and the owning client runs it too (re-running older ticks
    /// after each server snapshot to correct mispredictions). Because it's the same code with the
    /// same inputs on both sides, your own movement feels instant while the server stays authoritative.
    /// Unreal analogy: UCharacterMovementComponent's PerformMovement with client-side prediction.
    ///
    /// Collision approach: kinematic character + sphere sweeps ("move and slide") against the static
    /// city in the Unity Physics world. The hero is NOT a rigidbody. Reasons:
    ///  - Prediction stays stable: no solver, no contacts or impulses that can diverge between client
    ///    and server. The result only depends on position, velocity, input and the static colliders.
    ///  - Predicted dynamic physics re-steps the whole physics world for every rolled-back tick,
    ///    which gets expensive fast at 400 m/s with frequent corrections.
    ///  - Sweeps can't tunnel even at 400 m/s (6.7 m per tick at 60 Hz).
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    public partial struct FlightSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FlightConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency(); // main-thread lookups (input, chunks)
            var cfg = SystemAPI.GetSingleton<FlightConfig>();
            var hasCombatCfg = SystemAPI.TryGetSingleton<CombatConfig>(out var combatCfg);
            var dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f) return;
            var net = SystemAPI.GetSingleton<NetworkTime>();
            var tick = net.ServerTick.IsValid ? net.ServerTick.TickIndexForValidTick : 0u;
            var isServer = state.WorldUnmanaged.IsServer();
            var rate = SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate) && tickRate.SimulationTickRate > 0 ? tickRate.SimulationTickRate : 60;

            var hasPhysics = SystemAPI.TryGetSingleton<PhysicsWorldSingleton>(out var physics);
            var hits = new NativeList<DistanceHit>(8, Allocator.Temp);
            var chunks = SystemAPI.GetComponentLookup<BuildingChunk>(true);
            var inputs = SystemAPI.GetComponentLookup<PlayerInput>(true);
            var broken = new NativeList<BreakRequest>(4, Allocator.Temp);
            var powers = SystemAPI.GetComponentLookup<ToLive.Power.PowerState>(true);
            var moves = SystemAPI.GetComponentLookup<ToLive.Power.MoveState>(true);
            var hasPowerCfg = SystemAPI.TryGetSingleton<ToLive.Power.PowerConfig>(out var powerCfg);
            var drives = SystemAPI.GetComponentLookup<ToLive.Opposition.UnitDrive>(true); // server-only AI steering (response units)

            // WithAll<Simulate>: Netcode enables Simulate only on ghosts this world should simulate
            // this tick (all of them on the server, just your own player on a client).
            foreach (var (flight, combat, transform, entity) in
                     SystemAPI.Query<RefRW<FlightState>, RefRW<CombatState>, RefRW<LocalTransform>>().WithAll<Simulate>().WithEntityAccess())
            {
                var s = flight.ValueRO;
                var c = combat.ValueRO;
                var pos = transform.ValueRO.Position;

                // Hit-stop: freeze in place for a few ticks on impact.
                if (CombatSystem.Before(tick, c.HitStopUntilTick)) continue;

                // Grabbed: the server holds this body in front of its captor (GrabServerSystem).
                if (c.Action == CombatAction.Grabbed) continue;

                // Power: ritual locks you in place; tier/surge/burnout scale speeds; carrying slows you.
                var fcfg = cfg;
                if (hasPowerCfg && powers.HasComponent(entity))
                {
                    var ps = powers[entity];
                    if (ps.Phase == ToLive.Power.SurgePhase.Ritual)
                    {
                        s.Velocity = float3.zero;
                        flight.ValueRW = s;
                        continue;
                    }
                    var speedMul = powerCfg.Mods(ps).Speed;
                    if (moves.HasComponent(entity) && moves[entity].Carrying != Entity.Null) speedMul *= powerCfg.CarrySpeedScale;
                    fcfg.HoverMaxSpeed *= speedMul; fcfg.HoverAcceleration *= speedMul;
                    fcfg.CruiseMaxSpeed *= speedMul; fcfg.CruiseAcceleration *= speedMul;
                    fcfg.BoostMaxSpeed *= speedMul; fcfg.BoostAcceleration *= speedMul;
                }

                var input = inputs.HasComponent(entity) ? inputs[entity] : new PlayerInput { LookYaw = s.Yaw };
                var launched = c.Action == CombatAction.Launched || c.Action == CombatAction.KnockedOut;
                switch (c.Action)
                {
                    case CombatAction.Launched:
                    case CombatAction.KnockedOut:
                        // Reduced control: the body is a projectile (ballistic for launchers).
                        if (c.Ballistic == 1) s.Velocity.y -= combatCfg.LaunchGravity * dt;
                        s.Velocity *= math.exp(-(c.Ballistic == 2 ? combatCfg.AttackDamping : combatCfg.LaunchDrag) * dt);
                        s.Gear = FlightGear.Hover;
                        break;
                    case CombatAction.Dash:
                        break; // velocity set by CombatSystem
                    case CombatAction.Light:
                    case CombatAction.HeavyStrike:
                    case CombatAction.HeavyCharge:
                        s.Velocity *= math.exp(-combatCfg.AttackDamping * dt);
                        s.Gear = FlightGear.Hover;
                        break;
                    default:
                        if (drives.HasComponent(entity)) DriveStep(ref s, drives[entity], dt);
                        else Step(ref s, input, fcfg, dt);
                        break;
                }

                // Can this body smash through building chunks right now?
                var speed = math.length(s.Velocity);
                var canBreak = hasCombatCfg && ((launched && speed >= combatCfg.BreakSpeed) || speed >= combatCfg.FlyingBreakSpeed);
                var damage = 0f;

                if (hasPhysics)
                    pos = MoveAndSlide(physics, pos, ref s.Velocity, cfg, combatCfg, dt, ref hits, chunks, canBreak, launched, ref broken, ref damage, entity);
                else
                    pos += s.Velocity * dt;

                if (damage > 0f && c.Action != CombatAction.KnockedOut)
                {
                    // Shared damage path: knocks out at zero. Runs in the predicted loop on client and
                    // server alike, from the same predicted inputs.
                    Damage.Apply(ref c, damage, tick, rate, combatCfg.RespawnDelay);
                    combat.ValueRW = c;
                }

                s.Supersonic = (byte)(math.length(s.Velocity) >= cfg.SupersonicSpeed ? 1 : 0);
                flight.ValueRW = s;
                transform.ValueRW.Position = pos;
                transform.ValueRW.Rotation = quaternion.EulerZXY(math.radians(new float3(s.Pitch, s.Yaw, s.Bank)));
            }

            // Only the server decides what breaks. It queues the chunks for BreakSystem.
            if (isServer && broken.Length > 0 && SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graph))
            {
                var queue = SystemAPI.GetBuffer<BreakRequest>(graph);
                queue.AddRange(broken.AsArray());
            }
        }

        /// <summary>AI body (response unit): steer toward the requested velocity and turn to face.</summary>
        static void DriveStep(ref FlightState s, in ToLive.Opposition.UnitDrive d, float dt)
        {
            s.Gear = FlightGear.Hover;
            s.Velocity = MoveTowards(s.Velocity, d.Wish, math.max(1f, d.Accel) * dt);
            s.Yaw = MoveTowardsAngle(s.Yaw, d.FaceYaw, 120f * dt);
            s.Pitch = MoveTowardsAngle(s.Pitch, 0f, 120f * dt);
            s.Bank = 0f;
        }

        static void Step(ref FlightState s, in PlayerInput input, in FlightConfig cfg, float dt)
        {
            var thrusting = input.Thrust > cfg.ThrustDeadzone;
            var boosting = thrusting && input.Boost != 0;
            s.Gear = boosting ? FlightGear.Boost : thrusting ? FlightGear.Cruise : FlightGear.Hover;

            var lookRot = quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f)));
            var lookForward = math.mul(lookRot, new float3(0, 0, 1));
            var lookRight = math.mul(lookRot, new float3(1, 0, 0));
            var prevYaw = s.Yaw;
            var speed = math.length(s.Velocity);

            if (s.Gear == FlightGear.Hover)
            {
                // Full 3D movement relative to the camera, no gravity.
                var wish = lookRight * input.Move.x + lookForward * input.Move.y + new float3(0, 1, 0) * input.Vertical;
                var wishLen = math.length(wish);
                if (wishLen > 1f) wish /= wishLen;
                var target = wish * cfg.HoverMaxSpeed;
                var rate = speed > cfg.HoverMaxSpeed + 1f ? cfg.HoverOverspeedDeceleration : cfg.HoverAcceleration;
                s.Velocity = MoveTowards(s.Velocity, target, rate * dt);
                if (wishLen < 0.05f)
                    s.Velocity *= math.exp(-cfg.HoverDamping * dt); // strong damping: stop precisely

                // Body turns to face the camera's heading, levels out.
                s.Yaw = MoveTowardsAngle(s.Yaw, input.LookYaw, cfg.HoverTurnRate * dt);
                s.Pitch = MoveTowardsAngle(s.Pitch, 0f, cfg.HoverTurnRate * dt);
            }
            else
            {
                // Cruise / boost: turn the body toward the camera look direction at a limited rate,
                // then fly along the body's forward vector.
                var turnRate = s.Gear == FlightGear.Boost ? cfg.BoostTurnRate : cfg.CruiseTurnRate;
                s.Yaw = MoveTowardsAngle(s.Yaw, input.LookYaw, turnRate * dt);
                s.Pitch = MoveTowardsAngle(s.Pitch, math.clamp(input.LookPitch, -85f, 85f), turnRate * dt);

                float targetSpeed, accel;
                if (s.Gear == FlightGear.Boost)
                {
                    targetSpeed = cfg.BoostMaxSpeed;
                    accel = cfg.BoostAcceleration;
                }
                else
                {
                    targetSpeed = cfg.CruiseMaxSpeed * math.saturate(input.Thrust);
                    // Above cruise speed (boost released) decay with BoostDecay, otherwise normal.
                    accel = speed > targetSpeed
                        ? (speed > cfg.CruiseMaxSpeed + 1f ? cfg.BoostDecay : cfg.CruiseDeceleration)
                        : cfg.CruiseAcceleration;
                }
                var newSpeed = MoveTowards(speed, targetSpeed, accel * dt);
                var bodyForward = math.mul(quaternion.EulerZXY(math.radians(new float3(s.Pitch, s.Yaw, 0f))), new float3(0, 0, 1));
                s.Velocity = bodyForward * newSpeed;
            }

            // Bank into turns: roll proportional to the current yaw rate.
            var yawRate = math.abs(dt) > 0f ? DeltaAngle(prevYaw, s.Yaw) / dt : 0f;
            var maxRate = s.Gear == FlightGear.Boost ? cfg.BoostTurnRate : cfg.CruiseTurnRate;
            var targetBank = s.Gear == FlightGear.Hover ? 0f : -math.clamp(yawRate / math.max(maxRate, 1f), -1f, 1f) * cfg.MaxBankAngle;
            s.Bank = math.lerp(s.Bank, targetBank, math.saturate(cfg.BankResponsiveness * dt));
        }

        /// <summary>
        /// Sweep a sphere along the motion and slide along whatever we hit (walls, roofs, ground).
        /// Velocity into a surface is removed, so you can graze along buildings.
        /// When <paramref name="canBreak"/> is true, breakable building chunks are smashed instead:
        /// the body passes through, loses some speed and takes a little damage. This runs the same
        /// way on client and server, so prediction agrees; only the server turns it into real breaks.
        /// </summary>
        static float3 MoveAndSlide(in PhysicsWorldSingleton physics, float3 pos, ref float3 velocity,
            in FlightConfig cfg, in CombatConfig combatCfg, float dt, ref NativeList<DistanceHit> hits,
            ComponentLookup<BuildingChunk> chunks, bool canBreak, bool launched,
            ref NativeList<BreakRequest> broken, ref float damage, Entity self)
        {
            var filter = CollisionFilter.Default;
            var radius = cfg.CollisionRadius;
            var skin = cfg.CollisionSkin;

            // 1) Push out of anything we already overlap (e.g. a correction placed us inside a wall).
            hits.Clear();
            if (physics.OverlapSphere(pos, radius, ref hits, filter))
            {
                for (var i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (h.Distance >= 0f) continue;
                    if (canBreak && chunks.HasComponent(h.Entity)) continue; // we're smashing through it
                    pos += h.SurfaceNormal * (-h.Distance + skin);
                }
            }

            // 2) Sweep and slide, up to 6 contacts per tick (chunks smashed don't stop us).
            var collector = new IgnoreListClosestHitCollector(1f);
            var delta = velocity * dt;
            for (var iteration = 0; iteration < 6; iteration++)
            {
                var len = math.length(delta);
                if (len < 1e-5f) break;
                var dir = delta / len;

                collector.Reset(1f);
                if (!physics.SphereCastCustom(pos, radius, dir, len + skin, ref collector, filter) ||
                    math.dot(collector.Hit.SurfaceNormal, dir) >= 0f)
                {
                    pos += delta;
                    break;
                }
                var hit = collector.Hit;

                if (canBreak && chunks.HasComponent(hit.Entity))
                {
                    // Smash: keep going through the chunk, a bit slower.
                    collector.Ignore(hit.Entity);
                    broken.Add(new BreakRequest { Chunk = chunks[hit.Entity].Index, Velocity = velocity, Instigator = self });
                    var keep = 1f - combatCfg.ChunkSpeedLoss;
                    velocity *= keep;
                    delta *= keep;
                    damage += combatCfg.ChunkImpactDamage;
                    continue;
                }

                var travel = math.max(hit.Fraction * (len + skin) - skin, 0f);
                pos += dir * travel;

                var n = hit.SurfaceNormal;
                var remaining = dir * (len - travel);
                delta = remaining - n * math.dot(remaining, n);
                var into = math.dot(velocity, n);
                if (into < 0f)
                {
                    // A launched body slamming into unbreakable walls/ground gets hurt.
                    if (launched && -into > combatCfg.WallImpactSpeed)
                        damage += (-into - combatCfg.WallImpactSpeed) * combatCfg.WallImpactDamagePerMs;
                    velocity -= n * into;
                }
            }
            return pos;
        }

        /// <summary>Closest-hit collector that skips chunks we already smashed this step.</summary>
        struct IgnoreListClosestHitCollector : ICollector<ColliderCastHit>
        {
            FixedList128Bytes<Entity> m_Ignored;
            public ColliderCastHit Hit;
            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction { get; private set; }
            public int NumHits { get; private set; }

            public IgnoreListClosestHitCollector(float maxFraction)
            {
                m_Ignored = default;
                Hit = default;
                MaxFraction = maxFraction;
                NumHits = 0;
            }

            public void Reset(float maxFraction)
            {
                MaxFraction = maxFraction;
                NumHits = 0;
                Hit = default;
            }

            public void Ignore(Entity e)
            {
                if (m_Ignored.Length < m_Ignored.Capacity) m_Ignored.Add(e);
            }

            public bool AddHit(ColliderCastHit hit)
            {
                for (var i = 0; i < m_Ignored.Length; i++)
                    if (m_Ignored[i] == hit.Entity) return false;
                MaxFraction = hit.Fraction;
                Hit = hit;
                NumHits = 1;
                return true;
            }
        }

        static float3 MoveTowards(float3 current, float3 target, float maxDelta)
        {
            var d = target - current;
            var len = math.length(d);
            return len <= maxDelta || len < 1e-6f ? target : current + d / len * maxDelta;
        }

        static float MoveTowards(float current, float target, float maxDelta)
        {
            return math.abs(target - current) <= maxDelta ? target : current + math.sign(target - current) * maxDelta;
        }

        static float DeltaAngle(float from, float to)
        {
            var d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        static float MoveTowardsAngle(float current, float target, float maxDelta)
        {
            var d = DeltaAngle(current, target);
            var result = math.abs(d) <= maxDelta ? current + d : current + math.sign(d) * maxDelta;
            // keep in [-180, 180] so quantized ghost values stay small
            result %= 360f;
            if (result > 180f) result -= 360f;
            if (result < -180f) result += 360f;
            return result;
        }
    }
}
