using System.Text;
using ToLive.Combat;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using ToLive.Rival;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace ToLive.EditorTools
{
    /// <summary>Milestone 3B checks: heat, response units and the rival, per world.</summary>
    public static partial class DevProbe
    {
        /// <summary>Heat, every unit and the rival as each world sees them.</summary>
        public static string OppositionReport()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                var interp = 0u;
                using (var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>()))
                    if (tq.CalculateEntityCount() == 1)
                    {
                        var nt = tq.GetSingleton<NetworkTime>();
                        interp = nt.ServerTick.IsValid ? nt.ServerTick.TickIndexForValidTick : 0u;
                    }
                sb.Append(world.Name).Append(" tick ").Append(interp).Append('\n');
                using (var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()))
                    if (hq.CalculateEntityCount() == 1)
                    {
                        var h = hq.GetSingleton<HeatState>();
                        sb.Append($"  heat level={h.Level} value={h.Value:0.0} units={h.Units} rival={(h.Rival == Entity.Null ? "-" : "yes")} mode={h.RivalMode} difficulty={h.RivalDifficulty} respawnTick={h.RivalRespawnTick} downs={h.RivalDowns}\n");
                    }
                    else sb.Append("  (no heat state)\n");
                using (var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>(), ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>()))
                using (var units = uq.ToEntityArray(Allocator.Temp))
                    foreach (var e in units)
                    {
                        var u = em.GetComponentData<ResponseUnit>(e);
                        var c = em.GetComponentData<CombatState>(e);
                        var p = em.GetComponentData<LocalTransform>(e).Position;
                        sb.Append($"  {UnitKind.Name(u.Kind)} {e} mode={u.Mode} hp={c.Power:0}/{u.MaxHealth:0} action={CombatAction.Name(c.Action)} pos={Fmt(p)} target={(u.Target == Entity.Null ? "-" : u.Target.ToString())} shot={u.ShotTick} hit={u.ShotHit} beam={u.BeamPhase}\n");
                    }
                using (var rq = em.CreateEntityQuery(ComponentType.ReadOnly<Rocket>(), ComponentType.ReadOnly<LocalTransform>()))
                    if (rq.CalculateEntityCount() > 0) sb.Append($"  rockets={rq.CalculateEntityCount()}\n");
                using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>(), ComponentType.ReadOnly<CombatState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<PowerState>()))
                using (var chars = cq.ToEntityArray(Allocator.Temp))
                    foreach (var e in chars)
                    {
                        var k = em.GetComponentData<CharacterKind>(e).Kind;
                        var c = em.GetComponentData<CombatState>(e);
                        var pw = em.GetComponentData<PowerState>(e);
                        var p = em.GetComponentData<LocalTransform>(e).Position;
                        sb.Append($"  {(k == CharacterKind.Rival ? "RIVAL" : "human")} {e} power={c.Power:0} action={CombatAction.Name(c.Action)} phase={SurgePhase.Name(pw.Phase)} meter={pw.Meter:0.00} align={pw.Alignment:0.0} pos={Fmt(p)} lock={(c.LockTarget == Entity.Null ? "-" : "yes")} supp={(pw.SuppressedUntilTick != 0 && (int)(pw.SuppressedUntilTick - interp) > 0 ? "yes" : "-")}");
                        if (em.HasComponent<HeatBlame>(e)) sb.Append($" blame={em.GetComponentData<HeatBlame>(e).Amount:0}");
                        if (k == CharacterKind.Rival && em.HasComponent<RivalBrain>(e)) sb.Append($" act={RivalAct.Name(em.GetComponentData<RivalBrain>(e).Act)}");
                        if (em.HasComponent<PredictedGhost>(e)) sb.Append(" predicted");
                        sb.Append('\n');
                    }
            }
            return sb.ToString();
        }

        static World ServerWorld()
        {
            foreach (var w in World.All) if (w.IsCreated && w.IsServer()) return w;
            return null;
        }

        /// <summary>Sets raw heat on the server (and marks it as just risen).</summary>
        public static string SetHeat(float value)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadWrite<HeatState>());
            if (q.CalculateEntityCount() != 1) return "no heat state";
            var e = q.GetSingletonEntity();
            var h = em.GetComponentData<HeatState>(e);
            h.Value = value;
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            h.LastRiseTick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            em.SetComponentData(e, h);
            return $"heat {value}";
        }

        /// <summary>Damage the first unit of a kind (1 police, 2 gunship, 3 anti-god) through the shared path, attributed to the first human.</summary>
        public static string DamageUnit(int kind, float amount)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var human = Entity.Null;
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>()))
            using (var chars = cq.ToEntityArray(Allocator.Temp))
                foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) { human = c; break; }
            using var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>(), ComponentType.ReadWrite<CombatState>());
            using var units = uq.ToEntityArray(Allocator.Temp);
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            foreach (var e in units)
            {
                if (em.GetComponentData<ResponseUnit>(e).Kind != kind) continue;
                var c = em.GetComponentData<CombatState>(e);
                if (c.Action == CombatAction.KnockedOut) continue;
                c.LastHitTick = tick;
                var ko = Damage.Apply(ref c, amount, tick, Damage.TickRate(em), 4f);
                em.SetComponentData(e, c);
                em.SetComponentData(e, new LastAttacker { Value = human });
                return $"unit {e} hp {c.Power:0} destroyed={ko}";
            }
            return "no such unit";
        }

        /// <summary>Put a unit (by kind) at a position with a velocity, launched (as if thrown).</summary>
        public static string LaunchUnit(int kind, float x, float y, float z, float vx, float vy, float vz)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var human = Entity.Null;
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>()))
            using (var chars = cq.ToEntityArray(Allocator.Temp))
                foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) { human = c; break; }
            using var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>());
            using var units = uq.ToEntityArray(Allocator.Temp);
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            foreach (var e in units)
            {
                if (em.GetComponentData<ResponseUnit>(e).Kind != kind) continue;
                var c = em.GetComponentData<CombatState>(e);
                if (c.Action == CombatAction.KnockedOut) continue;
                c.Action = CombatAction.Launched; c.Ballistic = 0; c.ActionStartTick = tick; c.ActionEndTick = tick + 90;
                em.SetComponentData(e, c);
                var f = em.GetComponentData<FlightState>(e);
                f.Velocity = new float3(vx, vy, vz);
                em.SetComponentData(e, f);
                em.SetComponentData(e, LocalTransform.FromPosition(new float3(x, y, z)));
                em.SetComponentData(e, new LastAttacker { Value = human });
                return $"launched {e}";
            }
            return "no such unit";
        }

        /// <summary>Move a unit (by kind) to a position and stop it.</summary>
        public static string PlaceUnit(int kind, float x, float y, float z)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>());
            using var units = uq.ToEntityArray(Allocator.Temp);
            foreach (var e in units)
            {
                if (em.GetComponentData<ResponseUnit>(e).Kind != kind) continue;
                em.SetComponentData(e, LocalTransform.FromPosition(new float3(x, y, z)));
                em.SetComponentData(e, new FlightState());
                return $"placed {e}";
            }
            return "no such unit";
        }

        /// <summary>Rival power (to test knock-outs and disengage) and meter.</summary>
        public static string SetRival(float power, float meter)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            if (hq.CalculateEntityCount() != 1) return "no heat state";
            var r = hq.GetSingleton<HeatState>().Rival;
            if (r == Entity.Null || !em.Exists(r)) return "no rival";
            var c = em.GetComponentData<CombatState>(r);
            if (power >= 0f) c.Power = power;
            em.SetComponentData(r, c);
            var p = em.GetComponentData<PowerState>(r);
            if (meter >= 0f) p.Meter = meter;
            em.SetComponentData(r, p);
            return $"rival power {c.Power} meter {p.Meter}";
        }

        /// <summary>
        /// Break every grounded chunk (so the rest collapses) with an instigator: 0 nobody, 1 the first
        /// human, 2 the first response unit (as a rocket would). Returns heat before the break.
        /// </summary>
        public static string BreakGroundFloorBy(int who)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var instigator = Entity.Null;
            if (who == 1)
                using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>()))
                using (var chars = cq.ToEntityArray(Allocator.Temp))
                    foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) { instigator = c; break; }
            if (who == 2)
                using (var uq = em.CreateEntityQuery(ComponentType.ReadOnly<ResponseUnit>()))
                using (var units = uq.ToEntityArray(Allocator.Temp))
                    if (units.Length > 0) instigator = units[0];
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Destruction.BuildingGraph>());
            var g = q.GetSingletonEntity();
            var nodes = em.GetBuffer<ToLive.Destruction.ChunkNode>(g).ToNativeArray(Allocator.Temp);
            var req = em.GetBuffer<ToLive.Destruction.BreakRequest>(g);
            var n = 0;
            for (var i = 0; i < nodes.Length; i++)
                if (nodes[i].Grounded != 0) { req.Add(new ToLive.Destruction.BreakRequest { Chunk = i, Velocity = new float3(0, 0, -20), Instigator = instigator }); n++; }
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            return $"requested {n} grounded chunks by {instigator}; heat before {hq.GetSingleton<HeatState>().Value:0.0}";
        }

        /// <summary>Launch the rival from a position with a velocity, as if hit by the first human (e.g. into the building).</summary>
        public static string LaunchRival(float x, float y, float z, float vx, float vy, float vz)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            var r = hq.GetSingleton<HeatState>().Rival;
            if (r == Entity.Null || !em.Exists(r)) return "no rival";
            var human = Entity.Null;
            using (var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>()))
            using (var chars = cq.ToEntityArray(Allocator.Temp))
                foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) { human = c; break; }
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var cs = em.GetComponentData<CombatState>(r);
            cs.Action = CombatAction.Launched; cs.Ballistic = 0; cs.ActionStartTick = tick; cs.ActionEndTick = tick + 60; cs.LastHitTick = tick; cs.LastHitStrength = 100f;
            em.SetComponentData(r, cs);
            em.SetComponentData(r, new FlightState { Velocity = new float3(vx, vy, vz) });
            em.SetComponentData(r, LocalTransform.FromPosition(new float3(x, y, z)));
            em.SetComponentData(r, new LastAttacker { Value = human });
            return $"rival launched from ({x},{y},{z})";
        }

        // ---------------------------------------------------------------- snapshot size (Netcode metrics)

        static double s_NetEnd;
        static uint s_NetLastTick;
        static long s_NetBits, s_NetSnapshots, s_NetMaxBits;
        static readonly System.Collections.Generic.Dictionary<string, (long bits, long n, long inst)> s_NetPerType = new System.Collections.Generic.Dictionary<string, (long, long, long)>();

        /// <summary>
        /// Samples received snapshot sizes in the first client world for <paramref name="seconds"/>
        /// using the documented GhostMetricsMonitor singleton (Editor / NETCODE_DEBUG only).
        /// </summary>
        public static string StartNetSample(float seconds)
        {
            World client = null;
            foreach (var w in World.All) if (w.IsCreated && w.IsClient() && !w.IsThinClient()) { client = w; break; }
            if (client == null) return "no client";
            var em = client.EntityManager;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<GhostMetricsMonitor>()))
                if (q.CalculateEntityCount() == 0)
                {
                    var e = em.CreateEntity(typeof(GhostMetricsMonitor), typeof(SnapshotMetrics), typeof(GhostNames), typeof(GhostMetrics));
                    em.SetName(e, "MetricsMonitor");
                }
            s_NetBits = s_NetSnapshots = s_NetMaxBits = 0; s_NetLastTick = 0; s_NetPerType.Clear();
            s_NetEnd = UnityEditor.EditorApplication.timeSinceStartup + seconds;
            UnityEditor.EditorApplication.update -= NetSampleTick;
            UnityEditor.EditorApplication.update += NetSampleTick;
            return "sampling";
        }

        static void NetSampleTick()
        {
            if (UnityEditor.EditorApplication.timeSinceStartup > s_NetEnd) { UnityEditor.EditorApplication.update -= NetSampleTick; return; }
            foreach (var w in World.All)
            {
                if (!w.IsCreated || !w.IsClient() || w.IsThinClient()) continue;
                var em = w.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<SnapshotMetrics>());
                if (q.CalculateEntityCount() != 1) return;
                q.CompleteDependency();
                var m = q.GetSingleton<SnapshotMetrics>();
                if (m.SnapshotTick == s_NetLastTick || m.TotalSizeInBits == 0) return;
                s_NetLastTick = m.SnapshotTick;
                s_NetBits += m.TotalSizeInBits; s_NetSnapshots++; s_NetMaxBits = System.Math.Max(s_NetMaxBits, m.TotalSizeInBits);
                var e = q.GetSingletonEntity();
                var names = em.GetBuffer<GhostNames>(e);
                var metrics = em.GetBuffer<GhostMetrics>(e);
                for (var i = 0; i < metrics.Length && i < names.Length; i++)
                {
                    var key = names[i].Name.ToString();
                    s_NetPerType.TryGetValue(key, out var acc);
                    s_NetPerType[key] = (acc.bits + metrics[i].SizeInBits, acc.n + 1, System.Math.Max(acc.inst, metrics[i].InstanceCount));
                }
                return;
            }
        }

        public static string NetSampleSummary()
        {
            if (s_NetSnapshots == 0) return "no snapshots sampled";
            var sb = new StringBuilder();
            sb.Append($"snapshots={s_NetSnapshots} avgBytes={s_NetBits / 8.0 / s_NetSnapshots:0} maxBytes={s_NetMaxBits / 8.0:0}\n");
            foreach (var kv in s_NetPerType)
                if (kv.Value.bits > 0) sb.Append($"  {kv.Key}: avgBytes={kv.Value.bits / 8.0 / System.Math.Max(1, kv.Value.n):0.0} maxInstances={kv.Value.inst}\n");
            return sb.ToString();
        }

        /// <summary>Count of rival acts seen since <see cref="StartRivalTrace"/> (sampled every frame).</summary>
        public static string RivalTraceLog = "";
        static readonly System.Collections.Generic.Dictionary<string, int> s_RivalActs = new System.Collections.Generic.Dictionary<string, int>();
        static double s_RivalTraceEnd;
        static string s_LastAct = "";

        public static string StartRivalTrace(float seconds)
        {
            s_RivalActs.Clear(); RivalTraceLog = ""; s_LastAct = "";
            s_RivalTraceEnd = UnityEditor.EditorApplication.timeSinceStartup + seconds;
            UnityEditor.EditorApplication.update -= RivalTraceTick;
            UnityEditor.EditorApplication.update += RivalTraceTick;
            return "tracing";
        }

        static void RivalTraceTick()
        {
            if (UnityEditor.EditorApplication.timeSinceStartup > s_RivalTraceEnd) { UnityEditor.EditorApplication.update -= RivalTraceTick; return; }
            var w = ServerWorld(); if (w == null) return;
            var em = w.EntityManager;
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            if (hq.CalculateEntityCount() != 1) return;
            var r = hq.GetSingleton<HeatState>().Rival;
            if (r == Entity.Null || !em.Exists(r)) return;
            var b = em.GetComponentData<RivalBrain>(r);
            var c = em.GetComponentData<CombatState>(r);
            var key = RivalAct.Name(b.Act) + "/" + (c.Guarding != 0 ? "GUARDING" : CombatAction.Name(c.Action));
            s_RivalActs[key] = s_RivalActs.TryGetValue(key, out var n) ? n + 1 : 1;
            if (key != s_LastAct) { RivalTraceLog += key + " "; s_LastAct = key; if (RivalTraceLog.Length > 4000) RivalTraceLog = RivalTraceLog.Substring(2000); }
        }

        public static string RivalTraceSummary()
        {
            var sb = new StringBuilder();
            foreach (var kv in s_RivalActs) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");
            return sb.ToString();
        }
    }
}
