using ToLive.Combat;
using ToLive.Player;
using Unity.Entities;
using UnityEngine;

namespace ToLive.Opposition
{
    /// <summary>
    /// A response unit prefab (police drone, gunship, anti-god weapon): a server-simulated,
    /// interpolated ghost. It carries the same character components as the training dummy, so every
    /// existing move works on it (strike, launch, finisher, beam, grab, throw, smash through chunks).
    /// </summary>
    [DisallowMultipleComponent]
    public class ResponseUnitAuthoring : MonoBehaviour
    {
        [Tooltip("1 police, 2 gunship, 3 anti-god")] public byte Kind = UnitKind.Police;
        [Tooltip("Child with the tinted body mesh (flashes white when hit).")] public GameObject Body;
        [Tooltip("Child that turns toward the aim (rocket pods / beam barrel). Optional.")] public GameObject Aim;

        class Baker : Baker<ResponseUnitAuthoring>
        {
            public override void Bake(ResponseUnitAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(e, new ResponseUnit { Kind = a.Kind });
                AddComponent<UnitBrain>(e);
                AddComponent<UnitDrive>(e);
                AddComponent<FlightState>(e);
                AddComponent(e, new CombatState { Power = 30f });
                AddComponent<CombatTarget>(e);
                AddComponent<SupersonicFxState>(e);
                AddBuffer<PositionHistory>(e);
                AddComponent<HitFxState>(e);
                AddComponent(e, new ToLive.Power.PowerState { Tier = 1 });
                AddComponent<ToLive.Power.MoveState>(e);
                AddComponent<ToLive.Power.LastAttacker>(e);
                var body = a.Body != null ? GetEntity(a.Body, TransformUsageFlags.Dynamic) : Entity.Null;
                AddComponent(e, new UnitVisual { Body = body, Aim = a.Aim != null ? GetEntity(a.Aim, TransformUsageFlags.Dynamic) : Entity.Null });
            }
        }
    }
}
