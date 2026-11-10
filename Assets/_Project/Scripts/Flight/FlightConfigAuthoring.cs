using Unity.Entities;
using UnityEngine;

namespace ToLive.Flight
{
    /// <summary>
    /// ALL flight tuning lives here. It sits on the "Flight Config" object in the District subscene.
    /// Select it in the Hierarchy (open the subscene) and tweak values in the Inspector; with the
    /// subscene open you can even tweak them during Play Mode (Unity re-bakes live).
    /// Think of it as a Data Asset in Unreal.
    /// </summary>
    [DisallowMultipleComponent]
    public class FlightConfigAuthoring : MonoBehaviour
    {
        [Header("Hover (default gear)")]
        [Tooltip("Top speed in m/s.")] public float HoverMaxSpeed = 15f;
        [Tooltip("How fast you reach the stick's target speed (m/s²). Higher = snappier.")] public float HoverAcceleration = 60f;
        [Tooltip("Extra braking when you let go of the stick (per second). Higher = stops more precisely.")] public float HoverDamping = 6f;
        [Tooltip("Braking (m/s²) when dropping from cruise/boost into hover.")] public float HoverOverspeedDeceleration = 120f;
        [Tooltip("How fast the body turns to face the camera direction (deg/s).")] public float HoverTurnRate = 540f;

        [Header("Cruise (hold thrust)")]
        public float CruiseMaxSpeed = 80f;
        [Tooltip("m/s²")] public float CruiseAcceleration = 45f;
        [Tooltip("m/s² when easing off the trigger.")] public float CruiseDeceleration = 30f;
        [Tooltip("Max turn rate toward the camera direction (deg/s).")] public float CruiseTurnRate = 110f;

        [Header("Boost (cruise + hold boost)")]
        [Tooltip("Speed of sound is ~343 m/s.")] public float BoostMaxSpeed = 400f;
        [Tooltip("m/s²")] public float BoostAcceleration = 110f;
        [Tooltip("m/s² decay back to cruise speed after releasing boost.")] public float BoostDecay = 140f;
        [Tooltip("Max turn rate while boosting (deg/s).")] public float BoostTurnRate = 28f;

        [Header("Banking & input")]
        [Tooltip("Max roll angle into turns (degrees).")] public float MaxBankAngle = 50f;
        [Tooltip("How quickly the bank follows the turn (per second).")] public float BankResponsiveness = 5f;
        [Tooltip("Thrust below this counts as released.")] [Range(0f, 0.5f)] public float ThrustDeadzone = 0.15f;

        [Header("Events")]
        public float SupersonicSpeed = 343f;

        [Header("Collision")]
        [Tooltip("Radius of the sphere used to sweep the hero through the city.")] public float CollisionRadius = 0.7f;
        public float CollisionSkin = 0.03f;

        class Baker : Baker<FlightConfigAuthoring>
        {
            public override void Bake(FlightConfigAuthoring a)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new FlightConfig
                {
                    HoverMaxSpeed = a.HoverMaxSpeed,
                    HoverAcceleration = a.HoverAcceleration,
                    HoverDamping = a.HoverDamping,
                    HoverOverspeedDeceleration = a.HoverOverspeedDeceleration,
                    HoverTurnRate = a.HoverTurnRate,
                    CruiseMaxSpeed = a.CruiseMaxSpeed,
                    CruiseAcceleration = a.CruiseAcceleration,
                    CruiseDeceleration = a.CruiseDeceleration,
                    CruiseTurnRate = a.CruiseTurnRate,
                    BoostMaxSpeed = a.BoostMaxSpeed,
                    BoostAcceleration = a.BoostAcceleration,
                    BoostDecay = a.BoostDecay,
                    BoostTurnRate = a.BoostTurnRate,
                    MaxBankAngle = a.MaxBankAngle,
                    BankResponsiveness = a.BankResponsiveness,
                    ThrustDeadzone = a.ThrustDeadzone,
                    SupersonicSpeed = a.SupersonicSpeed,
                    CollisionRadius = a.CollisionRadius,
                    CollisionSkin = a.CollisionSkin,
                });
            }
        }
    }
}
