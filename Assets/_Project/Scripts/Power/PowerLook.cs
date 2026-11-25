using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// The LOOK of power (client-only, cosmetic). Lives on "Power Look" in Main.unity.
    /// Art rule: the world is muted; power is the only saturated colour. Trust and dominance each
    /// have one energy hue; a character's alignment blends between them.
    /// </summary>
    public class PowerLook : MonoBehaviour
    {
        [Header("Energy colours (HDR)")]
        [ColorUsage(false, true)] public Color TrustColor = new Color(0.35f, 0.95f, 1.6f);
        [ColorUsage(false, true)] public Color DominanceColor = new Color(1.45f, 0.08f, 0.38f);

        [Header("Fractured halo")]
        [Tooltip("Shards in the full (surge) halo.")] public int ShardCount = 28;
        [Tooltip("Shards visible in the faint tier-2 halo.")] public int Tier2Shards = 9;
        public float HaloRadius = 1.35f;
        public float ShardLength = 0.75f;
        [Tooltip("Orbit speed (rad/s) for pure trust and pure dominance.")] public float TrustOrbitSpeed = 0.5f, DominanceOrbitSpeed = 2.6f;
        [Tooltip("Glow intensity multiplier: tier-2 idle, ritual peak, surge.")] public float Tier2Glow = 0.8f, RitualGlow = 3.5f, SurgeGlow = 1.8f;

        [Header("Colour wash")]
        [Tooltip("Coloured light cast on the surroundings during ritual/surge.")] public float WashRange = 55f;
        public float WashIntensityRitual = 9f, WashIntensitySurge = 3.5f;

        [Header("Ritual world FX (cosmetic)")]
        public int RubblePieces = 60;
        public float LiftRadius = 16f;
        public float LiftHeight = 4f;
        public float GlassRange = 45f;

        [Header("Hero form (HeroPresenter)")]
        [Tooltip("Alignment (-1 trust .. +1 dominance) at or above which a character wears the dominance model.")]
        [Range(-1f, 1f)] public float DominanceFormAlignment = 0.35f;
        [Tooltip("...and its tier is at least this high (a surge always counts).")]
        public int DominanceFormMinTier = 2;
        [Tooltip("Alignment must drop this far below the threshold before the hero changes back (stops flicker).")]
        public float DominanceFormHysteresis = 0.1f;

        public static PowerLook Instance { get; private set; }
        void OnEnable() => Instance = this;

        public Color EnergyColor(float alignment)
        {
            var t = Mathf.Clamp01((alignment + 1f) * 0.5f);
            return Color.Lerp(TrustColor, DominanceColor, t);
        }
    }
}
