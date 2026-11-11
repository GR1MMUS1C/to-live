using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Rival
{
    /// <summary>
    /// SERVER: the AI rival. It is a normal player character (same prefab, flight, combat, power,
    /// beam, grab and ritual) whose "gamepad" is this brain: every tick it writes a
    /// <see cref="PlayerInput"/> into the character's Netcode input buffer, exactly where a client's
    /// input would arrive. So it can't do anything a player can't, and it looks and syncs like one.
    /// Unreal analogy: an AIController possessing the same Pawn class as the player and pressing the
    /// same input actions. It decides after the prediction step of tick T and writes the input for
    /// T+1 (one tick of "reaction", like a client's input arriving for an upcoming tick).
    ///
    /// Also owns the spawn rule: present when one human is connected, absent with two, overridable by
    /// the debug key; and its longer respawn after a knock-out.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))] // the prediction group is OrderFirst: we decide from this tick's result for the next tick
    public partial struct RivalSystem : ISystem
    {
        struct Foe
        {
            public Entity Entity;
            public float3 Position, Velocity;
            public CombatState Combat;
            public bool IsUnit;
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RivalConfig>();
            state.RequireForUpdate<PlayerSpawner>();
            state.RequireForUpdate<HeatState>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<RivalConfig>();
            var combatCfg = SystemAPI.TryGetSingleton<CombatConfig>(out var cc) ? cc : default;
            var hasPowerCfg = SystemAPI.TryGetSingleton<PowerConfig>(out var powerCfg);
            var rate = Damage.TickRate(em);
            var heatEntity = SystemAPI.GetSingletonEntity<HeatState>();
            var heat = em.GetComponentData<HeatState>(heatEntity);

            // ---- debug requests ----
            var rivalExists = heat.Rival != Entity.Null && em.Exists(heat.Rival);
            if (SystemAPI.TryGetSingletonRW<HeatDirector>(out var director))
            {
                if (director.ValueRO.PendingRivalToggle != 0)
                {
                    heat.RivalMode = rivalExists ? (byte)2 : (byte)1;
                    director.ValueRW.PendingRivalToggle = 0;
                    Debug.Log($"[ToLive] Debug: rival {(heat.RivalMode == 1 ? "forced on" : "forced off")}");
                }
                if (director.ValueRO.PendingRivalDifficulty != 0)
                {
                    heat.RivalDifficulty = (byte)((heat.RivalDifficulty + 1) % 3);
                    director.ValueRW.PendingRivalDifficulty = 0;
                    Debug.Log($"[ToLive] Debug: rival difficulty {heat.RivalDifficulty}");
                }
            }

            // ---- spawn rule ----
            var humans = SystemAPI.QueryBuilder().WithAll<NetworkId, NetworkStreamInGame>().Build().CalculateEntityCount();
            var wanted = heat.RivalMode == 1 || (heat.RivalMode == 0 && humans == 1);
            if (wanted && !rivalExists)
            {
                heat.Rival = SpawnRival(ref state, cfg, combatCfg, tick);
                heat.RivalRespawnTick = 0;
                rivalExists = heat.Rival != Entity.Null;
            }
            else if (!wanted && rivalExists)
            {
                em.DestroyEntity(heat.Rival);
                heat.Rival = Entity.Null;
                heat.RivalRespawnTick = 0;
                rivalExists = false;
                Debug.Log("[ToLive] Rival removed");
            }

            if (rivalExists)
            {
                var self = heat.Rival;
                var c = em.GetComponentData<CombatState>(self);
                if (c.Action == CombatAction.KnockedOut)
                {
                    // longer respawn than a player, announced on the HUD
                    if (heat.RivalRespawnTick == 0)
                    {
                        heat.RivalRespawnTick = tick + CombatSystem.Ticks(cfg.RespawnDelay, rate);
                        heat.RivalDowns++;
                        Debug.Log($"[ToLive] Rival knocked out (back in {cfg.RespawnDelay:0} s)");
                    }
                    else if (!CombatSystem.Before(tick, heat.RivalRespawnTick))
                    {
                        RespawnRival(ref state, self, cfg, combatCfg, hasPowerCfg, powerCfg);
                        heat.RivalRespawnTick = 0;
                        Debug.Log("[ToLive] Rival respawned");
                    }
                    WriteInput(em, self, net.ServerTick, default, false, false, false, false);
                }
                else Think(ref state, self, cfg, combatCfg, hasPowerCfg, powerCfg, heat.RivalDifficulty, tick, rate, net.ServerTick);
            }
            em.SetComponentData(heatEntity, heat);
        }

        // ------------------------------------------------------------------ spawning

        static float3 SpawnPosition(ref SystemState state, in RivalConfig cfg)
        {
            var em = state.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerSpawner>());
            var points = em.GetBuffer<PlayerSpawnPoint>(q.GetSingletonEntity(), true);
            return points.Length > 0 ? points[math.abs(cfg.SpawnPoint) % points.Length].Position : new float3(0, 120, 0);
        }

        static Entity SpawnRival(ref SystemState state, in RivalConfig cfg, in CombatConfig combatCfg, uint tick)
        {
            var em = state.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerSpawner>());
            var prefab = q.GetSingleton<PlayerSpawner>().PlayerPrefab;
            if (prefab == Entity.Null) return Entity.Null;

            // dominance-leaning by default, opposite of the first human's alignment when that leans dominance
            var alignment = math.abs(cfg.Alignment);
            using (var humans = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<PowerState>()).ToEntityArray(Allocator.Temp))
                foreach (var h in humans)
                    if (em.GetComponentData<CharacterKind>(h).Kind == CharacterKind.Human && em.GetComponentData<PowerState>(h).Alignment > 0.2f)
                    { alignment = -alignment; break; }

            var pos = SpawnPosition(ref state, cfg);
            var yaw = math.degrees(math.atan2(-pos.x, -pos.z));
            var e = em.Instantiate(prefab);
            em.SetComponentData(e, LocalTransform.FromPositionRotation(pos, quaternion.RotateY(math.radians(yaw))));
            em.SetComponentData(e, new FlightState { Yaw = yaw });
            em.SetComponentData(e, new CombatState { Power = combatCfg.MaxPower > 0f ? combatCfg.MaxPower : 100f });
            em.SetComponentData(e, new CharacterKind { Kind = CharacterKind.Rival });
            em.SetComponentData(e, new GhostOwner { NetworkId = -1 }); // owner-predicted prefab, owned by no player (interpolated on every client)
            em.SetComponentData(e, new PowerState { Tier = 1, BeamEnergy = 1f, Alignment = alignment });
            em.SetComponentData(e, new RivalBrain { Seed = tick * 747796405u + 2891336453u, StrafeSign = 1f });
            Debug.Log($"[ToLive] Rival spawned at {pos} (alignment {alignment:0.0})");
            return e;
        }

        static void RespawnRival(ref SystemState state, Entity self, in RivalConfig cfg, in CombatConfig combatCfg, bool hasPowerCfg, in PowerConfig powerCfg)
        {
            var em = state.EntityManager;
            var pos = SpawnPosition(ref state, cfg);
            // milestone 4B: a beaten rival comes back at the district edge (drained and expelled)
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Crisis.CrisisConfig>()))
                if (cq.CalculateEntityCount() == 1)
                    pos = ToLive.Crisis.DefeatSystem.EdgePoint(em.GetComponentData<LocalTransform>(self).Position, cq.GetSingleton<ToLive.Crisis.CrisisConfig>().ExpelDistance);
            var yaw = math.degrees(math.atan2(-pos.x, -pos.z));
            var c = em.GetComponentData<CombatState>(self);
            var f = em.GetComponentData<FlightState>(self);
            var xf = em.GetComponentData<LocalTransform>(self);
            var p = em.GetComponentData<PowerState>(self);
            var max = hasPowerCfg ? powerCfg.MaxPower(combatCfg.MaxPower, p) : combatCfg.MaxPower;
            CombatServerSystem.ResetCharacter(ref c, ref f, ref xf, pos, yaw, max);
            c.LockTarget = Entity.Null;
            em.SetComponentData(self, c); em.SetComponentData(self, f); em.SetComponentData(self, xf);
            var b = em.GetComponentData<RivalBrain>(self);
            b.Act = RivalAct.Approach; b.GuardUntilTick = 0; b.GuardSucceeded = 0;
            em.SetComponentData(self, b);
        }

        // ------------------------------------------------------------------ the brain

        static void Think(ref SystemState state, Entity self, in RivalConfig cfg, in CombatConfig combatCfg, bool hasPowerCfg, in PowerConfig powerCfg,
            byte difficulty, uint tick, int rate, NetworkTick serverTick)
        {
            var em = state.EntityManager;
            var preset = cfg.Preset(difficulty);
            var b = em.GetComponentData<RivalBrain>(self);
            var c = em.GetComponentData<CombatState>(self);
            var f = em.GetComponentData<FlightState>(self);
            var p = em.GetComponentData<PowerState>(self);
            var m = em.GetComponentData<MoveState>(self);
            var pos = em.GetComponentData<LocalTransform>(self).Position;
            var rnd = Unity.Mathematics.Random.CreateFromIndex(b.Seed ^ (tick * 2654435761u));
            var maxPower = hasPowerCfg ? powerCfg.MaxPower(combatCfg.MaxPower, p) : combatCfg.MaxPower;
            var grabRange = hasPowerCfg ? powerCfg.GrabRange : 4f;

            var inp = new PlayerInput { LookYaw = f.Yaw, LookPitch = 0f };
            bool pressLight = false, pressDash = false, pressLock = false, pressGrab = false;

            // Held by someone: mash to break free (a player would too).
            if (c.Action == CombatAction.Grabbed)
            {
                if (!CombatSystem.Before(tick, b.NextPressTick)) { pressLight = true; b.NextPressTick = tick + CombatSystem.Ticks(0.14f + preset.ReactionTime * 0.3f, rate); }
                em.SetComponentData(self, b);
                WriteInput(em, self, serverTick, inp, pressLight, false, false, false);
                return;
            }

            // ---- leash: never wander far out of the district (flying back like a player would) ----
            if (math.length(pos.xz) > 450f || pos.y > 420f)
            {
                var home = new float3(0f, 120f, 0f) - pos;
                var hl = math.length(home);
                var back = new PlayerInput { LookYaw = math.degrees(math.atan2(home.x, home.z)), LookPitch = math.clamp(-math.degrees(math.asin(math.clamp(home.y / math.max(1f, hl), -1f, 1f))), -70f, 70f), Thrust = 1f, Boost = (byte)(hl > 300f ? 1 : 0) };
                b.Act = RivalAct.Approach;
                em.SetComponentData(self, b);
                WriteInput(em, self, serverTick, back, false, false, false, false);
                return;
            }

            // ---- milestone 4B: crises. VANTA takes the opposite approach to the leading player ----
            if (CrisisPlan(ref state, self, pos, f, c, p, m, ref b, tick, rate, serverTick, preset, out var crisisFoe)) return;

            var foe = PickFoe(ref state, self, pos, cfg, out var found);
            if (crisisFoe != Entity.Null && em.Exists(crisisFoe))
            {
                foe = new Foe { Entity = crisisFoe, Position = em.GetComponentData<LocalTransform>(crisisFoe).Position, Velocity = em.GetComponentData<FlightState>(crisisFoe).Velocity, Combat = em.GetComponentData<CombatState>(crisisFoe), IsUnit = true };
                found = true;
            }
            if (!found)
            {
                // nobody to fight: drift toward the district centre and hover
                var toCentre = new float3(0, 90, 0) - pos;
                inp.LookYaw = math.degrees(math.atan2(toCentre.x, toCentre.z));
                inp.Move = new float2(0, math.lengthsq(toCentre) > 900f ? 1f : 0f);
                if (c.LockTarget != Entity.Null && !CombatSystem.Before(tick, b.LockPressTick)) { pressLock = true; b.LockPressTick = tick + CombatSystem.Ticks(0.3f, rate); }
                b.Act = RivalAct.Approach;
                em.SetComponentData(self, b);
                WriteInput(em, self, serverTick, inp, false, false, pressLock, false);
                return;
            }

            var to = foe.Position - pos;
            var dist = math.length(to);
            var dir = dist > 0.01f ? to / dist : new float3(0, 0, 1);
            var tc = foe.Combat;
            var dashReady = !CombatSystem.Before(tick, c.DashReadyTick);

            // aim at the foe, with human-like error (re-rolled on each decision)
            var aimYaw = math.degrees(math.atan2(dir.x, dir.z)) + b.AimNoise.x;
            var aimPitch = math.clamp(-math.degrees(math.asin(math.clamp(dir.y, -1f, 1f))) + b.AimNoise.y, -85f, 85f);
            inp.LookYaw = aimYaw; inp.LookPitch = aimPitch;

            // ---- forced acts ----
            if (m.Carrying != Entity.Null) { if (b.Act != RivalAct.Carry) { b.Act = RivalAct.Carry; b.ActTick = tick; } }
            if (p.Phase == SurgePhase.Ritual) b.Act = RivalAct.Ritual;

            // ---- reactive guard: notice an attack, react after the delay, maybe guard ----
            var attacking = !foe.IsUnit && dist < 8f &&
                            (tc.Action == CombatAction.Light || tc.Action == CombatAction.HeavyCharge || tc.Action == CombatAction.HeavyStrike || tc.Action == CombatAction.Dash);
            if (attacking && tc.ActionStartTick != b.SeenAttackTick)
            {
                b.SeenAttackTick = tc.ActionStartTick;
                var free = b.Act != RivalAct.Ritual && b.Act != RivalAct.Carry && b.Act != RivalAct.Beam && b.Act != RivalAct.Finisher;
                if (free && rnd.NextFloat() < preset.GuardChance)
                {
                    b.GuardFromTick = tick + CombatSystem.Ticks(preset.ReactionTime, rate);
                    b.GuardUntilTick = b.GuardFromTick + CombatSystem.Ticks(0.6f, rate);
                }
            }
            var guarding = b.GuardUntilTick != 0 && !CombatSystem.Before(tick, b.GuardFromTick) && CombatSystem.Before(tick, b.GuardUntilTick);
            if (guarding)
            {
                if (attacking) b.GuardUntilTick = math.max(b.GuardUntilTick, tick + CombatSystem.Ticks(0.25f, rate));
                if (c.LastHitTick != 0 && tick - c.LastHitTick <= 2) b.GuardSucceeded = 1; // blocked a hit
            }
            else if (b.GuardUntilTick != 0 && !CombatSystem.Before(tick, b.GuardUntilTick))
            {
                b.GuardUntilTick = 0;
                if (b.GuardSucceeded != 0) { b.Act = RivalAct.Heavy; b.ActTick = tick; } // punish with a heavy after the guard
                b.GuardSucceeded = 0;
            }

            // ---- decide (on a cadence, when not in the middle of something) ----
            var interruptible = b.Act == RivalAct.Approach || b.Act == RivalAct.Circle;
            if (interruptible && !CombatSystem.Before(tick, b.NextThinkTick) && !guarding)
            {
                b.AimNoise = rnd.NextFloat2(-1f, 1f) * preset.AimNoise;
                b.NextThinkTick = tick + CombatSystem.Ticks(preset.ThinkInterval * rnd.NextFloat(0.8f, 1.25f), rate);
                var next = dist > cfg.CircleRange * 1.6f ? RivalAct.Approach : RivalAct.Circle;
                var ritualReady = p.Meter >= 0.999f && p.Phase == SurgePhase.None;
                if (ritualReady && dist > 10f && (c.Power < cfg.RitualPowerThreshold * maxPower || rnd.NextFloat() < 0.5f)) next = RivalAct.Ritual;
                else if (c.Power < cfg.DisengagePower * maxPower && p.Phase != SurgePhase.Surge) next = RivalAct.Disengage;
                else if (tc.Action == CombatAction.Launched && tc.Ballistic == 2 && dist < grabRange + 6f && rnd.NextFloat() < preset.GrabChance) next = RivalAct.Carry;
                else if (tc.Action == CombatAction.Launched && tc.Ballistic != 2 && dist < 60f && rnd.NextFloat() < preset.FinisherChance) next = RivalAct.Finisher;
                else if (dist >= cfg.BeamMinRange && dist <= cfg.BeamMaxRange && p.BeamEnergy > 0.6f && !CombatSystem.Before(tick, b.NextBeamTick) && rnd.NextFloat() < preset.BeamChance) next = RivalAct.Beam;
                else if (dist < cfg.DashInRange && tc.Action != CombatAction.KnockedOut && rnd.NextFloat() < preset.Aggression) next = RivalAct.Chain;
                if (next != b.Act) { b.Act = next; b.ActTick = tick; b.Presses = 0; b.NextPressTick = tick; b.ChainGrab = (byte)(rnd.NextFloat() < preset.GrabChance * 0.5f ? 1 : 0); }
                if (rnd.NextFloat() < 0.15f) b.StrafeSign = -b.StrafeSign;
            }

            // lock-on for melee acts (toggle like a player)
            var meleeAct = b.Act == RivalAct.Chain || b.Act == RivalAct.Heavy || b.Act == RivalAct.Finisher || b.Act == RivalAct.Circle || (b.Act == RivalAct.Carry && m.Carrying == Entity.Null);
            var wantLock = meleeAct && dist < combatCfg.LockRange * 0.9f;
            if (!CombatSystem.Before(tick, b.LockPressTick))
            {
                if ((wantLock && c.LockTarget != foe.Entity) || (!wantLock && c.LockTarget != Entity.Null))
                { pressLock = true; b.LockPressTick = tick + CombatSystem.Ticks(0.25f, rate); }
            }

            var elapsed = (tick - b.ActTick) / (float)rate;
            switch (b.Act)
            {
                case RivalAct.Approach:
                    inp.Move = new float2(0, 1);
                    if (dist > 60f) inp.Thrust = 1f;
                    if (dist > 180f) inp.Boost = 1;
                    break;

                case RivalAct.Circle:
                    inp.Move = new float2(b.StrafeSign, math.clamp((dist - cfg.CircleRange) / 6f, -1f, 1f));
                    inp.Vertical = math.clamp(to.y / 5f, -1f, 1f) * 0.6f;
                    break;

                case RivalAct.Chain:
                    // grab chain (rolled once per chain): one light to stagger, then grab as soon as our swing
                    // ends while the target is still staggered (a grab needs a free hand, like for a player)
                    if (b.ChainGrab != 0 && b.Presses >= 1)
                    {
                        if (c.Action == CombatAction.None && tc.Action == CombatAction.Launched && tc.Ballistic == 2 && dist < grabRange)
                        { b.Act = RivalAct.Carry; b.ActTick = tick; pressGrab = true; b.GrabPressTick = tick + CombatSystem.Ticks(0.3f, rate); break; }
                        if (c.Action == CombatAction.None && elapsed > 0.6f) EndAct(ref b, tick);
                        inp.Move = new float2(0, dist > 2.5f ? 1f : 0f);
                        break;
                    }
                    if (b.Presses == 0 && dist > 4f && dashReady && c.Action == CombatAction.None) { pressDash = true; b.NextPressTick = tick + CombatSystem.Ticks(0.12f, rate); }
                    if (dist < 6f && b.Presses < 3 && !CombatSystem.Before(tick, b.NextPressTick))
                    { pressLight = true; b.Presses++; b.NextPressTick = tick + CombatSystem.Ticks(0.24f, rate); }
                    inp.Move = new float2(0, dist > 3f ? 1f : 0f);
                    if (b.Presses >= 3 && c.Action == CombatAction.None || elapsed > 2f)
                    {
                        EndAct(ref b, tick);
                        if (tc.Action == CombatAction.Launched && tc.Ballistic != 2 && rnd.NextFloat() < preset.FinisherChance) { b.Act = RivalAct.Finisher; b.ActTick = tick; b.Presses = 0; }
                    }
                    break;

                case RivalAct.Heavy:
                    inp.Move = new float2(0, dist > 3.5f ? 1f : 0f);
                    inp.HeavyHeld = (byte)(elapsed < cfg.HeavyChargeTime ? 1 : 0);
                    if (elapsed > cfg.HeavyChargeTime + 0.7f) EndAct(ref b, tick);
                    break;

                case RivalAct.Finisher:
                {
                    if (tc.Action != CombatAction.Launched || elapsed > 2.5f) { EndAct(ref b, tick); break; }
                    inp.Move = new float2(0, 1);
                    if (dist > 5f && dashReady && c.Action != CombatAction.Dash) pressDash = true;
                    if (dist < 5.5f && b.Presses == 0) { pressLight = true; b.Presses = 1; }
                    // drive the target through the building when it's roughly behind it
                    if (dist < 8f && TryBuildingAim(ref state, foe.Position, dir, out var bAim)) { inp.LookYaw = bAim.x; inp.LookPitch = bAim.y; }
                    if (b.Presses > 0 && elapsed > 0.8f) EndAct(ref b, tick);
                    break;
                }

                case RivalAct.Beam:
                {
                    // lead the target a little
                    var lead = math.normalizesafe(foe.Position + foe.Velocity * 0.15f - pos, dir);
                    inp.LookYaw = math.degrees(math.atan2(lead.x, lead.z)) + b.AimNoise.x * 0.5f;
                    inp.LookPitch = math.clamp(-math.degrees(math.asin(math.clamp(lead.y, -1f, 1f))) + b.AimNoise.y * 0.5f, -85f, 85f);
                    inp.BeamHeld = (byte)(elapsed < cfg.BeamHoldTime ? 1 : 0);
                    if (elapsed > cfg.BeamHoldTime + 0.2f || dist < cfg.BeamMinRange * 0.5f)
                    { b.NextBeamTick = tick + CombatSystem.Ticks(cfg.BeamCooldown, rate); EndAct(ref b, tick); }
                    break;
                }

                case RivalAct.Carry:
                    if (m.Carrying == Entity.Null)
                    {
                        // go and grab the staggered target
                        if ((tc.Action != CombatAction.Launched && elapsed > 0.2f) || elapsed > 1.5f) { EndAct(ref b, tick); break; }
                        inp.Move = new float2(0, 1);
                        if (dist > 6f && dashReady && c.Action == CombatAction.None) pressDash = true;
                        if (dist < grabRange * 0.9f && !CombatSystem.Before(tick, b.GrabPressTick))
                        { pressGrab = true; b.GrabPressTick = tick + CombatSystem.Ticks(0.3f, rate); }
                    }
                    else
                    {
                        // carrying: turn toward the building (or just up and away), then throw
                        if (TryBuildingAim(ref state, pos, new float3(0, 0, 0), out var bAim)) { inp.LookYaw = bAim.x; inp.LookPitch = bAim.y; }
                        else { inp.LookYaw = f.Yaw; inp.LookPitch = -20f; }
                        var held = (tick - m.GrabStartTick) / (float)rate;
                        if (held > cfg.CarryTime && !CombatSystem.Before(tick, b.GrabPressTick))
                        { pressGrab = true; b.GrabPressTick = tick + CombatSystem.Ticks(0.3f, rate); }
                    }
                    break;

                case RivalAct.Disengage:
                {
                    // turn away and fly off (cruise), then come back
                    var away = -dir;
                    inp.LookYaw = math.degrees(math.atan2(away.x, away.z));
                    inp.LookPitch = -15f;
                    inp.Thrust = 1f;
                    inp.Boost = (byte)(dist < 60f ? 1 : 0);
                    if (elapsed > cfg.DisengageTime) EndAct(ref b, tick);
                    break;
                }

                case RivalAct.Ritual:
                    inp.RitualHeld = (byte)(p.Phase == SurgePhase.None ? 1 : 0);
                    if (p.Phase == SurgePhase.Surge || (p.Phase == SurgePhase.None && elapsed > 1.5f)) EndAct(ref b, tick);
                    break;

                case RivalAct.Guard:
                    EndAct(ref b, tick);
                    break;
            }

            if (guarding)
            {
                inp.GuardHeld = 1;
                inp.Move = float2.zero;
                inp.HeavyHeld = 0; inp.BeamHeld = 0;
                pressLight = false; pressDash = false; pressGrab = false;
            }

            em.SetComponentData(self, b);
            WriteInput(em, self, serverTick, inp, pressLight, pressDash, pressLock, pressGrab);
        }

        /// <summary>
        /// Crisis behaviour. Force: fly to the crisis and beam it (the tower, or the crowd). Protect: hold up
        /// the failing chunk (guard next to it) or fight the attackers. Returns true when it wrote the input
        /// itself; crisisFoe asks the normal fight logic to target an attacker instead.
        /// A human close by takes priority (the contest).
        /// </summary>
        static bool CrisisPlan(ref SystemState state, Entity self, float3 pos, in FlightState f, in CombatState c, in PowerState p, in MoveState m,
            ref RivalBrain b, uint tick, int rate, NetworkTick serverTick, in RivalPreset preset, out Entity crisisFoe)
        {
            crisisFoe = Entity.Null;
            var em = state.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Crisis.CrisisState>());
            if (q.CalculateEntityCount() != 1) return false;
            var crisis = q.GetSingleton<ToLive.Crisis.CrisisState>();
            if (crisis.Phase != ToLive.Crisis.CrisisPhase.Active || crisis.RivalApproach == 0) return false;
            if (p.ExpelledUntilTick != 0 && CombatSystem.Before(tick, p.ExpelledUntilTick)) return false;
            if (m.Carrying != Entity.Null || c.Action == CombatAction.Launched) return false;
            // a human right here: fight them (the contest) instead
            using (var gq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<CombatState>()))
            using (var gods = gq.ToEntityArray(Allocator.Temp))
                foreach (var g in gods)
                    if (g != self && em.GetComponentData<CharacterKind>(g).Kind == CharacterKind.Human &&
                        em.GetComponentData<CombatState>(g).Action != CombatAction.KnockedOut &&
                        math.distance(em.GetComponentData<LocalTransform>(g).Position, pos) < 25f) return false;

            var inp = new PlayerInput { LookYaw = f.Yaw };
            var goal = crisis.Position;
            if (crisis.Kind == ToLive.Crisis.CrisisKind.Disaster) goal.y = 4f; // force: cut the tower's base so it comes down
            if (crisis.RivalApproach == 2)
            {
                // FORCE: get within beam range of the crisis, aim at it, fire
                var stand = goal + math.normalizesafe(new float3(pos.x - goal.x, 0, pos.z - goal.z), new float3(1, 0, 0)) * 35f + new float3(0, 18f, 0);
                var to = stand - pos; var d = math.length(to);
                var aim = math.normalizesafe(goal - pos);
                inp.LookYaw = math.degrees(math.atan2(aim.x, aim.z)) + b.AimNoise.x;
                inp.LookPitch = math.clamp(-math.degrees(math.asin(math.clamp(aim.y, -1f, 1f))) + b.AimNoise.y, -85f, 85f);
                if (d > 12f)
                {
                    // fly toward the stand-off point (movement is relative to the look direction)
                    var look = quaternion.EulerZXY(math.radians(new float3(inp.LookPitch, inp.LookYaw, 0f)));
                    var local = math.mul(math.inverse(look), math.normalizesafe(to));
                    inp.Move = math.normalizesafe(new float2(local.x, local.z));
                    inp.Vertical = math.clamp(local.y * 2f, -1f, 1f);
                    if (d > 80f)
                    {
                        inp.Thrust = 1f; inp.Move = float2.zero;
                        inp.LookYaw = math.degrees(math.atan2(to.x, to.z));
                        inp.LookPitch = math.clamp(-math.degrees(math.asin(math.clamp(to.y / d, -1f, 1f))), -60f, 60f);
                    }
                }
                // fire until nearly empty, then rest until mostly recharged (energy only regenerates while not firing)
                if (p.BeamEnergy < 0.08f) b.BeamResting = 1; else if (p.BeamEnergy > 0.8f) b.BeamResting = 0;
                inp.BeamHeld = (byte)(d < 40f && b.BeamResting == 0 ? 1 : 0);
                b.Act = RivalAct.Beam;
                var unlock = c.LockTarget != Entity.Null && !CombatSystem.Before(tick, b.LockPressTick);
                if (unlock) b.LockPressTick = tick + CombatSystem.Ticks(0.3f, rate);
                WriteInput(em, self, serverTick, inp, false, false, unlock, false);
                em.SetComponentData(self, b);
                return true;
            }

            // PROTECT
            if (crisis.Kind == ToLive.Crisis.CrisisKind.Disaster && crisis.WarnChunk >= 0)
            {
                using var gq = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingGraph>());
                if (gq.CalculateEntityCount() != 1) return false;
                var nodes = em.GetBuffer<ChunkNode>(gq.GetSingletonEntity(), true);
                var centre = nodes[crisis.WarnChunk].Center;
                var to = centre - pos; var d = math.length(to);
                inp.LookYaw = math.degrees(math.atan2(to.x, to.z));
                inp.LookPitch = math.clamp(-math.degrees(math.asin(math.clamp(d > 0.01f ? to.y / d : 0f, -1f, 1f))), -85f, 85f);
                if (d > 4f) { inp.Move = new float2(0, 1); if (d > 60f) inp.Thrust = 1f; }
                // hold it up: guard next to it, after a human-like reaction delay
                inp.GuardHeld = (byte)(d < 7f && tick - (crisis.WarnTick - CombatSystem.Ticks(2.5f, rate)) > CombatSystem.Ticks(preset.ReactionTime, rate) ? 1 : 0);
                b.Act = RivalAct.Guard;
                WriteInput(em, self, serverTick, inp, false, false, false, false);
                em.SetComponentData(self, b);
                return true;
            }
            if (crisis.Kind == ToLive.Crisis.CrisisKind.Attack)
            {
                // fight the attackers (normal combat against the nearest one)
                using var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Crisis.CrowdAttacker>(), ComponentType.ReadOnly<LocalTransform>());
                using var units = uq.ToEntityArray(Allocator.Temp);
                var best = float.MaxValue;
                foreach (var u in units)
                {
                    var d = math.distance(em.GetComponentData<LocalTransform>(u).Position, pos);
                    if (d < best && em.GetComponentData<CombatState>(u).Action != CombatAction.KnockedOut) { best = d; crisisFoe = u; }
                }
            }
            return false;
        }


        static void EndAct(ref RivalBrain b, uint tick)
        {
            b.Act = RivalAct.Circle;
            b.ActTick = tick;
            b.NextThinkTick = tick; // decide again right away
            b.Presses = 0;
        }

        /// <summary>Nearest human in range; a response unit shooting at us if it's much closer.</summary>
        static Foe PickFoe(ref SystemState state, Entity self, float3 pos, in RivalConfig cfg, out bool found)
        {
            var em = state.EntityManager;
            var best = default(Foe); var bestScore = float.MaxValue; found = false;
            using (var gods = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>()).ToEntityArray(Allocator.Temp))
                foreach (var g in gods)
                {
                    if (g == self || em.GetComponentData<CharacterKind>(g).Kind != CharacterKind.Human) continue;
                    var gc = em.GetComponentData<CombatState>(g);
                    if (gc.Action == CombatAction.KnockedOut) continue;
                    var gp = em.GetComponentData<LocalTransform>(g).Position;
                    var d = math.distance(gp, pos);
                    if (d > cfg.EngageRange || d >= bestScore) continue;
                    bestScore = d; found = true;
                    best = new Foe { Entity = g, Position = gp, Velocity = em.GetComponentData<FlightState>(g).Velocity, Combat = gc };
                }
            using (var units = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>(), ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>()).ToEntityArray(Allocator.Temp))
                foreach (var u in units)
                {
                    var ru = em.GetComponentData<ResponseUnit>(u);
                    if (ru.Target != self || ru.Mode == UnitMode.Dead) continue;
                    var up = em.GetComponentData<LocalTransform>(u).Position;
                    var d = math.distance(up, pos) * 2f; // prefer the human unless the unit is much closer
                    if (d > 160f || d >= bestScore) continue;
                    bestScore = d; found = true;
                    best = new Foe { Entity = u, Position = up, Velocity = em.GetComponentData<FlightState>(u).Velocity, Combat = em.GetComponentData<CombatState>(u), IsUnit = true };
                }
            return best;
        }

        /// <summary>
        /// Aim (yaw, pitch in degrees) from <paramref name="from"/> at the breakable building, if it is
        /// within reach and (when <paramref name="along"/> is set) roughly ahead along that direction.
        /// </summary>
        static bool TryBuildingAim(ref SystemState state, float3 from, float3 along, out float2 aim)
        {
            aim = default;
            var em = state.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingGraph>());
            if (q.CalculateEntityCount() != 1) return false;
            var centre = q.GetSingleton<BuildingGraph>().Center;
            var to = centre - from;
            var d = math.length(to);
            if (d > 200f || d < 1f) return false;
            var dir = to / d;
            if (math.lengthsq(along) > 0.01f && math.dot(math.normalizesafe(new float3(dir.x, 0, dir.z)), math.normalizesafe(new float3(along.x, 0, along.z))) < 0.35f) return false;
            aim = new float2(math.degrees(math.atan2(dir.x, dir.z)), math.clamp(-math.degrees(math.asin(math.clamp(dir.y, -1f, 1f))), -60f, 60f));
            return true;
        }

        /// <summary>
        /// Writes this tick's input into the character's Netcode input buffer, the same buffer a
        /// client's input lands in. InputEvent counters must keep increasing (Netcode compares a tick's
        /// count with the previous one to decide "pressed"), like CopyInputToBufferJob does.
        /// </summary>
        static void WriteInput(EntityManager em, Entity self, NetworkTick serverTick, PlayerInput input, bool light, bool dash, bool lockOn, bool grab)
        {
            // This runs after this tick's prediction, so the input is for the NEXT tick (like a client
            // sending input ahead of the server). Its press counters continue from the latest entry.
            var tick = serverTick;
            tick.Increment();
            var buffer = em.GetBuffer<InputBufferData<PlayerInput>>(self);
            buffer.GetDataAtTick(tick, out var last);
            var prev = last.InternalInput;
            input.Light.Count = prev.Light.Count + (light ? 1u : 0u);
            input.Dash.Count = prev.Dash.Count + (dash ? 1u : 0u);
            input.LockOn.Count = prev.LockOn.Count + (lockOn ? 1u : 0u);
            input.Grab.Count = prev.Grab.Count + (grab ? 1u : 0u);
            buffer.AddCommandData(new InputBufferData<PlayerInput> { Tick = tick, InternalInput = input });
        }
    }
}
