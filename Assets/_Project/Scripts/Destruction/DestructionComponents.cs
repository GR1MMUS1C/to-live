using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace ToLive.Destruction
{
    /// <summary>
    /// One structural (layer 1) chunk of the breakable building. It's a static collider in the
    /// main (predicted) physics world while intact.
    /// </summary>
    public struct BuildingChunk : IComponentData
    {
        public int Index;
    }

    /// <summary>Layer 2 sub-piece of a chunk, relative to the chunk's centre (spawned as debris).</summary>
    public struct ChunkPiece : IBufferElementData
    {
        public float3 LocalCenter;
        public float3 Size;
    }

    /// <summary>Added at runtime when a chunk is hidden: keeps its collider so "rebuild" can restore it.</summary>
    public struct ChunkHidden : IComponentData
    {
        public BlobAssetReference<Collider> SavedCollider;
    }

    /// <summary>
    /// The replicated break state of the building: a 128-bit "broken" mask plus a generation counter
    /// (bumped by "rebuild"). This lives on a pre-spawned ghost in the District subscene, so it's
    /// compact (delta-compressed, ~0 bytes when nothing changes) and a late joiner receives the
    /// current mask like any other ghost state. Unreal analogy: a replicated bitfield on a level actor.
    /// </summary>
    public struct BuildingState : IComponentData
    {
        public const int MaxChunks = 128;
        [GhostField] public uint Broken0;
        [GhostField] public uint Broken1;
        [GhostField] public uint Broken2;
        [GhostField] public uint Broken3;
        [GhostField] public uint Generation;
        [GhostField] public uint LastImpactTick;
        [GhostField(Quantization = 10)] public float3 LastImpactVelocity;

        public bool IsBroken(int i) => (Get(i >> 5) & (1u << (i & 31))) != 0;

        public void SetBroken(int i) => Set(i >> 5, Get(i >> 5) | (1u << (i & 31)));

        public int CountBroken() => math.countbits(Broken0) + math.countbits(Broken1) + math.countbits(Broken2) + math.countbits(Broken3);

        public uint Get(int word) => word switch { 0 => Broken0, 1 => Broken1, 2 => Broken2, _ => Broken3 };

        public void Set(int word, uint value)
        {
            switch (word)
            {
                case 0: Broken0 = value; break;
                case 1: Broken1 = value; break;
                case 2: Broken2 = value; break;
                default: Broken3 = value; break;
            }
        }
    }

    /// <summary>Static description of the building for structural support (baked, not networked).</summary>
    public struct BuildingGraph : IComponentData
    {
        public int ChunkCount;
        public float3 Center;
    }

    public struct ChunkNode : IBufferElementData
    {
        public float3 Center;
        public float3 Size;
        public byte Grounded;
        public int NeighborStart;
        public int NeighborCount;
    }

    public struct ChunkNeighbor : IBufferElementData
    {
        public int Index;
    }

    /// <summary>Server-only queue of chunks hit this tick (filled by the flight/impact code).</summary>
    [InternalBufferCapacity(8)]
    public struct BreakRequest : IBufferElementData
    {
        public int Chunk;
        public float3 Velocity;
        public Entity Instigator; // who gets surge fuel for this break (may be Entity.Null)
    }

    /// <summary>Server-only pending debug actions for the building.</summary>
    public struct BuildingCommand : IComponentData
    {
        public byte Rebuild;
        public byte DestroyAll;
    }

    /// <summary>What this world has already applied (to detect changes in the replicated mask).</summary>
    public struct BuildingApplied : IComponentData
    {
        public uint A0, A1, A2, A3;
        public uint Generation;
        public byte Initialized;

        public bool IsApplied(int i) => (Get(i >> 5) & (1u << (i & 31))) != 0;
        uint Get(int w) => w switch { 0 => A0, 1 => A1, 2 => A2, _ => A3 };
    }

    /// <summary>Client-only: chunks that just broke and need debris spawned.</summary>
    [InternalBufferCapacity(0)]
    public struct DebrisSpawnRequest : IBufferElementData
    {
        public int Chunk;
    }

    /// <summary>Debris settings (live cap etc.) and the debris render prefab.</summary>
    public struct DebrisConfig : IComponentData
    {
        public Entity Prefab;
        public int LiveDebrisCap;
        public int MaxTotalDebris;
        public float SleepSpeed;
        public float SleepTime;
        public float ImpactTransfer;
        public float Scatter;
        public float Density;
        public int MaxChunksPerFrame;
    }
}
