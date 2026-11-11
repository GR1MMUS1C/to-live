using Unity.Entities;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>The replicated heat / rival state ghost (one per district, spawned by the server).</summary>
    [DisallowMultipleComponent]
    public class OppositionStateAuthoring : MonoBehaviour
    {
        class Baker : Baker<OppositionStateAuthoring>
        {
            public override void Bake(OppositionStateAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new HeatState { RivalDifficulty = 1 });
            }
        }
    }
}
