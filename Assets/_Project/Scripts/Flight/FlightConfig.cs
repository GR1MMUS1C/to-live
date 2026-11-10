using Unity.Entities;


namespace ToLive.Flight
{
    /// <summary>Baked copy of <see cref="FlightConfigAuthoring"/>, readable from Burst systems.</summary>
    public struct FlightConfig : IComponentData
    {
        public float HoverMaxSpeed, HoverAcceleration, HoverDamping, HoverOverspeedDeceleration, HoverTurnRate;
        public float CruiseMaxSpeed, CruiseAcceleration, CruiseDeceleration, CruiseTurnRate;
        public float BoostMaxSpeed, BoostAcceleration, BoostDecay, BoostTurnRate;
        public float MaxBankAngle, BankResponsiveness, ThrustDeadzone;
        public float SupersonicSpeed;
        public float CollisionRadius, CollisionSkin;
    }

}
