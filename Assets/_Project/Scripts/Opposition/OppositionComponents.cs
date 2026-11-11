using ToLive.Combat;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Opposition
{
    public static class UnitKind
    {
        public const byte Police = 1;   // heat 1: light drones, weak tracer fire
        public const byte Gunship = 2;  // heat 2: slow visible rockets, knockback, breaks chunks
        public const byte AntiGod = 3;  // heat 3: one big emplacement-drone, telegraphed beam

        public static string Name(byte k) => k switch { Police => "POLICE DRONE", Gunship => "GUNSHIP", AntiGod => "ANTI-GOD WEAPON", _ => "UNIT" };
    }

    public static class UnitMode
    {
        public const byte Approach = 0;
        public const byte Engage = 1;
        public const byte Retreat = 2;  // heat dropped below this unit's level: leave and despawn
        public const byte Dead = 3;     // knocked out: falling wreck, despawns on landing
    }

    public static class AntiGodPhase
    {
        public const byte Idle = 0;
        public const byte Charging = 1; // aim line + build-up (dodge, break line of sight or rush it)
        public const byte Firing = 2;
        public const byte Cooldown = 3;
    }

    /// <summary>
    /// The district's heat and the rival's status, on one replicated ghost (the "Opposition State",
    /// spawned by the server). Heat 0..3 is a placeholder for milestone 4's dominance.
    /// </summary>
    public struct HeatState : IComponentData
    {
        [GhostField] public byte Level;                       // 0..3
        [GhostField(Quantization = 10)] public float Value;   // raw heat (levels are thresholds on this)
        [GhostField] public uint LastRiseTick;
        [GhostField] public byte Units;                       // response units alive (HUD)
        [GhostField] public Entity Rival;                     // the AI rival's character (Entity.Null if absent)
        [GhostField] public byte RivalMode;                   // 0 auto (1 human = present), 1 forced on, 2 forced off
        [GhostField] public byte RivalDifficulty;             // 0 easy, 1 normal, 2 hard
        [GhostField] public uint RivalRespawnTick;            // rival knocked out: back at this tick (0 = not waiting)
        [GhostField] public byte RivalDowns;                  // increments on each rival knock-out (HUD announcement)
    }

    /// <summary>Server-only data for the heat/spawn director (a plain singleton, not a ghost).</summary>
    public struct HeatDirector : IComponentData
    {
        public uint NextWaveTick;
        public uint Seed;
        public sbyte PendingLevelDelta;  // debug: raise/lower heat by whole levels
        public byte PendingClear;        // debug: remove all units
        public byte PendingSpawnEach;    // debug: spawn one of each type
        public byte PendingRivalToggle;  // debug: rival on/off
        public byte PendingRivalDifficulty;
        public byte PendingBenchUnits;   // benchmark: spawn this many units close by
    }

    /// <summary>
    /// THE HEAT SOURCE (placeholder until milestone 4's dominance). Anything that should raise the
    /// district's heat appends one of these on the server; <c>HeatSystem</c> applies them.
    /// Today: chunks broken, attacks on response units, ritual eruptions. <see cref="God"/> is the
    /// character to blame (the army targets gods that raised heat), Entity.Null for "nobody".
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct HeatEvent : IBufferElementData
    {
        public Entity God;
        public float Amount;
    }

    /// <summary>Tag on the server singleton that holds the <see cref="HeatEvent"/> queue.</summary>
    public struct HeatQueue : IComponentData { }

    /// <summary>Server only, on every god (players and the rival): recent heat this god caused.</summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct HeatBlame : IComponentData
    {
        public float Amount;
        public uint LastTick;
    }

    /// <summary>
    /// One response unit (police drone, gunship, anti-god weapon). The body itself is a normal
    /// character (FlightState + CombatState, Power = health), so it can be struck, launched, beamed,
    /// grabbed and thrown like the dummy. This holds what clients need to draw it.
    /// </summary>
    public struct ResponseUnit : IComponentData
    {
        [GhostField] public byte Kind;
        [GhostField] public byte Mode;
        [GhostField] public Entity Target;
        [GhostField(Quantization = 10)] public float MaxHealth;
        // last shot (cosmetic trigger for tracers and muzzle flashes)
        [GhostField] public uint ShotTick;
        [GhostField(Quantization = 10)] public float3 ShotEnd;
        [GhostField] public byte ShotHit;
        // anti-god beam telegraph
        [GhostField] public byte BeamPhase;
        [GhostField] public uint PhaseEndTick;
        [GhostField(Quantization = 10)] public float3 AimPoint;
    }

    /// <summary>Server-only AI memory of a unit.</summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct UnitBrain : IComponentData
    {
        public uint NextFireTick;
        public uint SpawnTick;
        public uint ModeTick;
        public uint SeenHitTick;
        public float StrafeSign;
        public float3 AimDir;     // anti-god: lagging aim direction
        public uint Seed;
        public float LastPower;   // to measure damage taken (heat from attacking units)
        public byte Pinned;       // debug-spawned: stays even when heat drops
        public float BeamDealt;   // anti-god: damage dealt by the current firing (log)
    }

    /// <summary>
    /// Server-only movement request for an AI body without player input (response units). The flight
    /// system steers toward <see cref="Wish"/> instead of reading a gamepad. Like an AIController's
    /// MoveTo feeding the CharacterMovement component.
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct UnitDrive : IComponentData
    {
        public float3 Wish;     // desired velocity, m/s
        public float Accel;     // m/s²
        public float FaceYaw;   // degrees
    }

    /// <summary>A gunship rocket: a small, slow, visible ghost. The server moves it and decides the hit.</summary>
    public struct Rocket : IComponentData
    {
        [GhostField(Quantization = 100)] public float3 Velocity;
        [GhostField] public Entity Owner;
        public uint SpawnTick;
    }

    /// <summary>0 = human player, 1 = the AI rival (same prefab, same systems).</summary>
    public struct CharacterKind : IComponentData
    {
        [GhostField] public byte Kind;
        public const byte Human = 0;
        public const byte Rival = 1;
    }

    /// <summary>Points at the visual child that turns to aim (gunship pods, anti-god barrel). Client cosmetics.</summary>
    public struct UnitVisual : IComponentData
    {
        public Entity Body;
        public Entity Aim;
    }

    public static class HeatSource
    {
        /// <summary>Queue heat (server only). <paramref name="god"/> may be Entity.Null.</summary>
        public static void Add(EntityManager em, Entity god, float amount)
        {
            if (amount <= 0f) return;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<HeatQueue>());
            if (q.CalculateEntityCount() != 1) return;
            em.GetBuffer<HeatEvent>(q.GetSingletonEntity()).Add(new HeatEvent { God = IsGod(em, god) ? god : Entity.Null, Amount = amount });
        }

        static bool TryConfig(EntityManager em, out OppositionConfig cfg)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<OppositionConfig>());
            cfg = q.CalculateEntityCount() == 1 ? q.GetSingleton<OppositionConfig>() : default;
            return q.CalculateEntityCount() == 1;
        }

        /// <summary>A chunk broke. Only destruction a god is responsible for raises heat (not the army's own rockets).</summary>
        public static void ChunkBroken(EntityManager em, Entity instigator)
        {
            var god = Responsible(em, instigator);
            if (!IsGod(em, god) || !TryConfig(em, out var cfg)) return;
            Add(em, god, cfg.HeatPerChunk);
        }

        /// <summary>
        /// <paramref name="count"/> chunks fell in a structural collapse caused by <paramref name="instigator"/>'s
        /// break. Same responsibility rules as <see cref="ChunkBroken"/>, one queued event, a lower per-chunk rate
        /// so one hit that drops the tower can't jump straight to level 3.
        /// </summary>
        public static void ChunksCollapsed(EntityManager em, Entity instigator, int count)
        {
            if (count <= 0) return;
            var god = Responsible(em, instigator);
            if (!IsGod(em, god) || !TryConfig(em, out var cfg)) return;
            Add(em, god, count * cfg.HeatPerCollapsedChunk);
        }

        /// <summary>A ritual eruption.</summary>
        public static void Eruption(EntityManager em, Entity caster)
        {
            if (!IsGod(em, caster) || !TryConfig(em, out var cfg)) return;
            Add(em, caster, cfg.HeatPerEruption);
        }

        /// <summary>A response unit lost <paramref name="damage"/> power because of <paramref name="attacker"/>.</summary>
        public static void UnitDamaged(EntityManager em, Entity attacker, float damage)
        {
            var god = Responsible(em, attacker);
            if (!IsGod(em, god) || !TryConfig(em, out var cfg)) return;
            Add(em, god, damage * cfg.HeatPerUnitDamage);
        }

        /// <summary>Gods are characters that can be blamed (players and the rival), not units or the dummy.</summary>
        public static bool IsGod(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) && em.HasComponent<HeatBlame>(e);

        /// <summary>
        /// Who is responsible for something this body did: if it was launched by someone, the launcher
        /// (a thrown unit that wrecks a building is the thrower's fault), otherwise the body itself.
        /// </summary>
        public static Entity Responsible(EntityManager em, Entity body)
        {
            if (body == Entity.Null || !em.Exists(body)) return Entity.Null;
            if (em.HasComponent<ToLive.Power.LastAttacker>(body) && em.HasComponent<CombatState>(body))
            {
                var la = em.GetComponentData<ToLive.Power.LastAttacker>(body).Value;
                var a = em.GetComponentData<CombatState>(body).Action;
                if (la != Entity.Null && em.Exists(la) && (a == CombatAction.Launched || a == CombatAction.KnockedOut)) return la;
            }
            if (em.HasComponent<Rocket>(body)) return em.GetComponentData<Rocket>(body).Owner;
            return body;
        }
    }
}
