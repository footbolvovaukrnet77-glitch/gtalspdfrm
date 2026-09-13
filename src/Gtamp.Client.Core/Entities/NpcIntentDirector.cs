using Gtamp.Shared.Entities;

namespace Gtamp.Client.Entities
{
    /// <summary>What a networked NPC should be told to do, in the game's own terms.</summary>
    public enum NpcIntent : byte
    {
        /// <summary>Nothing to issue; the ped is driven by position and gait alone.</summary>
        None = 0,

        /// <summary>Fight the replicated combat target.</summary>
        Fight = 1,

        /// <summary>Run away from the replicated combat target, or from wherever it was.</summary>
        Flee = 2,

        /// <summary>Hands up and stay there.</summary>
        Surrender = 3,

        /// <summary>Hands up, and cuffed with it.</summary>
        Arrest = 4,

        /// <summary>Play the replicated scenario where it stands.</summary>
        Scenario = 5,
    }

    /// <summary>
    /// Turns a networked NPC's replicated behaviour into one instruction for the game.
    /// <para>
    /// <b>Why this shape and not a server-side AI.</b> Section 11 of the specification
    /// asks for fleeing, chasing, attacking, surrender and arrest to be synchronised,
    /// and the roadmap recorded them as "the AI itself, not state about it" — which was
    /// true of a design that never existed and not true of the one that does. GTA V has
    /// the AI: it knows the pavements, the cover, the doors and the crowd. What it does
    /// not have is agreement between machines about who is fleeing from whom.
    /// </para>
    /// <para>
    /// So the server owns the <em>intent</em> and every client's game executes it. The
    /// server could not path a ped if it wanted to — it has no navigation mesh and
    /// never will — and a ped whose every footstep was dictated from a server would
    /// walk through walls the client can see. This is the division that makes the
    /// engine's own AI usable rather than something to be replaced.
    /// </para>
    /// <para>
    /// One intent at a time, and the order is a priority: arrest beats surrender beats
    /// flight beats a fight beats a scenario. A ped in handcuffs is not also picking a
    /// fight, and issuing both makes a ped that does neither.
    /// </para>
    /// </summary>
    public static class NpcIntentDirector
    {
        /// <summary>
        /// The instruction for this NPC, or <see cref="NpcIntent.None"/>.
        /// </summary>
        /// <param name="targetResolved">
        /// Whether the combat target names something this client has actually built.
        /// A fight or a flight without one is not issued: a ped told to fight nobody
        /// attacks in a direction nobody chose, which is worse than standing still.
        /// </param>
        public static NpcIntent Decide(PedEntity ped, bool targetResolved)
        {
            if (ped.Health <= 0 || ped.HasFlag(PlayerFlags.Dead))
            {
                return NpcIntent.None;
            }

            if (ped.HasBehaviour(PedBehaviourFlags.Arrested) || ped.HasBehaviour(PedBehaviourFlags.Cuffed))
            {
                return NpcIntent.Arrest;
            }

            if (ped.HasBehaviour(PedBehaviourFlags.Surrendered))
            {
                return NpcIntent.Surrender;
            }

            if (ped.HasBehaviour(PedBehaviourFlags.Fleeing))
            {
                return targetResolved ? NpcIntent.Flee : NpcIntent.None;
            }

            bool fighting = ped.HasBehaviour(PedBehaviourFlags.Attacking)
                || ped.HasBehaviour(PedBehaviourFlags.Chasing)
                || ped.HasBehaviour(PedBehaviourFlags.InCombat);

            if (fighting)
            {
                return targetResolved ? NpcIntent.Fight : NpcIntent.None;
            }

            return ped.ScenarioHash != 0 ? NpcIntent.Scenario : NpcIntent.None;
        }
    }
}
