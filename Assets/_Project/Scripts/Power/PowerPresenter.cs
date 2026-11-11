using System.Collections.Generic;
using ToLive.CameraRig;
using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Player;
using Material = UnityEngine.Material;
using Collider = UnityEngine.Collider;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ToLive.Power
{
    /// <summary>
    /// CLIENT-ONLY presentation of power: the fractured halo, the energy-colour wash (light +
    /// post-processing), the ritual's world reactions (rubble lifting, pressure rings, crater,
    /// glass blowout), the eruption, the ritual camera and the beam. Reads the replicated
    /// PowerState of every character, so other players and late joiners see the same phase/halo.
    /// Nothing here affects gameplay. Unreal analogy: a cosmetic manager reading replicated state.
    /// </summary>
    public class PowerPresenter : MonoBehaviour
    {
        [Tooltip("Global Volume pushed up during the local view's ritual/surge (colour + bloom).")]
        public Volume PowerVolume;

        class Visual
        {
            public HaloRig Halo;
            public byte LastPhase, LastTier;
            public uint RitualStart;
            public bool RubbleSpawned, GlassMid, Erupted;
            public float NextRing;
            public float EruptAge = 99f;
            public GameObject Crater;
            public LineRenderer Beam;
            public Transform BeamGlow, ChargeGlow;
        }

        readonly Dictionary<Entity, Visual> m_Visuals = new Dictionary<Entity, Visual>();
        World m_World;
        EntityQuery m_Query, m_TimeQuery, m_PhysicsQuery;
        static Material s_CraterMat, s_BeamMat, s_GlowMat;

        public static float LocalMeter, LocalBeamEnergy;
        public static byte LocalPhase, LocalTier;
        public static float LocalAlignment;

        void LateUpdate()
        {
            var world = ClientServerBootstrap.ClientWorld;
            var look = PowerLook.Instance;
            if (world == null || !world.IsCreated || look == null) { ClearAll(); return; }
            if (world != m_World)
            {
                ClearAll();
                m_World = world;
                var em0 = world.EntityManager;
                m_Query = em0.CreateEntityQuery(ComponentType.ReadOnly<PowerState>(), ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<MoveState>());
                m_TimeQuery = em0.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
                m_PhysicsQuery = em0.CreateEntityQuery(ComponentType.ReadOnly<PhysicsWorldSingleton>());
            }
            if (m_TimeQuery.CalculateEntityCount() != 1) return;
            var em = world.EntityManager;
            m_Query.CompleteDependency();
            var net = m_TimeQuery.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick + net.ServerTickFraction - 1f;
            var dt = Time.deltaTime;
            var cam = Camera.main;
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            var hasPhysics = m_PhysicsQuery.CalculateEntityCount() == 1;
            var physics = hasPhysics ? m_PhysicsQuery.GetSingleton<PhysicsWorldSingleton>() : default;

            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<Entity>();
            var volumeWeight = 0f;
            var volumeColor = Color.white;
            var localRitual = false;
            var localProgress = 0f;
            FlightCameraRig.RitualCam = false;

            foreach (var e in entities)
            {
                seen.Add(e);
                var p = em.GetComponentData<PowerState>(e);
                var ltw = em.GetComponentData<LocalToWorld>(e);
                var f = em.GetComponentData<FlightState>(e);
                var m = em.GetComponentData<MoveState>(e);
                var isLocal = em.HasComponent<GhostOwnerIsLocal>(e) && em.IsComponentEnabled<GhostOwnerIsLocal>(e);
                var center = (Vector3)ltw.Position + Vector3.up * 0.2f;
                var color = look.EnergyColor(p.Alignment);

                if (!m_Visuals.TryGetValue(e, out var v))
                {
                    v = new Visual { Halo = new HaloRig($"Halo_{e.Index}", look.ShardCount), LastPhase = p.Phase, Erupted = p.Phase == SurgePhase.Surge };
                    m_Visuals[e] = v;
                }

                var progress = p.Phase == SurgePhase.Ritual
                    ? Mathf.Clamp01((tick - p.PhaseStartTick) / Mathf.Max(1f, p.PhaseEndTick - p.PhaseStartTick))
                    : 0f;

                // phase transitions
                if (p.Phase == SurgePhase.Ritual && (v.LastPhase != SurgePhase.Ritual || v.RitualStart != p.PhaseStartTick))
                {
                    v.RitualStart = p.PhaseStartTick;
                    v.RubbleSpawned = v.GlassMid = v.Erupted = false;
                    v.NextRing = 0f;
                }
                if (p.Phase == SurgePhase.Surge && !v.Erupted)
                {
                    v.Erupted = true;
                    if (v.LastPhase == SurgePhase.Ritual || (tick - p.PhaseStartTick) < 30) Erupt(v, center, color, look, physics, hasPhysics, camPos, isLocal);
                }
                if (v.LastPhase != p.Phase || v.LastTier != p.Tier)
                    Debug.Log($"[ToLive] {world.Name} sees character {e.Index} phase={SurgePhase.Name(p.Phase)}({p.Phase}) tier={p.Tier} alignment={p.Alignment:0.00}");
                v.LastPhase = p.Phase;
                v.LastTier = p.Tier;
                v.EruptAge += dt;

                // ---------- halo ----------
                float intensity = 0f, glow = 0f, burst = 0f, wash = 0f;
                var shards = 0;
                switch (p.Phase)
                {
                    case SurgePhase.Ritual:
                        intensity = Mathf.Lerp(0.35f, 1f, progress);
                        shards = Mathf.RoundToInt(Mathf.Lerp(look.Tier2Shards, look.ShardCount, progress));
                        glow = Mathf.Lerp(1f, look.RitualGlow, progress * progress);
                        burst = -0.35f * progress + progress * progress * progress * 0.25f; // pulled in tight, then straining out
                        wash = look.WashIntensityRitual * progress * progress;
                        break;
                    case SurgePhase.Surge:
                        intensity = 1f;
                        shards = look.ShardCount;
                        var eruptFade = Mathf.Clamp01(1f - v.EruptAge / 0.8f);
                        glow = look.SurgeGlow + eruptFade * look.RitualGlow;
                        burst = eruptFade * 0.35f;
                        wash = look.WashIntensitySurge + eruptFade * look.WashIntensityRitual;
                        break;
                    case SurgePhase.Burnout:
                        intensity = 0.3f;
                        shards = look.Tier2Shards / 2;
                        glow = 0.35f * (0.5f + 0.5f * Mathf.PerlinNoise(Time.time * 6f, e.Index));
                        break;
                    default:
                        if (p.Tier >= 2) { intensity = 0.3f; shards = look.Tier2Shards; glow = look.Tier2Glow; }
                        break;
                }
                v.Halo.Update(center, p.Alignment, intensity, shards, glow, burst, color, wash, look.WashRange, look, dt);

                // ---------- ritual world reactions (cosmetic) ----------
                if (p.Phase == SurgePhase.Ritual)
                {
                    DebrisSystem.RitualFx.Add(new RitualDebrisFx
                    {
                        Caster = e.Index, Center = ltw.Position, Radius = look.LiftRadius, Height = look.LiftHeight,
                        Progress = progress, SpawnRubble = !v.RubbleSpawned, RubbleCount = look.RubblePieces, Tint = color,
                    });
                    v.RubbleSpawned = true;
                    v.NextRing -= dt;
                    if (v.NextRing <= 0f)
                    {
                        GroundRing.Spawn(new Vector3(center.x, GroundY(center, physics, hasPhysics) + 0.15f, center.z), color * Mathf.Lerp(0.8f, 2f, progress),
                            1f, Mathf.Lerp(6f, 22f, progress), 0.7f, Mathf.Lerp(0.3f, 0.9f, progress));
                        v.NextRing = Mathf.Lerp(0.55f, 0.18f, progress);
                    }
                    if (!v.GlassMid && progress > 0.55f) { v.GlassMid = true; BlowGlass(center, color, look.GlassRange * 0.6f, 10, physics, hasPhysics); }
                    UpdateCrater(v, center, progress * 7f + 1.5f, physics, hasPhysics);
                    CombatFx.RaiseRitual(center, progress);
                }
                else if (v.Crater != null && p.Phase != SurgePhase.Surge)
                {
                    Destroy(v.Crater); v.Crater = null;
                }

                // ---------- colour wash on the local view ----------
                var phaseWeight = p.Phase == SurgePhase.Ritual ? progress : p.Phase == SurgePhase.Surge ? 0.55f + 0.45f * Mathf.Clamp01(1f - v.EruptAge / 1.2f) : 0f;
                var proximity = isLocal ? 1f : Mathf.Clamp01(1f - (Vector3.Distance(camPos, center) - 20f) / 60f);
                if (phaseWeight * proximity > volumeWeight)
                {
                    volumeWeight = phaseWeight * proximity;
                    volumeColor = color;
                }

                if (isLocal)
                {
                    LocalMeter = p.Meter; LocalBeamEnergy = p.BeamEnergy; LocalPhase = p.Phase; LocalTier = p.Tier; LocalAlignment = p.Alignment;
                    if (p.Phase == SurgePhase.Ritual) { localRitual = true; localProgress = progress; }
                }

                // ---------- beam ----------
                UpdateBeam(v, ltw.Position, f, m, color, physics, hasPhysics);
            }

            // ritual camera for the local player
            FlightCameraRig.RitualCam = localRitual;
            FlightCameraRig.RitualProgress = localProgress;
            if (localRitual) CombatFx.Shake = Mathf.Max(CombatFx.Shake, 0.05f + 0.35f * localProgress * localProgress);

            // post-processing push
            if (PowerVolume != null)
            {
                PowerVolume.weight = Mathf.MoveTowards(PowerVolume.weight, volumeWeight, dt * 3f);
                var tint = volumeColor.maxColorComponent > 0f ? volumeColor / volumeColor.maxColorComponent : Color.white;
                if (PowerVolume.profile.TryGet<ColorAdjustments>(out var ca)) ca.colorFilter.value = Color.Lerp(Color.white, tint, 0.25f);
                if (PowerVolume.profile.TryGet<Bloom>(out var bloom)) bloom.tint.value = tint;
            }

            // clean up characters that left
            var gone = new List<Entity>();
            foreach (var kv in m_Visuals) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var e in gone) { Remove(m_Visuals[e]); m_Visuals.Remove(e); }
        }

        void Erupt(Visual v, Vector3 center, Color color, PowerLook look, PhysicsWorldSingleton physics, bool hasPhysics, Vector3 camPos, bool isLocal)
        {
            v.EruptAge = 0f;
            var ground = GroundY(center, physics, hasPhysics);
            GroundRing.Spawn(new Vector3(center.x, ground + 0.2f, center.z), color * 4f, 2f, 45f, 1.1f, 2.5f);
            GroundRing.Spawn(center, color * 3f, 1f, 30f, 0.8f, 1.5f, Vector3.forward);
            GroundRing.Spawn(center, color * 3f, 1f, 30f, 0.8f, 1.5f, Vector3.right);
            BlowGlass(center, color, look.GlassRange, 24, physics, hasPhysics);
            DebrisSystem.RitualFx.Add(new RitualDebrisFx { Center = center, Radius = look.LiftRadius * 1.3f, Height = look.LiftHeight, Progress = 1f, Fling = true });
            var near = isLocal || Vector3.Distance(camPos, center) < 70f;
            if (near)
            {
                CombatFx.Shake = Mathf.Max(CombatFx.Shake, isLocal ? 1.4f : 0.8f);
                ToLive.UI.CombatHud.Flash(Color.Lerp(Color.white, color / Mathf.Max(1f, color.maxColorComponent), 0.35f), isLocal ? 0.45f : 0.3f);
            }
            if (v.Crater != null) { v.Crater.transform.localScale *= 1.6f; }
            CombatFx.RaiseEruption(center);
        }

        void BlowGlass(Vector3 center, Color color, float range, int rays, PhysicsWorldSingleton physics, bool hasPhysics)
        {
            if (!hasPhysics) return;
            for (var i = 0; i < rays; i++)
            {
                var a = i * Mathf.PI * 2f / rays + UnityEngine.Random.Range(-0.2f, 0.2f);
                var dir = new Vector3(Mathf.Cos(a), UnityEngine.Random.Range(-0.05f, 0.35f), Mathf.Sin(a)).normalized;
                var origin = center + Vector3.up * UnityEngine.Random.Range(0f, 8f);
                var input = new RaycastInput { Start = origin, End = origin + dir * range, Filter = CollisionFilter.Default };
                if (physics.CastRay(input, out var hit))
                    GlassBurst.Spawn(hit.Position, hit.SurfaceNormal, color, 35);
            }
        }

        static float GroundY(Vector3 center, PhysicsWorldSingleton physics, bool hasPhysics)
        {
            if (!hasPhysics) return 0f;
            var input = new RaycastInput { Start = center, End = center + Vector3.down * 60f, Filter = CollisionFilter.Default };
            return physics.CastRay(input, out var hit) ? hit.Position.y : center.y - 60f;
        }

        void UpdateCrater(Visual v, Vector3 center, float radius, PhysicsWorldSingleton physics, bool hasPhysics)
        {
            var ground = GroundY(center, physics, hasPhysics);
            if (center.y - ground > 25f) return; // too high above the ground for a crater
            if (v.Crater == null)
            {
                if (s_CraterMat == null)
                {
                    s_CraterMat = FxMaterials.Lit();
                    s_CraterMat.SetColor("_BaseColor", new Color(0.08f, 0.08f, 0.09f));
                    s_CraterMat.SetFloat("_Smoothness", 0f);
                }
                v.Crater = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(v.Crater.GetComponent<Collider>());
                v.Crater.name = "Crater";
                v.Crater.GetComponent<MeshRenderer>().sharedMaterial = s_CraterMat;
            }
            v.Crater.transform.position = new Vector3(center.x, ground + 0.02f, center.z);
            v.Crater.transform.localScale = new Vector3(radius * 2f, 0.02f, radius * 2f);
        }

        void UpdateBeam(Visual v, float3 position, FlightState f, MoveState m, Color color, PhysicsWorldSingleton physics, bool hasPhysics)
        {
            if (s_BeamMat == null) s_BeamMat = FxMaterials.Unlit();
            if (s_GlowMat == null) s_GlowMat = FxMaterials.Unlit();
            var origin = (Vector3)position + Vector3.up * 0.6f;
            var dir = (Vector3)math.mul(quaternion.EulerZXY(math.radians(new float3(f.Pitch, f.Yaw, 0f))), new float3(0, 0, 1));
            var hdr = color * 3f;

            // charging: a growing glow at the chest
            if (m.Beam == 1)
            {
                if (v.ChargeGlow == null) v.ChargeGlow = MakeGlow("BeamCharge");
                v.ChargeGlow.gameObject.SetActive(true);
                v.ChargeGlow.position = origin + dir * 0.8f;
                v.ChargeGlow.localScale = Vector3.one * Mathf.Lerp(v.ChargeGlow.localScale.x, 0.9f, Time.deltaTime * 6f);
                SetGlowColor(v.ChargeGlow, hdr * 1.5f);
            }
            else if (v.ChargeGlow != null) { v.ChargeGlow.gameObject.SetActive(false); v.ChargeGlow.localScale = Vector3.one * 0.1f; }

            if (m.Beam == 2)
            {
                if (v.Beam == null)
                {
                    var go = new GameObject("Beam");
                    v.Beam = go.AddComponent<LineRenderer>();
                    v.Beam.material = s_BeamMat;
                    v.Beam.positionCount = 2;
                    v.Beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    v.BeamGlow = MakeGlow("BeamImpact");
                }
                var length = 90f;
                if (hasPhysics)
                {
                    var input = new RaycastInput { Start = origin + dir * 1.5f, End = origin + dir * 90f, Filter = CollisionFilter.Default };
                    if (physics.CastRay(input, out var hit)) length = 1.5f + hit.Fraction * 88.5f;
                }
                v.Beam.gameObject.SetActive(true);
                v.Beam.material.color = hdr * (1f + 0.25f * Mathf.Sin(Time.time * 40f));
                v.Beam.widthMultiplier = 0.9f + 0.15f * Mathf.Sin(Time.time * 55f);
                v.Beam.SetPosition(0, origin + dir * 0.6f);
                v.Beam.SetPosition(1, origin + dir * length);
                v.BeamGlow.gameObject.SetActive(true);
                v.BeamGlow.position = origin + dir * length;
                v.BeamGlow.localScale = Vector3.one * (2.2f + 0.4f * Mathf.Sin(Time.time * 30f));
                SetGlowColor(v.BeamGlow, hdr * 2f);
            }
            else if (v.Beam != null)
            {
                v.Beam.gameObject.SetActive(false);
                v.BeamGlow.gameObject.SetActive(false);
            }
        }

        static Transform MakeGlow(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.material = new Material(s_GlowMat);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.localScale = Vector3.one * 0.1f;
            return go.transform;
        }

        static void SetGlowColor(Transform t, Color c) => t.GetComponent<MeshRenderer>().material.color = c;

        void Remove(Visual v)
        {
            v.Halo.Destroy();
            if (v.Crater != null) Destroy(v.Crater);
            if (v.Beam != null) Destroy(v.Beam.gameObject);
            if (v.BeamGlow != null) Destroy(v.BeamGlow.gameObject);
            if (v.ChargeGlow != null) Destroy(v.ChargeGlow.gameObject);
        }

        void ClearAll()
        {
            foreach (var v in m_Visuals.Values) Remove(v);
            m_Visuals.Clear();
            m_World = null;
            FlightCameraRig.RitualCam = false;
            if (PowerVolume != null) PowerVolume.weight = 0f;
        }

        void OnDestroy() => ClearAll();
    }
}
