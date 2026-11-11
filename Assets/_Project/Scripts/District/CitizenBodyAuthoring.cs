using Unity.Entities;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>A dead citizen's body prefab (static-optimised ghost: sent once, then costs nothing).</summary>
    [DisallowMultipleComponent]
    public class CitizenBodyAuthoring : MonoBehaviour
    {
        class Baker : Baker<CitizenBodyAuthoring>
        {
            public override void Bake(CitizenBodyAuthoring a)
            {
                AddComponent<CitizenBody>(GetEntity(TransformUsageFlags.Dynamic));
            }
        }
    }
}
