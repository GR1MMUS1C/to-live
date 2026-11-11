using Unity.Entities;
using UnityEngine;

namespace ToLive.Rival
{
    /// <summary>
    /// ALL rival tuning: three difficulty presets plus shared ranges and timings. Lives on
    /// "Rival Config" in the District subscene. The rival plays with the same character, moves and
    /// numbers as a player; these only change how it decides.
    /// </summary>
    [DisallowMultipleComponent]
    public class RivalConfigAuthoring : MonoBehaviour
    {
        [System.Serializable]
        public class Preset
        {
            [Tooltip("Seconds before it reacts to what it sees.")] public float ReactionTime = 0.35f;
            [Tooltip("Chance to guard an attack it noticed in time.")] [Range(0f, 1f)] public float GuardChance = 0.45f;
            [Tooltip("Chance per decision to dash in for a chain.")] [Range(0f, 1f)] public float Aggression = 0.55f;
            [Tooltip("Seconds between decisions.")] public float ThinkInterval = 0.4f;
            [Range(0f, 1f)] public float BeamChance = 0.35f;
            [Tooltip("Chance to chase a launched target for a finisher.")] [Range(0f, 1f)] public float FinisherChance = 0.7f;
            [Tooltip("Chance to grab a staggered target.")] [Range(0f, 1f)] public float GrabChance = 0.6f;
            [Tooltip("Aim error in degrees.")] public float AimNoise = 5f;

            public RivalPreset Bake() => new RivalPreset
            {
                ReactionTime = ReactionTime, GuardChance = GuardChance, Aggression = Aggression, ThinkInterval = ThinkInterval,
                BeamChance = BeamChance, FinisherChance = FinisherChance, GrabChance = GrabChance, AimNoise = AimNoise,
            };
        }

        [Header("Difficulty presets (the HUD / debug key picks one; normal by default)")]
        public Preset Easy = new Preset { ReactionTime = 0.55f, GuardChance = 0.2f, Aggression = 0.35f, ThinkInterval = 0.6f, BeamChance = 0.2f, FinisherChance = 0.4f, GrabChance = 0.4f, AimNoise = 10f };
        public Preset Normal = new Preset();
        public Preset Hard = new Preset { ReactionTime = 0.22f, GuardChance = 0.7f, Aggression = 0.8f, ThinkInterval = 0.25f, BeamChance = 0.5f, FinisherChance = 0.95f, GrabChance = 0.85f, AimNoise = 2.5f };

        [Header("Ranges")]
        [Tooltip("Distance it circles at while waiting for an opening.")] public float CircleRange = 12f;
        [Tooltip("Dashes in for a chain from within this distance.")] public float DashInRange = 18f;
        [Tooltip("Ignores players further than this.")] public float EngageRange = 400f;

        [Header("Self-preservation")]
        [Tooltip("Backs off below this fraction of its max power.")] [Range(0f, 1f)] public float DisengagePower = 0.25f;
        public float DisengageTime = 3f;
        [Tooltip("Starts its ritual (meter full) when below this fraction of its power, or when the fight is going well.")] [Range(0f, 1f)] public float RitualPowerThreshold = 0.5f;

        [Header("Moves")]
        public float BeamMinRange = 22f;
        public float BeamMaxRange = 75f;
        public float BeamHoldTime = 1.6f;
        public float BeamCooldown = 5f;
        [Tooltip("How long it carries a grabbed target before throwing.")] public float CarryTime = 0.8f;
        public float HeavyChargeTime = 0.5f;

        [Header("Identity")]
        [Tooltip("Knocked-out rival returns after this many seconds (longer than a player).")] public float RespawnDelay = 12f;
        [Tooltip("-1 trust .. +1 dominance (flipped to the opposite of player 1's if that leans the same way).")] [Range(-1f, 1f)] public float Alignment = 0.8f;
        [Tooltip("Index of the player spawn point it (re)spawns at.")] public int SpawnPoint = 1;

        class Baker : Baker<RivalConfigAuthoring>
        {
            public override void Bake(RivalConfigAuthoring a)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new RivalConfig
                {
                    Easy = a.Easy.Bake(), Normal = a.Normal.Bake(), Hard = a.Hard.Bake(),
                    CircleRange = a.CircleRange, DashInRange = a.DashInRange, EngageRange = a.EngageRange,
                    DisengagePower = a.DisengagePower, DisengageTime = a.DisengageTime, RitualPowerThreshold = a.RitualPowerThreshold,
                    BeamMinRange = a.BeamMinRange, BeamMaxRange = a.BeamMaxRange, BeamHoldTime = a.BeamHoldTime, BeamCooldown = a.BeamCooldown,
                    CarryTime = a.CarryTime, HeavyChargeTime = a.HeavyChargeTime,
                    RespawnDelay = a.RespawnDelay, Alignment = a.Alignment, SpawnPoint = a.SpawnPoint,
                });
            }
        }
    }
}
