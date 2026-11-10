using Unity.Entities;
using UnityEngine;

namespace ToLive.Destruction
{
    /// <summary>
    /// Marker on the replicated "Building State" ghost prefab (instanced once in the District
    /// subscene as a pre-spawned ghost). Bakes the <see cref="BuildingState"/> component.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingStateAuthoring : MonoBehaviour
    {
        class Baker : Baker<BuildingStateAuthoring>
        {
            public override void Bake(BuildingStateAuthoring authoring)
            {
                AddComponent<BuildingState>(GetEntity(TransformUsageFlags.None));
            }
        }
    }
}
