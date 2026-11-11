using Unity.Entities;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>Gunship rocket prefab: an interpolated ghost the server moves and detonates.</summary>
    [DisallowMultipleComponent]
    public class RocketAuthoring : MonoBehaviour
    {
        class Baker : Baker<RocketAuthoring>
        {
            public override void Bake(RocketAuthoring a)
            {
                AddComponent<Rocket>(GetEntity(TransformUsageFlags.Dynamic));
            }
        }
    }
}
