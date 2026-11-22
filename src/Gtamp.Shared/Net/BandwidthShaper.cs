using System;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Adjusts a client's snapshot byte budget from what the link is actually doing.
    /// <para>
    /// A fixed budget serves the best-connected player and drowns the worst. Loss on
    /// a UDP link is usually congestion — the sender is putting more through than
    /// the path will carry — so the response is to send less, and to creep back up
    /// while the link stays clean. Additive increase, multiplicative decrease: the
    /// same shape TCP uses, for the same reason. It backs off fast enough to relieve
    /// congestion and recovers slowly enough not to re-cause it.
    /// </para>
    /// <para>
    /// This only changes <em>how much</em> is sent per snapshot. It never changes
    /// what the server keeps, and it never drops an entity permanently — a smaller
    /// budget means more entities are deferred to later snapshots, so a congested
    /// client converges more slowly and still converges.
    /// </para>
    /// </summary>
    public sealed class BandwidthShaper
    {
        /// <summary>Loss above this is treated as congestion and the budget is cut.</summary>
        public const double CongestionLossThreshold = 0.08;

        /// <summary>Loss below this is a clean link and the budget creeps back up.</summary>
        public const double HealthyLossThreshold = 0.02;

        public const double DecreaseFactor = 0.75;

        /// <summary>Fraction of the maximum added per healthy interval.</summary>
        public const double IncreaseFraction = 0.1;

        // An explicit flag rather than "_lastAdjustment <= 0": server time starts at
        // zero, so a zero timestamp is a real time, not an uninitialised one.
        private bool _initialised;
        private double _lastAdjustment;
        private int _lastPacketsSent;
        private int _lastPacketsLost;

        public BandwidthShaper(int maximumBudget, int minimumBudget = 256, double intervalSeconds = 1.0)
        {
            if (minimumBudget > maximumBudget)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumBudget), "The minimum budget cannot exceed the maximum.");
            }

            MaximumBudget = maximumBudget;
            MinimumBudget = minimumBudget;
            IntervalSeconds = intervalSeconds;
            CurrentBudget = maximumBudget;
        }

        public int MaximumBudget { get; }

        public int MinimumBudget { get; }

        public double IntervalSeconds { get; }

        public int CurrentBudget { get; private set; }

        public int Decreases { get; private set; }

        public int Increases { get; private set; }

        /// <summary>Loss measured over the most recent interval, not since the session began.</summary>
        public double RecentLoss { get; private set; }

        /// <summary>Reconsiders the budget from a peer's live counters.</summary>
        public void Update(NetStats stats, double now) => Update(stats.PacketsSent, stats.PacketsLost, now);

        /// <summary>
        /// Reconsiders the budget. Safe to call every tick; it only acts once per
        /// interval. Takes the raw cumulative counters rather than a stats object so it
        /// can be driven directly, which is also what makes it testable without
        /// fabricating a peer.
        /// </summary>
        public void Update(int packetsSent, int packetsLost, double now)
        {
            if (!_initialised)
            {
                _initialised = true;
                _lastAdjustment = now;
                _lastPacketsSent = packetsSent;
                _lastPacketsLost = packetsLost;
                return;
            }

            if (now - _lastAdjustment < IntervalSeconds)
            {
                return;
            }

            int sent = packetsSent - _lastPacketsSent;
            int lost = packetsLost - _lastPacketsLost;

            _lastAdjustment = now;
            _lastPacketsSent = packetsSent;
            _lastPacketsLost = packetsLost;

            // Too few packets to say anything about the link.
            if (sent < 5)
            {
                return;
            }

            RecentLoss = (double)lost / sent;

            if (RecentLoss > CongestionLossThreshold)
            {
                int reduced = (int)(CurrentBudget * DecreaseFactor);
                CurrentBudget = Math.Max(MinimumBudget, reduced);
                Decreases++;
                return;
            }

            if (RecentLoss <= HealthyLossThreshold && CurrentBudget < MaximumBudget)
            {
                int step = Math.Max(64, (int)(MaximumBudget * IncreaseFraction));
                CurrentBudget = Math.Min(MaximumBudget, CurrentBudget + step);
                Increases++;
            }
        }

        public void Reset()
        {
            CurrentBudget = MaximumBudget;
            _initialised = false;
            _lastAdjustment = 0;
            _lastPacketsSent = 0;
            _lastPacketsLost = 0;
            RecentLoss = 0;
        }
    }
}
