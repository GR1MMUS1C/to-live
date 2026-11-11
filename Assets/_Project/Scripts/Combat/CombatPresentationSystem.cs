using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>
    /// Client-only hit feedback: detects new hits from the replicated LastHitTick (flash, ring,
    /// camera shake) and draws knockback trails behind launched bodies. Purely cosmetic.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CombatPresentationSystem : SystemBase
    {
        readonly Dictionary<Entity, TrailRenderer> m_Trails = new Dictionary<Entity, TrailRenderer>();
        static Material s_TrailMaterial;

        protected override void OnDestroy()
        {
            foreach (var t in m_Trails.Values)
                if (t != null) Object.Destroy(t.gameObject);
            m_Trails.Clear();
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            var now = SystemAPI.Time.ElapsedTime;
            var seen = new NativeHashSet<Entity>(8, Allocator.Temp);

            foreach (var (combat, fx, ltw, entity) in SystemAPI.Query<RefRO<CombatState>, RefRW<HitFxState>, RefRO<LocalToWorld>>().WithEntityAccess())
            {
                var c = combat.ValueRO;
                if (c.LastHitTick != 0 && c.LastHitTick != fx.ValueRO.SeenHitTick)
                {
                    fx.ValueRW.SeenHitTick = c.LastHitTick;
                        fx.ValueRW.FlashUntil = now + 0.09;
                        var local = SystemAPI.HasComponent<GhostOwnerIsLocal>(entity) && SystemAPI.IsComponentEnabled<GhostOwnerIsLocal>(entity);
                        CombatFx.RaiseVictimHit(ltw.ValueRO.Position, c.LastHitStrength, local);
                }

                // Knockback trail while launched.
                var launched = c.Action == CombatAction.Launched && c.Ballistic != 2 || c.Action == CombatAction.KnockedOut;
                if (launched)
                {
                    seen.Add(entity);
                    if (!m_Trails.TryGetValue(entity, out var trail) || trail == null)
                    {
                        trail = CreateTrail();
                        m_Trails[entity] = trail;
                    }
                    trail.emitting = true;
                    trail.transform.position = ltw.ValueRO.Position;
                }
            }

            // Stop trails for bodies no longer launched (let them fade), clean up old ones.
            var stale = new List<Entity>();
            foreach (var kv in m_Trails)
            {
                if (kv.Value == null) { stale.Add(kv.Key); continue; }
                if (seen.Contains(kv.Key)) continue;
                kv.Value.emitting = false;
                if (!EntityManager.Exists(kv.Key) || kv.Value.positionCount == 0) stale.Add(kv.Key);
            }
            foreach (var e in stale)
            {
                if (m_Trails[e] != null) Object.Destroy(m_Trails[e].gameObject);
                m_Trails.Remove(e);
            }
        }

        static TrailRenderer CreateTrail()
        {
            if (s_TrailMaterial == null)
                { s_TrailMaterial = FxMaterials.Unlit(); s_TrailMaterial.color = new Color(1f, 0.95f, 0.8f); }
            var go = new GameObject("KnockbackTrail");
            var t = go.AddComponent<TrailRenderer>();
            t.sharedMaterial = s_TrailMaterial;
            t.time = 0.35f;
            t.widthCurve = AnimationCurve.Linear(0, 0.9f, 1, 0f);
            t.minVertexDistance = 0.5f;
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
        }
    }
}
