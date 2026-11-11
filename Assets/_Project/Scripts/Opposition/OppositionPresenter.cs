using System.Collections.Generic;
using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Player;
using ToLive.Power;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>What the HUD shows about heat, the rival and incoming fire (filled by <see cref="OppositionPresenter"/>).</summary>
    public static class OppositionView
    {
        public static byte HeatLevel, Units;
        public static float HeatValue, HeatNext;   // raw heat and the next level's threshold
        public static bool RivalPresent, RivalNear, RivalLocked, RivalDown;
        public static float3 RivalPosition;
        public static float RivalPower, RivalMaxPower, RivalRespawnIn;
        public static byte RivalPhase, RivalDifficulty, RivalDowns;
        public static bool IncomingBeam;           // the anti-god weapon is charging/firing at the local player
        public static float IncomingProgress;      // 0..1 build-up
        public static bool IncomingFiring;
        public static float3 IncomingFrom;
        public static bool Suppressed;             // local player's surge gain suppressed
        public static readonly List<(float3 pos, float health, byte kind)> DamagedUnits = new List<(float3, float, byte)>();
    }

    /// <summary>
    /// CLIENT ONLY cosmetics for the response force: tracers and muzzle flashes from the replicated
    /// last-shot fields, rocket trails and explosions (a rocket ghost disappearing = it exploded),
    /// the anti-god weapon's aim line, build-up and beam, unit tints and hit flashes, barrel aiming,
    /// and wreck debris when a destroyed unit despawns. Nothing here affects gameplay.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class OppositionPresenter : SystemBase
    {
        class UnitFx
        {
            public uint SeenShot;
            public byte Mode, Kind;
            public float3 LastPos, LastVel;
            public LineRenderer Aim;
            public double LastHitTime;
            public uint SeenHit;
            public bool FiredSound;
        }

        readonly Dictionary<Entity, UnitFx> m_Units = new Dictionary<Entity, UnitFx>();
        readonly Dictionary<Entity, (TrailRenderer trail, float3 pos)> m_Rockets = new Dictionary<Entity, (TrailRenderer, float3)>();
        readonly List<(LineRenderer line, float until, float life)> m_Tracers = new List<(LineRenderer, float, float)>();
        readonly Stack<LineRenderer> m_TracerPool = new Stack<LineRenderer>();
        Material m_Mat;

        static readonly float4 PoliceColor = new float4(0.75f, 0.78f, 0.85f, 1f);
        static readonly float4 GunshipColor = new float4(0.38f, 0.42f, 0.34f, 1f);
        static readonly float4 AntiGodColor = new float4(0.16f, 0.16f, 0.19f, 1f);

        protected override void OnDestroy()
        {
            foreach (var u in m_Units.Values) if (u.Aim != null) Object.Destroy(u.Aim.gameObject);
            foreach (var r in m_Rockets.Values) if (r.trail != null) Object.Destroy(r.trail.gameObject);
            foreach (var t in m_Tracers) if (t.line != null) Object.Destroy(t.line.gameObject);
            foreach (var l in m_TracerPool) if (l != null) Object.Destroy(l.gameObject);
        }

        Material Mat()
        {
            if (m_Mat == null) m_Mat = FxMaterials.Unlit();
            return m_Mat;
        }

        LineRenderer NewLine(string name, Color color, float width)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = Mat();
            lr.material.color = color;
            lr.positionCount = 2;
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            var now = (float)SystemAPI.Time.ElapsedTime;
            var dt = SystemAPI.Time.DeltaTime;
            var hasCfg = SystemAPI.TryGetSingleton<OppositionConfig>(out var cfg);
            var net = SystemAPI.TryGetSingleton<NetworkTime>(out var nt) ? nt : default;
            var interpTick = net.InterpolationTick.IsValid ? net.InterpolationTick.TickIndexForValidTick : 0u;
            var rate = Damage.TickRate(EntityManager);

            // local player
            var local = Entity.Null;
            foreach (var (_, e) in SystemAPI.Query<RefRO<GhostOwnerIsLocal>>().WithEntityAccess()) local = e;
            var localPos = local != Entity.Null ? SystemAPI.GetComponent<LocalTransform>(local).Position : float3.zero;
            var localLock = local != Entity.Null ? SystemAPI.GetComponent<CombatState>(local).LockTarget : Entity.Null;
            OppositionView.Suppressed = false;
            if (local != Entity.Null && SystemAPI.HasComponent<PowerState>(local))
            {
                var lp = SystemAPI.GetComponent<PowerState>(local);
                var serverTick = net.ServerTick.IsValid ? net.ServerTick.TickIndexForValidTick : 0u;
                OppositionView.Suppressed = lp.SuppressedUntilTick != 0 && CombatSystem.Before(serverTick, lp.SuppressedUntilTick);
            }

            // ---- heat + rival status ----
            OppositionView.RivalPresent = false; OppositionView.RivalNear = false; OppositionView.RivalLocked = false;
            if (SystemAPI.TryGetSingleton<HeatState>(out var heat))
            {
                OppositionView.HeatLevel = heat.Level;
                OppositionView.HeatValue = heat.Value;
                OppositionView.HeatNext = hasCfg ? (heat.Level >= 3 ? cfg.MaxHeat : cfg.ThresholdFor(heat.Level + 1)) : 100f;
                OppositionView.Units = heat.Units;
                OppositionView.RivalDifficulty = heat.RivalDifficulty;
                OppositionView.RivalDowns = heat.RivalDowns;
                if (heat.Rival != Entity.Null && SystemAPI.Exists(heat.Rival) && SystemAPI.HasComponent<CombatState>(heat.Rival))
                {
                    var rc = SystemAPI.GetComponent<CombatState>(heat.Rival);
                    var rp = SystemAPI.HasComponent<PowerState>(heat.Rival) ? SystemAPI.GetComponent<PowerState>(heat.Rival) : default;
                    OppositionView.RivalPresent = true;
                    OppositionView.RivalPosition = SystemAPI.GetComponent<LocalTransform>(heat.Rival).Position;
                    OppositionView.RivalPower = rc.Power;
                    OppositionView.RivalMaxPower = rp.Tier >= 2 ? 150f : 100f;
                    OppositionView.RivalPhase = rp.Phase;
                    OppositionView.RivalDown = rc.Action == CombatAction.KnockedOut;
                    OppositionView.RivalLocked = localLock == heat.Rival;
                    OppositionView.RivalNear = local != Entity.Null && math.distance(localPos, OppositionView.RivalPosition) < 60f;
                    OppositionView.RivalRespawnIn = heat.RivalRespawnTick != 0 && interpTick != 0 ? math.max(0f, (int)(heat.RivalRespawnTick - interpTick) / (float)rate) : 0f;
                }
            }

            // ---- units ----
            OppositionView.IncomingBeam = false; OppositionView.IncomingFiring = false;
            OppositionView.DamagedUnits.Clear();
            var colors = GetComponentLookup<URPMaterialPropertyBaseColor>();
            var transforms = GetComponentLookup<LocalTransform>();
            var seen = new NativeHashSet<Entity>(16, Allocator.Temp);
            foreach (var (unitRef, combatRef, ltw, visual, flight, fx, e) in
                     SystemAPI.Query<RefRO<ResponseUnit>, RefRO<CombatState>, RefRO<LocalToWorld>, RefRO<UnitVisual>, RefRO<ToLive.Player.FlightState>, RefRO<HitFxState>>().WithEntityAccess())
            {
                var u = unitRef.ValueRO;
                var c = combatRef.ValueRO;
                var pos = ltw.ValueRO.Position;
                seen.Add(e);
                if (!m_Units.TryGetValue(e, out var ufx)) { ufx = new UnitFx { SeenShot = u.ShotTick, SeenHit = c.LastHitTick }; m_Units[e] = ufx; }
                ufx.Mode = u.Mode; ufx.Kind = u.Kind; ufx.LastPos = pos; ufx.LastVel = flight.ValueRO.Velocity;

                // tint: base colour, white hit flash, dark when wrecked
                var col = u.Kind == UnitKind.Gunship ? GunshipColor : u.Kind == UnitKind.AntiGod ? AntiGodColor : PoliceColor;
                if (u.Mode == UnitMode.Dead) col = new float4(0.08f, 0.08f, 0.08f, 1f);
                else if (fx.ValueRO.FlashUntil > SystemAPI.Time.ElapsedTime) col = new float4(1f, 1f, 1f, 1f);
                if (colors.HasComponent(visual.ValueRO.Body)) colors[visual.ValueRO.Body] = new URPMaterialPropertyBaseColor { Value = col };

                if (c.LastHitTick != ufx.SeenHit) { ufx.SeenHit = c.LastHitTick; ufx.LastHitTime = now; }
                if (u.Mode != UnitMode.Dead && now - ufx.LastHitTime < 4f && u.MaxHealth > 0f)
                    OppositionView.DamagedUnits.Add((pos + new float3(0, u.Kind == UnitKind.AntiGod ? 7f : u.Kind == UnitKind.Gunship ? 3.5f : 1.5f, 0), math.saturate(c.Power / u.MaxHealth), u.Kind));

                // new shot: tracer / launch puff
                if (u.ShotTick != ufx.SeenShot)
                {
                    ufx.SeenShot = u.ShotTick;
                    if (u.Kind == UnitKind.Police)
                    {
                        var from = pos + math.normalizesafe(u.ShotEnd - pos) * 1.2f;
                        SpawnTracer(from, u.ShotEnd, u.ShotHit != 0 ? new Color(1f, 0.85f, 0.45f) : new Color(1f, 0.7f, 0.3f, 0.7f));
                        CombatFx.RaiseTracer(from, u.ShotEnd);
                        if (u.ShotHit == 0 && math.distance(u.ShotEnd, localPos) < 6f) CombatFx.Shake = math.max(CombatFx.Shake, 0.05f); // whiz
                    }
                    else if (u.Kind == UnitKind.Gunship)
                    {
                        ImpactRing.Spawn(u.ShotEnd, 0.6f);
                        CombatFx.RaiseRocketLaunch(u.ShotEnd);
                    }
                }

                // aim child: gunship pods follow the target, anti-god barrel follows the beam aim
                var aimPoint = u.Kind == UnitKind.AntiGod && u.BeamPhase != AntiGodPhase.Idle ? u.AimPoint
                    : u.Target != Entity.Null && transforms.HasComponent(u.Target) ? transforms[u.Target].Position : pos + ltw.ValueRO.Forward * 10f;
                if (transforms.HasComponent(visual.ValueRO.Aim))
                {
                    var aimDir = math.normalizesafe(aimPoint - pos, ltw.ValueRO.Forward);
                    var localDir = math.mul(math.inverse(ltw.ValueRO.Rotation), aimDir);
                    var t = transforms[visual.ValueRO.Aim];
                    t.Rotation = quaternion.LookRotationSafe(localDir, new float3(0, 1, 0));
                    transforms[visual.ValueRO.Aim] = t;
                }

                // anti-god telegraph
                if (u.Kind == UnitKind.AntiGod) UpdateAntiGod(ufx, u, pos, interpTick, rate, hasCfg ? cfg : default, local, localPos, now);
            }

            // units that disappeared: wreck debris if they were destroyed
            var gone = new List<Entity>();
            foreach (var kv in m_Units) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var e in gone)
            {
                var ufx = m_Units[e];
                if (ufx.Aim != null) Object.Destroy(ufx.Aim.gameObject);
                if (ufx.Mode == UnitMode.Dead && hasCfg)
                {
                    var stats = cfg.Stats(ufx.Kind);
                    DebrisSystem.Wrecks.Add(new WreckFx { Position = ufx.LastPos, Velocity = ufx.LastVel, Count = stats.WreckPieces, Size = ufx.Kind == UnitKind.AntiGod ? 1.6f : ufx.Kind == UnitKind.Gunship ? 0.9f : 0.4f });
                    ImpactRing.Spawn(ufx.LastPos, ufx.Kind == UnitKind.AntiGod ? 4f : 2f);
                    CombatFx.RaiseRocketExplode(ufx.LastPos);
                }
                m_Units.Remove(e);
            }

            // ---- rockets: trail while alive, explosion when the ghost goes away ----
            var seenRockets = new NativeHashSet<Entity>(8, Allocator.Temp);
            foreach (var (rocket, ltw, e) in SystemAPI.Query<RefRO<Rocket>, RefRO<LocalToWorld>>().WithEntityAccess())
            {
                seenRockets.Add(e);
                if (!m_Rockets.TryGetValue(e, out var r) || r.trail == null)
                {
                    var go = new GameObject("RocketTrail");
                    var tr = go.AddComponent<TrailRenderer>();
                    tr.sharedMaterial = Mat();
                    tr.material.color = new Color(0.85f, 0.85f, 0.8f, 0.8f);
                    tr.time = 0.9f; tr.widthMultiplier = 0.6f;
                    tr.widthCurve = AnimationCurve.Linear(0, 1, 1, 0.1f);
                    tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r = (tr, ltw.ValueRO.Position);
                    go.transform.position = ltw.ValueRO.Position;
                }
                r.trail.transform.position = ltw.ValueRO.Position;
                r.pos = ltw.ValueRO.Position;
                m_Rockets[e] = r;
            }
            var goneRockets = new List<Entity>();
            foreach (var kv in m_Rockets) if (!seenRockets.Contains(kv.Key)) goneRockets.Add(kv.Key);
            foreach (var e in goneRockets)
            {
                var r = m_Rockets[e];
                ImpactRing.Spawn(r.pos, hasCfg ? cfg.RocketRadius * 0.6f : 4f);
                ImpactRing.Spawn(r.pos, hasCfg ? cfg.RocketRadius * 0.3f : 2f);
                CombatFx.RaiseRocketExplode(r.pos);
                var d = math.distance(r.pos, localPos);
                if (d < 40f) CombatFx.Shake = math.max(CombatFx.Shake, math.lerp(0.5f, 0.05f, d / 40f));
                if (r.trail != null) { r.trail.emitting = false; Object.Destroy(r.trail.gameObject, r.trail.time); }
                m_Rockets.Remove(e);
            }

            // ---- tracer fade ----
            for (var i = m_Tracers.Count - 1; i >= 0; i--)
            {
                var t = m_Tracers[i];
                if (t.line == null) { m_Tracers.RemoveAt(i); continue; }
                var a = math.saturate((t.until - now) / t.life);
                t.line.widthMultiplier = 0.12f * a;
                if (now >= t.until) { t.line.enabled = false; m_TracerPool.Push(t.line); m_Tracers.RemoveAt(i); }
            }
        }

        void SpawnTracer(float3 from, float3 to, Color color)
        {
            var lr = m_TracerPool.Count > 0 ? m_TracerPool.Pop() : NewLine("Tracer", color, 0.12f);
            lr.enabled = true;
            lr.material.color = color;
            lr.SetPosition(0, from); lr.SetPosition(1, to);
            const float life = 0.09f;
            m_Tracers.Add((lr, (float)SystemAPI.Time.ElapsedTime + life, life));
        }

        void UpdateAntiGod(UnitFx ufx, in ResponseUnit u, float3 pos, uint interpTick, int rate, in OppositionConfig cfg, Entity local, float3 localPos, float now)
        {
            var charging = u.BeamPhase == AntiGodPhase.Charging;
            var firing = u.BeamPhase == AntiGodPhase.Firing;
            if (!charging && !firing || u.Mode == UnitMode.Dead)
            {
                if (ufx.Aim != null) ufx.Aim.enabled = false;
                ufx.FiredSound = false;
                return;
            }
            if (ufx.Aim == null) ufx.Aim = NewLine("AntiGodBeam", new Color(1f, 0.1f, 0.08f), 0.1f);
            var lr = ufx.Aim;
            lr.enabled = true;
            var from = pos + math.normalizesafe(u.AimPoint - pos) * 5f;
            lr.SetPosition(0, from);
            lr.SetPosition(1, u.AimPoint);

            var chargeTicks = math.max(1f, cfg.BeamChargeTime * rate);
            var remaining = interpTick != 0 ? (int)(u.PhaseEndTick - interpTick) / (float)rate : 0f;
            var progress = charging ? math.saturate(1f - remaining * rate / chargeTicks) : 1f;
            var locked = charging && remaining <= cfg.BeamAimLockTime;
            if (charging)
            {
                // thin flickering line that thickens; solid and brighter once the aim locks
                var flicker = locked ? 1f : 0.6f + 0.4f * math.sin(now * 40f);
                lr.widthMultiplier = math.lerp(0.06f, locked ? 0.45f : 0.25f, progress) * flicker;
                lr.material.color = locked ? new Color(4f, 0.5f, 0.35f) : new Color(1.6f, 0.12f, 0.1f);
                CombatFx.RaiseAntiGodCharge(pos, progress);
                ufx.FiredSound = false;
            }
            else
            {
                lr.widthMultiplier = cfg.BeamRadius * 1.4f * (0.85f + 0.15f * math.sin(now * 60f));
                lr.material.color = new Color(6f, 2.5f, 2f);
                if (!ufx.FiredSound)
                {
                    ufx.FiredSound = true;
                    CombatFx.RaiseAntiGodFire(from, u.AimPoint);
                    ImpactRing.Spawn(u.AimPoint, 3f);
                    var d = math.distance(localPos, u.AimPoint);
                    if (d < 60f) CombatFx.Shake = math.max(CombatFx.Shake, 0.5f);
                }
            }

            if (local != Entity.Null && u.Target == local)
            {
                OppositionView.IncomingBeam = true;
                OppositionView.IncomingProgress = progress;
                OppositionView.IncomingFiring = firing;
                OppositionView.IncomingFrom = pos;
            }
        }
    }
}
