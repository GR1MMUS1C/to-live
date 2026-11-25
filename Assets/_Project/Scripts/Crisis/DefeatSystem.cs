using ToLive.Combat;
using ToLive.District;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Crisis
{
    /// <summary>
    /// SERVER: "a beaten god is drained, thrown out of the district and loses influence there"
    /// (replaces knock-out + respawn for players and the rival; the dummy and units keep the old rules).
    /// On the knock-out: the loser loses a share of their trust and fear here (the winner takes part of
    /// it), loses their surge, and is locked out: after the knock-out they come back at the district
    /// edge and can't re-enter or gain standing until the lockout ends. Like a GameMode "eliminated"
    /// rule with a timed re-entry.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(CombatServerSystem))]
    public partial struct DefeatSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CrisisConfig>();
            state.RequireForUpdate<DistrictState>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<CrisisConfig>();
            var combatCfg = SystemAPI.TryGetSingleton<CombatConfig>(out var cc) ? cc : default;
            var hasPowerCfg = SystemAPI.TryGetSingleton<PowerConfig>(out var powerCfg);
            var rivalDelay = SystemAPI.TryGetSingleton<ToLive.Rival.RivalConfig>(out var rc) ? rc.RespawnDelay : 12f;
            var rate = Damage.TickRate(em);
            var districtEntity = SystemAPI.GetSingletonEntity<DistrictState>();
            var boundary = cfg.ExpelDistance - 40f;

            using var gods = SystemAPI.QueryBuilder().WithAll<CharacterKind, DefeatState, CombatState, PowerState, LocalTransform>().Build().ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var god in gods)
            {
                var c = em.GetComponentData<CombatState>(god);
                var ds = em.GetComponentData<DefeatState>(god);
                var p = em.GetComponentData<PowerState>(god);
                var kind = em.GetComponentData<CharacterKind>(god).Kind;

                // ---- drained: on the knock-out ----
                if (c.Action == CombatAction.KnockedOut && c.ActionStartTick != ds.HandledKoTick)
                {
                    ds.HandledKoTick = c.ActionStartTick; ds.Pending = 1;
                    float trust = 0f, fear = 0f;
                    foreach (var s in em.GetBuffer<GodStanding>(districtEntity, true)) if (s.God == god) { trust = s.Trust; fear = s.Fear; }
                    var lossT = trust * cfg.DefeatLoss; var lossF = fear * cfg.DefeatLoss;
                    DistrictSource.Add(em, god, -lossT, -lossF, Cause.Defeated, (int)math.round(lossT + lossF));
                    var winner = HeatSource.Responsible(em, em.GetComponentData<LastAttacker>(god).Value);
                    if (winner != god && HeatSource.IsGod(em, winner))
                        DistrictSource.Add(em, winner, lossT * cfg.WinnerShare, lossF * cfg.WinnerShare, Cause.Victory, (int)math.round((lossT + lossF) * cfg.WinnerShare));
                    // the surge is gone
                    p.Meter = 0f;
                    if (hasPowerCfg && (p.Phase == SurgePhase.Ritual || p.Phase == SurgePhase.Surge))
                    { p.Phase = SurgePhase.Burnout; p.PhaseStartTick = tick; p.PhaseEndTick = tick + CombatSystem.Ticks(powerCfg.BurnoutTime, rate); }
                    var down = kind == CharacterKind.Rival ? rivalDelay : combatCfg.RespawnDelay;
                    p.ExpelledUntilTick = tick + CombatSystem.Ticks(down + cfg.LockoutTime, rate);
                    em.SetComponentData(god, p);
                    em.SetComponentData(god, ds);
                    using (var dq = em.CreateEntityQuery(ComponentType.ReadWrite<CrisisDirector>()))
                        if (dq.CalculateEntityCount() == 1) { var d = dq.GetSingleton<CrisisDirector>(); d.PendingHolderCheck = tick + 2; dq.SetSingleton(d); }
                    CombatFxServer.Defeat(god);
                    Debug.Log($"[ToLive] {god} drained and expelled: lost {lossT:0} trust, {lossF:0} fear (winner {winner})");
                    continue;
                }

                // ---- expelled: humans come back at the district edge (the rival does it in RivalSystem) ----
                if (ds.Pending != 0 && kind == CharacterKind.Human && c.Action == CombatAction.KnockedOut && !CombatSystem.Before(tick, c.ActionEndTick))
                {
                    var xf = em.GetComponentData<LocalTransform>(god);
                    var f = em.GetComponentData<FlightState>(god);
                    var pos = EdgePoint(xf.Position, cfg.ExpelDistance);
                    var yaw = math.degrees(math.atan2(-pos.x, -pos.z));
                    var max = hasPowerCfg ? powerCfg.MaxPower(combatCfg.MaxPower, p) : combatCfg.MaxPower;
                    CombatServerSystem.ResetCharacter(ref c, ref f, ref xf, pos, yaw, max);
                    em.SetComponentData(god, c); em.SetComponentData(god, f); em.SetComponentData(god, xf);
                    ds.Pending = 0;
                    em.SetComponentData(god, ds);
                    continue;
                }
                if (ds.Pending != 0 && c.Action != CombatAction.KnockedOut) { ds.Pending = 0; em.SetComponentData(god, ds); }

                // ---- locked out: can't re-enter the district until the lockout ends ----
                if (p.ExpelledUntilTick != 0 && CombatSystem.Before(tick, p.ExpelledUntilTick) && c.Action != CombatAction.KnockedOut)
                {
                    var xf = em.GetComponentData<LocalTransform>(god);
                    var pos = xf.Position;
                    if (math.abs(pos.x) < boundary && math.abs(pos.z) < boundary)
                    {
                        // pushed back to the nearest edge
                        if (math.abs(pos.x) > math.abs(pos.z)) pos.x = math.sign(pos.x == 0 ? 1 : pos.x) * boundary;
                        else pos.z = math.sign(pos.z == 0 ? 1 : pos.z) * boundary;
                        xf.Position = pos;
                        em.SetComponentData(god, xf);
                        var f = em.GetComponentData<FlightState>(god);
                        f.Velocity = float3.zero;
                        em.SetComponentData(god, f);
                    }
                }
            }
        }

        /// <summary>A point at the district edge in the direction of <paramref name="from"/> (outside the district).</summary>
        public static float3 EdgePoint(float3 from, float distance)
        {
            var dir = math.normalizesafe(new float3(from.x, 0f, from.z), new float3(1, 0, 0));
            return dir * distance + new float3(0, 120f, 0);
        }
    }

    /// <summary>Server-side hook for defeat feedback (clients show it from the replicated lockout).</summary>
    public static class CombatFxServer
    {
        public static event System.Action<Entity> OnDefeat;
        public static void Defeat(Entity god) => OnDefeat?.Invoke(god);
    }
}
