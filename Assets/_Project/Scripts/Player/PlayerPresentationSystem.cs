using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>
    /// Client-only cosmetics: tints each player's body by NetworkId and fires the placeholder
    /// "crossed supersonic" effect. Runs in the presentation group (like Tick in an Unreal
    /// cosmetic component), never affects the simulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class PlayerPresentationSystem : SystemBase
    {
        static readonly float4[] s_Palette =
        {
            new float4(0.95f, 0.45f, 0.15f, 1f), // orange
            new float4(0.20f, 0.60f, 1.00f, 1f), // blue
            new float4(0.35f, 0.85f, 0.35f, 1f), // green
            new float4(0.85f, 0.30f, 0.85f, 1f), // magenta
        };

        public static Color ColorFor(int networkId)
        {
            var c = s_Palette[(math.max(networkId, 1) - 1) % s_Palette.Length];
            return new Color(c.x, c.y, c.z, 1f);
        }

        protected override void OnUpdate()
        {
            var colors = GetComponentLookup<URPMaterialPropertyBaseColor>();
            foreach (var (owner, visual) in SystemAPI.Query<RefRO<GhostOwner>, RefRW<PlayerVisual>>())
            {
                if (visual.ValueRO.ColorApplied != 0 || !colors.HasComponent(visual.ValueRO.Body)) continue;
                var c = s_Palette[(math.max(owner.ValueRO.NetworkId, 1) - 1) % s_Palette.Length];
                colors[visual.ValueRO.Body] = new URPMaterialPropertyBaseColor { Value = c };
                visual.ValueRW.ColorApplied = 1;
            }

            // Flying pose: lean the visual pivot forward with speed (superhero pose in cruise/boost).
            var transforms = GetComponentLookup<LocalTransform>();
            var dt = SystemAPI.Time.DeltaTime;
            foreach (var (flight, visual, ltw) in SystemAPI.Query<RefRO<FlightState>, RefRW<PlayerVisual>, RefRO<LocalToWorld>>())
            {
                if (!transforms.HasComponent(visual.ValueRO.Pivot)) continue;
                var f = flight.ValueRO;
                var forwardSpeed = math.dot(f.Velocity, ltw.ValueRO.Forward);
                var target = f.Gear == FlightGear.Hover ? math.clamp(forwardSpeed / 15f, -1f, 1f) * 20f : 80f;
                var lean = math.lerp(visual.ValueRO.Lean, target, 1f - math.exp(-6f * dt));
                visual.ValueRW.Lean = lean;
                var t = transforms[visual.ValueRO.Pivot];
                t.Rotation = quaternion.RotateX(math.radians(lean));
                transforms[visual.ValueRO.Pivot] = t;
            }

            foreach (var (flight, fx, ltw) in SystemAPI.Query<RefRO<FlightState>, RefRW<SupersonicFxState>, RefRO<LocalToWorld>>())
            {
                var now = flight.ValueRO.Supersonic;
                if (now != 0 && fx.ValueRO.WasSupersonic == 0)
                {
                    Debug.Log($"[ToLive] Crossed supersonic at {ltw.ValueRO.Position}");
                    SupersonicRing.Spawn(ltw.ValueRO.Position, ltw.ValueRO.Forward);
                }
                fx.ValueRW.WasSupersonic = now;
            }
        }
    }
}
