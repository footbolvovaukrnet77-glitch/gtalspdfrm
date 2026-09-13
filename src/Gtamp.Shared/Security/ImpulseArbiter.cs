using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.Security
{
    public enum ImpulseVerdict : byte
    {
        Accepted = 0,

        /// <summary>The entity named does not exist on the server.</summary>
        RejectedNoEntity = 1,

        /// <summary>A push of zero, or one made of infinities.</summary>
        RejectedNotANumber = 2,

        /// <summary>Beyond anything the game itself produces; reduced rather than dropped.</summary>
        Clamped = 3,

        /// <summary>The sender is nowhere near the thing it claims to have pushed.</summary>
        RejectedOutOfRange = 4,

        /// <summary>Players are pushed only by the server, never by another client.</summary>
        RejectedNotPushable = 5,
    }

    public readonly struct ImpulseResolution
    {
        public ImpulseResolution(ImpulseVerdict verdict, NetVector3 impulse, string detail)
        {
            Verdict = verdict;
            Impulse = impulse;
            Detail = detail;
        }

        public ImpulseVerdict Verdict { get; }

        /// <summary>What the server will relay: the claim, possibly reduced.</summary>
        public NetVector3 Impulse { get; }

        public string Detail { get; }

        public bool Accepted => Verdict == ImpulseVerdict.Accepted || Verdict == ImpulseVerdict.Clamped;
    }

    /// <summary>
    /// Decides whether one client may shove an entity, and how hard.
    /// <para>
    /// Section 12 of the specification asks for forces and impulses to be
    /// synchronised, and says what to do when a piece of GTA V's physics cannot be run
    /// on the server: client physics, server validation, server state, correction. It
    /// adds, in as many words, not to give up on physics because it is hard.
    /// </para>
    /// <para>
    /// This is that arrangement for the one part of physics that is an <em>event</em>
    /// rather than a state. A push has no lasting record: a car that was shoved and a
    /// car that was driven end up as the same position and the same velocity, so
    /// replicating the state replicates the result and loses the shove. On the machine
    /// that did it the car leaps; everywhere else it slides to where it landed. The
    /// impulse travels so that every machine's own physics produces the same leap, and
    /// the owner's position remains authoritative and corrects whatever drift the
    /// separate simulations leave behind.
    /// </para>
    /// <para>
    /// A player is never pushed by another client. Being moved is the one thing a
    /// player must not be able to do to another player, and an impulse is exactly that
    /// with a physics engine in between. Server-side mods can push a player; a client
    /// cannot.
    /// </para>
    /// </summary>
    public static class ImpulseArbiter
    {
        /// <summary>
        /// Ceiling on one push, in newton-seconds per unit mass.
        /// <para>
        /// Well above what a collision or an explosion produces and far below what
        /// sends a car into orbit. Clamped rather than rejected, for the same reason
        /// damage is: dropping an over-claim lets a modified client suppress a real one
        /// by exaggerating it.
        /// </para>
        /// </summary>
        public const float MaxMagnitude = 400f;

        /// <summary>How far from the pusher the thing being pushed may be, in metres.</summary>
        public const float MaxRange = 60f;

        public static ImpulseResolution Resolve(
            NetEntity? target, NetVector3 pusherPosition, NetVector3 impulse, bool fromServerSideMod)
        {
            if (target == null)
            {
                return new ImpulseResolution(ImpulseVerdict.RejectedNoEntity, NetVector3.Zero, "no such entity");
            }

            if (!IsFinite(impulse) || impulse.LengthSquared <= 0.0001f)
            {
                return new ImpulseResolution(
                    ImpulseVerdict.RejectedNotANumber, NetVector3.Zero, "the impulse is zero or not a number");
            }

            if (!fromServerSideMod && target.Type == EntityType.Player)
            {
                return new ImpulseResolution(
                    ImpulseVerdict.RejectedNotPushable,
                    NetVector3.Zero,
                    "a client may not push a player");
            }

            if (!fromServerSideMod
                && NetVector3.DistanceSquared(pusherPosition, target.Position) > MaxRange * MaxRange)
            {
                return new ImpulseResolution(
                    ImpulseVerdict.RejectedOutOfRange,
                    NetVector3.Zero,
                    $"the target is {NetVector3.Distance(pusherPosition, target.Position):0.#} m away");
            }

            float length = impulse.Length;
            if (length > MaxMagnitude)
            {
                float scale = MaxMagnitude / length;
                var reduced = new NetVector3(impulse.X * scale, impulse.Y * scale, impulse.Z * scale);
                return new ImpulseResolution(
                    ImpulseVerdict.Clamped, reduced, $"claimed {length:0}, clamped to {MaxMagnitude:0}");
            }

            return new ImpulseResolution(ImpulseVerdict.Accepted, impulse, string.Empty);
        }

        private static bool IsFinite(NetVector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X)
            && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
            && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }
}
