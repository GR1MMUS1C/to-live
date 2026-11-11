using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Flight;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>
    /// SERVER: the heat director. Applies queued <see cref="HeatEvent"/>s (the heat source), decays heat
    /// after a quiet period, derives the level 0..3, and spawns the response force in budgeted waves
    /// at the district edge. Like an Unreal "AI director" actor in the GameMode. Everything it decides is
    /// replicated through the <see cref="HeatState"/> ghost and the unit ghosts.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BreakSystem))]
    public partial struct HeatSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var q = state.EntityManager.CreateEntity(typeof(HeatQueue), typeof(HeatDirector));
            state.EntityManager.AddBuffer<HeatEvent>(q);
            state.EntityManager.SetComponentData(q, new HeatDirector { Seed = 0x9E3779B9u });
            state.EntityManager.SetName(q, "HeatQueue");
            state.RequireForUpdate<OppositionConfig>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<OppositionConfig>();
            var rate = Damage.TickRate(em);
            var dt = SystemAPI.Time.DeltaTime;

            // The replicated state ghost (spawned once).
            if (!SystemAPI.TryGetSingletonEntity<HeatState>(out var stateEntity))
            {
                if (cfg.StatePrefab == Entity.Null) return;
                stateEntity = em.Instantiate(cfg.StatePrefab);
                em.SetComponentData(stateEntity, new HeatState { RivalDifficulty = 1 });
                Debug.Log("[ToLive] Opposition state spawned");
            }
            var heat = em.GetComponentData<HeatState>(stateEntity);
            var directorEntity = SystemAPI.GetSingletonEntity<HeatDirector>();
            var director = em.GetComponentData<HeatDirector>(directorEntity);
            var random = Unity.Mathematics.Random.CreateFromIndex(director.Seed ^ tick);

            // ---- producers that need per-tick bookkeeping: damage dealt to units ----
            var damaged = new NativeList<(Entity attacker, float lost)>(Allocator.Temp);
            foreach (var (brain, combat, attacker) in SystemAPI.Query<RefRW<UnitBrain>, RefRO<CombatState>, RefRO<ToLive.Power.LastAttacker>>())
            {
                var c = combat.ValueRO;
                var lost = brain.ValueRO.LastPower - c.Power;
                if (lost == 0f) continue;
                brain.ValueRW.LastPower = c.Power;
                if (lost > 0f) damaged.Add((attacker.ValueRO.Value, lost));
            }
            foreach (var (attacker, lost) in damaged) HeatSource.UnitDamaged(em, attacker, lost);

            // ---- apply the heat queue ----
            var queue = SystemAPI.GetSingletonBuffer<HeatEvent>();
            if (queue.Length > 0)
            {
                var events = queue.ToNativeArray(Allocator.Temp);
                queue.Clear();
                // milestone 4: heat is dominance behaviour. A god's heat is scaled by how much the district
                // fears rather than trusts them, so a trusted protector draws little or no army.
                var hasDistrict = SystemAPI.TryGetSingletonEntity<ToLive.District.DistrictState>(out var districtEntity);
                foreach (var ev in events)
                {
                    var amount = ev.Amount;
                    if (hasDistrict && ev.God != Entity.Null)
                    {
                        float trust = 0f, fear = 0f;
                        foreach (var s in em.GetBuffer<ToLive.District.GodStanding>(districtEntity, true))
                            if (s.God == ev.God) { trust = s.Trust; fear = s.Fear; }
                        var fearShare = (fear + 1f) / (trust + fear + 2f);
                        amount *= math.lerp(cfg.TrustedHeatScale, 1f, math.saturate(fearShare * 2f)); // neutral or feared = full heat, trusted = TrustedHeatScale
                    }
                    heat.Value = math.min(cfg.MaxHeat, heat.Value + amount);
                    heat.LastRiseTick = tick;
                    if (ev.God != Entity.Null && em.Exists(ev.God) && em.HasComponent<HeatBlame>(ev.God))
                    {
                        var b = em.GetComponentData<HeatBlame>(ev.God);
                        b.Amount += amount; b.LastTick = tick;
                        em.SetComponentData(ev.God, b);
                    }
                }
            }

            // ---- debug: whole levels up/down ----
            if (director.PendingLevelDelta != 0)
            {
                var target = math.clamp(heat.Level + director.PendingLevelDelta, 0, 3);
                var before = heat.Value;
                heat.Value = target == 0 ? 0f : target >= 3 ? (cfg.Level3 + cfg.MaxHeat) * 0.5f : (cfg.ThresholdFor(target) + cfg.ThresholdFor(target + 1)) * 0.5f; // middle of the level
                heat.LastRiseTick = tick;
                // debug heat is blamed on the human players (so the force has someone to shoot)
                foreach (var (kind, blame) in SystemAPI.Query<RefRO<CharacterKind>, RefRW<HeatBlame>>())
                    if (kind.ValueRO.Kind == CharacterKind.Human)
                    {
                        blame.ValueRW.Amount = target == 0 ? 0f : math.max(blame.ValueRO.Amount, heat.Value);
                        blame.ValueRW.LastTick = tick;
                    }
                director.PendingLevelDelta = 0;
                Debug.Log($"[ToLive] Debug heat level -> {target}");
            }

            // ---- decay after a quiet period ----
            if (heat.Value > 0f && tick - heat.LastRiseTick > CombatSystem.Ticks(cfg.QuietTime, rate))
                heat.Value = math.max(0f, heat.Value - cfg.DecayPerSecond * dt);
            foreach (var blame in SystemAPI.Query<RefRW<HeatBlame>>())
                if (blame.ValueRO.Amount > 0f && tick - blame.ValueRO.LastTick > CombatSystem.Ticks(cfg.BlameQuietTime, rate))
                    blame.ValueRW.Amount = math.max(0f, blame.ValueRO.Amount - cfg.BlameDecayPerSecond * dt);

            var newLevel = cfg.LevelFor(heat.Value);
            if (newLevel != heat.Level) Debug.Log($"[ToLive] Heat level {heat.Level} -> {newLevel} (heat {heat.Value:0})");
            heat.Level = newLevel;

            // ---- count the force ----
            int police = 0, gunships = 0, antigods = 0;
            var all = new NativeList<Entity>(16, Allocator.Temp);
            foreach (var (unit, e) in SystemAPI.Query<RefRO<ResponseUnit>>().WithEntityAccess())
            {
                all.Add(e);
                if (unit.ValueRO.Mode == UnitMode.Dead) continue;
                switch (unit.ValueRO.Kind)
                {
                    case UnitKind.Police: police++; break;
                    case UnitKind.Gunship: gunships++; break;
                    case UnitKind.AntiGod: antigods++; break;
                }
            }

            // ---- debug: clear / spawn one of each ----
            if (director.PendingClear != 0)
            {
                foreach (var e in all) em.DestroyEntity(e);
                using (var rq = SystemAPI.QueryBuilder().WithAll<Rocket>().Build().ToEntityArray(Allocator.Temp))
                    foreach (var r in rq) em.DestroyEntity(r);
                police = gunships = antigods = 0;
                director.PendingClear = 0;
                Debug.Log("[ToLive] Debug: all response units cleared");
            }
            if (director.PendingSpawnEach != 0)
            {
                Spawn(ref state, cfg, UnitKind.Police, tick, rate, ref random, true);
                Spawn(ref state, cfg, UnitKind.Gunship, tick, rate, ref random, true);
                Spawn(ref state, cfg, UnitKind.AntiGod, tick, rate, ref random, true);
                police++; gunships++; antigods++;
                director.PendingSpawnEach = 0;
                Debug.Log("[ToLive] Debug: spawned one of each response unit");
            }

            if (director.PendingBenchUnits != 0)
            {
                // benchmark: a full force close to the action, everyone fighting the humans
                int n = director.PendingBenchUnits, anti = n >= 3 ? 1 : 0, guns = (int)math.round((n - anti) * 0.3f);
                for (var i = 0; i < n; i++)
                    Spawn(ref state, cfg, i < anti ? UnitKind.AntiGod : i < anti + guns ? UnitKind.Gunship : UnitKind.Police, tick, rate, ref random, true, 140f);
                foreach (var (kind, blame) in SystemAPI.Query<RefRO<CharacterKind>, RefRW<HeatBlame>>())
                    if (kind.ValueRO.Kind == CharacterKind.Human) { blame.ValueRW.Amount = 100f; blame.ValueRW.LastTick = tick; }
                Debug.Log($"[ToLive] Benchmark: spawned {n} response units ({anti} anti-god, {guns} gunships)");
                director.PendingBenchUnits = 0;
            }

            // ---- waves (budgeted) ----
            if (heat.Level >= 1 && CombatSystem.Before(director.NextWaveTick, tick + 1))
            {
                var total = police + gunships + antigods;
                for (var i = 0; i < cfg.PolicePerWave && police < cfg.MaxPolice && total < cfg.MaxUnits; i++)
                { Spawn(ref state, cfg, UnitKind.Police, tick, rate, ref random, false); police++; total++; }
                if (heat.Level >= 2)
                    for (var i = 0; i < cfg.GunshipsPerWave && gunships < cfg.MaxGunships && total < cfg.MaxUnits; i++)
                    { Spawn(ref state, cfg, UnitKind.Gunship, tick, rate, ref random, false); gunships++; total++; }
                if (heat.Level >= 3 && antigods < cfg.MaxAntiGod && total < cfg.MaxUnits)
                { Spawn(ref state, cfg, UnitKind.AntiGod, tick, rate, ref random, false); antigods++; }
                director.NextWaveTick = tick + CombatSystem.Ticks(cfg.WaveInterval, rate);
            }
            else if (heat.Level == 0) director.NextWaveTick = tick + CombatSystem.Ticks(1f, rate); // first wave ~1 s after heat 1

            heat.Units = (byte)math.min(255, police + gunships + antigods);
            director.Seed = random.NextUInt();
            em.SetComponentData(directorEntity, director);
            em.SetComponentData(stateEntity, heat);
        }

        /// <summary>Spawns one unit at the district edge, facing the centre.</summary>
        public static Entity Spawn(ref SystemState state, in OppositionConfig cfg, byte kind, uint tick, int rate, ref Unity.Mathematics.Random random, bool pinned, float distance = 0f)
        {
            var prefab = kind switch { UnitKind.Gunship => cfg.GunshipPrefab, UnitKind.AntiGod => cfg.AntiGodPrefab, _ => cfg.PolicePrefab };
            if (prefab == Entity.Null) return Entity.Null;
            var em = state.EntityManager;
            var stats = cfg.Stats(kind);
            var angle = random.NextFloat(0f, math.PI * 2f);
            var dist = distance > 0f ? distance : cfg.SpawnDistance;
            var pos = new float3(math.cos(angle) * dist, random.NextFloat(cfg.SpawnHeightMin, cfg.SpawnHeightMax), math.sin(angle) * dist);
            var yaw = math.degrees(math.atan2(-pos.x, -pos.z));
            var e = em.Instantiate(prefab);
            em.SetComponentData(e, LocalTransform.FromPositionRotation(pos, quaternion.RotateY(math.radians(yaw))));
            em.SetComponentData(e, new FlightState { Yaw = yaw });
            em.SetComponentData(e, new CombatState { Power = stats.Health });
            em.SetComponentData(e, new ResponseUnit { Kind = kind, Mode = UnitMode.Approach, MaxHealth = stats.Health });
            em.SetComponentData(e, new UnitBrain
            {
                SpawnTick = tick, ModeTick = tick, LastPower = stats.Health, Pinned = (byte)(pinned ? 1 : 0),
                StrafeSign = random.NextBool() ? 1f : -1f, Seed = random.NextUInt(1, uint.MaxValue),
                NextFireTick = tick + CombatSystem.Ticks(random.NextFloat(1f, 2.5f), rate),
                AimDir = math.normalizesafe(-pos),
            });
            return e;
        }
    }
}
