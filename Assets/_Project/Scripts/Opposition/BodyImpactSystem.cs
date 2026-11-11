using ToLive.Combat;
using ToLive.Flight;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>
    /// SERVER: a launched or thrown body is a projectile. When it flies fast into another body
    /// (a thrown police drone into a gunship, a launched player into the rival), both take damage
    /// and the one that was hit is launched along. Chunk smashing is already handled by the flight
    /// sweep; this adds body-to-body hits. Distance checks only (heroes are not physics bodies).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial struct BodyImpactSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<OppositionConfig>();
            state.RequireForUpdate<CombatConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var cfg = SystemAPI.GetSingleton<OppositionConfig>();
            var combatCfg = SystemAPI.GetSingleton<CombatConfig>();
            var em = state.EntityManager;
            var rate = Damage.TickRate(em);
            var cooldown = CombatSystem.Ticks(0.3f, rate);

            using var bodies = SystemAPI.QueryBuilder().WithAll<CombatState, FlightState, LocalTransform>().Build().ToEntityArray(Allocator.Temp);
            foreach (var mover in bodies)
            {
                var mc = em.GetComponentData<CombatState>(mover);
                if (mc.Action != CombatAction.Launched || mc.Ballistic == 2) continue; // launched / thrown / finisher, not a stagger
                var mf = em.GetComponentData<FlightState>(mover);
                var speed = math.length(mf.Velocity);
                if (speed < cfg.BodyImpactSpeed) continue;
                var mp = em.GetComponentData<LocalTransform>(mover).Position;
                var launcher = em.HasComponent<LastAttacker>(mover) ? em.GetComponentData<LastAttacker>(mover).Value : Entity.Null;
                var responsible = launcher != Entity.Null && em.Exists(launcher) ? launcher : mover;

                foreach (var other in bodies)
                {
                    if (other == mover || other == launcher) continue;
                    var oc = em.GetComponentData<CombatState>(other);
                    if (oc.Action == CombatAction.KnockedOut || oc.Action == CombatAction.Grabbed) continue;
                    if (oc.LastHitTick != 0 && tick - oc.LastHitTick < cooldown) continue;
                    var op = em.GetComponentData<LocalTransform>(other).Position;
                    if (math.distance(op, mp) > cfg.BodyImpactRadius) continue;

                    var damage = speed * cfg.BodyImpactDamagePerSpeed;
                    var of = em.GetComponentData<FlightState>(other);
                    var inRitual = em.HasComponent<PowerState>(other) && em.GetComponentData<PowerState>(other).Phase == SurgePhase.Ritual;
                    oc.LastHitTick = tick;
                    oc.LastHitStrength = speed;
                    if (!inRitual)
                    {
                        oc.Action = CombatAction.Launched;
                        oc.Ballistic = 1;
                        oc.ActionStartTick = tick;
                        oc.ActionEndTick = tick + CombatSystem.Ticks(0.8f, rate);
                        oc.ComboStep = 0; oc.QueuedLight = 0; oc.Charge = 0f;
                        of.Velocity = mf.Velocity * cfg.BodyImpactLaunchScale + new float3(0, 4f, 0);
                    }
                    Damage.Apply(ref oc, damage, tick, rate, combatCfg.RespawnDelay);
                    em.SetComponentData(other, oc);
                    em.SetComponentData(other, of);
                    if (em.HasComponent<LastAttacker>(other)) em.SetComponentData(other, new LastAttacker { Value = responsible });
                    SurgeFuel.AddDamage(em, responsible, other, damage);

                    // the projectile body takes the same hit and loses half its speed
                    mc.LastHitTick = tick;
                    mc.LastHitStrength = speed;
                    Damage.Apply(ref mc, damage, tick, rate, combatCfg.RespawnDelay);
                    mf.Velocity *= 0.5f;
                    Debug.Log($"[ToLive] Body impact {mover} -> {other} at {speed:0} m/s ({damage:0} damage)");
                    break; // one body per tick
                }
                em.SetComponentData(mover, mc);
                em.SetComponentData(mover, mf);
            }
        }
    }
}
