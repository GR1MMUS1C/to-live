using ToLive.Combat;
using ToLive.Flight;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace ToLive.Power
{
    /// <summary>
    /// Predicted power logic, run before combat each tick: the ritual input and the surge phase
    /// timeline (Ritual → Surge → Burnout), beam charge/fire/energy, and the holder side of
    /// grab-and-throw. Like <see cref="CombatSystem"/>, the owner predicts it so pressing the ritual
    /// or the beam reacts instantly; everything that touches the world or another character
    /// (breaks, damage, knockback, the grabbed victim) is applied by server systems.
    /// Timers are tick numbers, so re-simulation after a correction lands on the same result.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(CombatSystem))]
    public partial struct PowerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PowerConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var full = !net.IsPartialTick;
            var dt = SystemAPI.Time.DeltaTime;
            var cfg = SystemAPI.GetSingleton<PowerConfig>();
            var rate = 60;
            foreach (var r in SystemAPI.Query<RefRO<ClientServerTickRate>>())
                if (r.ValueRO.SimulationTickRate > 0) rate = r.ValueRO.SimulationTickRate;

            var inputs = SystemAPI.GetComponentLookup<PlayerInput>(true);
            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var combats = SystemAPI.GetComponentLookup<CombatState>(true);

            // candidates for grabbing
            var targets = new NativeList<Entity>(8, Allocator.Temp);
            foreach (var (_, e) in SystemAPI.Query<RefRO<CombatTarget>>().WithEntityAccess()) targets.Add(e);

            foreach (var (power, move, combat, flight, xf, self) in
                     SystemAPI.Query<RefRW<PowerState>, RefRW<MoveState>, RefRW<CombatState>, RefRW<FlightState>, RefRO<LocalTransform>>()
                         .WithAll<Simulate>().WithEntityAccess())
            {
                var p = power.ValueRO;
                var m = move.ValueRO;
                var c = combat.ValueRO;
                var f = flight.ValueRO;
                var input = inputs.HasComponent(self) ? inputs[self] : default;
                var busy = c.Action == CombatAction.KnockedOut || c.Action == CombatAction.Grabbed || c.Action == CombatAction.Launched;

                // ---------- Surge phase timeline ----------
                switch (p.Phase)
                {
                    case SurgePhase.None:
                        if (input.RitualHeld != 0 && p.Meter >= 0.999f && !busy && m.Carrying == Entity.Null)
                        {
                            if (full) p.RitualHoldTicks = (byte)math.min(255, p.RitualHoldTicks + 1);
                            if (p.RitualHoldTicks >= CombatSystem.Ticks(cfg.RitualHoldTime, rate))
                            {
                                p.Phase = SurgePhase.Ritual;
                                p.PhaseStartTick = tick;
                                p.PhaseEndTick = tick + CombatSystem.Ticks(cfg.RitualTime, rate);
                                p.Erupted = 0;
                                p.RitualHoldTicks = 0;
                                m.Beam = 0;
                                c.Action = CombatAction.None; c.ComboStep = 0; c.QueuedLight = 0; c.Charge = 0;
                            }
                        }
                        else p.RitualHoldTicks = 0;
                        break;
                    case SurgePhase.Ritual:
                        f.Velocity = float3.zero; // locked in place, straining
                        if (!CombatSystem.Before(tick, p.PhaseEndTick))
                        {
                            p.Phase = SurgePhase.Surge;
                            p.PhaseStartTick = tick;
                            p.PhaseEndTick = tick + CombatSystem.Ticks(cfg.SurgeTime, rate);
                            p.Meter = 1f;
                        }
                        break;
                    case SurgePhase.Surge:
                        p.Meter = math.saturate(1f - (tick - p.PhaseStartTick) / math.max(1f, p.PhaseEndTick - p.PhaseStartTick));
                        if (!CombatSystem.Before(tick, p.PhaseEndTick))
                        {
                            p.Phase = SurgePhase.Burnout;
                            p.PhaseStartTick = tick;
                            p.PhaseEndTick = tick + CombatSystem.Ticks(cfg.BurnoutTime, rate);
                            p.Meter = 0f;
                        }
                        break;
                    case SurgePhase.Burnout:
                        if (!CombatSystem.Before(tick, p.PhaseEndTick)) p.Phase = SurgePhase.None;
                        break;
                }
                var locked = p.Phase == SurgePhase.Ritual;

                // ---------- Beam ----------
                var aim = math.mul(quaternion.EulerZXY(math.radians(new float3(input.LookPitch, input.LookYaw, 0f))), new float3(0, 0, 1));
                if (!inputs.HasComponent(self)) aim = math.mul(quaternion.EulerZXY(math.radians(new float3(f.Pitch, f.Yaw, 0f))), new float3(0, 0, 1));
                var canBeam = !busy && !locked && m.Carrying == Entity.Null && c.Guarding == 0 &&
                              (c.Action == CombatAction.None || c.Action == CombatAction.Dash);
                if (input.BeamHeld != 0 && canBeam)
                {
                    if (m.Beam == 0 && p.BeamEnergy >= cfg.BeamMinEnergy) { m.Beam = 1; m.BeamStartTick = tick; }
                    if (m.Beam == 1 && tick - m.BeamStartTick >= CombatSystem.Ticks(cfg.BeamChargeTime, rate)) m.Beam = 2;
                    if (m.Beam == 2)
                    {
                        p.BeamEnergy = math.max(0f, p.BeamEnergy - cfg.BeamDrainPerSecond * dt);
                        var fired = tick - m.BeamStartTick;
                        if (p.BeamEnergy <= 0f || fired >= CombatSystem.Ticks(cfg.BeamChargeTime + cfg.BeamMaxTime, rate)) m.Beam = 0;
                    }
                }
                else m.Beam = 0;
                if (m.Beam == 0) p.BeamEnergy = math.min(1f, p.BeamEnergy + cfg.BeamRegenPerSecond * dt);
                if (m.Beam != 0)
                {
                    f.Velocity *= math.exp(-6f * dt); // planted while firing
                    f.Yaw = math.degrees(math.atan2(aim.x, aim.z));
                    f.Pitch = math.clamp(-math.degrees(math.asin(math.clamp(aim.y, -1f, 1f))), -85f, 85f); // replicated: others draw the beam from this
                }

                // ---------- Grab (holder side) ----------
                if (input.Grab.IsSet && !locked && !busy)
                {
                    if (m.Carrying != Entity.Null)
                    {
                        // debounce: the press that started the grab must not also throw
                        if (tick - m.GrabStartTick >= CombatSystem.Ticks(0.2f, rate)) m.Throw = 1; // server launches the victim
                    }
                    else if (c.Action == CombatAction.None || c.Action == CombatAction.Dash)
                    {
                        var pos = xf.ValueRO.Position;
                        var best = Entity.Null; var bestD = cfg.GrabRange;
                        foreach (var t in targets)
                        {
                            if (t == self || !transforms.HasComponent(t) || !combats.HasComponent(t)) continue;
                            var tc = combats[t];
                            // only staggered / launched (or guard-broken = staggered) targets can be grabbed
                            if (tc.Action != CombatAction.Launched) continue;
                            var d = math.distance(transforms[t].Position, pos);
                            if (d < bestD) { bestD = d; best = t; }
                        }
                        if (best != Entity.Null)
                        {
                            m.Carrying = best; m.GrabStartTick = tick; m.Throw = 0;
                            c.Action = CombatAction.None; c.ComboStep = 0; // end the dash / chain
                            f.Velocity *= 0.3f;
                            c.LockTarget = Entity.Null;
                        }
                    }
                }
                if (m.Carrying != Entity.Null && m.Throw == 0 && tick - m.GrabStartTick >= CombatSystem.Ticks(cfg.MaxCarryTime, rate))
                    m.Throw = 1; // carried too long: throw automatically

                // ---------- Victim side: mash to break free ----------
                if (c.Action == CombatAction.Grabbed && (input.Light.IsSet || input.Dash.IsSet) && m.Mash < 255) m.Mash++;
                if (c.Action != CombatAction.Grabbed) m.Mash = 0;

                power.ValueRW = p;
                move.ValueRW = m;
                combat.ValueRW = c;
                flight.ValueRW = f;
            }
        }
    }
}
