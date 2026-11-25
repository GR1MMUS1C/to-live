using System.Text;
using ToLive.Combat;
using ToLive.Crisis;
using ToLive.Destruction;
using ToLive.District;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace ToLive.EditorTools
{
    /// <summary>Milestone 4B checks: crises, ownership, rebuild/scar and defeat, per world.</summary>
    public static partial class DevProbe
    {
        public static string CrisisReport()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrisisState>(), ComponentType.ReadOnly<DistrictHold>());
                if (q.CalculateEntityCount() != 1) { sb.Append(world.Name).Append(": no district\n"); continue; }
                var e = q.GetSingletonEntity();
                var c = em.GetComponentData<CrisisState>(e);
                var h = em.GetComponentData<DistrictHold>(e);
                var d = em.GetComponentData<DistrictState>(e);
                var tick = 0u;
                using (var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>()))
                    if (tq.CalculateEntityCount() == 1) { var nt = tq.GetSingleton<NetworkTime>(); tick = nt.ServerTick.IsValid ? nt.ServerTick.TickIndexForValidTick : 0u; }
                var broken = 0;
                using (var bq = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingState>()))
                    if (bq.CalculateEntityCount() == 1) broken = bq.GetSingleton<BuildingState>().CountBroken();
                using var bodyQ = em.CreateEntityQuery(ComponentType.ReadOnly<CitizenBody>());
                var holderKind = KindOf(em, h.Holder);
                sb.Append($"{world.Name}: crisis#{c.Seq} {CrisisKind.Name(c.Kind)} phase={c.Phase} outcome={CrisisOutcome.Name(c.Outcome)} by {KindOf(em, c.Resolver)} progress={c.Progress}/{c.Goal} saved={c.Saved} lost={c.Lost} warn={c.WarnChunk} left={((int)(c.EndTick - tick)) / 60f:0}s rivalApproach={c.RivalApproach}\n");
                sb.Append($"  holder={holderKind} mode={(h.Mode == DistrictHold.Trust ? "trust" : h.Mode == DistrictHold.Dominance ? "dominance" : "-")} fortify={h.FortifyLevel} rebuilt={h.Rebuilt} brokenChunks={broken} bodies={bodyQ.CalculateEntityCount()} alive={d.Alive}/{d.Population} meter={d.Meter:0.00}");
                using (var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()))
                    if (hq.CalculateEntityCount() == 1) { var hs = hq.GetSingleton<HeatState>(); sb.Append($" heat={hs.Level}/{hs.Value:0} units={hs.Units}"); }
                sb.Append('\n');
                foreach (var s in em.GetBuffer<GodStanding>(e))
                {
                    var p = em.Exists(s.God) && em.HasComponent<PowerState>(s.God) ? em.GetComponentData<PowerState>(s.God) : default;
                    var pos = em.Exists(s.God) ? em.GetComponentData<LocalTransform>(s.God).Position : float3.zero;
                    var expelled = p.ExpelledUntilTick != 0 && tick != 0 && CombatSystem.Before(tick, p.ExpelledUntilTick);
                    sb.Append($"  {KindOf(em, s.God)} trust={s.Trust:0.0} fear={s.Fear:0.0} align={p.Alignment:0.00} meter={p.Meter:0.00} fuel={p.Fuel:0} tier={p.Tier} expelled={(expelled ? "yes" : "-")} pos={Fmt(pos)}\n");
                }
            }
            return sb.ToString();
        }

        static string KindOf(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e)) return "-";
            if (!em.HasComponent<CharacterKind>(e)) return e.ToString();
            return em.GetComponentData<CharacterKind>(e).Kind == CharacterKind.Rival ? "VANTA" : "human" + e.Index;
        }

        /// <summary>Move the first human next to the failing chunk (disaster), ready to guard.</summary>
        public static string GoToFailingChunk()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrisisState>());
            var c = q.GetSingleton<CrisisState>();
            if (c.WarnChunk < 0) return "no failing chunk";
            using var gq = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingGraph>());
            var centre = em.GetBuffer<ChunkNode>(gq.GetSingletonEntity())[c.WarnChunk].Center;
            var human = FirstHuman(em);
            var dir = math.normalizesafe(new float3(centre.x, 0, centre.z) - new float3(0, 0, -84f), new float3(0, 0, 1));
            var p = centre + dir * 4f;
            Teleport(p.x, p.y, p.z, math.degrees(math.atan2(-dir.x, -dir.z)));
            return $"next to chunk {c.WarnChunk} at {Fmt(centre)}";
        }

        static double s_AutoHoldEnd;
        static short s_AutoHoldChunk = -1;

        /// <summary>
        /// For <paramref name="seconds"/>, play the protect path of a disaster like an attentive player:
        /// whenever a chunk starts to fail, fly next to it and guard until it is resolved.
        /// </summary>
        public static string AutoHold(float seconds)
        {
            s_AutoHoldEnd = UnityEditor.EditorApplication.timeSinceStartup + seconds;
            UnityEditor.EditorApplication.update -= AutoHoldTick;
            UnityEditor.EditorApplication.update += AutoHoldTick;
            return "auto-holding";
        }

        static void AutoHoldTick()
        {
            if (UnityEditor.EditorApplication.timeSinceStartup > s_AutoHoldEnd)
            {
                UnityEditor.EditorApplication.update -= AutoHoldTick;
                ToLive.Input.InputReader.GuardHeld = false;
                return;
            }
            var w = ServerWorld(); if (w == null) return;
            using var q = w.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CrisisState>());
            if (q.CalculateEntityCount() != 1) return;
            var c = q.GetSingleton<CrisisState>();
            if (c.Phase != CrisisPhase.Active || c.Kind != CrisisKind.Disaster || c.WarnChunk < 0) { ToLive.Input.InputReader.GuardHeld = false; s_AutoHoldChunk = -1; return; }
            if (c.WarnChunk != s_AutoHoldChunk) { GoToFailingChunk(); s_AutoHoldChunk = c.WarnChunk; }
            ToLive.Input.InputReader.GuardHeld = true;
        }

        /// <summary>The rival's position, distance to the crisis, beam state and energy (server).</summary>
        public static string RivalCrisisState()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            var r = hq.GetSingleton<HeatState>().Rival;
            if (r == Entity.Null || !em.Exists(r)) return "no rival";
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrisisState>());
            var c = q.GetSingleton<CrisisState>();
            var p = em.GetComponentData<LocalTransform>(r).Position;
            var m = em.GetComponentData<MoveState>(r);
            var pw = em.GetComponentData<PowerState>(r);
            var cs = em.GetComponentData<CombatState>(r);
            return $"rival at {Fmt(p)} dist {math.distance(p, c.Position):0} beam={m.Beam} energy={pw.BeamEnergy:0.00} action={CombatAction.Name(cs.Action)} lock={(cs.LockTarget == Entity.Null ? "-" : "yes")}";
        }

        /// <summary>Put the rival next to fortification tower <paramref name="index"/> (it is not the holder, so the tower should shoot it).</summary>
        public static string RivalToTower(int index)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var fq = em.CreateEntityQuery(ComponentType.ReadOnly<Fortification>());
            using var forts = fq.ToComponentDataArray<Fortification>(Allocator.Temp);
            foreach (var f in forts)
                if (f.Index == index)
                {
                    var r = LaunchRival(f.Top.x + 20f, f.Top.y + 5f, f.Top.z, 0f, 0f, 0f);
                    var p = em.GetComponentData<CombatState>(em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()).GetSingleton<HeatState>().Rival).Power;
                    return $"{r}; rival power {p:0}";
                }
            return "no such tower";
        }

        public static string RivalPowerAndTowerShot()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var r = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()).GetSingleton<HeatState>().Rival;
            var h = em.CreateEntityQuery(ComponentType.ReadOnly<DistrictHold>()).GetSingleton<DistrictHold>();
            return $"rival power {em.GetComponentData<CombatState>(r).Power:0}, last tower shot tick {h.TowerShotTick} tower {h.TowerShotIndex} at {(h.TowerShotTarget == r ? "VANTA" : h.TowerShotTarget.ToString())}";
        }

        /// <summary>Move the first human to the crisis marker (+height).</summary>
        public static string GoToCrisis(float height)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrisisState>());
            var c = q.GetSingleton<CrisisState>();
            Teleport(c.Position.x, c.Position.y + height, c.Position.z, 0f);
            return $"at crisis {Fmt(c.Position)}";
        }

        /// <summary>Knock out the first human, credited to the rival (if any) or nobody: tests "drained and expelled".</summary>
        public static string DefeatPlayer()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var human = FirstHuman(em);
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            var rival = hq.CalculateEntityCount() == 1 ? hq.GetSingleton<HeatState>().Rival : Entity.Null;
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var c = em.GetComponentData<CombatState>(human);
            c.LastHitTick = tick;
            Damage.Apply(ref c, 10000f, tick, Damage.TickRate(em), 4f);
            em.SetComponentData(human, c);
            em.SetComponentData(human, new LastAttacker { Value = rival });
            return $"player knocked out by {(rival == Entity.Null ? "nobody" : "VANTA")}";
        }

        /// <summary>Contest test: human number <paramref name="winner"/> (0 = first, 1 = second) knocks out the other human here.</summary>
        public static string HumanBeatsHuman(int winner)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var humans = new System.Collections.Generic.List<Entity>();
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<GhostOwner>()))
            using (var chars = cq.ToEntityArray(Allocator.Temp))
                foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) humans.Add(c);
            if (humans.Count < 2) return "need two humans";
            humans.Sort((a, b) => em.GetComponentData<GhostOwner>(a).NetworkId.CompareTo(em.GetComponentData<GhostOwner>(b).NetworkId));
            var win = humans[winner]; var lose = humans[1 - winner];
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var cs = em.GetComponentData<CombatState>(lose);
            cs.LastHitTick = tick;
            Damage.Apply(ref cs, 10000f, tick, Damage.TickRate(em), 4f);
            em.SetComponentData(lose, cs);
            em.SetComponentData(lose, new LastAttacker { Value = win });
            return $"{win} beat {lose}";
        }

        /// <summary>Add trust / fear to the second human (contest tests).</summary>
        public static string AddStandingSecond(float trust, float fear)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            Entity second = Entity.Null; var best = -1;
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<GhostOwner>()))
            using (var chars = cq.ToEntityArray(Allocator.Temp))
                foreach (var c in chars)
                    if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human && em.GetComponentData<GhostOwner>(c).NetworkId > best)
                    { best = em.GetComponentData<GhostOwner>(c).NetworkId; second = c; }
            DistrictSource.Add(em, second, trust, fear, Cause.Debug);
            return $"second human {second}";
        }

        /// <summary>Add trust / fear to the rival's standing (tests).</summary>
        public static string AddStandingRival(float trust, float fear)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var r = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()).GetSingleton<HeatState>().Rival;
            if (r == Entity.Null) return "no rival";
            DistrictSource.Add(em, r, trust, fear, Cause.Debug);
            return "queued";
        }

        /// <summary>Clear every god's trust and fear (tests start from exact values).</summary>
        public static string ResetStandings()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<DistrictState>());
            em.GetBuffer<GodStanding>(q.GetSingletonEntity()).Clear();
            return "standings cleared";
        }

        /// <summary>Change the disaster warning time on the server at runtime (tests; the Inspector value is unchanged).</summary>
        public static string SetWarnTime(float seconds)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            using var q = w.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<CrisisConfig>());
            var c = q.GetSingleton<CrisisConfig>();
            c.WarnTime = seconds;
            q.SetSingleton(c);
            return $"WarnTime {seconds}";
        }

        /// <summary>Put the first human 6 m from the rival, facing it (to attack it with PlayCode).</summary>
        public static string GoToRival()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var r = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()).GetSingleton<HeatState>().Rival;
            var p = em.GetComponentData<LocalTransform>(r).Position;
            Teleport(p.x, p.y, p.z - 6f, 0f);
            return $"next to VANTA at {Fmt(p)}";
        }

        /// <summary>Kill <paramref name="count"/> living rioters (uprising), blamed on the first human.</summary>
        public static string KillRioters(int count)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var human = FirstHuman(em);
            using var sq = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            var citizens = em.GetBuffer<CitizenSim>(sq.GetSingletonEntity()).ToNativeArray(Allocator.Temp);
            var n = 0;
            for (var i = 0; i < citizens.Length && n < count; i++)
                if (citizens[i].Alive != 0 && citizens[i].Riot != 0) { CrowdHazards.Kill(em, citizens[i].Position, 0.3f, 0f, human, Cause.CiviliansKilled); n++; }
            return $"killing {n} rioters";
        }
    }
}
