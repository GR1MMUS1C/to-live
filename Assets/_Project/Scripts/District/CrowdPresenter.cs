using System.Collections.Generic;
using ToLive.Opposition;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ToLive.District
{
    /// <summary>What the HUD shows about the district (filled by <see cref="CrowdPresenter"/>).</summary>
    public static class DistrictView
    {
        public static bool Present;
        public static float Meter, Hold, LeaderAlignment;
        public static bool LeaderIsLocal, HasLeader;
        public static int Population, Alive, Gathering, Fleeing, Cowering, Bodies;
        public static float MyTrust, MyFear;
        public static Color DistrictColor = Color.gray;
        public static readonly List<(string text, float time, bool good)> Feed = new List<(string, float, bool)>();
    }

    /// <summary>
    /// CLIENT ONLY. Draws the crowd from the replicated district state: one local agent per citizen
    /// (not networked) that walks, gathers and cheers, flees or cowers according to its replicated
    /// 2-bit mood, and disappears when its alive bit clears (the body is a replicated static ghost).
    /// Agent positions are cosmetic and only roughly match the server's; everything players must agree
    /// on (alive, mood, bodies, standing) is replicated. Also recolours the city: roof crowns, street
    /// lamps and a sky tint take the leader's colour (trust cyan / dominance crimson) by how strongly
    /// they hold the district. Like client-side crowd actors driven by a replicated manager.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CrowdPresenter : SystemBase
    {
        Entity[] m_Agents = new Entity[0];
        float3[] m_Pos, m_Target;
        bool[] m_Hidden;
        int m_Population = -1;
        uint m_SeenCause;
        float4 m_Accent = new float4(0.16f, 0.16f, 0.17f, 1f);
        Volume m_Tint;
        ColorAdjustments m_Adjust;
        Unity.Mathematics.Random m_Random = new Unity.Mathematics.Random(0xC1712);

        static readonly float4 Unheld = new float4(0.16f, 0.16f, 0.17f, 1f);

        protected override void OnDestroy()
        {
            if (m_Tint != null) Object.Destroy(m_Tint.gameObject);
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            DistrictView.Present = false;
            if (!SystemAPI.TryGetSingletonEntity<DistrictState>(out var districtEntity)) return;
            if (!SystemAPI.TryGetSingleton<DistrictConfig>(out var cfg) || !SystemAPI.TryGetSingletonEntity<CrowdLayout>(out var layoutEntity)) return;
            var d = SystemAPI.GetComponent<DistrictState>(districtEntity);
            var homes = SystemAPI.GetBuffer<CitizenHome>(layoutEntity).ToNativeArray(Allocator.Temp);
            var dt = SystemAPI.Time.DeltaTime;
            var time = (float)SystemAPI.Time.ElapsedTime;
            var net = SystemAPI.TryGetSingleton<NetworkTime>(out var nt) ? nt : default;
            var interp = net.InterpolationTick.IsValid ? net.InterpolationTick.TickIndexForValidTick : 0u;
            var rate = ToLive.Combat.Damage.TickRate(EntityManager);

            // ---- agents: (re)create when the population changes ----
            var population = math.min(d.Population, homes.Length);
            if (population != m_Population && cfg.CitizenPrefab != Entity.Null)
            {
                foreach (var a in m_Agents) if (EntityManager.Exists(a)) EntityManager.DestroyEntity(a);
                m_Agents = new Entity[population];
                m_Pos = new float3[population]; m_Target = new float3[population]; m_Hidden = new bool[population];
                for (var i = 0; i < population; i++)
                {
                    var e = EntityManager.Instantiate(cfg.CitizenPrefab);
                    m_Agents[i] = e; m_Pos[i] = homes[i].Position; m_Target[i] = homes[i].Position;
                    var shade = 0.42f + 0.18f * ((i * 37) % 10) / 10f;
                    if (EntityManager.HasComponent<URPMaterialPropertyBaseColor>(e))
                        EntityManager.SetComponentData(e, new URPMaterialPropertyBaseColor { Value = new float4(shade, shade, shade * 1.02f, 1f) });
                }
                m_Population = population;
            }

            // gods and recent threats (for gather / flee directions)
            var gods = new NativeList<float3>(4, Allocator.Temp);
            var local = Entity.Null;
            foreach (var (_, xf, e) in SystemAPI.Query<RefRO<CharacterKind>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                gods.Add(xf.ValueRO.Position);
                if (SystemAPI.HasComponent<GhostOwnerIsLocal>(e) && SystemAPI.IsComponentEnabled<GhostOwnerIsLocal>(e)) local = e;
            }
            var threats = new NativeList<float3>(4, Allocator.Temp);
            var fleeTicks = (uint)(cfg.FleeTime * rate);
            void T(float3 p, uint t) { if (t != 0 && interp - t < fleeTicks) threats.Add(p); }
            T(d.Threat0, d.ThreatTick0); T(d.Threat1, d.ThreatTick1); T(d.Threat2, d.ThreatTick2); T(d.Threat3, d.ThreatTick3);

            var aliveBits = SystemAPI.GetBuffer<CitizenAliveBits>(districtEntity).ToNativeArray(Allocator.Temp);
            var moodBits = SystemAPI.GetBuffer<CitizenMoodBits>(districtEntity).ToNativeArray(Allocator.Temp);
            var leaderColor = PowerLook.Instance != null ? PowerLook.Instance.EnergyColor(d.LeaderAlignment) : Color.white;
            var leaderLdr = leaderColor.maxColorComponent > 1f ? leaderColor / leaderColor.maxColorComponent : leaderColor;

            var transforms = GetComponentLookup<LocalTransform>();
            var colors = GetComponentLookup<URPMaterialPropertyBaseColor>();
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int gathering = 0, fleeing = 0, cowering = 0;
            for (var i = 0; i < m_Agents.Length; i++)
            {
                var e = m_Agents[i];
                if (!transforms.HasComponent(e)) continue;
                var alive = (i >> 5) < aliveBits.Length && (aliveBits[i >> 5].Bits & (1u << (i & 31))) != 0;
                if (!alive)
                {
                    if (!m_Hidden[i]) { ecb.AddComponent<DisableRendering>(e); m_Hidden[i] = true; }
                    continue;
                }
                if (m_Hidden[i]) { ecb.RemoveComponent<DisableRendering>(e); m_Hidden[i] = false; m_Pos[i] = homes[i].Position; }
                var mood = (i >> 4) < moodBits.Length ? (byte)((moodBits[i >> 4].Bits >> ((i & 15) * 2)) & 3) : Mood.Wander;
                var home = homes[i];
                var pos = m_Pos[i];
                var y = home.Position.y;
                var facing = float3.zero;
                switch (mood)
                {
                    case Mood.Wander:
                        if (math.distancesq(pos.xz, m_Target[i].xz) < 0.3f) m_Target[i] = home.Position + home.Street * m_Random.NextFloat(-cfg.WanderRange, cfg.WanderRange);
                        facing = Step(ref pos, m_Target[i], cfg.WalkSpeed * dt);
                        break;
                    case Mood.Gather:
                    {
                        gathering++;
                        var focus = Nearest(gods, pos, out var found);
                        if (found)
                        {
                            var angle = i * 2.39996f;
                            var spot = new float3(focus.x + math.cos(angle) * cfg.GatherDistance, y, focus.z + math.sin(angle) * cfg.GatherDistance);
                            var along = math.dot(spot - home.Position, home.Street);
                            Step(ref pos, home.Position + home.Street * math.clamp(along, -40f, 40f), cfg.WalkSpeed * 1.6f * dt);
                            facing = focus - pos;
                        }
                        y += math.abs(math.sin(time * 7f + i)) * 0.35f; // cheering: little jumps
                        break;
                    }
                    case Mood.Flee:
                    {
                        fleeing++;
                        var from = threats.Length > 0 ? Nearest(threats, pos, out _) : Nearest(gods, pos, out _);
                        var dir = math.dot(pos - from, home.Street) >= 0f ? home.Street : -home.Street;
                        var next = pos + dir * cfg.RunSpeed * dt;
                        if (math.abs(math.dot(next - home.Position, home.Street)) < 70f) pos = next;
                        facing = dir;
                        break;
                    }
                    case Mood.Cower:
                        cowering++;
                        y -= 0.45f; // crouched
                        break;
                }
                pos.y = home.Position.y;
                m_Pos[i] = pos;
                var rot = math.lengthsq(facing.xz) > 1e-4f ? quaternion.LookRotationSafe(math.normalize(new float3(facing.x, 0, facing.z)), new float3(0, 1, 0)) : transforms[e].Rotation;
                transforms[e] = LocalTransform.FromPositionRotation(new float3(pos.x, y + 0.9f, pos.z), rot);
                if (colors.HasComponent(e) && mood == Mood.Gather)
                {
                    var shade = 0.42f + 0.18f * ((i * 37) % 10) / 10f;
                    var c = Color.Lerp(new Color(shade, shade, shade), leaderLdr, 0.35f);
                    colors[e] = new URPMaterialPropertyBaseColor { Value = new float4(c.r, c.g, c.b, 1f) };
                }
                else if (colors.HasComponent(e))
                {
                    var shade = 0.42f + 0.18f * ((i * 37) % 10) / 10f;
                    colors[e] = new URPMaterialPropertyBaseColor { Value = new float4(shade, shade, shade * 1.02f, 1f) };
                }
            }
            ecb.Playback(EntityManager);

            // ---- recolour the city: leader colour by how strongly they hold it ----
            var hold = math.saturate(d.Hold);
            var target = d.Leader == Entity.Null ? Unheld : math.lerp(Unheld, new float4(leaderColor.r, leaderColor.g, leaderColor.b, 1f) * (1.2f + 1.6f * hold), math.smoothstep(0.05f, 0.8f, hold)); // brighter = held harder (blooms)
            m_Accent = math.lerp(m_Accent, target, 1f - math.exp(-1.5f * dt));
            foreach (var (accent, color) in SystemAPI.Query<RefRO<DistrictAccent>, RefRW<URPMaterialPropertyBaseColor>>())
            {
                var s = accent.ValueRO.Strength;
                color.ValueRW.Value = new float4(math.lerp(Unheld.xyz, m_Accent.xyz, math.saturate(s)) * math.max(1f, s), 1f);
            }
            UpdateSkyTint(new Color(m_Accent.x, m_Accent.y, m_Accent.z), d.Leader == Entity.Null ? 0f : hold);

            // ---- HUD view ----
            DistrictView.Present = true;
            DistrictView.Meter = d.Meter; DistrictView.Hold = hold; DistrictView.LeaderAlignment = d.LeaderAlignment;
            DistrictView.HasLeader = d.Leader != Entity.Null;
            DistrictView.LeaderIsLocal = d.Leader != Entity.Null && d.Leader == local;
            DistrictView.Population = d.Population; DistrictView.Alive = d.Alive;
            DistrictView.Gathering = gathering; DistrictView.Fleeing = fleeing; DistrictView.Cowering = cowering;
            DistrictView.DistrictColor = leaderLdr;
            DistrictView.MyTrust = 0f; DistrictView.MyFear = 0f;
            foreach (var s in SystemAPI.GetBuffer<GodStanding>(districtEntity))
                if (s.God == local) { DistrictView.MyTrust = s.Trust; DistrictView.MyFear = s.Fear; }
            DistrictView.Bodies = SystemAPI.QueryBuilder().WithAll<CitizenBody>().Build().CalculateEntityCount();
            foreach (var c in SystemAPI.GetBuffer<CauseEntry>(districtEntity))
            {
                if (c.Seq <= m_SeenCause) continue;
                m_SeenCause = c.Seq;
                if (c.God != local || c.Kind == Cause.Debug) continue;
                var good = c.Kind == Cause.ProtectedCrowd || c.Kind == Cause.UnitDestroyed || c.Kind == Cause.RivalDefeated;
                var text = Cause.Text(c.Kind, c.Value);
                var last = DistrictView.Feed.Count - 1;
                if (last >= 0 && DistrictView.Feed[last].text == text) DistrictView.Feed[last] = (text, UnityEngine.Time.unscaledTime, good); // same line again: refresh it
                else DistrictView.Feed.Add((text, UnityEngine.Time.unscaledTime, good));
                if (DistrictView.Feed.Count > 5) DistrictView.Feed.RemoveAt(0);
            }
        }

        void UpdateSkyTint(Color color, float weight)
        {
            if (m_Tint == null)
            {
                // a runtime global volume (no shader lookups): a colour filter in the district's colour
                var go = new GameObject("District Tint (runtime)");
                m_Tint = go.AddComponent<Volume>();
                m_Tint.isGlobal = true;
                m_Tint.priority = 5f;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                m_Adjust = profile.Add<ColorAdjustments>(false);
                m_Adjust.colorFilter.overrideState = true;
                m_Tint.sharedProfile = profile;
            }
            var max = Mathf.Max(0.001f, color.maxColorComponent);
            var tint = Color.Lerp(Color.white, color / max, 0.22f);
            m_Adjust.colorFilter.value = tint;
            m_Tint.weight = Mathf.Lerp(m_Tint.weight, Mathf.Clamp01(weight), 1f - Mathf.Exp(-1.5f * UnityEngine.Time.deltaTime));
        }

        static float3 Step(ref float3 pos, float3 target, float step)
        {
            var d = target - pos; d.y = 0;
            var len = math.length(d);
            if (len < 1e-4f) return float3.zero;
            pos += d / len * math.min(len, step);
            return d;
        }

        static float3 Nearest(NativeList<float3> points, float3 p, out bool found)
        {
            found = false; var best = float.MaxValue; var r = p;
            foreach (var q in points) { var d = math.distancesq(q, p); if (d < best) { best = d; r = q; found = true; } }
            return r;
        }
    }
}
