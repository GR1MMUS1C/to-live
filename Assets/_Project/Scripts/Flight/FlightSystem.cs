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
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var cfg = SystemAPI.GetSingleton<FlightConfig>();
            var dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f) return;

            var hasPhysics = SystemAPI.TryGetSingleton<PhysicsWorldSingleton>(out var physics);
            var hits = new NativeList<DistanceHit>(8, Allocator.Temp);

            // WithAll<Simulate>: Netcode enables Simulate only on ghosts this world should simulate
            // this tick (all of them on the server, just your own player on a client).
            foreach (var (input, flight, transform) in
                     SystemAPI.Query<RefRO<PlayerInput>, RefRW<FlightState>, RefRW<LocalTransform>>().WithAll<Simulate>())
            {
                var s = flight.ValueRO;
                var pos = transform.ValueRO.Position;
                Step(ref s, input.ValueRO, cfg, dt);

                if (hasPhysics)
                    pos = MoveAndSlide(physics, pos, ref s.Velocity, cfg, dt, ref hits);
                else
                    pos += s.Velocity * dt;

                s.Supersonic = (byte)(math.length(s.Velocity) >= cfg.SupersonicSpeed ? 1 : 0);
                flight.ValueRW = s;
                transform.ValueRW.Position = pos;
                transform.ValueRW.Rotation = quaternion.EulerZXY(math.radians(new float3(s.Pitch, s.Yaw, s.Bank)));
            }
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
        /// Velocity into a surface is removed, so you can graze along buildings. No damage.
        /// </summary>
        static float3 MoveAndSlide(in PhysicsWorldSingleton physics, float3 pos, ref float3 velocity,
            in FlightConfig cfg, float dt, ref NativeList<DistanceHit> hits)
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
                    if (h.Distance < 0f)
                        pos += h.SurfaceNormal * (-h.Distance + skin);
                }
            }

            // 2) Sweep and slide, up to 4 contacts per tick.
            var delta = velocity * dt;
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var len = math.length(delta);
                if (len < 1e-5f) break;
                var dir = delta / len;

                if (!physics.SphereCast(pos, radius, dir, len + skin, out var hit, filter) ||
                    math.dot(hit.SurfaceNormal, dir) >= 0f)
                {
                    pos += delta;
                    break;
                }

                var travel = math.max(hit.Fraction * (len + skin) - skin, 0f);
                pos += dir * travel;

                var n = hit.SurfaceNormal;
                var remaining = dir * (len - travel);
                delta = remaining - n * math.dot(remaining, n);
                var into = math.dot(velocity, n);
                if (into < 0f) velocity -= n * into;
            }
            return pos;
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
