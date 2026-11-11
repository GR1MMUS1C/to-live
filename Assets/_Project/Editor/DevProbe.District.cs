using System.Text;
using ToLive.Combat;
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
    /// <summary>Milestone 4A checks: district state, crowd and standings per world.</summary>
    public static partial class DevProbe
    {
        /// <summary>District state as each world sees it: meter, leader, population, moods, bodies, standings.</summary>
        public static string DistrictReport()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<DistrictState>());
                if (q.CalculateEntityCount() != 1) { sb.Append(world.Name).Append(": no district\n"); continue; }
                var e = q.GetSingletonEntity();
                var d = em.GetComponentData<DistrictState>(e);
                int[] moods = new int[4]; var alive = 0;
                var aliveBits = em.GetBuffer<CitizenAliveBits>(e); var moodBits = em.GetBuffer<CitizenMoodBits>(e);
                for (var i = 0; i < d.Population; i++)
                {
                    if ((i >> 5) >= aliveBits.Length || (aliveBits[i >> 5].Bits & (1u << (i & 31))) == 0) continue;
                    alive++;
                    if ((i >> 4) < moodBits.Length) moods[(moodBits[i >> 4].Bits >> ((i & 15) * 2)) & 3]++;
                }
                using var bq = em.CreateEntityQuery(ComponentType.ReadOnly<CitizenBody>());
                var heat = "";
                using (var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>()))
                    if (hq.CalculateEntityCount() == 1) { var h = hq.GetSingleton<HeatState>(); heat = $" heat={h.Level}/{h.Value:0} units={h.Units}"; }
                sb.Append($"{world.Name}: meter={d.Meter:0.00} leader={(d.Leader == Entity.Null ? "-" : d.Leader.ToString())} leaderAlign={d.LeaderAlignment:0.00} hold={d.Hold:0.00} alive={d.Alive}/{d.Population} (bits {alive}) wander={moods[0]} gather={moods[1]} flee={moods[2]} cower={moods[3]} bodies={bq.CalculateEntityCount()}{heat}\n");
                foreach (var s in em.GetBuffer<GodStanding>(e))
                {
                    var kind = em.Exists(s.God) && em.HasComponent<CharacterKind>(s.God) ? (em.GetComponentData<CharacterKind>(s.God).Kind == CharacterKind.Rival ? "rival" : "human") : "?";
                    var p = em.Exists(s.God) && em.HasComponent<PowerState>(s.God) ? em.GetComponentData<PowerState>(s.God) : default;
                    sb.Append($"  {kind} {s.God} trust={s.Trust:0.0} fear={s.Fear:0.0} align={p.Alignment:0.00} meter={p.Meter:0.00} fuel={p.Fuel:0.0} tier={p.Tier}\n");
                }
                var feed = em.GetBuffer<CauseEntry>(e);
                if (feed.Length > 0) { sb.Append("  feed:"); foreach (var c in feed) sb.Append($" [{Cause.Text(c.Kind, c.Value)}]"); sb.Append('\n'); }
            }
            return sb.ToString();
        }

        static Entity FirstHuman(EntityManager em)
        {
            using var cq = em.CreateEntityQuery(ComponentType.ReadOnly<CharacterKind>());
            using var chars = cq.ToEntityArray(Allocator.Temp);
            foreach (var c in chars) if (em.GetComponentData<CharacterKind>(c).Kind == CharacterKind.Human) return c;
            return Entity.Null;
        }

        /// <summary>Kill <paramref name="count"/> living citizens nearest to the first human, blamed on them (through the hazard queue).</summary>
        public static string KillCitizensNearPlayer(int count)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            var human = FirstHuman(em);
            using var sq = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            var server = sq.GetSingletonEntity();
            var citizens = em.GetBuffer<CitizenSim>(server).ToNativeArray(Allocator.Temp);
            var p = em.GetComponentData<LocalTransform>(human).Position;
            var order = new NativeList<(float d, int i)>(Allocator.Temp);
            for (var i = 0; i < citizens.Length; i++) if (citizens[i].Alive != 0) order.Add((math.distance(citizens[i].Position, p), i));
            order.Sort(new DistanceComparer());
            var n = math.min(count, order.Length);
            for (var k = 0; k < n; k++) CrowdHazards.Kill(em, citizens[order[k].i].Position, 0.3f, 0f, human, Cause.CiviliansKilled);
            return $"killing {n} citizens near the player";
        }

        struct DistanceComparer : System.Collections.Generic.IComparer<(float d, int i)>
        {
            public int Compare((float d, int i) a, (float d, int i) b) => a.d.CompareTo(b.d);
        }

        /// <summary>Add trust / fear to the first human's standing (debug, through the district source).</summary>
        public static string AddStanding(float trust, float fear)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            DistrictSource.Add(em, FirstHuman(em), trust, fear, Cause.Debug);
            return "queued";
        }

        /// <summary>Teleport the first human to hover above the densest part of the crowd (street level + height).</summary>
        public static string MoveToCrowd(float height)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var sq = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            var citizens = em.GetBuffer<CitizenSim>(sq.GetSingletonEntity()).ToNativeArray(Allocator.Temp);
            var best = float3.zero; var bestN = -1;
            for (var i = 0; i < citizens.Length; i += 3)
            {
                if (citizens[i].Alive == 0) continue;
                var n = DistrictSystem.CountNear(citizens, citizens[i].Position, 30f);
                if (n > bestN) { bestN = n; best = citizens[i].Position; }
            }
            Teleport(best.x, best.y + height, best.z, 0f);
            return $"moved over {bestN} citizens at {Fmt(best)}";
        }

        /// <summary>Bring the rival next to the first human and knock it out, as if the human did it.</summary>
        public static string KnockOutRivalHere()
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var hq = em.CreateEntityQuery(ComponentType.ReadOnly<HeatState>());
            var r = hq.GetSingleton<HeatState>().Rival;
            if (r == Entity.Null || !em.Exists(r)) return "no rival";
            var human = FirstHuman(em);
            var p = em.GetComponentData<LocalTransform>(human).Position;
            em.SetComponentData(r, LocalTransform.FromPosition(p + new float3(4f, 0f, 0f)));
            using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            var tick = tq.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;
            var c = em.GetComponentData<CombatState>(r);
            c.LastHitTick = tick;
            Damage.Apply(ref c, 10000f, tick, Damage.TickRate(em), 4f);
            em.SetComponentData(r, c);
            em.SetComponentData(r, new LastAttacker { Value = human });
            return "rival knocked out next to the player";
        }

        /// <summary>Living citizens within <paramref name="radius"/> of the first human (server).</summary>
        public static string CrowdNearPlayer(float radius)
        {
            var w = ServerWorld(); if (w == null) return "no server";
            var em = w.EntityManager;
            using var sq = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            var citizens = em.GetBuffer<CitizenSim>(sq.GetSingletonEntity()).ToNativeArray(Allocator.Temp);
            var p = em.GetComponentData<LocalTransform>(FirstHuman(em)).Position;
            return $"{DistrictSystem.CountNear(citizens, p, radius)} citizens within {radius} m";
        }
    }
}
