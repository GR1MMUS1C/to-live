using ToLive.Input;
using ToLive.Player;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.CameraRig
{
    /// <summary>
    /// Client-only third-person camera (never networked). Like an Unreal SpringArm + Camera:
    ///  - This script owns the orbit (yaw/pitch from right stick / mouse) and moves the
    ///    <see cref="Target"/> transform onto the local player every frame.
    ///  - A Cinemachine camera with a "Third Person Follow" body follows Target with damping (the lag).
    ///  - Distance and field of view grow with speed; a Unity Physics sphere cast against the city
    ///    shortens the arm so the camera never clips into buildings (the spring arm's collision test).
    /// All tuning values are on this component.
    /// </summary>
    public class FlightCameraRig : MonoBehaviour
    {
        public static float Yaw;
        public static float Pitch = 10f;

        [Header("References")]
        public Transform Target;
        public CinemachineCamera Camera;

        [Header("Orbit")]
        [Tooltip("Right stick turn speed, degrees per second.")] public float StickSensitivity = 180f;
        [Tooltip("Mouse degrees per pixel.")] public float MouseSensitivity = 0.12f;
        public bool InvertY;
        public float MinPitch = -75f, MaxPitch = 80f;

        [Header("Speed response (hover -> full boost)")]
        public float HoverDistance = 6f;
        public float BoostDistance = 14f;
        public float HoverFov = 60f;
        public float BoostFov = 85f;
        [Tooltip("Speed (m/s) at which distance/FOV reach their 'boost' values.")] public float FullEffectSpeed = 400f;
        [Tooltip("Shapes the speed curve (x = speed / FullEffectSpeed).")] public AnimationCurve SpeedCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Tooltip("How quickly distance/FOV follow speed changes (per second).")] public float Responsiveness = 3f;
        [Tooltip("How far (metres) the camera trails behind when moving fast. Small = tight, large = floaty.")] public float LagDistance = 2.5f;
        [Tooltip("Upper limit on Cinemachine follow damping (seconds), used at low speed.")] public float MaxDamping = 0.12f;
        [Tooltip("Pivot offset from the player (up, in metres).")] public float PivotHeight = 1.2f;

        [Header("Lock-on framing")]
        [Tooltip("How fast the camera swings to frame the lock target (per second).")] public float LockTurnSpeed = 6f;
        [Tooltip("Extra pitch (down) while locked, so you see both characters.")] public float LockPitchOffset = 8f;
        [Tooltip("Extra camera distance per metre between you and the target.")] public float LockDistanceFactor = 0.3f;
        public float LockMaxExtraDistance = 10f;

        [Header("Impact shake")]
        [Tooltip("How fast camera shake fades (metres per second).")] public float ShakeDecay = 2.5f;

        [Header("Collision")]
        public float CameraRadius = 0.35f;
        public float MinDistance = 1.5f;

        float m_SpeedT;
        float m_CurrentDistance;
        World m_World;
        EntityQuery m_PlayerQuery, m_PhysicsQuery;
        CinemachineThirdPersonFollow m_Follow;

        public static float LocalSpeed { get; private set; }
        public static byte LocalGear { get; private set; }
        public static bool HasLocalPlayer { get; private set; }
        public static bool HasLockTarget { get; private set; }
        public static float3 LockTargetPosition { get; private set; }
        public static float LockTargetPower;
        public static float LocalPower { get; private set; }
        public static byte LocalAction { get; private set; }
        public static float LocalCharge { get; private set; }
        public static uint LocalKoEndTick { get; private set; }

        void Awake()
        {
            if (Camera != null) m_Follow = Camera.GetComponent<CinemachineThirdPersonFollow>();
            m_CurrentDistance = HoverDistance;
        }

        void Update()
        {
            // Orbit input (only while playing, not in menus).
            if (!GameplayInputGate.Blocked)
            {
                var dt = Time.unscaledDeltaTime;
                var stick = InputReader.LookStick;
                var mouse = InputReader.LookDelta;
                var invert = InvertY ? -1f : 1f;
                Yaw += stick.x * StickSensitivity * dt + mouse.x * MouseSensitivity;
                Pitch -= (stick.y * StickSensitivity * dt + mouse.y * MouseSensitivity) * invert;
                Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
                Yaw = Mathf.Repeat(Yaw + 180f, 360f) - 180f;
            }
        }

        void LateUpdate()
        {
            HasLocalPlayer = TryGetLocalPlayer(out var playerPos, out var velocity, out var gear);
            if (!HasLocalPlayer || Target == null) return;

            LocalSpeed = math.length(velocity);
            LocalGear = gear;

            var dt = Time.deltaTime;
            var t = SpeedCurve.Evaluate(Mathf.Clamp01(LocalSpeed / Mathf.Max(FullEffectSpeed, 1f)));
            m_SpeedT = Mathf.Lerp(m_SpeedT, t, 1f - Mathf.Exp(-Responsiveness * dt));

            var pivot = (Vector3)playerPos + Vector3.up * PivotHeight;

            // Soft lock-on: steer the orbit so the camera frames you and the target (you can still nudge it).
            var lockExtra = 0f;
            HasLockTarget = TryGetLockTarget(out var lockPos, out LockTargetPower);
            if (HasLockTarget)
            {
                var focus = Vector3.Lerp(pivot, (Vector3)lockPos, 0.5f);
                var toFocus = focus - pivot;
                var sep = Vector3.Distance(pivot, lockPos);
                if (toFocus.sqrMagnitude > 0.01f)
                {
                    var look = Quaternion.LookRotation(((Vector3)lockPos - pivot).normalized);
                    var e = look.eulerAngles;
                    var wantPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x) + LockPitchOffset, MinPitch, MaxPitch);
                    var k = 1f - Mathf.Exp(-LockTurnSpeed * dt);
                    Yaw = Mathf.Repeat(Mathf.LerpAngle(Yaw, e.y, k) + 180f, 360f) - 180f;
                    Pitch = Mathf.Lerp(Pitch, wantPitch, k);
                }
                lockExtra = Mathf.Min(sep * LockDistanceFactor, LockMaxExtraDistance);
            }
            LockTargetPosition = lockPos;

            var rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            // Camera shake on impacts (decays quickly).
            var shake = ToLive.Combat.CombatFx.Shake;
            if (shake > 0.001f)
            {
                pivot += (Vector3)UnityEngine.Random.insideUnitSphere * shake;
                ToLive.Combat.CombatFx.Shake = Mathf.MoveTowards(shake, 0f, ShakeDecay * dt);
            }
            Target.SetPositionAndRotation(pivot, rotation);

            // Desired arm length, shortened by collision against the city.
            var desired = Mathf.Lerp(HoverDistance, BoostDistance, m_SpeedT) + lockExtra;
            var allowed = desired;
            if (TryGetPhysics(out var physics) &&
                physics.SphereCast(pivot, CameraRadius, -(rotation * Vector3.forward), desired, out var hit, CollisionFilter.Default))
            {
                allowed = Mathf.Max(MinDistance, hit.Fraction * desired - CameraRadius * 0.5f);
            }
            // Pull in instantly when blocked, ease back out when clear.
            m_CurrentDistance = allowed < m_CurrentDistance ? allowed : Mathf.Lerp(m_CurrentDistance, allowed, 1f - Mathf.Exp(-4f * dt));

            if (m_Follow != null)
            {
                m_Follow.CameraDistance = m_CurrentDistance;
                // Cinemachine damping is time-based, so at 400 m/s even 0.1 s means 40 m of lag.
                // Convert the desired lag in metres into seconds for the current speed.
                var damp = Mathf.Min(MaxDamping, LagDistance / Mathf.Max(LocalSpeed, 1f));
                m_Follow.Damping = new Vector3(damp, damp, damp);
            }
            if (Camera != null)
            {
                var lens = Camera.Lens;
                lens.FieldOfView = Mathf.Lerp(HoverFov, BoostFov, m_SpeedT);
                Camera.Lens = lens;
            }
        }

        bool EnsureQueries()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated) { m_World = null; return false; }
            if (m_World != world)
            {
                m_World = world;
                m_PlayerQuery = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<FlightState>(), ComponentType.ReadOnly<GhostOwnerIsLocal>());
                m_PhysicsQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PhysicsWorldSingleton>());
            }
            return true;
        }

        bool TryGetLocalPlayer(out float3 position, out float3 velocity, out byte gear)
        {
            position = default; velocity = default; gear = 0;
            if (!EnsureQueries()) return false;
            m_PlayerQuery.CompleteDependency();
            // GhostOwnerIsLocal is an enableable tag, so use ToEntityArray (respects enabled bits)
            // instead of GetSingletonEntity (which refuses enableable queries).
            using var entities = m_PlayerQuery.ToEntityArray(Allocator.Temp);
            if (entities.Length != 1) return false;
            var entity = entities[0];
            var em = m_World.EntityManager;
            position = em.GetComponentData<LocalToWorld>(entity).Position;
            var flight = em.GetComponentData<FlightState>(entity);
            velocity = flight.Velocity;
            gear = flight.Gear;
            m_LocalEntity = entity;
            if (em.HasComponent<ToLive.Combat.CombatState>(entity))
            {
                var c = em.GetComponentData<ToLive.Combat.CombatState>(entity);
                LocalPower = c.Power;
                LocalAction = c.Action;
                LocalCharge = c.Charge;
                LocalKoEndTick = c.ActionEndTick;
            }
            return true;
        }

        Entity m_LocalEntity;

        bool TryGetLockTarget(out float3 position, out float power)
        {
            position = default; power = 0f;
            if (m_World == null || !m_World.IsCreated) return false;
            var em = m_World.EntityManager;
            if (!em.Exists(m_LocalEntity) || !em.HasComponent<ToLive.Combat.CombatState>(m_LocalEntity)) return false;
            var target = em.GetComponentData<ToLive.Combat.CombatState>(m_LocalEntity).LockTarget;
            if (target == Entity.Null || !em.Exists(target) || !em.HasComponent<LocalToWorld>(target)) return false;
            position = em.GetComponentData<LocalToWorld>(target).Position;
            if (em.HasComponent<ToLive.Combat.CombatState>(target)) power = em.GetComponentData<ToLive.Combat.CombatState>(target).Power;
            return true;
        }

        bool TryGetPhysics(out PhysicsWorldSingleton physics)
        {
            physics = default;
            if (m_World == null || !m_World.IsCreated || m_PhysicsQuery.CalculateEntityCount() != 1) return false;
            m_PhysicsQuery.CompleteDependency();
            physics = m_PhysicsQuery.GetSingleton<PhysicsWorldSingleton>();
            return physics.NumBodies > 0;
        }
    }
}
