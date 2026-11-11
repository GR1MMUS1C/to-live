using Unity.Entities;
using UnityEngine;

namespace ToLive.Destruction
{
    /// <summary>
    /// Cosmetic debris settings (client only). Lives on "Debris Config" in the District subscene.
    /// Debris never affects gameplay: it is simulated per client in a separate physics world.
    /// </summary>
    [DisallowMultipleComponent]
    public class DebrisConfigAuthoring : MonoBehaviour
    {
        [Tooltip("Render prefab for a debris piece (a unit cube, no collider).")]
        public GameObject DebrisPrefab;
        [Tooltip("Max debris pieces actively simulated. Extra pieces are frozen (oldest first).")]
        public int LiveDebrisCap = 1000;
        [Tooltip("Max debris pieces kept at all (simulated + settled). Oldest are removed.")]
        public int MaxTotalDebris = 3000;
        [Tooltip("Below this speed (m/s) a piece counts as resting.")] public float SleepSpeed = 0.4f;
        [Tooltip("Seconds of resting before a piece stops simulating.")] public float SleepTime = 0.75f;
        [Tooltip("How much of the impactor's velocity the debris inherits (0..1).")] public float ImpactTransfer = 0.35f;
        [Tooltip("Random outward scatter speed (m/s).")] public float Scatter = 6f;
        [Tooltip("Density for debris mass (kg per m³).")] public float Density = 600f;
        [Tooltip("Broken chunks turned into debris per frame (spreads the spawn cost of a big collapse).")] public int MaxChunksPerFrame = 12;

        class Baker : Baker<DebrisConfigAuthoring>
        {
            public override void Bake(DebrisConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new DebrisConfig
                {
                    Prefab = a.DebrisPrefab != null ? GetEntity(a.DebrisPrefab, TransformUsageFlags.Dynamic) : Entity.Null,
                    LiveDebrisCap = a.LiveDebrisCap, MaxTotalDebris = a.MaxTotalDebris,
                    SleepSpeed = a.SleepSpeed, SleepTime = a.SleepTime,
                    ImpactTransfer = a.ImpactTransfer, Scatter = a.Scatter, Density = a.Density,
                    MaxChunksPerFrame = a.MaxChunksPerFrame,
                });
            }
        }
    }
}
