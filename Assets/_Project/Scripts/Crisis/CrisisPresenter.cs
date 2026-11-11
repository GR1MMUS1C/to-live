using ToLive.Combat;
using ToLive.Destruction;
using ToLive.District;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Crisis
{
    /// <summary>What the HUD shows about the crisis, the holder and defeat (filled by <see cref="CrisisPresenter"/>).</summary>
    public static class CrisisView
    {
        public static bool Active;
        public static byte Kind, RivalApproach;
        public static string Title = "", Objective = "", Protect = "", Force = "";
        public static float Remaining, Distance;
        public static int Progress, Goal, Saved, Lost;
        public static float3 Marker;
        public static string Outcome = "";
        public static float OutcomeTime = -100f;
        public static bool OutcomeGood;
        public static string Holder = "";
        public static float ExpelledFor;     // seconds left of the local player's lockout (0 = none)
        public static bool TargetIsLocal;
    }

    /// <summary>
    /// CLIENT ONLY. Draws the crisis from the replicated state: a beacon column visible from the air,
    /// a pulsing ring around a failing chunk, fortification towers raised up to the replicated level and
    /// their shots, and fills <see cref="CrisisView"/> for the HUD. Also raises the SFX hooks
    /// (<see cref="CombatFx"/>). Purely cosmetic.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CrisisPresenter : SystemBase
    {
        LineRenderer m_Beacon, m_Ring, m_Fall, m_TowerShot;
        float m_TowerShotUntil;
        uint m_SeenSeq, m_SeenShot;
        byte m_SeenPhase;
        int m_FortifyShown = -1;
        Material m_Mat;

        protected override void OnDestroy()
        {
            foreach (var l in new[] { m_Beacon, m_Ring, m_Fall, m_TowerShot }) if (l != null) Object.Destroy(l.gameObject);
        }

        LineRenderer Line(string name, int points, float width)
        {
            if (m_Mat == null) m_Mat = FxMaterials.Unlit();
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = m_Mat;
            lr.material = new Material(m_Mat);
            lr.positionCount = points;
            lr.widthMultiplier = width;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            CrisisView.Active = false;
            if (!SystemAPI.TryGetSingletonEntity<DistrictState>(out var districtEntity)) return;
            var crisis = SystemAPI.GetComponent<CrisisState>(districtEntity);
            var hold = SystemAPI.GetComponent<DistrictHold>(districtEntity);
            var net = SystemAPI.TryGetSingleton<NetworkTime>(out var nt) ? nt : default;
            var interp = net.InterpolationTick.IsValid ? net.InterpolationTick.TickIndexForValidTick : 0u;
            var serverTick = net.ServerTick.IsValid ? net.ServerTick.TickIndexForValidTick : 0u;
            var rate = Damage.TickRate(EntityManager);
            var time = (float)SystemAPI.Time.ElapsedTime;
            if (m_Beacon == null) { m_Beacon = Line("CrisisBeacon", 2, 3f); m_Ring = Line("FailingChunkRing", 33, 0.35f); m_Fall = Line("FailingChunkFall", 2, 0.15f); m_TowerShot = Line("TowerShot", 2, 0.25f); }

            var local = Entity.Null; var localPos = float3.zero;
            foreach (var (_, xf, e) in SystemAPI.Query<RefRO<ToLive.Opposition.CharacterKind>, RefRO<LocalTransform>>().WithEntityAccess())
                if (SystemAPI.HasComponent<GhostOwnerIsLocal>(e) && SystemAPI.IsComponentEnabled<GhostOwnerIsLocal>(e)) { local = e; localPos = xf.ValueRO.Position; }

            // ---- announcements / SFX hooks ----
            if (crisis.Seq != m_SeenSeq && crisis.Phase == CrisisPhase.Active) { m_SeenSeq = crisis.Seq; CombatFx.RaiseCrisisStart(crisis.Position, crisis.Kind); }
            if (crisis.Phase == CrisisPhase.Resolved && m_SeenPhase != CrisisPhase.Resolved && crisis.Seq == m_SeenSeq)
            {
                var resolverIsLocal = crisis.Resolver != Entity.Null && crisis.Resolver == local;
                CrisisView.Outcome = $"{CrisisKind.Name(crisis.Kind)}: {CrisisOutcome.Name(crisis.Outcome)}" +
                                     (crisis.Outcome == CrisisOutcome.Failed ? "" : resolverIsLocal ? " - by you" : crisis.Resolver != Entity.Null ? " - by another god" : "");
                CrisisView.OutcomeGood = crisis.Outcome == CrisisOutcome.Protected;
                CrisisView.OutcomeTime = UnityEngine.Time.unscaledTime;
                CombatFx.RaiseCrisisEnd(crisis.Position, crisis.Outcome);
            }
            m_SeenPhase = crisis.Phase;

            // ---- live crisis ----
            var active = crisis.Phase == CrisisPhase.Active;
            var beaconColor = crisis.Kind == CrisisKind.Disaster ? new Color(2.2f, 1.3f, 0.3f) : crisis.Kind == CrisisKind.Attack ? new Color(2.2f, 0.35f, 0.2f) : new Color(1.6f, 0.3f, 1.4f);
            m_Beacon.enabled = active;
            if (active)
            {
                var basePos = new Vector3(crisis.Position.x, 0f, crisis.Position.z);
                m_Beacon.SetPosition(0, basePos); m_Beacon.SetPosition(1, basePos + Vector3.up * 400f);
                m_Beacon.material.color = beaconColor * (0.75f + 0.25f * Mathf.Sin(time * 3f));
                CrisisView.Active = true;
                CrisisView.Kind = crisis.Kind; CrisisView.RivalApproach = crisis.RivalApproach;
                CrisisView.Marker = crisis.Position;
                CrisisView.Distance = local != Entity.Null ? math.distance(localPos, crisis.Position) : 0f;
                CrisisView.Remaining = interp != 0 ? math.max(0f, (int)(crisis.EndTick - interp) / (float)rate) : 0f;
                CrisisView.Progress = crisis.Progress; CrisisView.Goal = crisis.Goal; CrisisView.Saved = crisis.Saved; CrisisView.Lost = crisis.Lost;
                CrisisView.TargetIsLocal = crisis.Target != Entity.Null && crisis.Target == local;
                CrisisView.Title = CrisisKind.Name(crisis.Kind);
                switch (crisis.Kind)
                {
                    case CrisisKind.Disaster:
                        CrisisView.Objective = $"Chunks held up {crisis.Saved}/{crisis.Goal}   fallen {crisis.Lost}";
                        CrisisView.Protect = "PROTECT: fly to the red ring and GUARD (LB) to hold the failing chunk up while people evacuate";
                        CrisisView.Force = "FORCE: bring the tower down yourself (fast, but people die)";
                        break;
                    case CrisisKind.Attack:
                        CrisisView.Objective = crisis.Target != Entity.Null ? "VANTA is attacking the crowd" : $"Attackers destroyed {crisis.Progress}/{crisis.Goal}   people killed {crisis.Lost}";
                        CrisisView.Protect = "PROTECT: destroy the attackers away from the people";
                        CrisisView.Force = "FORCE: flatten them and everything around them";
                        break;
                    case CrisisKind.Uprising:
                        CrisisView.Objective = CrisisView.TargetIsLocal ? $"The people are rising against YOU   standing down {crisis.Progress}/{crisis.Goal} s" : $"The people are rising against a feared god   {crisis.Progress}/{crisis.Goal} s";
                        CrisisView.Protect = "PROTECT: stay near them and harm no one until they calm down";
                        CrisisView.Force = "FORCE: crush it (a massacre: lasting fear)";
                        break;
                }
            }

            // failing chunk: a pulsing ring (the catch radius) and the fall line
            var warn = active && crisis.Kind == CrisisKind.Disaster && crisis.WarnChunk >= 0 && SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graphEntity);
            m_Ring.enabled = warn; m_Fall.enabled = warn;
            if (warn)
            {
                var nodes = SystemAPI.GetBuffer<ChunkNode>(SystemAPI.GetSingletonEntity<BuildingGraph>());
                var c = (Vector3)(float3)nodes[crisis.WarnChunk].Center;
                var cfg = SystemAPI.TryGetSingleton<CrisisConfig>(out var cc) ? cc : default;
                var left = math.saturate((int)(crisis.WarnTick - interp) / math.max(1f, cfg.WarnTime * rate));
                var r = math.max(2f, cfg.CatchRadius);
                for (var i = 0; i < 33; i++)
                {
                    var a = i / 32f * Mathf.PI * 2f;
                    m_Ring.SetPosition(i, c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                }
                var pulse = 0.5f + 0.5f * Mathf.Sin(time * (8f + 20f * (1f - left)));
                m_Ring.material.color = new Color(3f, 0.25f, 0.15f) * (0.6f + 0.6f * pulse);
                m_Ring.widthMultiplier = 0.25f + 0.35f * (1f - left);
                m_Fall.SetPosition(0, c); m_Fall.SetPosition(1, new Vector3(c.x, 0f, c.z));
                m_Fall.material.color = new Color(1.5f, 0.15f, 0.1f) * pulse;
            }

            // ---- fortifications: raised up to the replicated level ----
            if (hold.FortifyLevel != m_FortifyShown)
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                foreach (var (fort, e) in SystemAPI.Query<RefRO<Fortification>>().WithEntityAccess().WithOptions(EntityQueryOptions.IncludeDisabledEntities))
                {
                    var show = fort.ValueRO.Index < hold.FortifyLevel;
                    var hidden = SystemAPI.HasComponent<DisableRendering>(e);
                    if (show && hidden) ecb.RemoveComponent<DisableRendering>(e);
                    else if (!show && !hidden) ecb.AddComponent<DisableRendering>(e);
                }
                ecb.Playback(EntityManager);
                m_FortifyShown = hold.FortifyLevel;
            }
            if (hold.TowerShotTick != m_SeenShot && hold.TowerShotTick != 0)
            {
                m_SeenShot = hold.TowerShotTick;
                foreach (var fort in SystemAPI.Query<RefRO<Fortification>>())
                {
                    if (fort.ValueRO.Index != hold.TowerShotIndex) continue;
                    var to = SystemAPI.HasComponent<LocalTransform>(hold.TowerShotTarget) ? SystemAPI.GetComponent<LocalTransform>(hold.TowerShotTarget).Position : fort.ValueRO.Top;
                    m_TowerShot.SetPosition(0, fort.ValueRO.Top); m_TowerShot.SetPosition(1, to);
                    m_TowerShot.material.color = DistrictView.DistrictColor * 2f;
                    m_TowerShotUntil = time + 0.12f;
                    CombatFx.RaiseTracer(fort.ValueRO.Top, to);
                    break;
                }
            }
            m_TowerShot.enabled = time < m_TowerShotUntil;

            // ---- holder and defeat ----
            CrisisView.Holder = hold.Holder == Entity.Null ? "nobody holds it" :
                (hold.Holder == local ? "YOU hold it" : "held by another god") + (hold.Mode == DistrictHold.Trust ? " through trust" : " through dominance") +
                (hold.Mode == DistrictHold.Dominance && hold.FortifyLevel > 0 ? $" ({hold.FortifyLevel} towers)" : "");
            CrisisView.ExpelledFor = 0f;
            if (local != Entity.Null && SystemAPI.HasComponent<PowerState>(local))
            {
                var p = SystemAPI.GetComponent<PowerState>(local);
                if (p.ExpelledUntilTick != 0 && serverTick != 0 && CombatSystem.Before(serverTick, p.ExpelledUntilTick))
                    CrisisView.ExpelledFor = (int)(p.ExpelledUntilTick - serverTick) / (float)rate;
            }
        }
    }
}
