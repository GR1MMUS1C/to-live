using Unity.Entities;
using Unity.Mathematics;

namespace ToLive.Combat
{
    /// <summary>
    /// Client-side hooks for hit feedback. Systems raise events here; the camera reads the shake
    /// amount, and audio/VFX can subscribe later (placeholder SFX hooks).
    /// </summary>
    public static class CombatFx
    {
        /// <summary>Current camera shake amplitude (metres); decays in the camera rig.</summary>
        public static float Shake;

        /// <summary>(position, kind) kind: 1-3 light step, 4 heavy, 5 victim hit, 6 chunk break.</summary>
        public static event System.Action<float3, byte> OnImpact;

        public static void RaisePredictedHit(float3 position, byte kind)
        {
            Shake = math.max(Shake, kind >= 3 ? 0.35f : 0.15f);
            ImpactRing.Spawn(position, kind >= 3 ? 1.6f : 1f);
            OnImpact?.Invoke(position, kind);
        }

        /// <summary>SFX hooks for the transformation: rising hum (progress 0..1) and the eruption.</summary>
        public static event System.Action<float3, float> OnRitual;
        public static event System.Action<float3> OnEruption;
        public static void RaiseRitual(float3 position, float progress) => OnRitual?.Invoke(position, progress);
        public static void RaiseEruption(float3 position) => OnEruption?.Invoke(position);

        public static void RaiseVictimHit(float3 position, float strength, bool involvesLocalPlayer)
        {
            if (involvesLocalPlayer) Shake = math.max(Shake, math.clamp(strength / 120f, 0.1f, 0.6f));
            ImpactRing.Spawn(position, math.clamp(strength / 40f, 0.8f, 3f));
            OnImpact?.Invoke(position, 5);
        }
    }

    /// <summary>Client-only bookkeeping for hit effects (not networked).</summary>
    public struct HitFxState : IComponentData
    {
        public uint SeenHitTick;
        public double FlashUntil;
        public byte Flashing;
    }
}
