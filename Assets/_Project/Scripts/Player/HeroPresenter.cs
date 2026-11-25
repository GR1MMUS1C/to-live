using System.Collections.Generic;
using ToLive.Combat;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>
    /// CLIENT-ONLY: shows the rigged hero model on every human player instead of the grey-box
    /// capsule. One GameObject per player follows the entity's visual pivot (so the flight lean
    /// still applies) and an Animator blends walk/run on ground speed. Nothing here affects gameplay.
    /// Unreal analogy: a cosmetic SkeletalMeshComponent + AnimBP driven by replicated movement.
    /// The training dummy and the AI rival keep their capsules for now.
    /// </summary>
    public class HeroPresenter : MonoBehaviour
    {
        [Tooltip("Hero model prefab (Animator + SkinnedMeshRenderer), built by Tools > To Live > Build Hero Look.")]
        public GameObject HeroPrefab;
        [Tooltip("Dominance-form model, worn at high dominance (see PowerLook). Empty = always the base model.")]
        public GameObject DominancePrefab;

        [Header("Placement")]
        [Tooltip("Feet height relative to the visual pivot: the old 2 m capsule's bottom.")]
        public float FootOffset = -1f;
        [Tooltip("Within this many metres of the ground (below the feet) the hero walks/runs; higher up it holds the flying pose.")]
        public float GroundedDistance = 0.4f;

        [Header("Animation")]
        [Tooltip("Animator 'Speed' damping in seconds (like an AnimBP FInterpTo).")]
        public float SpeedDamping = 0.1f;
        [Tooltip("Ground speed (m/s) the run clip matches at normal playback. Faster hover speeds play it faster.")]
        public float RunAnimSpeed = 4.5f;
        [Tooltip("Cap on that playback rate, so very fast hovering doesn't blur the legs.")]
        public float MaxPlaybackRate = 2f;

        [Header("Feedback tints (multiply the texture)")]
        public Color NormalTint = new Color(0.92f, 0.92f, 0.92f, 1f);
        public Color KnockedOutTint = new Color(0.32f, 0.32f, 0.34f, 1f);
        [ColorUsage(false, true)] public Color HitFlashTint = new Color(3f, 3f, 3f, 1f);
        [Tooltip("Seconds of energy-coloured flash when the hero swaps form.")]
        public float FormFlashTime = 0.35f;

        static readonly int k_Speed = Animator.StringToHash("Speed");
        static readonly int k_Grounded = Animator.StringToHash("Grounded");
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        class Visual
        {
            public GameObject Base, Dominance;
            public bool IsDominance;
            public float FlashAge = 99f;
            public Color FlashColor;
            public Entity Body, Pivot;
            public Active Current => IsDominance && Dominance != null ? new Active(Dominance) : new Active(Base);
        }

        readonly struct Active
        {
            public readonly GameObject Go;
            public readonly Animator Animator;
            public readonly Renderer[] Renderers;
            public Active(GameObject go) { Go = go; Animator = go.GetComponent<Animator>(); Renderers = go.GetComponentsInChildren<Renderer>(true); }
        }

        readonly Dictionary<Entity, Visual> m_Visuals = new Dictionary<Entity, Visual>();
        MaterialPropertyBlock m_Block;
        World m_World;
        EntityQuery m_Query, m_PhysicsQuery;

        void LateUpdate()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated || HeroPrefab == null) { ClearAll(); return; }
            if (world != m_World)
            {
                ClearAll();
                m_World = world;
                var em0 = world.EntityManager;
                m_Query = em0.CreateEntityQuery(ComponentType.ReadOnly<PlayerVisual>(), ComponentType.ReadOnly<GhostOwner>(),
                    ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<PowerState>());
                m_PhysicsQuery = em0.CreateEntityQuery(ComponentType.ReadOnly<PhysicsWorldSingleton>());
            }
            m_Block ??= new MaterialPropertyBlock();
            var em = world.EntityManager;
            m_Query.CompleteDependency();
            var look = PowerLook.Instance;
            var hasPhysics = m_PhysicsQuery.CalculateEntityCount() == 1;
            var physics = hasPhysics ? m_PhysicsQuery.GetSingleton<PhysicsWorldSingleton>() : default;
            var now = world.Time.ElapsedTime;
            var dt = Time.deltaTime;

            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<Entity>();
            foreach (var e in entities)
            {
                if (em.HasComponent<Opposition.CharacterKind>(e) && em.GetComponentData<Opposition.CharacterKind>(e).Kind == Opposition.CharacterKind.Rival)
                    continue; // the rival stays a capsule for now
                var pv = em.GetComponentData<PlayerVisual>(e);
                if (!em.HasComponent<LocalToWorld>(pv.Pivot)) continue;
                seen.Add(e);

                if (!m_Visuals.TryGetValue(e, out var v))
                {
                    v = new Visual { Base = Instantiate(HeroPrefab), Body = pv.Body, Pivot = pv.Pivot };
                    v.Base.name = $"Hero_{e.Index}";
                    if (DominancePrefab != null)
                    {
                        v.Dominance = Instantiate(DominancePrefab);
                        v.Dominance.name = $"HeroDominance_{e.Index}";
                        v.Dominance.SetActive(false);
                    }
                    m_Visuals[e] = v;
                    SetCapsuleHidden(em, v, true);
                }

                // ---------- form (trust/neutral vs dominance) ----------
                var p = em.GetComponentData<PowerState>(e);
                var threshold = look == null ? 0f : look.DominanceFormAlignment - (v.IsDominance ? look.DominanceFormHysteresis : 0f);
                var wantDominance = look != null && p.Alignment >= threshold
                                    && (p.Tier >= look.DominanceFormMinTier || p.Phase == SurgePhase.Surge);
                if (wantDominance != v.IsDominance)
                {
                    var speed = v.Current.Animator != null ? v.Current.Animator.GetFloat(k_Speed) : 0f;
                    v.IsDominance = wantDominance;
                    v.Base.SetActive(!(wantDominance && v.Dominance != null));
                    if (v.Dominance != null) v.Dominance.SetActive(wantDominance);
                    if (v.Current.Animator != null) v.Current.Animator.SetFloat(k_Speed, speed);
                    // The flash plays even without a dominance model, so the form change still reads.
                    v.FlashAge = 0f;
                    v.FlashColor = look != null ? look.EnergyColor(p.Alignment) : Color.white;
                    var chest = (Vector3)em.GetComponentData<LocalToWorld>(e).Position + Vector3.up * 0.3f;
                    GroundRing.Spawn(chest, v.FlashColor * 3f, 0.3f, 4f, 0.35f, 0.5f, Vector3.up);
                    Debug.Log($"[ToLive] {world.Name} hero {e.Index} form -> {(wantDominance ? "DOMINANCE" : "BASE")}{(v.Dominance == null && wantDominance ? " (no dominance model yet)" : "")}");
                }
                var cur = v.Current;

                // ---------- follow the leaning pivot; feet at the bottom of the old capsule ----------
                var root = em.GetComponentData<LocalToWorld>(e).Position;
                var grounded = IsGrounded(root, physics, hasPhysics, out var groundY);
                var pivot = em.GetComponentData<LocalToWorld>(pv.Pivot);
                var rot = (Quaternion)pivot.Rotation;
                var pos = (Vector3)pivot.Position + rot * new Vector3(0f, FootOffset, 0f);
                if (grounded) pos.y = groundY; // the flight sphere sits lower than the old capsule: keep the feet on the street
                cur.Go.transform.SetPositionAndRotation(pos, rot);

                // ---------- animation: walk/run on the ground, a held pose in the air ----------
                var f = em.GetComponentData<FlightState>(e);
                var groundSpeed = math.length(f.Velocity.xz);
                if (cur.Animator != null)
                {
                    cur.Animator.SetBool(k_Grounded, grounded);
                    cur.Animator.SetFloat(k_Speed, grounded ? groundSpeed : 0f, SpeedDamping, dt);
                    cur.Animator.speed = grounded ? Mathf.Clamp(groundSpeed / Mathf.Max(0.1f, RunAnimSpeed), 1f, MaxPlaybackRate) : 1f;
                }

                // ---------- tint: knocked-out grey, hit flash, form flash ----------
                var tint = NormalTint;
                if (em.HasComponent<CombatState>(e) && em.GetComponentData<CombatState>(e).Action == CombatAction.KnockedOut) tint = KnockedOutTint;
                if (em.HasComponent<HitFxState>(e) && em.GetComponentData<HitFxState>(e).FlashUntil > now) tint = HitFlashTint;
                v.FlashAge += dt;
                if (v.FlashAge < FormFlashTime)
                    tint = Color.Lerp(v.FlashColor * 2f, tint, v.FlashAge / FormFlashTime);
                m_Block.SetColor(k_BaseColor, tint);
                foreach (var r in cur.Renderers) r.SetPropertyBlock(m_Block);
            }

            // players who left (or despawned) lose their hero
            var gone = new List<Entity>();
            foreach (var kv in m_Visuals) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var e in gone) { Remove(em, m_Visuals[e]); m_Visuals.Remove(e); }
        }

        bool IsGrounded(float3 root, PhysicsWorldSingleton physics, bool hasPhysics, out float groundY)
        {
            groundY = 0f;
            if (!hasPhysics) return false;
            var feet = root + new float3(0f, FootOffset, 0f);
            var input = new RaycastInput { Start = root, End = feet - new float3(0f, GroundedDistance, 0f), Filter = CollisionFilter.Default };
            if (!physics.CastRay(input, out var hit)) return false;
            groundY = hit.Position.y;
            return true;
        }

        // Hides the capsule body and the front marker (every child of the pivot) with Entities
        // Graphics' DisableRendering tag, on this client only.
        static void SetCapsuleHidden(EntityManager em, Visual v, bool hidden)
        {
            var targets = new List<Entity> { v.Body };
            if (em.Exists(v.Pivot) && em.HasBuffer<Child>(v.Pivot))
                foreach (var c in em.GetBuffer<Child>(v.Pivot)) targets.Add(c.Value);
            foreach (var t in targets)
            {
                if (!em.Exists(t)) continue;
                if (hidden && !em.HasComponent<DisableRendering>(t)) em.AddComponent<DisableRendering>(t);
                else if (!hidden && em.HasComponent<DisableRendering>(t)) em.RemoveComponent<DisableRendering>(t);
            }
        }

        void Remove(EntityManager em, Visual v)
        {
            if (v.Base != null) Destroy(v.Base);
            if (v.Dominance != null) Destroy(v.Dominance);
            SetCapsuleHidden(em, v, false); // only matters if the entity outlives the hero (presenter disabled)
        }

        void ClearAll()
        {
            var alive = m_World != null && m_World.IsCreated;
            foreach (var v in m_Visuals.Values)
            {
                if (alive) Remove(m_World.EntityManager, v);
                else { if (v.Base != null) Destroy(v.Base); if (v.Dominance != null) Destroy(v.Dominance); }
            }
            m_Visuals.Clear();
            m_World = null;
        }

        void OnDisable() => ClearAll();
    }
}
