using ToLive.Combat;
using ToLive.Destruction;
using ToLive.District;
using ToLive.Opposition;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Crisis
{
    /// <summary>
    /// SERVER: the crisis director and what a district becomes. One crisis at a time:
    /// <list type="bullet">
    /// <item>Disaster (the failing tower): chunks start to fail one by one; hover next to one while
    /// guarding to hold it up as people evacuate (protect), or bring the tower down yourself (force).</item>
    /// <item>Attack: units (or the rival) shoot at a crowd; destroy them with little collateral (protect)
    /// or flatten everything (force).</item>
    /// <item>Uprising: people rise against a feared god; stand down near them without harming anyone
    /// (protect) or crush it (force: a massacre with lasting fear).</item>
    /// </list>
    /// Outcomes go through the district source, then ownership is re-checked. A held district changes:
    /// trust rebuilds (chunks, bodies cleared, people return), dominance scars and fortifies (towers in
    /// the holder's colour that shoot challengers). Unreal analogy: a GameMode "director" with a
    /// replicated objective actor.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(DistrictSystem))]
    public partial struct CrisisSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var e = state.EntityManager.CreateEntity(typeof(CrisisDirector));
            state.EntityManager.AddBuffer<CrisisTally>(e);
            state.EntityManager.SetComponentData(e, new CrisisDirector { Seed = 0xC21515u });
            state.EntityManager.SetName(e, "CrisisDirector");
            state.RequireForUpdate<CrisisConfig>();
            state.RequireForUpdate<DistrictState>();
            state.RequireForUpdate<CrowdServer>();
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var net = SystemAPI.GetSingleton<NetworkTime>();
            if (!net.ServerTick.IsValid) return;
            var tick = net.ServerTick.TickIndexForValidTick;
            var em = state.EntityManager;
            var cfg = SystemAPI.GetSingleton<CrisisConfig>();
            var rate = Damage.TickRate(em);
            var districtEntity = SystemAPI.GetSingletonEntity<DistrictState>();
            var directorEntity = SystemAPI.GetSingletonEntity<CrisisDirector>();
            var director = em.GetComponentData<CrisisDirector>(directorEntity);
            var crisis = em.GetComponentData<CrisisState>(districtEntity);
            var hold = em.GetComponentData<DistrictHold>(districtEntity);
            var rnd = Unity.Mathematics.Random.CreateFromIndex(director.Seed ^ tick);
            if (director.NextCrisisTick == 0) director.NextCrisisTick = tick + CombatSystem.Ticks(cfg.FirstCrisisDelay, rate);

            // ---- debug ----
            if (director.PendingStart != 0)
            {
                if (crisis.Phase == CrisisPhase.Active) Resolve(ref state, ref crisis, ref director, cfg, CrisisOutcome.Failed, Entity.Null, tick, rate, true);
                Start(ref state, ref crisis, ref director, cfg, director.PendingStart, tick, rate, ref rnd);
                director.PendingStart = 0;
            }
            if (director.PendingOutcome != 0 && crisis.Phase == CrisisPhase.Active)
            {
                var who = BestGodInTally(em, directorEntity, crisis);
                Resolve(ref state, ref crisis, ref director, cfg, director.PendingOutcome, who, tick, rate, false);
                director.PendingOutcome = 0;
            }

            // ---- crisis lifecycle ----
            switch (crisis.Phase)
            {
                case CrisisPhase.None:
                    if (!CombatSystem.Before(tick, director.NextCrisisTick))
                        Start(ref state, ref crisis, ref director, cfg, PickKind(em, hold, cfg, ref rnd), tick, rate, ref rnd);
                    break;
                case CrisisPhase.Active:
                    switch (crisis.Kind)
                    {
                        case CrisisKind.Disaster: UpdateDisaster(ref state, ref crisis, ref director, cfg, tick, rate, ref rnd); break;
                        case CrisisKind.Attack: UpdateAttack(ref state, ref crisis, ref director, cfg, tick, rate); break;
                        case CrisisKind.Uprising: UpdateUprising(ref state, ref crisis, ref director, cfg, tick, rate); break;
                    }
                    break;
                case CrisisPhase.Resolved:
                    if (!CombatSystem.Before(tick, crisis.ResolvedTick + CombatSystem.Ticks(cfg.OutcomeShowTime, rate)))
                    {
                        crisis.Phase = CrisisPhase.None;
                        director.NextCrisisTick = tick + CombatSystem.Ticks(cfg.CrisisInterval, rate);
                    }
                    break;
            }

            // ---- ownership ----
            hold = em.GetComponentData<DistrictHold>(districtEntity);
            if (director.PendingHolderCheck != 0 && CombatSystem.Before(director.PendingHolderCheck, tick + 1))
            {
                UpdateHolder(em, districtEntity, ref hold, cfg);
                director.PendingHolderCheck = 0;
            }
            if (CombatSystem.Before(director.NextHoldTick, tick + 1))
            {
                director.NextHoldTick = tick + CombatSystem.Ticks(1f, rate);
                if (hold.Holder != Entity.Null && em.Exists(hold.Holder))
                {
                    SurgeFuel.AddTierProgress(em, hold.Holder, cfg.HoldTierFuelPerSecond); // tiers come from holding districts
                    // a holder whose standing has faded away loses the district
                    var (t, f) = Standing(em, districtEntity, hold.Holder);
                    if (math.max(t, f) < cfg.HoldThreshold * 0.5f) { hold.Holder = Entity.Null; hold.Mode = DistrictHold.None; Debug.Log("[ToLive] The district is no longer held"); }
                }
                else if (hold.Holder != Entity.Null) { hold.Holder = Entity.Null; hold.Mode = DistrictHold.None; }
            }

            // ---- what the district becomes ----
            DistrictChanges(ref state, ref hold, ref director, cfg, tick, rate);

            em.SetComponentData(districtEntity, crisis);
            em.SetComponentData(districtEntity, hold);
            em.SetComponentData(directorEntity, director);
        }

        // ================================================================ start / resolve

        static byte PickKind(EntityManager em, in DistrictHold hold, in CrisisConfig cfg, ref Unity.Mathematics.Random rnd)
        {
            var feared = MostFeared(em, cfg.UprisingFearMin) != Entity.Null;
            if (hold.Mode == DistrictHold.Dominance) return feared && rnd.NextBool() ? CrisisKind.Uprising : CrisisKind.Attack; // tyrants fight the army and their people
            if (hold.Mode == DistrictHold.Trust) return rnd.NextFloat() < 0.6f ? CrisisKind.Disaster : CrisisKind.Attack;        // protectors fight disasters and the rival
            var roll = rnd.NextInt(0, feared ? 3 : 2);
            return roll == 0 ? CrisisKind.Disaster : roll == 1 ? CrisisKind.Attack : CrisisKind.Uprising;
        }

        void Start(ref SystemState state, ref CrisisState c, ref CrisisDirector director, in CrisisConfig cfg, byte kind, uint tick, int rate, ref Unity.Mathematics.Random rnd)
        {
            var em = state.EntityManager;
            em.GetBuffer<CrisisTally>(SystemAPI.GetSingletonEntity<CrisisDirector>()).Clear();
            var seq = c.Seq + 1;
            c = new CrisisState { Kind = kind, Phase = CrisisPhase.Active, Seq = seq, StartTick = tick, WarnChunk = -1 };
            var districtEntity = SystemAPI.GetSingletonEntity<DistrictState>();
            var d = em.GetComponentData<DistrictState>(districtEntity);

            // VANTA takes the opposite approach to the leading player (if it is in the district)
            if (SystemAPI.TryGetSingleton<HeatState>(out var heat) && heat.Rival != Entity.Null && em.Exists(heat.Rival))
                c.RivalApproach = d.Leader != Entity.Null && d.Leader != heat.Rival && d.LeaderAlignment > 0.2f ? (byte)1 : (byte)2;

            switch (kind)
            {
                case CrisisKind.Disaster:
                {
                    var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
                    var graph = em.GetComponentData<BuildingGraph>(graphEntity);
                    var bs = SystemAPI.GetSingleton<BuildingState>();
                    if (bs.CountBroken() > graph.ChunkCount / 2) // too damaged to fail dramatically: rebuild it first
                    {
                        var bc = em.GetComponentData<BuildingCommand>(graphEntity); bc.Rebuild = 1; em.SetComponentData(graphEntity, bc);
                    }
                    c.Position = graph.Center; c.Radius = cfg.EvacRadius;
                    c.EndTick = tick + CombatSystem.Ticks(cfg.DisasterTime, rate);
                    c.Goal = (ushort)cfg.ChunksToHold;
                    director.NextCrackTick = tick + CombatSystem.Ticks(2f, rate);
                    break;
                }
                case CrisisKind.Attack:
                {
                    c.Position = DensestCrowd(em, 0f); c.Radius = 40f;
                    c.EndTick = tick + CombatSystem.Ticks(cfg.AttackTime, rate);
                    var hold = em.GetComponentData<DistrictHold>(districtEntity);
                    if (hold.Mode == DistrictHold.Trust && heat.Rival != Entity.Null && em.Exists(heat.Rival))
                    {
                        // protectors get the rival: VANTA attacks the crowd
                        c.Target = heat.Rival; c.Goal = 1; c.RivalApproach = 2;
                    }
                    else if (SystemAPI.TryGetSingleton<OppositionConfig>(out var opp))
                    {
                        var n = 0;
                        for (var i = 0; i < cfg.AttackPolice + cfg.AttackGunships; i++)
                        {
                            var kindU = i < cfg.AttackPolice ? UnitKind.Police : UnitKind.Gunship;
                            var u = HeatSystem.Spawn(ref state, opp, kindU, tick, rate, ref rnd, true);
                            if (u == Entity.Null) continue;
                            var a = rnd.NextFloat(0f, math.PI * 2f);
                            var p = c.Position + new float3(math.cos(a), 0f, math.sin(a)) * cfg.AttackSpawnDistance + new float3(0, 50f + rnd.NextFloat(0f, 30f), 0);
                            em.SetComponentData(u, LocalTransform.FromPosition(p));
                            em.AddComponentData(u, new CrowdAttacker { Point = c.Position });
                            n++;
                        }
                        c.Goal = (ushort)n;
                    }
                    break;
                }
                case CrisisKind.Uprising:
                {
                    var target = MostFeared(em, 0f);
                    c.Target = target;
                    var near = target != Entity.Null ? em.GetComponentData<LocalTransform>(target).Position : float3.zero;
                    c.Position = DensestCrowd(em, 0f, target != Entity.Null ? near : (float3?)null);
                    c.Radius = cfg.UprisingRadius;
                    c.EndTick = tick + CombatSystem.Ticks(cfg.UprisingTime, rate);
                    c.Goal = (ushort)math.round(cfg.StandDownTime);
                    SetRioters(em, c.Position, cfg.UprisingRadius, true);
                    break;
                }
            }
            Debug.Log($"[ToLive] Crisis #{c.Seq} started: {CrisisKind.Name(kind)} at {c.Position} (goal {c.Goal}, rival approach {c.RivalApproach})");
        }

        void Resolve(ref SystemState state, ref CrisisState c, ref CrisisDirector director, in CrisisConfig cfg, byte outcome, Entity resolver, uint tick, int rate, bool silent)
        {
            var em = state.EntityManager;
            var districtEntity = SystemAPI.GetSingletonEntity<DistrictState>();
            var hold = em.GetComponentData<DistrictHold>(districtEntity);
            if (!silent)
                switch (outcome)
                {
                    case CrisisOutcome.Protected:
                        DistrictSource.Add(em, resolver, cfg.TrustOnProtect, c.Kind == CrisisKind.Uprising ? -cfg.TrustOnProtect * 0.5f : 0f, Cause.CrisisProtected);
                        break;
                    case CrisisOutcome.Dominated:
                        DistrictSource.Add(em, resolver, -cfg.TrustOnProtect * 0.3f, cfg.FearOnDominate, Cause.CrisisDominated);
                        break;
                    case CrisisOutcome.Failed:
                        if (hold.Holder != Entity.Null) DistrictSource.Add(em, hold.Holder, -cfg.TrustLostOnFail, -cfg.FearLostOnFail, Cause.CrisisFailed);
                        if (c.Kind == CrisisKind.Uprising && c.Target != Entity.Null) DistrictSource.Add(em, c.Target, 0f, -cfg.FearLostOnFail, Cause.CrisisFailed);
                        if (c.Kind == CrisisKind.Disaster) CollapseTower(ref state); // ignored: the tower comes down on the street
                        break;
                }
            // attackers that are still alive leave; rioters calm down
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdAttacker>()))
            using (var units = q.ToEntityArray(Allocator.Temp))
                foreach (var u in units)
                {
                    em.RemoveComponent<CrowdAttacker>(u);
                    var b = em.GetComponentData<UnitBrain>(u); b.Pinned = 0; em.SetComponentData(u, b);
                }
            SetRioters(em, c.Position, 1e6f, false);
            c.Phase = CrisisPhase.Resolved; c.Outcome = outcome; c.Resolver = resolver; c.ResolvedTick = tick; c.WarnChunk = -1;
            director.PendingHolderCheck = tick + 2; // after the outcome's standing has been applied
            Debug.Log($"[ToLive] Crisis #{c.Seq} {CrisisKind.Name(c.Kind)}: {CrisisOutcome.Name(outcome)} by {resolver}");
        }

        // ================================================================ per kind

        void UpdateDisaster(ref SystemState state, ref CrisisState c, ref CrisisDirector director, in CrisisConfig cfg, uint tick, int rate, ref Unity.Mathematics.Random rnd)
        {
            var em = state.EntityManager;
            var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
            var nodes = em.GetBuffer<ChunkNode>(graphEntity, true).ToNativeArray(Allocator.Temp);
            var bs = SystemAPI.GetSingleton<BuildingState>();

            // people near the tower evacuate
            if (tick % (uint)rate == 0) CrowdHazards.Scare(em, new float3(c.Position.x, 0f, c.Position.z), cfg.EvacRadius);

            // force: a god brings the tower down
            var directorEntity = SystemAPI.GetSingletonEntity<CrisisDirector>();
            foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true))
                if (t.Chunks >= cfg.DominateChunks) { Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Dominated, t.God, tick, rate, false); return; }

            // a chunk starts to fail ...
            if (c.WarnChunk < 0 && !CombatSystem.Before(tick, director.NextCrackTick))
            {
                var best = -1; var bestScore = float.MinValue;
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (bs.IsBroken(i) || nodes[i].Grounded != 0) continue;
                    var score = nodes[i].Center.y + rnd.NextFloat(0f, 25f); // high chunks first, with some randomness
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0) { c.WarnChunk = (short)best; c.WarnTick = tick + CombatSystem.Ticks(cfg.WarnTime, rate); }
            }
            // ... and falls unless a god holds it up (hovering next to it, guarding)
            if (c.WarnChunk >= 0 && !CombatSystem.Before(tick, c.WarnTick))
            {
                var centre = nodes[c.WarnChunk].Center;
                var holder = Entity.Null;
                foreach (var (kind, combat, xf, e) in SystemAPI.Query<RefRO<CharacterKind>, RefRO<CombatState>, RefRO<LocalTransform>>().WithEntityAccess())
                    if (combat.ValueRO.Guarding != 0 && math.distance(xf.ValueRO.Position, centre) <= cfg.CatchRadius) { holder = e; break; }
                if (holder != Entity.Null)
                {
                    c.Saved++; c.Progress++;
                    CrisisTallies.Record(em, holder, held: 1);
                    DistrictSource.Add(em, holder, cfg.TrustPerHold, 0f, Cause.HeldChunk);
                    Debug.Log($"[ToLive] Disaster: chunk {c.WarnChunk} held up by {holder}");
                }
                else
                {
                    var req = em.GetBuffer<BreakRequest>(graphEntity);
                    req.Add(new BreakRequest { Chunk = c.WarnChunk, Velocity = new float3(0, -5, 0), Instigator = Entity.Null });
                    c.Lost++;
                }
                c.WarnChunk = -1;
                director.NextCrackTick = tick + CombatSystem.Ticks(cfg.CrackInterval, rate);
            }

            if (c.Progress >= c.Goal) { Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Protected, BestGodInTally(em, directorEntity, c), tick, rate, false); return; }
            if (!CombatSystem.Before(tick, c.EndTick)) Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Failed, Entity.Null, tick, rate, false);
        }

        void UpdateAttack(ref SystemState state, ref CrisisState c, ref CrisisDirector director, in CrisisConfig cfg, uint tick, int rate)
        {
            var em = state.EntityManager;
            var directorEntity = SystemAPI.GetSingletonEntity<CrisisDirector>();
            bool over;
            if (c.Target != Entity.Null)
            {
                // the rival attacks the crowd: over when it is knocked out
                over = !em.Exists(c.Target) || em.GetComponentData<CombatState>(c.Target).Action == CombatAction.KnockedOut;
                c.Progress = (ushort)(over ? 1 : 0);
            }
            else
            {
                var alive = 0;
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdAttacker>(), ComponentType.ReadOnly<ResponseUnit>()))
                using (var units = q.ToEntityArray(Allocator.Temp))
                    foreach (var u in units) if (em.GetComponentData<ResponseUnit>(u).Mode != UnitMode.Dead) alive++;
                c.Progress = (ushort)math.max(0, c.Goal - alive);
                over = alive == 0;
            }
            c.Lost = (ushort)math.min(65535, CountDeaths(em, directorEntity));
            if (over)
            {
                var resolver = c.Target != Entity.Null && em.Exists(c.Target)
                    ? HeatSource.Responsible(em, em.GetComponentData<LastAttacker>(c.Target).Value)
                    : BestGodInTally(em, directorEntity, c);
                var collateral = 0f;
                foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true)) if (t.God == resolver) collateral = t.Deaths + t.Chunks / 5f;
                Resolve(ref state, ref c, ref director, cfg, HeatSource.IsGod(em, resolver) ? (collateral > cfg.CollateralLimit ? CrisisOutcome.Dominated : CrisisOutcome.Protected) : CrisisOutcome.Failed, resolver, tick, rate, false);
                return;
            }
            if (!CombatSystem.Before(tick, c.EndTick)) Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Failed, Entity.Null, tick, rate, false);
        }

        void UpdateUprising(ref SystemState state, ref CrisisState c, ref CrisisDirector director, in CrisisConfig cfg, uint tick, int rate)
        {
            var em = state.EntityManager;
            var directorEntity = SystemAPI.GetSingletonEntity<CrisisDirector>();
            // force: a massacre crushes it
            foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true))
                if (t.RioterKills >= cfg.CrushKills) { Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Dominated, t.God, tick, rate, false); return; }

            if (tick % (uint)rate == 0 && c.Target != Entity.Null && em.Exists(c.Target))
            {
                // defiance erodes the target's fear every second it lasts
                DistrictSource.Add(em, c.Target, 0f, -cfg.UprisingErosionPerSecond, Cause.Uprising);
                // protect: the target stands down near the people, harming no one
                var p = em.GetComponentData<LocalTransform>(c.Target).Position;
                var combat = em.GetComponentData<CombatState>(c.Target);
                var move = em.GetComponentData<MoveState>(c.Target);
                var calm = math.distance(p, c.Position) <= cfg.StandDownRadius && move.Beam == 0 &&
                           (combat.Action == CombatAction.None || combat.Guarding != 0);
                var harmed = false;
                foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true)) if (t.God == c.Target && t.Deaths > 0) harmed = true;
                if (calm && !harmed) c.Progress++;
                else if (!calm) c.Progress = 0;
                if (harmed) { c.Progress = 0; ResetDeaths(em, directorEntity, c.Target); }
                if (c.Progress >= c.Goal) { Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Protected, c.Target, tick, rate, false); return; }
            }
            if (!CombatSystem.Before(tick, c.EndTick)) Resolve(ref state, ref c, ref director, cfg, CrisisOutcome.Failed, Entity.Null, tick, rate, false);
        }

        // ================================================================ ownership and district changes

        static void UpdateHolder(EntityManager em, Entity districtEntity, ref DistrictHold hold, in CrisisConfig cfg)
        {
            Entity best = Entity.Null; float bestV = 0f, second = 0f; byte mode = DistrictHold.None;
            foreach (var s in em.GetBuffer<GodStanding>(districtEntity, true))
            {
                if (!em.Exists(s.God)) continue;
                var v = math.max(s.Trust, s.Fear);
                if (v > bestV) { second = bestV; bestV = v; best = s.God; mode = s.Trust >= s.Fear ? DistrictHold.Trust : DistrictHold.Dominance; }
                else if (v > second) second = v;
            }
            if (best != Entity.Null && bestV >= cfg.HoldThreshold && bestV - second >= cfg.HoldMargin)
            {
                if (hold.Holder != best || hold.Mode != mode) Debug.Log($"[ToLive] District now held by {best} through {(mode == DistrictHold.Trust ? "trust" : "dominance")}");
                hold.Holder = best; hold.Mode = mode;
            }
        }

        void DistrictChanges(ref SystemState state, ref DistrictHold hold, ref CrisisDirector director, in CrisisConfig cfg, uint tick, int rate)
        {
            var em = state.EntityManager;
            if (hold.Mode == DistrictHold.Trust)
            {
                // trust rebuilds: one chunk at a time, supported from below or from an intact neighbour
                if (CombatSystem.Before(director.NextRebuildTick, tick + 1) && SystemAPI.TryGetSingletonEntity<BuildingGraph>(out var graphEntity))
                {
                    director.NextRebuildTick = tick + CombatSystem.Ticks(cfg.RebuildInterval, rate);
                    var bsRef = SystemAPI.GetSingletonRW<BuildingState>();
                    var bs = bsRef.ValueRO;
                    var nodes = em.GetBuffer<ChunkNode>(graphEntity, true);
                    var neighbors = em.GetBuffer<ChunkNeighbor>(graphEntity, true);
                    var pick = -1; var lowest = float.MaxValue;
                    for (var i = 0; i < nodes.Length && i < BuildingState.MaxChunks; i++)
                    {
                        if (!bs.IsBroken(i)) continue;
                        var supported = nodes[i].Grounded != 0;
                        for (var k = 0; k < nodes[i].NeighborCount && !supported; k++)
                        {
                            var j = neighbors[nodes[i].NeighborStart + k].Index;
                            if (j >= 0 && j < BuildingState.MaxChunks && !bs.IsBroken(j) && nodes[j].Center.y < nodes[i].Center.y + 0.5f) supported = true;
                        }
                        if (supported && nodes[i].Center.y < lowest) { lowest = nodes[i].Center.y; pick = i; }
                    }
                    if (pick >= 0) { bs.ClearBroken(pick); bsRef.ValueRW = bs; hold.Rebuilt++; }
                }
                // bodies are cleared, then people return home
                using (var bq = em.CreateEntityQuery(ComponentType.ReadOnly<CitizenBody>()))
                using (var bodies = bq.ToEntityArray(Allocator.Temp))
                    foreach (var b in bodies)
                        if (tick - em.GetComponentData<CitizenBody>(b).DeathTick > CombatSystem.Ticks(cfg.BodyClearTime, rate)) em.DestroyEntity(b);
                if (CombatSystem.Before(director.NextReturnTick, tick + 1))
                {
                    director.NextReturnTick = tick + CombatSystem.Ticks(cfg.ReturnInterval, rate);
                    ReturnOneCitizen(ref state);
                }
                // towers come down (one per interval)
                if (hold.FortifyLevel > 0 && CombatSystem.Before(director.NextFortifyTick, tick + 1))
                { hold.FortifyLevel--; director.NextFortifyTick = tick + CombatSystem.Ticks(cfg.FortifyInterval, rate); }
            }
            else if (hold.Mode == DistrictHold.Dominance)
            {
                // dominance scars and fortifies: damage and bodies stay, towers go up in the holder's colour
                if (CombatSystem.Before(director.NextFortifyTick, tick + 1))
                {
                    director.NextFortifyTick = tick + CombatSystem.Ticks(cfg.FortifyInterval, rate);
                    if (hold.FortifyLevel < cfg.MaxFortify) { hold.FortifyLevel++; Debug.Log($"[ToLive] Fortification {hold.FortifyLevel} raised"); }
                }
            }

            // towers shoot gods who challenge the holder
            if (hold.FortifyLevel > 0 && hold.Holder != Entity.Null && CombatSystem.Before(director.NextTowerShotTick, tick + 1))
            {
                director.NextTowerShotTick = tick + CombatSystem.Ticks(cfg.TowerFireInterval, rate);
                using var tq = em.CreateEntityQuery(ComponentType.ReadOnly<Fortification>());
                using var towers = tq.ToComponentDataArray<Fortification>(Allocator.Temp);
                var seen = new NativeHashSet<int>(8, Allocator.Temp);
                foreach (var t in towers)
                {
                    if (t.Index >= hold.FortifyLevel || !seen.Add(t.Index)) continue;
                    foreach (var (kind, combat, xf, e) in SystemAPI.Query<RefRO<CharacterKind>, RefRO<CombatState>, RefRO<LocalTransform>>().WithEntityAccess())
                    {
                        if (e == hold.Holder || combat.ValueRO.Action == CombatAction.KnockedOut) continue;
                        var d = math.distance(xf.ValueRO.Position, t.Top);
                        if (d > cfg.TowerRange) continue;
                        UnitAISystem.HitFrom(em, Entity.Null, e, cfg.TowerDamage, math.normalizesafe(xf.ValueRO.Position - t.Top), 3f, tick, rate);
                        hold.TowerShotTick = tick; hold.TowerShotIndex = (byte)t.Index; hold.TowerShotTarget = e;
                        return;
                    }
                }
            }
        }

        void ReturnOneCitizen(ref SystemState state)
        {
            var em = state.EntityManager;
            var serverEntity = SystemAPI.GetSingletonEntity<CrowdServer>();
            var bodied = new NativeHashSet<int>(16, Allocator.Temp);
            using (var bq = em.CreateEntityQuery(ComponentType.ReadOnly<CitizenBody>()))
            using (var bodies = bq.ToComponentDataArray<CitizenBody>(Allocator.Temp))
                foreach (var b in bodies) bodied.Add(b.Index);
            var citizens = em.GetBuffer<CitizenSim>(serverEntity);
            var homes = SystemAPI.GetSingletonBuffer<CitizenHome>(true);
            for (var i = 0; i < citizens.Length; i++)
            {
                if (citizens[i].Alive != 0 || bodied.Contains(i)) continue;
                var c = citizens[i];
                c.Alive = 1; c.Mood = Mood.Wander; c.Position = homes[i].Position; c.Target = homes[i].Position; c.Riot = 0;
                citizens[i] = c;
                return;
            }
        }

        // ================================================================ helpers

        void CollapseTower(ref SystemState state)
        {
            var em = state.EntityManager;
            var graphEntity = SystemAPI.GetSingletonEntity<BuildingGraph>();
            var nodes = em.GetBuffer<ChunkNode>(graphEntity, true).ToNativeArray(Allocator.Temp);
            var req = em.GetBuffer<BreakRequest>(graphEntity);
            for (var i = 0; i < nodes.Length; i++)
                if (nodes[i].Grounded != 0) req.Add(new BreakRequest { Chunk = i, Velocity = new float3(0, -8, 0), Instigator = Entity.Null });
        }

        static void SetRioters(EntityManager em, float3 at, float radius, bool riot)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            if (q.CalculateEntityCount() != 1) return;
            var citizens = em.GetBuffer<CitizenSim>(q.GetSingletonEntity());
            for (var i = 0; i < citizens.Length; i++)
            {
                var c = citizens[i];
                if (c.Alive == 0) continue;
                if (riot && math.distance(c.Position.xz, at.xz) > radius) continue;
                c.Riot = (byte)(riot ? 1 : 0);
                if (riot) { c.Mood = Mood.Gather; c.RiotPoint = at; }
                citizens[i] = c;
            }
        }

        /// <summary>The densest spot of living citizens (optionally near a point).</summary>
        public static float3 DensestCrowd(EntityManager em, float minDistance, float3? near = null)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdServer>());
            var citizens = em.GetBuffer<CitizenSim>(q.GetSingletonEntity()).ToNativeArray(Allocator.Temp);
            var best = float3.zero; var bestScore = float.MinValue;
            for (var i = 0; i < citizens.Length; i += 4)
            {
                if (citizens[i].Alive == 0) continue;
                var score = (float)DistrictSystem.CountNear(citizens, citizens[i].Position, 30f);
                if (near.HasValue) score -= math.distance(citizens[i].Position, near.Value) * 0.05f;
                if (score > bestScore) { bestScore = score; best = citizens[i].Position; }
            }
            return best;
        }

        static Entity MostFeared(EntityManager em, float minFear)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<DistrictState>());
            if (q.CalculateEntityCount() != 1) return Entity.Null;
            var best = Entity.Null; var bestFear = minFear;
            foreach (var s in em.GetBuffer<GodStanding>(q.GetSingletonEntity(), true))
                if (em.Exists(s.God) && s.Fear >= bestFear && s.Fear > s.Trust) { bestFear = s.Fear; best = s.God; }
            return best;
        }

        static (float trust, float fear) Standing(EntityManager em, Entity districtEntity, Entity god)
        {
            foreach (var s in em.GetBuffer<GodStanding>(districtEntity, true)) if (s.God == god) return (s.Trust, s.Fear);
            return (0f, 0f);
        }

        static Entity BestGodInTally(EntityManager em, Entity directorEntity, in CrisisState c)
        {
            var best = Entity.Null; var score = 0;
            foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true))
            {
                if (!HeatSource.IsGod(em, t.God)) continue;
                var s = t.Held * 10 + t.UnitsDestroyed * 10 + t.Chunks + t.RioterKills + t.Deaths;
                if (s > score) { score = s; best = t.God; }
            }
            if (best == Entity.Null && c.Target != Entity.Null && HeatSource.IsGod(em, c.Target)) best = c.Target;
            return best;
        }

        static int CountDeaths(EntityManager em, Entity directorEntity)
        {
            var n = 0;
            foreach (var t in em.GetBuffer<CrisisTally>(directorEntity, true)) n += t.Deaths;
            return n;
        }

        static void ResetDeaths(EntityManager em, Entity directorEntity, Entity god)
        {
            var buf = em.GetBuffer<CrisisTally>(directorEntity);
            for (var i = 0; i < buf.Length; i++) if (buf[i].God == god) { var t = buf[i]; t.Deaths = 0; buf[i] = t; }
        }
    }
}
