using Unity.Entities;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>The replicated district ghost prefab (spawned once by the server).</summary>
    [DisallowMultipleComponent]
    public class DistrictStateAuthoring : MonoBehaviour
    {
        class Baker : Baker<DistrictStateAuthoring>
        {
            public override void Bake(DistrictStateAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent<DistrictState>(e);
                AddBuffer<GodStanding>(e);
                AddBuffer<CitizenAliveBits>(e);
                AddBuffer<CitizenMoodBits>(e);
                AddBuffer<CauseEntry>(e);
            }
        }
    }
}
