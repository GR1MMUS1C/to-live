using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Crisis
{
    public static class CrisisKind
    {
        public const byte None = 0;
        public const byte Disaster = 1;   // the failing tower: only a god can hold it up
        public const byte Attack = 2;     // the army (or the rival) attacks a crowd
        public const byte Uprising = 3;   // citizens rise against a feared god

        public static string Name(byte k) => k switch { Disaster => "DISASTER: THE TOWER IS FAILING", Attack => "ATTACK ON THE CROWD", Uprising => "UPRISING", _ => "" };
    }

    public static class CrisisPhase
    {
        public const byte None = 0;
        public const byte Active = 1;
        public const byte Resolved = 2;   // outcome shown for a few seconds
    }

    public static class CrisisOutcome
    {
        public const byte None = 0;
        public const byte Protected = 1;  // ended by protecting people
        public const byte Dominated = 2;  // ended by force, with collateral
        public const byte Failed = 3;     // ignored / lost

        public static string Name(byte o) => o switch { Protected => "PROTECTED", Dominated => "ENDED BY FORCE", Failed => "FAILED", _ => "" };
    }

    /// <summary>
    /// The live crisis, on the district ghost (milestone 4B). Compact and replicated, so a late joiner
    /// sees the same crisis, marker, timer, progress and outcome. Like a replicated "objective" actor.
    /// </summary>
    public struct CrisisState : IComponentData
    {
        [GhostField] public byte Kind;
        [GhostField] public byte Phase;
        [GhostField] public byte Outcome;
        [GhostField] public uint Seq;                              // increments with each new crisis
        [GhostField(Quantization = 10)] public float3 Position;    // marker
        [GhostField(Quantization = 10)] public float Radius;
        [GhostField] public uint StartTick, EndTick;               // timer
        [GhostField] public uint ResolvedTick;
        [GhostField] public Entity Target;                         // uprising: the feared god; attack: the attacking rival (if any)
        [GhostField] public Entity Resolver;                       // who ended it
        [GhostField] public ushort Saved, Lost;                    // disaster: chunks held up / fallen; attack: attackers left / people killed
        [GhostField] public ushort Goal;                           // what "done" means (chunks to hold, attackers, stand-down seconds...)
        [GhostField] public ushort Progress;                       // toward Goal
        [GhostField] public short WarnChunk;                       // disaster: chunk about to fall (-1 none)
        [GhostField] public uint WarnTick;                         // ... when it falls
        [GhostField] public byte RivalApproach;                    // 0 none, 1 protect, 2 force (VANTA takes the opposite of the leading player)
    }

    /// <summary>Who holds the district, and how (milestone 4B ownership). On the district ghost.</summary>
    public struct DistrictHold : IComponentData
    {
        [GhostField] public Entity Holder;
        [GhostField] public byte Mode;            // 0 nobody, 1 trust, 2 dominance
        [GhostField] public byte FortifyLevel;    // dominance: towers raised so far
        [GhostField] public byte Rebuilt;         // trust: chunks rebuilt (wraps; HUD only)
        [GhostField] public uint TowerShotTick;   // a tower fired (client draws the tracer)
        [GhostField] public byte TowerShotIndex;
        [GhostField] public Entity TowerShotTarget;

        public const byte None = 0, Trust = 1, Dominance = 2;
    }

    /// <summary>Baked fortification spot (a grey-box tower raised when the district is dominated).</summary>
    public struct Fortification : IComponentData
    {
        public int Index;
        public float3 Top;   // muzzle / beacon position
    }

    /// <summary>Server-only director memory.</summary>
    public struct CrisisDirector : IComponentData
    {
        public uint NextCrisisTick;
        public uint NextCrackTick;
        public uint StandDownSinceTick;   // uprising: when the target started standing down (0 = not)
        public uint NextRebuildTick, NextFortifyTick, NextReturnTick, NextTowerShotTick, NextHoldTick;
        public uint Seed;
        public byte PendingStart;          // debug: start this crisis kind now
        public byte PendingOutcome;        // debug: force this outcome
        public uint PendingHolderCheck;    // re-check who holds the district at this tick (after a crisis or a defeat)
        public float CrisisFuelBase;
    }

    /// <summary>
    /// Server-only, per god: what they did during the current crisis (deaths caused, chunks broken,
    /// attackers destroyed, chunks held). Reset when a crisis starts. Fed by the DistrictSource producers.
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct CrisisTally : IBufferElementData
    {
        public Entity God;
        public int Deaths, Chunks, UnitsDestroyed, Held, RioterKills;
    }

    /// <summary>Server-only: a unit spawned by an Attack crisis (it shoots at the crowd until stopped).</summary>
    public struct CrowdAttacker : IComponentData
    {
        public float3 Point;
    }

    /// <summary>
    /// Server-only per god: defeat bookkeeping ("drained and expelled" replaces knock-out + respawn).
    /// Lives on the player prefab (players and the rival).
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct DefeatState : IComponentData
    {
        public uint HandledKoTick;   // the knock-out (ActionStartTick) already drained
        public byte Pending;         // drained, waiting for the respawn at the district edge
    }

    public static class CrisisTallies
    {
        /// <summary>Record something a god did during a live crisis (server). Safe to call any time.</summary>
        public static void Record(EntityManager em, Entity god, int deaths = 0, int chunks = 0, int units = 0, int held = 0, int rioters = 0)
        {
            if (god == Entity.Null) return;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrisisDirector>());
            if (q.CalculateEntityCount() != 1) return;
            var buf = em.GetBuffer<CrisisTally>(q.GetSingletonEntity());
            for (var i = 0; i < buf.Length; i++)
            {
                if (buf[i].God != god) continue;
                var t = buf[i];
                t.Deaths += deaths; t.Chunks += chunks; t.UnitsDestroyed += units; t.Held += held; t.RioterKills += rioters;
                buf[i] = t;
                return;
            }
            buf.Add(new CrisisTally { God = god, Deaths = deaths, Chunks = chunks, UnitsDestroyed = units, Held = held, RioterKills = rioters });
        }
    }
}
