using Unity.Entities;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>Benchmark-only citizen ghost prefab (option A in docs/PERF.md).</summary>
    [DisallowMultipleComponent]
    public class CitizenProbeAuthoring : MonoBehaviour
    {
        class Baker : Baker<CitizenProbeAuthoring>
        {
            public override void Bake(CitizenProbeAuthoring a)
            {
                AddComponent<CitizenProbe>(GetEntity(TransformUsageFlags.Dynamic));
            }
        }
    }
}
