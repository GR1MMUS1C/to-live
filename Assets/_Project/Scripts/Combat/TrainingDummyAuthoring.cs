using ToLive.Player;
using Unity.Entities;
using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>
    /// The training dummy prefab (a server-simulated, interpolated ghost). It uses the same
    /// flight + combat state as a player so it can be hit, launched and smashed through buildings,
    /// but it has no input: it just hovers and brakes.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrainingDummyAuthoring : MonoBehaviour
    {
        public GameObject Body;
        public GameObject VisualPivot;

        class Baker : Baker<TrainingDummyAuthoring>
        {
            public override void Bake(TrainingDummyAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<TrainingDummy>(entity);
                AddComponent<FlightState>(entity);
                AddComponent(entity, new CombatState { Power = 100f });
                AddComponent<CombatTarget>(entity);
                AddComponent<SupersonicFxState>(entity);
                AddBuffer<PositionHistory>(entity);
                AddComponent<HitFxState>(entity);
                AddComponent(entity, new ToLive.Power.PowerState { Tier = 1, BeamEnergy = 1f });
                AddComponent<ToLive.Power.MoveState>(entity);
                AddComponent<ToLive.Power.LastAttacker>(entity);
                AddComponent(entity, new PlayerVisual
                {
                    Body = authoring.Body != null ? GetEntity(authoring.Body, TransformUsageFlags.Dynamic) : Entity.Null,
                    Pivot = authoring.VisualPivot != null ? GetEntity(authoring.VisualPivot, TransformUsageFlags.Dynamic) : Entity.Null,
                });
            }
        }
    }
}
