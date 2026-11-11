using System.Text;
using ToLive.CameraRig;
using ToLive.Input;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Developer helper for automated play-mode checks (used from the Unity CLI with `script eval`).
    /// Not used by the game. It can fake gameplay input and report every player's state per world.
    /// </summary>
    public static class DevProbe
    {
        /// <summary>One line per player per world: id, position, gear, speed, supersonic.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<GhostOwner>());
                using var entities = q.ToEntityArray(Allocator.Temp);
                using var conns = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                sb.Append($"{world.Name}: connections={conns.CalculateEntityCount()} players={entities.Length}");
                if (world.IsClient())
                {
                    using var ack = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>());
                    if (ack.CalculateEntityCount() == 1) sb.Append($" rtt={ack.GetSingleton<NetworkSnapshotAck>().EstimatedRTT:0}ms");
                }
                sb.AppendLine();
                foreach (var e in entities)
                {
                    var f = em.GetComponentData<FlightState>(e);
                    var t = em.GetComponentData<LocalTransform>(e);
                    var owner = em.GetComponentData<GhostOwner>(e);
                    var local = em.HasComponent<GhostOwnerIsLocal>(e) && em.IsComponentEnabled<GhostOwnerIsLocal>(e);
                    var predicted = em.HasComponent<PredictedGhost>(e);
                    sb.AppendLine($"  player {owner.NetworkId}{(local ? " (local)" : "")}{(predicted ? " predicted" : " interpolated")} pos={Fmt(t.Position)} gear={FlightGear.Name(f.Gear)} speed={math.length(f.Velocity):0.0} yaw={f.Yaw:0} pitch={f.Pitch:0} bank={f.Bank:0} supersonic={f.Supersonic}");
                }
            }
            sb.AppendLine($"camera: yaw={FlightCameraRig.Yaw:0} pitch={FlightCameraRig.Pitch:0} hasLocal={FlightCameraRig.HasLocalPlayer} speed={FlightCameraRig.LocalSpeed:0.0}");
            var cam = Camera.main;
            if (cam != null) sb.AppendLine($"main camera pos={cam.transform.position} fov={cam.fieldOfView:0.0}");
            return sb.ToString();
        }

        /// <summary>Overrides device input until <see cref="ReleaseInput"/> is called.</summary>
        public static string FakeInput(float moveX, float moveY, float vertical, float thrust, bool boost, float yaw, float pitch)
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = false; // stop it overwriting the statics
            InputReader.Move = new Vector2(moveX, moveY);
            InputReader.Vertical = vertical;
            InputReader.Thrust = thrust;
            InputReader.Boost = boost;
            InputReader.LookStick = Vector2.zero;
            InputReader.LookDelta = Vector2.zero;
            FlightCameraRig.Yaw = yaw;
            FlightCameraRig.Pitch = pitch;
            GameplayInputGate.Blocked = false;
            return "input faked";
        }

        /// <summary>Fake combat buttons: presses are one-shot, holds stay until changed.</summary>
        public static string FakeCombat(bool light, bool dash, bool lockOn, bool heavyHeld, bool guardHeld)
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = false;
            if (light) InputReader.LightPresses++;
            if (dash) InputReader.DashPresses++;
            if (lockOn) InputReader.LockOnPresses++;
            InputReader.HeavyHeld = heavyHeld;
            InputReader.GuardHeld = guardHeld;
            GameplayInputGate.Blocked = false;
            return "combat input faked";
        }

        static string s_Sequence;
        static int s_SequenceIndex;
        static double s_NextPressTime, s_Interval;
        public static string LastSequenceLog = "";

        /// <summary>
        /// Plays a button sequence in real time, one step every <paramref name="interval"/> seconds.
        /// L = light press, D = dash press, K = lock-on press, H = start holding heavy, h = release heavy,
        /// G = hold guard, g = release guard, . = wait. After each step the combat state is logged.
        /// </summary>
        public static string PlaySequence(string sequence, float interval)
        {
            FakeCombat(false, false, false, false, false);
            s_Sequence = sequence; s_SequenceIndex = 0; s_Interval = interval;
            s_NextPressTime = EditorApplication.timeSinceStartup;
            LastSequenceLog = "";
            EditorApplication.update -= SequenceTick;
            EditorApplication.update += SequenceTick;
            return "playing " + sequence;
        }

        /// <summary>Same as <see cref="PlaySequence"/> but digits (CLI-quoting friendly):
        /// 0 wait, 1 light, 2 dash, 3 lock, 4 hold heavy, 5 release heavy, 6 hold guard, 7 release guard, 8 grab/throw.</summary>
        public static string PlayCode(string digits, float interval)
        {
            var map = ".LDKHhGgP";
            var sb = new StringBuilder();
            foreach (var d in digits) sb.Append(d >= '0' && d <= '8' ? map[d - '0'] : '.');
            return PlaySequence(sb.ToString(), interval);
        }

        static void SequenceTick()
        {
            if (EditorApplication.timeSinceStartup < s_NextPressTime) return;
            if (s_SequenceIndex >= s_Sequence.Length) { EditorApplication.update -= SequenceTick; return; }
            var ch = s_Sequence[s_SequenceIndex++];
            switch (ch)
            {
                case 'L': InputReader.LightPresses++; break;
                case 'D': InputReader.DashPresses++; break;
                case 'K': InputReader.LockOnPresses++; break;
                case 'H': InputReader.HeavyHeld = true; break;
                case 'h': InputReader.HeavyHeld = false; break;
                case 'G': InputReader.GuardHeld = true; break;
                case 'g': InputReader.GuardHeld = false; break;
                case 'P': InputReader.GrabPresses++; break;
            }
            s_NextPressTime = EditorApplication.timeSinceStartup + s_Interval;
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                using var q = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Combat.CombatState>(), ComponentType.ReadOnly<ToLive.Combat.TrainingDummy>(), ComponentType.ReadOnly<LocalTransform>());
                using var es = q.ToEntityArray(Allocator.Temp);
                foreach (var e in es)
                {
                    var c = world.EntityManager.GetComponentData<ToLive.Combat.CombatState>(e);
                    var p = world.EntityManager.GetComponentData<LocalTransform>(e).Position;
                    LastSequenceLog += $"{ch}: dummy power={c.Power:0} action={ToLive.Combat.CombatAction.Name(c.Action)} pos={Fmt(p)}\n";
                }
            }
        }

        static int s_FilmCount, s_FilmIndex;
        static double s_FilmNext, s_FilmInterval;
        static string s_FilmPrefix;

        /// <summary>
        /// Captures <paramref name="count"/> Game-view screenshots every <paramref name="interval"/> s
        /// to Logs/{prefix}_N.png, timed inside the Editor (no CLI latency).
        /// </summary>
        public static string Film(int prefixId, int count, float interval)
        {
            s_FilmPrefix = "film" + prefixId; s_FilmCount = count; s_FilmIndex = 0; s_FilmInterval = interval;
            s_FilmNext = EditorApplication.timeSinceStartup;
            EditorApplication.update -= FilmTick;
            EditorApplication.update += FilmTick;
            return "filming";
        }

        static void FilmTick()
        {
            if (s_FilmIndex >= s_FilmCount) { EditorApplication.update -= FilmTick; return; }
            if (EditorApplication.timeSinceStartup < s_FilmNext) return;
            ScreenCapture.CaptureScreenshot(System.IO.Path.GetFullPath($"Logs/{s_FilmPrefix}_{s_FilmIndex}.png"));
            s_FilmIndex++;
            s_FilmNext += s_FilmInterval;
        }

        public static string HaloDebug()
        {
            var sb = new StringBuilder();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
            {
                if (r.gameObject.name != "Shard") continue;
                sb.AppendLine($"{r.transform.parent.name} active={r.gameObject.activeInHierarchy} mat={(r.sharedMaterial == null ? "null" : r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name)} pos={r.transform.position} scale={r.transform.lossyScale}");
                if (sb.Length > 1500) break;
            }
            return sb.ToString();
        }

        /// <summary>Fake the milestone-3 inputs (holds stay until changed; grab is a press).</summary>
        public static string FakePower(bool beamHeld, bool ritualHeld, bool grabPress)
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = false;
            InputReader.BeamHeld = beamHeld;
            InputReader.RitualHeld = ritualHeld;
            if (grabPress) InputReader.GrabPresses++;
            GameplayInputGate.Blocked = false;
            return "power input faked";
        }

        /// <summary>Power state (tier, alignment, meter, phase, beam, grab) of every character per world.</summary>
        public static string PowerReport()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Power.PowerState>(), ComponentType.ReadOnly<ToLive.Power.MoveState>(),
                    ComponentType.ReadOnly<ToLive.Combat.CombatState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<FlightState>());
                using var es = q.ToEntityArray(Allocator.Temp);
                sb.AppendLine(world.Name + ":");
                foreach (var e in es)
                {
                    var p = em.GetComponentData<ToLive.Power.PowerState>(e);
                    var m = em.GetComponentData<ToLive.Power.MoveState>(e);
                    var c = em.GetComponentData<ToLive.Combat.CombatState>(e);
                    var t = em.GetComponentData<LocalTransform>(e);
                    var f = em.GetComponentData<FlightState>(e);
                    var who = em.HasComponent<ToLive.Combat.TrainingDummy>(e) ? "dummy" : (em.HasComponent<GhostOwner>(e) ? $"player{em.GetComponentData<GhostOwner>(e).NetworkId}" : "?");
                    sb.AppendLine($"  {who} tier={p.Tier} align={p.Alignment:0.00} meter={p.Meter:0.00} fuel={p.Fuel:0} phase={ToLive.Power.SurgePhase.Name(p.Phase)}({p.Phase}) erupted={p.Erupted} beam={m.Beam} energy={p.BeamEnergy:0.00} carrying={(m.Carrying == Entity.Null ? "-" : "yes")} grabbedBy={(m.GrabbedBy == Entity.Null ? "-" : "yes")} mash={m.Mash} power={c.Power:0} action={ToLive.Combat.CombatAction.Name(c.Action)} pos={Fmt(t.Position)} speed={math.length(f.Velocity):0.0}");
                }
            }
            return sb.ToString();
        }

        /// <summary>Combat state of every character in every world.</summary>
        public static string CombatReport()
        {
            var sb = new StringBuilder();
            foreach (var world in World.All)
            {
                if (!world.IsCreated || (!world.IsClient() && !world.IsServer())) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Combat.CombatState>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<FlightState>());
                using var entities = q.ToEntityArray(Allocator.Temp);
                sb.Append(world.Name).Append(':');
                using (var bq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Destruction.BuildingState>()))
                    if (bq.CalculateEntityCount() == 1)
                    {
                        var bs = bq.GetSingleton<ToLive.Destruction.BuildingState>();
                        sb.Append($" broken={bs.CountBroken()} gen={bs.Generation}");
                    }
                using (var hq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Destruction.ChunkHidden>()))
                    sb.Append($" hiddenChunks={hq.CalculateEntityCount()}");
                sb.AppendLine();
                foreach (var e in entities)
                {
                    var c = em.GetComponentData<ToLive.Combat.CombatState>(e);
                    var t = em.GetComponentData<LocalTransform>(e);
                    var f = em.GetComponentData<FlightState>(e);
                    var who = em.HasComponent<ToLive.Combat.TrainingDummy>(e) ? "dummy" : (em.HasComponent<GhostOwner>(e) ? $"player{em.GetComponentData<GhostOwner>(e).NetworkId}" : "?");
                    sb.AppendLine($"  {who} pos={Fmt(t.Position)} speed={math.length(f.Velocity):0.0} power={c.Power:0} action={ToLive.Combat.CombatAction.Name(c.Action)}({c.Action}) combo={c.ComboStep} lock={(c.LockTarget == Entity.Null ? "-" : "yes")} guard={c.Guarding} charge={c.Charge:0.00} lastHit={c.LastHitTick}");
                }
            }
            sb.AppendLine($"debris live={ToLive.Destruction.DebrisSystem.LiveDebris} total={ToLive.Destruction.DebrisSystem.TotalDebris} fps={ToLive.UI.CombatHud.SmoothedFps:0} ms={ToLive.UI.CombatHud.SmoothedFrameMs:0.0}");
            return sb.ToString();
        }

        /// <summary>Teleports the server-side local player (id 1) next to the dummy, facing it.</summary>
        public static string MoveNearDummy(float distance)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var dq = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Combat.TrainingDummy>(), ComponentType.ReadOnly<LocalTransform>());
                using var pq = em.CreateEntityQuery(ComponentType.ReadWrite<LocalTransform>(), ComponentType.ReadWrite<FlightState>(), ComponentType.ReadOnly<GhostOwner>());
                using var dummies = dq.ToEntityArray(Allocator.Temp);
                using var players = pq.ToEntityArray(Allocator.Temp);
                if (dummies.Length == 0 || players.Length == 0) return "no dummy or player";
                var dp = em.GetComponentData<LocalTransform>(dummies[0]).Position;
                var home = em.GetComponentData<ToLive.Combat.TrainingDummy>(dummies[0]);
                var away = math.normalizesafe(math.mul(quaternion.RotateY(math.radians(home.HomeYaw)), new float3(0, 0, 1)));
                var p = dp + away * distance;
                var yaw = math.degrees(math.atan2(-away.x, -away.z));
                em.SetComponentData(players[0], LocalTransform.FromPositionRotation(p, quaternion.RotateY(math.radians(yaw))));
                em.SetComponentData(players[0], new FlightState { Yaw = yaw });
                FlightCameraRig.Yaw = yaw;
                FlightCameraRig.Pitch = 0f;
                return $"moved to {Fmt(p)} yaw {yaw:0}";
            }
            return "no server";
        }

        /// <summary>Server: break every ground-floor chunk, which should make the rest collapse.</summary>
        public static string BreakGroundFloor()
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Destruction.BuildingGraph>());
                if (q.CalculateEntityCount() != 1) return "no building";
                var g = q.GetSingletonEntity();
                var nodes = em.GetBuffer<ToLive.Destruction.ChunkNode>(g).ToNativeArray(Allocator.Temp);
                var req = em.GetBuffer<ToLive.Destruction.BreakRequest>(g);
                var n = 0;
                for (var i = 0; i < nodes.Length; i++)
                    if (nodes[i].Grounded != 0) { req.Add(new ToLive.Destruction.BreakRequest { Chunk = i, Velocity = new float3(0, 0, -20) }); n++; }
                return $"requested {n} grounded chunks";
            }
            return "no server";
        }

        public static string Teleport(float x, float y, float z, float yaw, float pitch = 0f, float speed = 0f)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var pq = em.CreateEntityQuery(ComponentType.ReadWrite<LocalTransform>(), ComponentType.ReadWrite<FlightState>(), ComponentType.ReadOnly<GhostOwner>());
                using var players = pq.ToEntityArray(Allocator.Temp);
                if (players.Length == 0) return "no player";
                em.SetComponentData(players[0], LocalTransform.FromPositionRotation(new float3(x, y, z), quaternion.RotateY(math.radians(yaw))));
                var dir = math.mul(quaternion.EulerZXY(math.radians(new float3(pitch, yaw, 0))), new float3(0, 0, 1));
                em.SetComponentData(players[0], new FlightState { Yaw = yaw, Pitch = pitch, Velocity = dir * speed });
                FlightCameraRig.Yaw = yaw; FlightCameraRig.Pitch = pitch;
                return "ok";
            }
            return "no server";
        }

        /// <summary>Server: sphere-cast straight down at (x,z) and report what was hit.</summary>
        public static string ProbeDown(float x, float z)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<Unity.Physics.PhysicsWorldSingleton>());
                q.CompleteDependency();
                var pw = q.GetSingleton<Unity.Physics.PhysicsWorldSingleton>();
                if (!pw.SphereCast(new float3(x, 300f, z), 0.7f, new float3(0, -1, 0), 400f, out var hit, Unity.Physics.CollisionFilter.Default))
                    return "no hit";
                var isChunk = em.HasComponent<ToLive.Destruction.BuildingChunk>(hit.Entity);
                return $"hit {hit.Entity} at y={hit.Position.y:0.0} chunk={isChunk} bodies={pw.NumBodies}";
            }
            return "no server";
        }

        public static string TraceLog = "";
        static double s_TraceEnd;
        static string s_LastTraceLine;

        /// <summary>Records server player 1 height/speed/gear every editor frame for a few seconds.</summary>
        public static string StartTrace(float seconds)
        {
            TraceLog = "";
            s_TraceEnd = EditorApplication.timeSinceStartup + seconds;
            EditorApplication.update -= TraceTick;
            EditorApplication.update += TraceTick;
            return "tracing";
        }

        static void TraceTick()
        {
            if (EditorApplication.timeSinceStartup > s_TraceEnd) { EditorApplication.update -= TraceTick; return; }
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var pq = em.CreateEntityQuery(ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<GhostOwner>());
                using var players = pq.ToEntityArray(Allocator.Temp);
                if (players.Length == 0) return;
                var t = em.GetComponentData<LocalTransform>(players[0]);
                var f = em.GetComponentData<FlightState>(players[0]);
                var c = em.HasComponent<ToLive.Combat.CombatState>(players[0]) ? em.GetComponentData<ToLive.Combat.CombatState>(players[0]) : default;
                var line = $"power={c.Power:0} action={ToLive.Combat.CombatAction.Name(c.Action)} y={t.Position.y:0} v={math.length(f.Velocity):0}";
                if (s_LastTraceLine != line) TraceLog += $"{EditorApplication.timeSinceStartup:0.00}s {line}\n";
                s_LastTraceLine = line;
            }
        }

        public static string SetPlayerPower(float power)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadWrite<ToLive.Combat.CombatState>(), ComponentType.ReadOnly<GhostOwner>());
                using var es = q.ToEntityArray(Allocator.Temp);
                if (es.Length == 0) return "no player";
                var c = em.GetComponentData<ToLive.Combat.CombatState>(es[0]);
                c.Power = power;
                em.SetComponentData(es[0], c);
                return $"player power {power}";
            }
            return "no server";
        }

        /// <summary>Sends a debug RPC from the Nth client world in this process (0 = the host's own client).</summary>
        public static string SendDebugFromClient(int clientIndex, int command)
        {
            var clients = ClientServerBootstrap.ClientWorlds;
            if (clientIndex >= clients.Count) return "no such client";
            var em = clients[clientIndex].EntityManager;
            var e = em.CreateEntity(typeof(ToLive.Combat.DebugCommandRequest), typeof(SendRpcCommandRequest));
            em.SetComponentData(e, new ToLive.Combat.DebugCommandRequest { Command = (byte)command });
            return $"sent {command} from {clients[clientIndex].Name}";
        }

        /// <summary>Is the camera behind the local player, looking toward the lock target?</summary>
        public static string CameraLockCheck()
        {
            var cam = Camera.main;
            if (cam == null || !FlightCameraRig.HasLockTarget) return "no camera or no lock";
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsClient()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<GhostOwnerIsLocal>());
                using var es = q.ToEntityArray(Allocator.Temp);
                if (es.Length == 0) continue;
                var player = em.GetComponentData<LocalToWorld>(es[0]).Position;
                var target = FlightCameraRig.LockTargetPosition;
                var camPos = (float3)cam.transform.position;
                var toTarget = math.normalizesafe(target - player);
                var camToPlayer = math.normalizesafe(player - camPos);
                var lookDot = math.dot((float3)cam.transform.forward, math.normalizesafe(target - camPos));
                return $"behind={math.dot(camToPlayer, toTarget):0.00} (1 = directly behind) lookAtTarget={lookDot:0.00} (1 = target centred) camYaw={FlightCameraRig.Yaw:0}";
            }
            return "no local player";
        }

        public static string SetDummyPower(float power)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadWrite<ToLive.Combat.CombatState>(), ComponentType.ReadOnly<ToLive.Combat.TrainingDummy>());
                using var es = q.ToEntityArray(Allocator.Temp);
                foreach (var e in es)
                {
                    var c = em.GetComponentData<ToLive.Combat.CombatState>(e);
                    c.Power = power;
                    em.SetComponentData(e, c);
                }
                return $"dummy power {power}";
            }
            return "no server";
        }

        /// <summary>Server: break the first <paramref name="count"/> wall chunks (for sync tests).</summary>
        public static string BreakSome(int count)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ToLive.Destruction.BuildingGraph>());
                if (q.CalculateEntityCount() != 1) return "no building";
                var g = q.GetSingletonEntity();
                var nodes = em.GetBuffer<ToLive.Destruction.ChunkNode>(g).ToNativeArray(Allocator.Temp);
                var req = em.GetBuffer<ToLive.Destruction.BreakRequest>(g);
                var n = 0;
                // pick high chunks so it doesn't collapse everything
                for (var i = nodes.Length - 1; i >= 0 && n < count; i--)
                    if (nodes[i].Grounded == 0) { req.Add(new ToLive.Destruction.BreakRequest { Chunk = i, Velocity = new float3(0, 0, -15) }); n++; }
                return $"requested {n}";
            }
            return "no server";
        }

        /// <summary>Puts the local player at a viewpoint looking at the breakable building.</summary>
        public static string ViewBuilding(float distance, float height, float angleDeg = -1f)
        {
            var center = ToLive.Destruction.DebrisSystem.BuildingCenter;
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var pq = em.CreateEntityQuery(ComponentType.ReadWrite<LocalTransform>(), ComponentType.ReadWrite<FlightState>(), ComponentType.ReadOnly<GhostOwner>());
                using var players = pq.ToEntityArray(Allocator.Temp);
                if (players.Length == 0) return "no player";
                var dir = angleDeg < 0f ? math.normalizesafe(new float3(-58f - center.x, 0, -58f - center.z))
                                        : new float3(math.sin(math.radians(angleDeg)), 0, math.cos(math.radians(angleDeg)));
                var p = new float3(center.x, height, center.z) + dir * distance;
                var to = center - p;
                var yaw = math.degrees(math.atan2(to.x, to.z));
                em.SetComponentData(players[0], LocalTransform.FromPositionRotation(p, quaternion.RotateY(math.radians(yaw))));
                em.SetComponentData(players[0], new FlightState { Yaw = yaw });
                FlightCameraRig.Yaw = yaw;
                FlightCameraRig.Pitch = -math.degrees(math.atan2(to.y, math.length(to.xz)));
                return $"viewpoint {Fmt(p)}";
            }
            return "no server";
        }

        public static string StartBenchmark(int cap, int mult = 1, bool ritual = false) { ToLive.UI.PerfBenchmark.StartInEditor(cap, System.IO.Path.GetFullPath("Logs/bench-editor.csv"), mult, ritual); return "started"; }

        public static string ReleaseInput()
        {
            var reader = Object.FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = true;
            return "input released";
        }

        /// <summary>
        /// Adds a second, full client world in this same process and connects it to the local host,
        /// so replication can be checked without a second Editor. (Both clients read the same
        /// keyboard/gamepad input, so this is only for automated checks.)
        /// </summary>
        public static string AddSecondClient()
        {
            var ui = Object.FindAnyObjectByType<ToLive.UI.GameUI>();
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld2");
            Unity.Scenes.SceneSystem.LoadSceneAsync(client.Unmanaged, ui.GameplayScene.SceneGUID);
            var connect = client.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
            client.EntityManager.SetComponentData(connect, new NetworkStreamRequestConnect
            {
                Endpoint = Unity.Networking.Transport.NetworkEndpoint.LoopbackIpv4.WithPort(ToLive.Net.NetSession.DefaultPort)
            });
            return "second client connecting";
        }

        static string Fmt(float3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
    }
}
