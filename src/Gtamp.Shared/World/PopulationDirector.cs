using System.Collections.Generic;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.World
{
    /// <summary>One player, as far as the traffic decision is concerned.</summary>
    public readonly struct PopulationCandidate
    {
        public PopulationCandidate(uint playerId, NetVector3 position, bool wasSource)
        {
            PlayerId = playerId;
            Position = position;
            WasSource = wasSource;
        }

        public uint PlayerId { get; }

        public NetVector3 Position { get; }

        /// <summary>Whether this player was the source on the previous decision. Used for hysteresis.</summary>
        public bool WasSource { get; }
    }

    /// <summary>
    /// Decides which players spawn the ambient traffic everybody else sees.
    /// <para>
    /// Ambient traffic and pedestrians are local to each client in every co-op mod for
    /// this game, and were here too: two players standing on the same corner saw
    /// entirely different cars, which is the plainest possible statement that they are
    /// not in the same world. Section 15 of the specification lists traffic among the
    /// things to synchronise and this is the first half of doing it.
    /// </para>
    /// <para>
    /// <b>Why a client spawns it rather than the server.</b> Traffic follows GTA V's
    /// road network — its nodes, its lanes, its junction rules — and that network is
    /// game data the server does not have and cannot be given. A server that spawned
    /// cars would put them through walls. So the game keeps spawning traffic, on
    /// exactly one machine per group of players, and that machine hands what appeared
    /// to the server, which replicates it to everyone including back to the spawner.
    /// Every other client stops spawning its own.
    /// </para>
    /// <para>
    /// <b>One source per cluster, not one per server.</b> Five players in five cities
    /// are the case the whole architecture exists for (specification section 5). A
    /// single global source would leave four of them on empty streets. So a player is
    /// a source unless somebody with a lower id is close enough to be spawning traffic
    /// they can already see — which yields exactly one source per group and one per
    /// lone player, with no grid, no cells and no boundary to stand on.
    /// </para>
    /// </summary>
    public static class PopulationDirector
    {
        /// <summary>
        /// How close two players have to be for one of them to cover the other, in
        /// metres.
        /// <para>
        /// 400 is a little under FiveM's own focus radius of 424 units, so a car
        /// spawned for the source is still within the distance the other player would
        /// have been shown it at. Larger leaves a gap between the two players where
        /// neither spawns; smaller has both spawning into the same street.
        /// </para>
        /// </summary>
        public const float CoverageRadius = 400f;

        /// <summary>
        /// Extra distance a player who is already the source keeps it over.
        /// <para>
        /// Without it two players walking in and out of each other's radius swap the
        /// role several times a minute, and each swap is a street's worth of cars
        /// deleted and respawned. The role is cheap to keep and expensive to move, so
        /// it is sticky.
        /// </para>
        /// </summary>
        public const float Hysteresis = 60f;

        /// <summary>
        /// Whether <paramref name="candidate"/> should spawn ambient traffic, given
        /// everyone else on the server.
        /// </summary>
        public static bool IsSource(in PopulationCandidate candidate, IReadOnlyList<PopulationCandidate> all)
        {
            float radius = candidate.WasSource ? CoverageRadius + Hysteresis : CoverageRadius;

            for (int i = 0; i < all.Count; i++)
            {
                PopulationCandidate other = all[i];
                if (other.PlayerId == candidate.PlayerId)
                {
                    continue;
                }

                // Only a lower id can take the role, which is what makes the answer
                // stable: two players covering each other must not both stand down,
                // and must not both stand up.
                if (other.PlayerId >= candidate.PlayerId)
                {
                    continue;
                }

                if (NetVector3.Distance(candidate.Position, other.Position) <= radius)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
