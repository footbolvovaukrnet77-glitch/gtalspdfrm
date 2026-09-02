namespace Gtamp.Server.Players
{
    /// <summary>
    /// How many gunshots one player may have relayed to everyone else, per second.
    /// <para>
    /// A shot message is small and unreliable, but it is <em>fanned out</em>: one
    /// packet in becomes one packet per nearby player out. That makes it the cheapest
    /// amplification in the protocol, and a client is free to send at whatever rate it
    /// likes. The budget bounds the damage rather than detecting the intent.
    /// </para>
    /// <para>
    /// <b>Why over-budget is dropped and not punished.</b> The ceiling has to clear
    /// the fastest weapon in the game — a minigun runs near 50 rounds a second — so
    /// the gap between "legitimate sustained fire" and "abuse" is a factor of two, not
    /// of a hundred. Counting a violation there would eventually kick a player for
    /// owning a minigun. What is lost by dropping instead is a muzzle flash: damage is
    /// arbitrated from a separate report against the server's own world, so a dropped
    /// shot message costs visuals and nothing else.
    /// </para>
    /// </summary>
    public sealed class ShotBudget
    {
        /// <summary>Sustained rounds per second. Comfortably above a minigun, far below a flood.</summary>
        public const double RoundsPerSecond = 80d;

        /// <summary>
        /// Burst allowance. A weapon that fires several rounds inside one client frame
        /// reports them together, and a player who has not fired for a while should
        /// not have their first burst clipped.
        /// </summary>
        public const double Burst = 20d;

        private double _tokens = Burst;
        private double _lastRefill;
        private bool _started;

        /// <summary>Tokens currently available. Diagnostic; the admin console prints it.</summary>
        public double Available => _tokens;

        /// <summary>Shots this session has had dropped for being over budget.</summary>
        public long Dropped { get; private set; }

        /// <summary>
        /// True when this shot may be relayed. <paramref name="now"/> is the server
        /// clock in seconds.
        /// </summary>
        public bool TryTake(double now)
        {
            if (!_started)
            {
                _started = true;
                _lastRefill = now;
            }

            double elapsed = now - _lastRefill;
            if (elapsed > 0d)
            {
                _lastRefill = now;
                _tokens += elapsed * RoundsPerSecond;
                if (_tokens > Burst)
                {
                    _tokens = Burst;
                }
            }

            if (_tokens < 1d)
            {
                Dropped++;
                return false;
            }

            _tokens -= 1d;
            return true;
        }
    }
}
