using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>One one-shot task to issue on a remote ped this frame.</summary>
    public enum PostureTask : byte
    {
        None = 0,

        /// <summary>TASK_JUMP.</summary>
        Jump = 1,

        /// <summary>TASK_CLIMB.</summary>
        Climb = 2,

        /// <summary>TASK_PARACHUTE.</summary>
        OpenParachute = 3,

        /// <summary>The parachute state ended; clear the task so the ped lands normally.</summary>
        EndParachute = 4,
    }

    /// <summary>
    /// Decides which posture task a remote ped should be given, from the transition
    /// between the flags it had last frame and the flags it has now.
    /// <para>
    /// These three flags — jump, climb, parachute — travelled from the first commit
    /// and were applied by nothing, which is documented in ENTITY_SYSTEM.md with a
    /// reason each. The reason given for jump and climb was that they are transitions
    /// a second or two long and arrive too late to be worth issuing. That is an
    /// argument about latency and it does not survive the numbers: the interpolation
    /// delay is 120 ms and a jump is close to a second, so the flag lands with most of
    /// the arc still ahead of it. What was actually wrong was issuing the task *while*
    /// the flag was set, every frame, which restarts it continuously — so it is issued
    /// on the transition into the flag and not again until the flag has gone away.
    /// </para>
    /// <para>
    /// Falling stays unapplied on purpose and always will: a ped with nothing under it
    /// falls by itself, and forcing it would fight the game's own physics with a copy
    /// of the same physics a hundred milliseconds behind.
    /// </para>
    /// <para>
    /// Two of the remaining flags are NOT here and are not oversights.
    /// <c>Melee</c> needs the entity being struck and only the flag is replicated;
    /// issuing a melee task without a target makes the ped swing at the air in a
    /// direction nobody chose. <c>InCover</c> needs a cover point in the world rather
    /// than a state of the ped, and a guessed one pins the ped to the wrong wall. Both
    /// need a field that does not exist yet, and inventing the missing half locally is
    /// how a replicated state ends up looking worse than not replicating it.
    /// </para>
    /// </summary>
    public static class PostureDirector
    {
        /// <summary>
        /// The task to issue, given the flags applied last frame and the flags now.
        /// <para>
        /// <paramref name="settled"/> is false while the ped is being placed rather
        /// than walked. A hard correction teleports the ped, and a jump started on the
        /// frame it is teleported is a jump from the wrong place — so the transition is
        /// swallowed rather than queued: by the time the ped has settled the player has
        /// finished jumping anyway.
        /// </para>
        /// </summary>
        public static PostureTask Decide(PlayerFlags previous, PlayerFlags current, bool settled)
        {
            bool wasParachuting = (previous & PlayerFlags.Parachuting) != 0;
            bool parachuting = (current & PlayerFlags.Parachuting) != 0;

            // Ending the canopy is applied even mid-correction: a ped left with an open
            // parachute it no longer has drifts down a street it should be standing in.
            if (wasParachuting && !parachuting)
            {
                return PostureTask.EndParachute;
            }

            if (!settled)
            {
                return PostureTask.None;
            }

            if (parachuting && !wasParachuting)
            {
                return PostureTask.OpenParachute;
            }

            // While a parachute is open the ped is in the air and neither of the two
            // below can mean anything.
            if (parachuting)
            {
                return PostureTask.None;
            }

            if ((current & PlayerFlags.Climbing) != 0 && (previous & PlayerFlags.Climbing) == 0)
            {
                return PostureTask.Climb;
            }

            if ((current & PlayerFlags.Jumping) != 0 && (previous & PlayerFlags.Jumping) == 0)
            {
                return PostureTask.Jump;
            }

            return PostureTask.None;
        }

        /// <summary>
        /// True while the ped should be left to the water rather than tasked.
        /// <para>
        /// A ped whose target coordinate is in water swims to it without being told,
        /// so swimming needs nothing applied. What it does need is for the locomotion
        /// task not to be re-issued as a walk every time the destination moves, which
        /// makes a swimmer stutter on the surface.
        /// </para>
        /// </summary>
        public static bool IsInWater(PlayerFlags flags) =>
            (flags & (PlayerFlags.Swimming | PlayerFlags.Diving)) != 0;
    }
}
