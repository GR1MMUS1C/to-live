using Unity.Entities;
using UnityEngine;

namespace ToLive.District
{
    /// <summary>A roof crown / street lamp that glows in the colour of whoever holds the district.</summary>
    [DisallowMultipleComponent]
    public class DistrictAccentAuthoring : MonoBehaviour
    {
        [Range(0f, 2f)] public float Strength = 1f;

        class Baker : Baker<DistrictAccentAuthoring>
        {
            public override void Bake(DistrictAccentAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.Renderable), new DistrictAccent { Strength = a.Strength });
            }
        }
    }
}
