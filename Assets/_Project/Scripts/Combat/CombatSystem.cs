using ToLive.Flight;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace ToLive.Combat
{
    /// <summary>
    /// The combat state machine: lock-on, light chain, heavy charge, dash, guard, hit detection.
    ///
    /// Runs in the prediction loop BEFORE flight, so the attacker's own client predicts its moves
    /// instantly (like an Unreal GAS ability with local prediction). Hits are only APPLIED on the
    /// server: the server checks the strike against where each target was on the attacker's screen
    /// (positions rewound by the attacker's interpolation delay, the documented "server-side rewind"
    /// approach), then writes damage/knockback into the victim's predicted state, which the
    /// victim's client receives in the next snapshot. The attacking client also predicts its own
    /// hit-stop and hit effects so the impact feels immediate.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(FlightSystem))]
    public partial struct CombatSystem : ISystem
    {
        const int HistoryLength = 32;

        struct TargetInfo
        {
            public Entity Entity;
            public float3 Position;
            public byte Action;
        }

        struct HitEvent
        {
            public Entity Attacker, Victim;
            public byte Step;          // 1-3 light, 0 heavy
            public float Charge;
            public float3 FacingDir;   // attacker facing (flat-ish)
            public float3 AimDir;      // attacker camera direction (for finishers)
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency(); // we read lookups on the main thread
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var cfg = SystemAPI.GetSingleton<CombatConfig>();
            var tick = net.ServerTick.TickIndexForValidTick;
            var rate = TickRate(ref state);
            var isServer = state.WorldUnmanaged.IsServer();
            var firstTime = net.IsFirstTimeFullyPredictingTick;
            var dt = SystemAPI.Time.DeltaTime;

            // Snapshot of every target this tick.
            var targets = new NativeList<TargetInfo>(8, Allocator.Temp);
            foreach (var (xf, cs, e) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<CombatState>>().WithAll<CombatTarget>().WithEntityAccess())
                targets.Add(new TargetInfo { Entity = e, Position = xf.ValueRO.Position, Action = cs.ValueRO.Action });

            var inputs = SystemAPI.GetComponentLookup<PlayerInput>(true);
            var delays = SystemAPI.GetComponentLookup<CommandDataInterpolationDelay>(true);
            var histories = SystemAPI.GetBufferLookup<PositionHistory>(true);
            var dummies = SystemAPI.GetComponentLookup<TrainingDummy>(true);
            var powerStates = SystemAPI.GetComponentLookup<ToLive.Power.PowerState>(true);
            var moveStates = SystemAPI.GetComponentLookup<ToLive.Power.MoveState>(true);
            var hits = new NativeList<HitEvent>(4, Allocator.Temp);

            foreach (var (combat, flight, xf, self) in
                     SystemAPI.Query<RefRW<CombatState>, RefRW<FlightState>, RefRO<LocalTransform>>().WithAll<Simulate>().WithEntityAccess())
            {
                var c = combat.ValueRO;
                var f = flight.ValueRO;
                var pos = xf.ValueRO.Position;
                var hasInput = inputs.HasComponent(self);
                var input = hasInput ? inputs[self] : default;

                if (c.Action == CombatAction.KnockedOut) continue;       // server respawn system handles it
                if (c.Action == CombatAction.Grabbed) continue;          // held: only mashing (PowerSystem)
                if (powerStates.HasComponent(self) && powerStates[self].Phase == ToLive.Power.SurgePhase.Ritual) continue; // locked
                if (moveStates.HasComponent(self) && (moveStates[self].Carrying != Entity.Null || moveStates[self].Beam != 0)) { combat.ValueRW = c; flight.ValueRW = f; continue; }
                if (Before(tick, c.HitStopUntilTick)) continue;          // frozen by hit-stop
                if (c.Action == CombatAction.Launched)
                {
                    if (!Before(tick, c.ActionEndTick)) c.Action = CombatAction.None;
                    else { combat.ValueRW = c; continue; }
                }

                var aimDir = math.mul(quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f))), new float3(0, 0, 1));
                if (!hasInput) aimDir = math.mul(quaternion.EulerZXY(math.radians(new float3(f.Pitch, f.Yaw, 0f))), new float3(0, 0, 1));

                // ---- Lock-on ----
                if (input.LockOn.IsSet)
                    c.LockTarget = c.LockTarget == Entity.Null ? PickTarget(self, pos, aimDir, targets, cfg) : Entity.Null;
                var lockValid = TryGetTarget(c.LockTarget, targets, out var lockInfo) &&
                                lockInfo.Action != CombatAction.KnockedOut &&
                                math.distance(lockInfo.Position, pos) <= cfg.LockBreakRange;
                if (!lockValid) c.LockTarget = Entity.Null;

                // ---- Guard ----
                var dummyGuard = dummies.HasComponent(self) && dummies[self].Guard != 0;
                c.Guarding = (byte)((input.GuardHeld != 0 || dummyGuard) && c.Action == CombatAction.None ? 1 : 0);

                // ---- Dash ----
                if (input.Dash.IsSet && c.Guarding == 0 && !Before(tick, c.DashReadyTick) &&
                    (c.Action == CombatAction.None || c.Action == CombatAction.Light))
                {
                    var dir = aimDir;
                    if (lockValid) dir = math.normalizesafe(lockInfo.Position - pos, aimDir);
                    else if (math.lengthsq(input.Move) > 0.01f || math.abs(input.Vertical) > 0.1f)
                    {
                        var look = quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f)));
                        dir = math.normalizesafe(math.mul(look, new float3(input.Move.x, 0, input.Move.y)) + new float3(0, input.Vertical, 0), aimDir);
                    }
                    c.Action = CombatAction.Dash;
                    c.ActionStartTick = tick;
                    c.ActionEndTick = tick + Ticks(cfg.DashDuration, rate);
                    c.DashReadyTick = tick + Ticks(cfg.DashDuration + cfg.DashCooldown, rate);
                    c.ComboStep = 0; c.QueuedLight = 0;
                    f.Velocity = dir * cfg.DashSpeed;
                    Face(ref f, dir);
                }
                if (c.Action == CombatAction.Dash)
                {
                    if (!Before(tick, c.ActionEndTick)) { c.Action = CombatAction.None; f.Velocity *= 0.5f; }
                    else if (lockValid)
                    {
                        // keep steering toward a moving (e.g. launched) target while dashing
                        var d = math.normalizesafe(lockInfo.Position - pos, aimDir);
                        f.Velocity = d * cfg.DashSpeed;
                        Face(ref f, d);
                    }
                }

                // ---- Light chain ----
                var lightPressed = input.Light.IsSet && c.Guarding == 0;
                if (lightPressed)
                {
                    if (c.Action == CombatAction.None || c.Action == CombatAction.Dash)
                        StartLight(ref c, 1, tick);
                    else if (c.Action == CombatAction.Light && c.ComboStep < 3)
                        c.QueuedLight = 1;
                }

                // ---- Heavy ----
                if (c.Action == CombatAction.None && input.HeavyHeld != 0 && c.Guarding == 0 && !lightPressed)
                {
                    c.Action = CombatAction.HeavyCharge;
                    c.ActionStartTick = tick;
                    c.Charge = 0f;
                }
                if (c.Action == CombatAction.HeavyCharge)
                {
                    c.Charge = math.saturate((tick - c.ActionStartTick) / math.max(1f, cfg.HeavyMaxChargeTime * rate));
                    if (input.HeavyHeld == 0)
                    {
                        c.Action = CombatAction.HeavyStrike;
                        c.ActionStartTick = tick;
                        c.HitDone = 0;
                    }
                }

                // ---- Attack timeline: homing, hit check, chain ----
                if (c.Action == CombatAction.Light || c.Action == CombatAction.HeavyStrike)
                {
                    var isLight = c.Action == CombatAction.Light;
                    var elapsed = tick - c.ActionStartTick;
                    var activeTick = Ticks(isLight ? cfg.LightActiveTime : cfg.HeavyActiveTime, rate);
                    var duration = Ticks(isLight ? cfg.LightDuration : cfg.HeavyDuration, rate);

                    var facing = aimDir;
                    if (lockValid)
                    {
                        var to = lockInfo.Position - pos;
                        var dist = math.length(to);
                        facing = dist > 0.5f ? to / dist : aimDir;
                        if (elapsed < activeTick && dist < cfg.HomingRange)
                        {
                            if (dist > cfg.StrikeDistance + 0.2f)
                            {
                                var remaining = math.max((activeTick - elapsed) / (float)rate, dt);
                                f.Velocity = facing * math.min(cfg.HomingMaxSpeed, (dist - cfg.StrikeDistance) / remaining);
                            }
                            else f.Velocity = float3.zero; // arrived: plant and strike
                        }
                    }
                    Face(ref f, facing);

                    if (elapsed >= activeTick && c.HitDone == 0)
                    {
                        c.HitDone = 1;
                        f.Velocity = float3.zero; // plant on the strike so homing never overshoots the target
                        var strike = pos + facing * cfg.HitReachForward;
                        var rewindTick = tick;
                        if (isServer && delays.HasComponent(self))
                            rewindTick = tick - delays[self].Delay - 1; // what the attacker saw (interp delay + 1 render frame)
                        var victim = FindVictim(self, strike, c.LockTarget, targets, cfg, isServer, rewindTick, histories);
                        if (victim != Entity.Null)
                        {
                            c.HitStopUntilTick = tick + Ticks(cfg.HitStopTime, rate);
                            hits.Add(new HitEvent
                            {
                                Attacker = self, Victim = victim, Step = isLight ? c.ComboStep : (byte)0,
                                Charge = c.Charge, FacingDir = facing, AimDir = aimDir,
                            });
                            if (!isServer && firstTime)
                                CombatFx.RaisePredictedHit(strike, isLight ? c.ComboStep : (byte)4);
                        }
                    }

                    if (elapsed >= duration)
                    {
                        if (isLight && c.QueuedLight != 0 && c.ComboStep < 3)
                            StartLight(ref c, (byte)(c.ComboStep + 1), tick);
                        else
                        {
                            c.Action = CombatAction.None;
                            c.ComboStep = 0;
                            c.QueuedLight = 0;
                            c.Charge = 0f;
                        }
                    }
                }

                combat.ValueRW = c;
                flight.ValueRW = f;
            }

            if (isServer && hits.Length > 0)
                ApplyHits(ref state, hits, cfg, tick, rate);
        }

        void ApplyHits(ref SystemState state, NativeList<HitEvent> hits, in CombatConfig cfg, uint tick, int rate)
        {
            var combats = SystemAPI.GetComponentLookup<CombatState>();
            var flights = SystemAPI.GetComponentLookup<FlightState>();
            var powers = SystemAPI.GetComponentLookup<ToLive.Power.PowerState>(true);
            var lastAttackers = SystemAPI.GetComponentLookup<ToLive.Power.LastAttacker>();
            var hasPowerCfg = SystemAPI.TryGetSingleton<ToLive.Power.PowerConfig>(out var powerCfg);
            var fuel = new NativeList<(Entity e, float amount)>(Allocator.Temp);
            foreach (var hit in hits)
            {
                if (!combats.HasComponent(hit.Victim)) continue;
                var v = combats[hit.Victim];
                var vf = flights[hit.Victim];
                if (v.Action == CombatAction.KnockedOut) continue;

                float damage, speed, duration;
                float3 dir;
                var launch = true;
                byte ballistic = 1;
                var flat = math.normalizesafe(new float3(hit.FacingDir.x, 0, hit.FacingDir.z), hit.FacingDir);

                if (v.Action == CombatAction.Launched && v.Ballistic != 2)
                {
                    // FINISHER: a launched target hit again is driven hard where the attacker aims.
                    damage = cfg.FinisherDamage; speed = cfg.FinisherSpeed; duration = cfg.FinisherDuration;
                    dir = math.normalizesafe(hit.AimDir, hit.FacingDir);
                    ballistic = 0;
                }
                else if (hit.Step == 3)
                {
                    damage = cfg.LauncherDamage; speed = cfg.LauncherSpeed; duration = cfg.LauncherDuration;
                    dir = math.normalize(flat + new float3(0, cfg.LauncherUpBias, 0));
                }
                else if (hit.Step == 0)
                {
                    damage = math.lerp(cfg.HeavyMinDamage, cfg.HeavyMaxDamage, hit.Charge);
                    speed = math.lerp(cfg.HeavyMinSpeed, cfg.HeavyMaxSpeed, hit.Charge);
                    duration = cfg.HeavyLaunchDuration;
                    dir = math.normalizesafe(hit.AimDir + flat, flat);
                }
                else
                {
                    damage = cfg.LightDamage; speed = cfg.LightKnockback; duration = cfg.LightStagger;
                    dir = flat;
                    launch = false;
                }

                // Guard: from the front it soaks most of the hit and prevents launching.
                var victimFacing = math.mul(quaternion.RotateY(math.radians(vf.Yaw)), new float3(0, 0, 1));
                if (v.Guarding != 0 && math.dot(victimFacing, -flat) > cfg.GuardFrontDot)
                {
                    damage *= cfg.GuardDamageScale;
                    speed *= cfg.GuardKnockbackScale;
                    launch = false;
                    duration = cfg.LightStagger;
                }

                // Power: attacker's tier/surge multiplies damage and knockback; the victim's phase defends.
                var victimInRitual = false;
                if (hasPowerCfg)
                {
                    var am = powers.HasComponent(hit.Attacker) ? powerCfg.Mods(powers[hit.Attacker]) : ToLive.Power.PowerMods.One;
                    var vp = powers.HasComponent(hit.Victim) ? powers[hit.Victim] : default;
                    var vm = powerCfg.Mods(vp);
                    damage = damage * am.Damage / vm.Defense;
                    speed *= am.Knockback;
                    victimInRitual = vp.Phase == ToLive.Power.SurgePhase.Ritual;
                }

                v.LastHitTick = tick;
                v.LastHitStrength = speed;
                v.HitStopUntilTick = tick + Ticks(cfg.HitStopTime, rate);
                v.ComboStep = 0; v.QueuedLight = 0; v.Charge = 0f; v.HitDone = 1;
                if (!victimInRitual) // the ritual can't be interrupted by knockback
                {
                    v.Action = CombatAction.Launched;
                    v.ActionStartTick = tick;
                    v.ActionEndTick = tick + Ticks(duration, rate);
                    v.Ballistic = launch ? ballistic : (byte)2; // 2 = stagger: no gravity, can't be finished
                    vf.Velocity = dir * speed;
                }
                if (lastAttackers.HasComponent(hit.Victim)) lastAttackers[hit.Victim] = new ToLive.Power.LastAttacker { Value = hit.Attacker };
                fuel.Add((hit.Attacker, damage * (hasPowerCfg ? powerCfg.FuelPerDamageDealt : 1f)));
                fuel.Add((hit.Victim, damage * (hasPowerCfg ? powerCfg.FuelPerDamageTaken : 0.6f)));

                Damage.Apply(ref v, damage, tick, rate, cfg.RespawnDelay); // knocks out at zero (keeps the launch velocity)

                combats[hit.Victim] = v;
                flights[hit.Victim] = vf;

                // attacker shares the hit-stop
                if (combats.HasComponent(hit.Attacker))
                {
                    var a = combats[hit.Attacker];
                    a.HitStopUntilTick = tick + Ticks(cfg.HitStopTime, rate);
                    combats[hit.Attacker] = a;
                }
            }
            foreach (var (e, amount) in fuel) ToLive.Power.SurgeFuel.Add(state.EntityManager, e, amount);
        }

        static Entity FindVictim(Entity self, float3 strike, Entity lockTarget, NativeList<TargetInfo> targets,
            in CombatConfig cfg, bool isServer, uint rewindTick, BufferLookup<PositionHistory> histories)
        {
            var best = Entity.Null;
            var bestDist = float.MaxValue;
            foreach (var t in targets)
            {
                if (t.Entity == self || t.Action == CombatAction.KnockedOut) continue;
                var p = t.Position;
                if (isServer && histories.HasBuffer(t.Entity))
                {
                    var h = histories[t.Entity];
                    if (h.Length == HistoryLength)
                    {
                        var entry = h[(int)(rewindTick % HistoryLength)];
                        if (entry.Tick == rewindTick) p = entry.Position;
                    }
                }
                var d = math.distance(p, strike);
                if (d > cfg.HitRange) continue;
                if (t.Entity == lockTarget) return t.Entity; // lock target wins if in range
                if (d < bestDist) { bestDist = d; best = t.Entity; }
            }
            return best;
        }

        static Entity PickTarget(Entity self, float3 pos, float3 aim, NativeList<TargetInfo> targets, in CombatConfig cfg)
        {
            var best = Entity.Null;
            var bestScore = float.MaxValue;
            foreach (var t in targets)
            {
                if (t.Entity == self || t.Action == CombatAction.KnockedOut) continue;
                var to = t.Position - pos;
                var d = math.length(to);
                if (d > cfg.LockRange || d < 0.01f) continue;
                var dot = math.dot(to / d, aim);
                if (dot < cfg.LockConeDot) continue;
                var score = d * (2f - dot); // prefer close and centred
                if (score < bestScore) { bestScore = score; best = t.Entity; }
            }
            return best;
        }

        static bool TryGetTarget(Entity e, NativeList<TargetInfo> targets, out TargetInfo info)
        {
            info = default;
            if (e == Entity.Null) return false;
            foreach (var t in targets)
                if (t.Entity == e) { info = t; return true; }
            return false;
        }

        static void StartLight(ref CombatState c, byte step, uint tick)
        {
            c.Action = CombatAction.Light;
            c.ComboStep = step;
            c.QueuedLight = 0;
            c.HitDone = 0;
            c.ActionStartTick = tick;
        }

        static void Face(ref FlightState f, float3 dir)
        {
            if (math.lengthsq(dir) < 1e-6f) return;
            f.Yaw = math.degrees(math.atan2(dir.x, dir.z));
            f.Pitch = math.clamp(-math.degrees(math.asin(math.clamp(dir.y, -1f, 1f))), -80f, 80f);
        }

        /// <summary>true while <paramref name="tick"/> is before <paramref name="until"/> (wrap-safe).</summary>
        public static bool Before(uint tick, uint until) => (int)(until - tick) > 0;

        public static uint Ticks(float seconds, int rate) => (uint)math.max(1, (int)math.round(seconds * rate));

        int TickRate(ref SystemState state)
        {
            foreach (var r in SystemAPI.Query<RefRO<ClientServerTickRate>>())
                if (r.ValueRO.SimulationTickRate > 0) return r.ValueRO.SimulationTickRate;
            return 60;
        }
    }
}
