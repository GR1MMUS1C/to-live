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
            CompleteDependency();
            // Body colour every frame: player colour (or dummy red), white flash when hit, grey when knocked out.
            var colors = GetComponentLookup<URPMaterialPropertyBaseColor>();
            var owners = GetComponentLookup<GhostOwner>(true);
            var combats = GetComponentLookup<ToLive.Combat.CombatState>(true);
            var fxs = GetComponentLookup<ToLive.Combat.HitFxState>(true);
            var kinds = GetComponentLookup<ToLive.Opposition.CharacterKind>(true);
            var now = SystemAPI.Time.ElapsedTime;
            foreach (var (visual, entity) in SystemAPI.Query<RefRO<PlayerVisual>>().WithEntityAccess())
            {
                if (!colors.HasComponent(visual.ValueRO.Body)) continue;
                var c = owners.HasComponent(entity)
                    ? s_Palette[(math.max(owners[entity].NetworkId, 1) - 1) % s_Palette.Length]
                    : new float4(0.85f, 0.18f, 0.15f, 1f); // training dummy
                if (kinds.HasComponent(entity) && kinds[entity].Kind == ToLive.Opposition.CharacterKind.Rival)
                    c = new float4(0.42f, 0.1f, 0.62f, 1f); // the AI rival: deep violet
                if (combats.HasComponent(entity) && combats[entity].Action == ToLive.Combat.CombatAction.KnockedOut)
                    c = new float4(0.3f, 0.3f, 0.32f, 1f);
                if (fxs.HasComponent(entity) && fxs[entity].FlashUntil > now)
                    c = new float4(1f, 1f, 1f, 1f);
                colors[visual.ValueRO.Body] = new URPMaterialPropertyBaseColor { Value = c };
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
                var supersonic = flight.ValueRO.Supersonic;
                if (supersonic != 0 && fx.ValueRO.WasSupersonic == 0)
                {
                    Debug.Log($"[ToLive] Crossed supersonic at {ltw.ValueRO.Position}");
                    SupersonicRing.Spawn(ltw.ValueRO.Position, ltw.ValueRO.Forward);
                }
                fx.ValueRW.WasSupersonic = supersonic;
            }
        }
    }
}
