using System;
using Gtamp.Shared.Core;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Where a ragdolling character's limbs actually are, as offsets from its root
    /// position.
    /// <para>
    /// <b>Why a pose has to travel at all.</b> Position, heading and a ragdoll flag
    /// are enough to describe a character who is standing. They are not enough to
    /// describe one who is falling: every machine runs its own physics solver, and
    /// two solvers handed the same starting state diverge within a few frames — a
    /// body that lands face-down against a wall here lands on its back in the road
    /// there. Replicating only the root position hides that, because the root
    /// keeps up while everything attached to it is somewhere else entirely.
    /// </para>
    /// <para>
    /// <b>Why three bones and not the whole skeleton.</b> A GTA V ped has over
    /// eighty bones; sending them all would cost more per ragdolling player than
    /// everything else in this protocol combined. Head and both feet pin the two
    /// ends of the body, which fixes the orientation a root position cannot
    /// express, and the solver's own constraints fill in the rest plausibly. The
    /// technique — correcting a small set of bones with impulses rather than
    /// replicating a skeleton — is the one RAGECOOP-V (MIT) uses; this is an
    /// independent implementation of the same idea, not a copy of their code.
    /// </para>
    /// <para>
    /// <b>What this does not give you.</b> The limbs are pulled toward the reported
    /// positions, not placed at them: the pose is an input to physics, so a client
    /// under load or with a long RTT sees a body that lags and settles rather than
    /// one that matches frame for frame. Arms, spine and head rotation are not
    /// replicated at all and are whatever the local solver produced. This is
    /// visibly better than nothing and visibly short of exact.
    /// </para>
    /// </summary>
    public readonly struct RagdollPose : IEquatable<RagdollPose>
    {
        /// <summary>No pose. What a character who is not ragdolling reports.</summary>
        public static readonly RagdollPose None = default;

        public RagdollPose(NetVector3 head, NetVector3 rightFoot, NetVector3 leftFoot)
        {
            Head = head;
            RightFoot = rightFoot;
            LeftFoot = leftFoot;
        }

        /// <summary>Head bone, relative to the character's root position.</summary>
        public NetVector3 Head { get; }

        public NetVector3 RightFoot { get; }

        public NetVector3 LeftFoot { get; }

        /// <summary>
        /// True when nothing is being reported. Distinguishing this from a real pose
        /// matters: an all-zero pose would otherwise be read as "all three bones are
        /// exactly at the root", which is a physically impossible instruction that
        /// would fold the body into a point.
        /// </summary>
        public bool IsNone =>
            Head.Equals(NetVector3.Zero)
            && RightFoot.Equals(NetVector3.Zero)
            && LeftFoot.Equals(NetVector3.Zero);

        public static RagdollPose Lerp(RagdollPose from, RagdollPose to, float t)
        {
            // A pose that has just appeared or just gone has nothing to blend with:
            // interpolating from "none" would drag the limbs out of the root.
            if (from.IsNone || to.IsNone)
            {
                return to;
            }

            return new RagdollPose(
                NetVector3.Lerp(from.Head, to.Head, t),
                NetVector3.Lerp(from.RightFoot, to.RightFoot, t),
                NetVector3.Lerp(from.LeftFoot, to.LeftFoot, t));
        }

        public void Write(NetWriter writer)
        {
            writer.WriteBoneOffset(Head);
            writer.WriteBoneOffset(RightFoot);
            writer.WriteBoneOffset(LeftFoot);
        }

        public static RagdollPose Read(NetReader reader)
        {
            NetVector3 head = reader.ReadBoneOffset();
            NetVector3 rightFoot = reader.ReadBoneOffset();
            NetVector3 leftFoot = reader.ReadBoneOffset();
            return new RagdollPose(head, rightFoot, leftFoot);
        }

        public bool Equals(RagdollPose other) =>
            Head.Equals(other.Head) && RightFoot.Equals(other.RightFoot) && LeftFoot.Equals(other.LeftFoot);

        public override bool Equals(object? obj) => obj is RagdollPose other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Head.GetHashCode();
                hash = (hash * 397) ^ RightFoot.GetHashCode();
                return (hash * 397) ^ LeftFoot.GetHashCode();
            }
        }

        public static bool operator ==(RagdollPose a, RagdollPose b) => a.Equals(b);

        public static bool operator !=(RagdollPose a, RagdollPose b) => !a.Equals(b);

        public override string ToString() => IsNone ? "none" : $"head {Head} rf {RightFoot} lf {LeftFoot}";
    }
}
