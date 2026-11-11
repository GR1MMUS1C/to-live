using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.District
{
    /// <summary>What a citizen is doing (2 bits per citizen on the wire).</summary>
    public static class Mood
    {
        public const byte Wander = 0;
        public const byte Gather = 1;   // near a god they trust: gather and cheer
        public const byte Flee = 2;     // violence, eruptions, army fire, a god they fear
        public const byte Cower = 3;    // too close to run: crouch in place

        public static string Name(byte m) => m switch { Gather => "gather", Flee => "flee", Cower => "cower", _ => "wander" };
    }

    /// <summary>Why a god's standing moved (the HUD cause feed).</summary>
    public static class Cause
    {
        public const byte ProtectedCrowd = 1;   // +trust: stayed near people without harming them
        public const byte UnitDestroyed = 2;    // +trust: destroyed an army unit that threatened people
        public const byte RivalDefeated = 3;    // +trust: knocked out the rival while people watched
        public const byte CiviliansKilled = 4;  // +fear -trust: civilian deaths you caused
        public const byte Destruction = 5;      // +fear: buildings broken
        public const byte EruptionNearCrowd = 6;// +fear -trust
        public const byte RecklessPass = 7;     // -trust +fear: supersonic over a crowd
        public const byte Debug = 8;

        public static string Text(byte kind, int value) => kind switch
        {
            ProtectedCrowd => "+trust: protected the crowd",
            UnitDestroyed => "+trust: destroyed an army unit",
            RivalDefeated => "+trust: defeated the rival in front of the crowd",
            CiviliansKilled => value == 1 ? "+fear: 1 civilian killed" : $"+fear: {value} civilians killed",
            Destruction => value == 1 ? "+fear: destruction" : $"+fear: destruction ({value} chunks)",
            EruptionNearCrowd => "+fear: eruption near a crowd",
            RecklessPass => "-trust: reckless supersonic pass over people",
            Debug => "debug",
            _ => "",
        };
    }

    /// <summary>
    /// THE DISTRICT: one replicated ghost per district. Who holds it and how (trust vs dominance),
    /// the population, every citizen's alive bit and mood (2 bits), recent violence points (so client
    /// crowds flee the right way) and the cause feed. Like a replicated GameState actor for one area.
    /// Milestone 4B hooks: crises, rebuilding/scarring and contesting read and write this state.
    /// </summary>
    public struct DistrictState : IComponentData
    {
        [GhostField(Quantization = 1000)] public float Meter;   // -1 trust .. +1 dominance (who holds it, and how)
        [GhostField] public Entity Leader;                      // the god with the largest standing (Entity.Null = nobody)
        [GhostField(Quantization = 1000)] public float LeaderAlignment; // -1 trust .. +1 dominance of the leader's own standing
        [GhostField(Quantization = 1000)] public float Hold;    // 0..1 how strongly the leader holds the district
        [GhostField] public ushort Population;                  // citizens spawned
        [GhostField] public ushort Alive;
        [GhostField(Quantization = 10)] public float3 Threat0, Threat1, Threat2, Threat3;
        [GhostField] public uint ThreatTick0, ThreatTick1, ThreatTick2, ThreatTick3;
        [GhostField] public uint CauseSeq;                      // increments with each cause entry
    }

    /// <summary>Trust and fear one god has earned in this district (replicated buffer on the district ghost).</summary>
    [InternalBufferCapacity(4)]
    public struct GodStanding : IBufferElementData
    {
        [GhostField] public Entity God;
        [GhostField(Quantization = 10)] public float Trust;
        [GhostField(Quantization = 10)] public float Fear;
    }

    /// <summary>32 citizens' alive flags per element (bit i = citizen 32*index + i).</summary>
    [InternalBufferCapacity(0)]
    public struct CitizenAliveBits : IBufferElementData
    {
        [GhostField] public uint Bits;
    }

    /// <summary>16 citizens' moods per element (2 bits each, see <see cref="Mood"/>).</summary>
    [InternalBufferCapacity(0)]
    public struct CitizenMoodBits : IBufferElementData
    {
        [GhostField] public uint Bits;
    }

    /// <summary>Recent standing changes, newest last (ring of 8) for the HUD cause feed.</summary>
    [InternalBufferCapacity(8)]
    public struct CauseEntry : IBufferElementData
    {
        [GhostField] public uint Seq;
        [GhostField] public Entity God;
        [GhostField] public byte Kind;
        [GhostField] public short Value;
    }

    /// <summary>A dead citizen: a static ghost that stays where they fell (sent once, then free).</summary>
    public struct CitizenBody : IComponentData
    {
        [GhostField] public ushort Index;
    }

    /// <summary>Baked street positions where citizens live (identical in every world, no bandwidth).</summary>
    [InternalBufferCapacity(0)]
    public struct CitizenHome : IBufferElementData
    {
        public float3 Position;
        public float3 Street;   // unit vector along the street (citizens walk along it)
    }

    /// <summary>Tag on the singleton that holds the baked <see cref="CitizenHome"/> list.</summary>
    public struct CrowdLayout : IComponentData { }

    /// <summary>A light strip / lamp that takes the district's colour (client cosmetics).</summary>
    public struct DistrictAccent : IComponentData
    {
        public float Strength;  // 1 = roof crowns, lower for subtler accents
    }

    // ------------------------------------------------------------------ server only

    /// <summary>Server-side state of one citizen (a buffer element on a server singleton, not an entity).</summary>
    public struct CitizenSim : IBufferElementData
    {
        public float3 Position;
        public float3 Target;
        public byte Alive;
        public byte Mood;
        public uint MoodUntilTick;
        public uint NextThinkTick;
        public float3 FleeFrom;
        public Entity Focus;        // the god they gather around
    }

    /// <summary>Tag on the server singleton holding <see cref="CitizenSim"/>, <see cref="CrowdHazard"/> and <see cref="DistrictEvent"/> queues.</summary>
    public struct CrowdServer : IComponentData
    {
        public uint NextFuelTick;
        public uint NextFeedTick;
        public uint Seed;
        public int PendingReset;      // debug / benchmark: respawn the crowd with this many citizens
    }

    public static class HazardKind
    {
        public const byte Scare = 0;     // violence nearby: people flee (no deaths)
        public const byte Kill = 1;      // kills inside Radius, scares inside ScareRadius
        public const byte Segment = 2;   // a beam from Position to End
    }

    /// <summary>
    /// THE CROWD HAZARD QUEUE (server): anything that can kill or scare citizens appends one of these.
    /// Responsible is the god (or unit) to blame, using the same rules as <c>HeatSource.Responsible</c>.
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct CrowdHazard : IBufferElementData
    {
        public byte Kind;
        public float3 Position, End;
        public float Radius, ScareRadius;
        public Entity Responsible;
        public byte Source;           // a Cause for the fear feed (Destruction, EruptionNearCrowd, ...)
    }

    /// <summary>
    /// THE DISTRICT SOURCE QUEUE (server): every change to a god's trust or fear goes through here
    /// (see <see cref="DistrictSource"/>), like HeatSource and SurgeFuel.
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct DistrictEvent : IBufferElementData
    {
        public Entity God;
        public float Trust, Fear;
        public byte Cause;
        public int Count;
    }

    /// <summary>
    /// THE DISTRICT SOURCE: the one place trust and fear change. Producers call these (server only).
    /// Milestone 4B adds crises (stopping a disaster, ending an uprising) as new producers here.
    /// </summary>
    public static class DistrictSource
    {
        static bool TryQueue(EntityManager em, out DynamicBuffer<DistrictEvent> queue, out DistrictConfig cfg)
        {
            queue = default; cfg = default;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            using var c = em.CreateEntityQuery(ComponentType.ReadOnly<DistrictConfig>());
            if (q.CalculateEntityCount() != 1 || c.CalculateEntityCount() != 1) return false;
            queue = em.GetBuffer<DistrictEvent>(q.GetSingletonEntity());
            cfg = c.GetSingleton<DistrictConfig>();
            return true;
        }

        static bool IsGod(EntityManager em, Entity e) => ToLive.Opposition.HeatSource.IsGod(em, e);

        public static void Add(EntityManager em, Entity god, float trust, float fear, byte cause, int count = 1)
        {
            if (!IsGod(em, god) || !TryQueue(em, out var q, out _)) return;
            q.Add(new DistrictEvent { God = god, Trust = trust, Fear = fear, Cause = cause, Count = count });
        }

        /// <summary>Civilians killed by whoever is responsible for <paramref name="cause"/>.</summary>
        public static void CiviliansKilled(EntityManager em, Entity responsible, int count)
        {
            var god = ToLive.Opposition.HeatSource.Responsible(em, responsible);
            if (!IsGod(em, god) || !TryQueue(em, out var q, out var cfg)) return;
            q.Add(new DistrictEvent { God = god, Trust = -cfg.TrustLostPerDeath * count, Fear = cfg.FearPerDeath * count, Cause = Cause.CiviliansKilled, Count = count });
            ToLive.Opposition.HeatSource.CiviliansKilled(em, god, count);
        }

        public static void Destruction(EntityManager em, Entity responsible, int chunks)
        {
            var god = ToLive.Opposition.HeatSource.Responsible(em, responsible);
            if (!IsGod(em, god) || !TryQueue(em, out var q, out var cfg)) return;
            q.Add(new DistrictEvent { God = god, Trust = -cfg.TrustLostPerChunk * chunks, Fear = cfg.FearPerChunk * chunks, Cause = Cause.Destruction, Count = chunks });
        }
    }

    /// <summary>Helpers to queue crowd hazards (server only).</summary>
    public static class CrowdHazards
    {
        static bool TryQueue(EntityManager em, out DynamicBuffer<CrowdHazard> queue)
        {
            queue = default;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            if (q.CalculateEntityCount() != 1) return false;
            queue = em.GetBuffer<CrowdHazard>(q.GetSingletonEntity());
            return true;
        }

        public static void Kill(EntityManager em, float3 pos, float radius, float scareRadius, Entity responsible, byte source)
        {
            if (TryQueue(em, out var q)) q.Add(new CrowdHazard { Kind = HazardKind.Kill, Position = pos, Radius = radius, ScareRadius = scareRadius, Responsible = responsible, Source = source });
        }

        public static void Beam(EntityManager em, float3 from, float3 to, float radius, Entity responsible)
        {
            if (TryQueue(em, out var q)) q.Add(new CrowdHazard { Kind = HazardKind.Segment, Position = from, End = to, Radius = radius, ScareRadius = radius + 15f, Responsible = responsible, Source = Cause.Destruction });
        }

        public static void Scare(EntityManager em, float3 pos, float scareRadius)
        {
            if (TryQueue(em, out var q)) q.Add(new CrowdHazard { Kind = HazardKind.Scare, Position = pos, ScareRadius = scareRadius });
        }
    }
}
