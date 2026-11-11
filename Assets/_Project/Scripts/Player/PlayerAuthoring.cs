using Unity.Entities;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>
    /// Put on the Player prefab root (next to the Ghost Authoring Component).
    /// Authoring components + Bakers are how GameObjects become ECS data: the Baker runs in the
    /// editor and writes plain components, a bit like Unreal cooking a Blueprint's defaults.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerAuthoring : MonoBehaviour
    {
        [Tooltip("Child that has the body MeshRenderer and a URP Material Property Base Color component.")]
        public GameObject Body;

        [Tooltip("Child that holds the visible meshes; leaned forward at speed for a flying pose.")]
        public GameObject VisualPivot;

        class Baker : Baker<PlayerAuthoring>
        {
            public override void Bake(PlayerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<PlayerInput>(entity);
                AddComponent<FlightState>(entity);
                AddComponent<SupersonicFxState>(entity);
                AddComponent(entity, new ToLive.Combat.CombatState { Power = 100f });
                AddComponent<ToLive.Combat.CombatTarget>(entity);
                AddBuffer<ToLive.Combat.PositionHistory>(entity);
                AddComponent<ToLive.Combat.HitFxState>(entity);
                AddComponent(entity, new ToLive.Power.PowerState { Tier = 1, BeamEnergy = 1f });
                AddComponent<ToLive.Power.MoveState>(entity);
                AddComponent<ToLive.Power.LastAttacker>(entity);
                // Milestone 3B: human or AI rival (same prefab), heat blame and the rival's brain (server only)
                AddComponent<ToLive.Opposition.CharacterKind>(entity);
                AddComponent<ToLive.Opposition.HeatBlame>(entity);
                AddComponent<ToLive.Rival.RivalBrain>(entity);
                AddComponent(entity, new PlayerVisual
                {
                    Body = authoring.Body != null ? GetEntity(authoring.Body, TransformUsageFlags.Dynamic) : Entity.Null,
                    Pivot = authoring.VisualPivot != null ? GetEntity(authoring.VisualPivot, TransformUsageFlags.Dynamic) : Entity.Null,
                });
            }
        }
    }
}
