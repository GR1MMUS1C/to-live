using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace ToLive.Destruction
{
    /// <summary>
    /// One layer-1 structural chunk. The GameObject also has a BoxCollider (baked into a static
    /// Unity Physics collider) and a MeshRenderer. <see cref="Pieces"/> are its layer-2 sub-pieces,
    /// spawned as cosmetic debris when it breaks.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkAuthoring : MonoBehaviour
    {
        public int Index;
        public List<Vector3> PieceCenters = new List<Vector3>(); // relative to the chunk centre (world axes)
        public List<Vector3> PieceSizes = new List<Vector3>();

        class Baker : Baker<ChunkAuthoring>
        {
            public override void Bake(ChunkAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new BuildingChunk { Index = authoring.Index });
                var pieces = AddBuffer<ChunkPiece>(entity);
                for (var i = 0; i < authoring.PieceCenters.Count && i < authoring.PieceSizes.Count; i++)
                    pieces.Add(new ChunkPiece { LocalCenter = authoring.PieceCenters[i], Size = authoring.PieceSizes[i] });
            }
        }
    }
}
