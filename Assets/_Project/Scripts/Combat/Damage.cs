using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ToLive.Combat
{
    /// <summary>
    /// The ONE place that subtracts power. Every damage path (strikes, impacts, beam, throw,
    /// eruption, …) goes through <see cref="Apply"/>, so reaching zero always performs the same
    /// knock-out transition (the respawn system only picks up <see cref="CombatAction.KnockedOut"/>).
    /// Pure functions of the state + tick, so they're safe in the prediction loop.
    /// </summary>
    public static class Damage
    {
        /// <summary>
        /// Subtracts <paramref name="amount"/>. Returns true if this knocked the target out.
        /// Call it AFTER setting any launch/stagger state for the hit, so a knock-out wins.
        /// A target that is already knocked out takes no further damage.
        /// </summary>
        public static bool Apply(ref CombatState c, float amount, uint tick, int tickRate, float respawnDelay)
        {
            if (c.Action == CombatAction.KnockedOut) return false;
            c.Power = math.max(0f, c.Power - math.max(0f, amount));
            if (c.Power > 0f) return false;
            KnockOut(ref c, tick, tickRate, respawnDelay);
            return true;
        }

        /// <summary>The knock-out transition: falls as a ballistic body, respawns after the delay.</summary>
        public static void KnockOut(ref CombatState c, uint tick, int tickRate, float respawnDelay)
        {
            c.Power = 0f;
            c.Action = CombatAction.KnockedOut;
            c.ActionStartTick = tick;
            c.ActionEndTick = tick + CombatSystem.Ticks(respawnDelay > 0f ? respawnDelay : 4f, tickRate);
            c.Ballistic = 1;
            c.LockTarget = Entity.Null;
            c.ComboStep = 0; c.QueuedLight = 0; c.Charge = 0f; c.Guarding = 0;
        }

        /// <summary>The simulation tick rate of a world (ClientServerTickRate, default 60).</summary>
        public static int TickRate(EntityManager em)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (q.CalculateEntityCount() == 1)
            {
                var r = q.GetSingleton<ClientServerTickRate>().SimulationTickRate;
                if (r > 0) return r;
            }
            return 60;
        }

        /// <summary>Configured respawn delay (CombatConfig), default 4 s.</summary>
        public static float RespawnDelay(EntityManager em)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CombatConfig>());
            return q.CalculateEntityCount() == 1 ? q.GetSingleton<CombatConfig>().RespawnDelay : 4f;
        }
    }
}
