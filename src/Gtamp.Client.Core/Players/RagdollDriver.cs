using System;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>Which limbs a correction actually asks for this frame.</summary>
    [Flags]
    public enum RagdollBones : byte
    {
        None = 0,
        Head = 1,
        RightFoot = 2,
        LeftFoot = 4,
    }

    /// <summary>The impulses to hand the physics solver for one ragdolling ped this frame.</summary>
    public readonly struct RagdollCorrection
    {
        public RagdollCorrection(RagdollBones applied, NetVector3 head, NetVector3 rightFoot, NetVector3 leftFoot)
        {
            Applied = applied;
            Head = head;
            RightFoot = rightFoot;
            LeftFoot = leftFoot;
        }

        /// <summary>Only the limbs named here have a meaningful impulse; the rest are zero.</summary>
        public RagdollBones Applied { get; }

        public NetVector3 Head { get; }

        public NetVector3 RightFoot { get; }

        public NetVector3 LeftFoot { get; }

        public bool IsEmpty => Applied == RagdollBones.None;

        public bool Has(RagdollBones bone) => (Applied & bone) != 0;
    }

    /// <summary>
    /// Turns a replicated <see cref="RagdollPose"/> into the impulses that pull a
    /// local ragdoll toward it.
    /// <para>
    /// <b>Why impulses and not placement.</b> A ragdolling ped is owned by the
    /// physics solver. Writing bone positions directly fights it: the solver
    /// re-derives them from its own constraints on the next step, and the result is
    /// the twitching that replicated ragdolls are famous for. Applying a force
    /// proportional to the error leaves the solver in charge and lets it satisfy its
    /// constraints while ending up where it was told — the body still bends at the
    /// joints, still collides with the world, and still lands roughly where the
    /// owner's copy landed.
    /// </para>
    /// <para>
    /// <b>What it costs.</b> Proportional correction is a spring, and a spring
    /// overshoots. The gain here is deliberately low enough that a body settles
    /// rather than oscillates, which means a body far from where it should be takes
    /// a visible second or two to get there. It also means the correction can lose:
    /// a limb pinned under geometry cannot be pulled through it, and the impulse
    /// will keep being applied to no effect until the ragdoll ends and the ped is
    /// placed outright.
    /// </para>
    /// <para>
    /// The technique — correcting head and both feet with Euphoria impulses rather
    /// than replicating a skeleton — is the one RAGECOOP-V (MIT licence) arrived at.
    /// The constants, the deadzone, the settle delay and this implementation are
    /// ours; no code was copied.
    /// </para>
    /// </summary>
    public static class RagdollDriver
    {
        /// <summary>Euphoria part indices. These are the engine's numbering, not ours.</summary>
        public const int HeadPart = 20;

        public const int RightFootPart = 6;

        public const int LeftFootPart = 3;

        /// <summary>Impulse per metre of error. A spring constant, in effect.</summary>
        public const float Gain = 20f;

        /// <summary>
        /// Impulse ceiling. Without it a body that has diverged badly is hit hard
        /// enough to launch it, which is a far worse artefact than the divergence.
        /// </summary>
        public const float MaxImpulse = 50f;

        /// <summary>
        /// Below this error the limb is left alone. Physics is never exact and a
        /// solver nudged every frame never comes to rest — a settled body that
        /// quivers reads as a bug even though it is within centimetres.
        /// </summary>
        public const float Deadzone = 0.08f;

        /// <summary>
        /// Frames to let the local solver run before correcting anything. The first
        /// moments of a fall are the ones the solver is best at and the network is
        /// worst at: the pose in hand describes a body a round-trip old, and pulling
        /// on limbs mid-impact produces a fall that looks nothing like either copy.
        /// </summary>
        public const int SettleFrames = 10;

        /// <summary>
        /// Root error beyond which impulses are hopeless and the ped is placed. Eight
        /// metres of divergence is not a limb out of position, it is two different
        /// falls, and no clamped impulse closes that gap before the ragdoll ends.
        /// </summary>
        public const float PlaceDistance = RemotePedController.HardCorrectDistance;

        /// <summary>True once the local solver has had <see cref="SettleFrames"/> to establish the fall.</summary>
        public static bool ShouldCorrect(int framesRagdolling) => framesRagdolling >= SettleFrames;

        /// <summary>
        /// Computes this frame's impulses. <paramref name="root"/> is where the ped
        /// should be; the bone arguments are where its limbs actually are, in world
        /// space, as the local solver has them.
        /// </summary>
        public static RagdollCorrection Compute(
            in RagdollPose pose,
            NetVector3 root,
            NetVector3 head,
            NetVector3 rightFoot,
            NetVector3 leftFoot)
        {
            // Nothing was reported. An all-zero pose is not "all limbs at the root" —
            // that instruction would fold the body into a point.
            if (pose.IsNone)
            {
                return default;
            }

            RagdollBones applied = RagdollBones.None;

            if (TryImpulse(root + pose.Head, head, out NetVector3 headImpulse))
            {
                applied |= RagdollBones.Head;
            }

            if (TryImpulse(root + pose.RightFoot, rightFoot, out NetVector3 rightImpulse))
            {
                applied |= RagdollBones.RightFoot;
            }

            if (TryImpulse(root + pose.LeftFoot, leftFoot, out NetVector3 leftImpulse))
            {
                applied |= RagdollBones.LeftFoot;
            }

            return new RagdollCorrection(applied, headImpulse, rightImpulse, leftImpulse);
        }

        /// <summary>
        /// The impulse for one limb, or false when the limb is close enough to leave
        /// alone.
        /// </summary>
        public static bool TryImpulse(NetVector3 target, NetVector3 actual, out NetVector3 impulse)
        {
            NetVector3 error = target - actual;
            float distance = error.Length;
            if (distance < Deadzone)
            {
                impulse = NetVector3.Zero;
                return false;
            }

            impulse = error * Gain;
            float magnitude = distance * Gain;
            if (magnitude > MaxImpulse)
            {
                impulse = impulse * (MaxImpulse / magnitude);
            }

            return true;
        }
    }
}
