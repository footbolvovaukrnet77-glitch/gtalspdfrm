using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Ragdoll replication: the pose on the wire, and the impulses that chase it.
    /// <para>
    /// The defect these cover is the one a test alone could never have found. The
    /// ragdoll flag replicated correctly from the first phase; what did not
    /// replicate was where the body actually went, because each machine ran its own
    /// solver from the moment the flag arrived and nothing ever compared the
    /// results. Everything was in the right place except the player.
    /// </para>
    /// </summary>
    public class RagdollSyncTests
    {
        private static RagdollPose Pose() => new RagdollPose(
            new NetVector3(0.2f, -0.1f, 0.9f),
            new NetVector3(-0.3f, 0.4f, 0.05f),
            new NetVector3(0.35f, 0.45f, 0.06f));

        [Fact]
        public void APoseSurvivesTheWire()
        {
            var writer = new NetWriter(64);
            RagdollPose sent = Pose();
            sent.Write(writer);

            RagdollPose received = RagdollPose.Read(new NetReader(writer.ToArray()));

            // Quantisation is 1/128 m, so equality is to within half a step.
            Assert.True(NetVector3.Distance(sent.Head, received.Head) < 0.005f);
            Assert.True(NetVector3.Distance(sent.RightFoot, received.RightFoot) < 0.005f);
            Assert.True(NetVector3.Distance(sent.LeftFoot, received.LeftFoot) < 0.005f);
        }

        [Fact]
        public void APoseStaysInsideItsBudget()
        {
            // Eighteen bytes is the worst case the design assumes: two varint bytes an
            // axis at the far edge of the quantiser's range. If a wider quantiser ever
            // creeps in, the ragdoll's share of a snapshot grows without anyone
            // noticing.
            var extreme = new NetWriter(64);
            var farthest = new NetVector3(
                Quantize.BoneOffsetExtent, -Quantize.BoneOffsetExtent, Quantize.BoneOffsetExtent);
            new RagdollPose(farthest, farthest, farthest).Write(extreme);

            Assert.Equal(18, extreme.ToArray().Length);

            // A real pose is well inside it: limbs sit within a metre of the root, and
            // a varint charges for the magnitude it is given.
            var typical = new NetWriter(64);
            Pose().Write(typical);

            Assert.True(typical.ToArray().Length <= 18);
        }

        [Fact]
        public void AnAllZeroPoseIsNotAnInstruction()
        {
            // Zero offsets would read as "every limb is exactly at the root", which
            // folds a body into a point. It has to mean "nothing reported" instead.
            Assert.True(RagdollPose.None.IsNone);
            Assert.False(Pose().IsNone);
        }

        [Fact]
        public void AnUpdateOnlyCarriesAPoseWhileRagdolling()
        {
            // The client update is sent in full twenty times a second, so an
            // unconditional pose is bandwidth spent per player per second on saying
            // "not falling".
            var upright = new ClientStateUpdateMessage { Flags = PlayerFlags.None, Ragdoll = Pose() };
            var uprightWithNothingToSay = new ClientStateUpdateMessage { Flags = PlayerFlags.None };

            // A pose sitting on an upright update costs nothing, because it is not
            // written at all.
            Assert.Equal(uprightWithNothingToSay.Serialize().Length, upright.Serialize().Length);

            var falling = new ClientStateUpdateMessage { Flags = PlayerFlags.Ragdoll, Ragdoll = Pose() };
            var poseAlone = new NetWriter(32);
            Pose().Write(poseAlone);

            // And a falling one costs the pose, plus the one byte the ragdoll flag
            // adds to the flags varint. Nothing else.
            Assert.Equal(
                poseAlone.ToArray().Length + 1,
                falling.Serialize().Length - upright.Serialize().Length);
        }

        [Fact]
        public void AnUpdateRoundTripsThePoseItSent()
        {
            var sent = new ClientStateUpdateMessage
            {
                Flags = PlayerFlags.Ragdoll,
                Health = 120,
                Ragdoll = Pose(),
            };

            ClientStateUpdateMessage received = ClientStateUpdateMessage.Deserialize(sent.Serialize());

            Assert.Equal(PlayerFlags.Ragdoll, received.Flags);
            Assert.Equal(120, received.Health);
            Assert.False(received.Ragdoll.IsNone);
            Assert.True(NetVector3.Distance(sent.Ragdoll.Head, received.Ragdoll.Head) < 0.005f);
        }

        [Fact]
        public void AnUprightUpdateReadsBackWithoutAPose()
        {
            var sent = new ClientStateUpdateMessage { Flags = PlayerFlags.None, Ragdoll = Pose(), Ammo = 42 };

            ClientStateUpdateMessage received = ClientStateUpdateMessage.Deserialize(sent.Serialize());

            // The pose was skipped, and skipping it did not shift everything after it.
            Assert.True(received.Ragdoll.IsNone);
            Assert.Equal(42, received.Ammo);
        }

        [Fact]
        public void APoseTravelsOnTheEntityDelta()
        {
            var baseline = new PlayerEntity(new EntityId(7)) { Flags = PlayerFlags.Ragdoll };
            var current = new PlayerEntity(new EntityId(7)) { Flags = PlayerFlags.Ragdoll, Ragdoll = Pose() };
            var serializer = new PlayerEntitySerializer();

            var writer = new NetWriter(128);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new PlayerEntity(new EntityId(7)) { Flags = PlayerFlags.Ragdoll };
            serializer.ReadDelta(new NetReader(writer.ToArray()), applied);

            Assert.False(applied.Ragdoll.IsNone);
            Assert.True(NetVector3.Distance(current.Ragdoll.Head, applied.Ragdoll.Head) < 0.005f);
        }

        [Fact]
        public void AnUnchangedPoseCostsNothing()
        {
            RagdollPose pose = Pose();
            var baseline = new PlayerEntity(new EntityId(7)) { Ragdoll = pose };
            var current = new PlayerEntity(new EntityId(7)) { Ragdoll = pose };
            var serializer = new PlayerEntitySerializer();

            var withPose = new NetWriter(128);
            serializer.WriteDelta(withPose, baseline, current);

            var withoutPose = new NetWriter(128);
            serializer.WriteDelta(
                withoutPose, new PlayerEntity(new EntityId(7)), new PlayerEntity(new EntityId(7)));

            Assert.Equal(withoutPose.ToArray().Length, withPose.ToArray().Length);
        }

        [Fact]
        public void ACloneCarriesThePose()
        {
            var entity = new PlayerEntity(new EntityId(3)) { Ragdoll = Pose() };

            var clone = (PlayerEntity)entity.Clone();

            Assert.Equal(entity.Ragdoll, clone.Ragdoll);
        }

        [Fact]
        public void ALimbInsideTheDeadzoneIsLeftAlone()
        {
            // Nudging a settled body every frame keeps it quivering, which reads as a
            // bug even though it is within centimetres of correct.
            bool wanted = RagdollDriver.TryImpulse(
                new NetVector3(1f, 1f, 1f), new NetVector3(1.02f, 1f, 1f), out NetVector3 impulse);

            Assert.False(wanted);
            Assert.Equal(NetVector3.Zero, impulse);
        }

        [Fact]
        public void AnImpulsePullsTowardTheTarget()
        {
            bool wanted = RagdollDriver.TryImpulse(
                new NetVector3(2f, 0f, 0f), new NetVector3(1f, 0f, 0f), out NetVector3 impulse);

            Assert.True(wanted);
            Assert.True(impulse.X > 0f);
            Assert.Equal(RagdollDriver.Gain, impulse.X, 3);
        }

        [Fact]
        public void AFarLimbIsNotLaunched()
        {
            // Unclamped, a body five metres out of place is hit with an impulse that
            // sends it into orbit — an artefact far worse than the divergence.
            RagdollDriver.TryImpulse(new NetVector3(5f, 0f, 0f), NetVector3.Zero, out NetVector3 impulse);

            Assert.Equal(RagdollDriver.MaxImpulse, impulse.Length, 3);
        }

        [Fact]
        public void AMissingPoseProducesNoImpulses()
        {
            RagdollCorrection correction = RagdollDriver.Compute(
                RagdollPose.None, NetVector3.Zero, NetVector3.Zero, NetVector3.Zero, NetVector3.Zero);

            Assert.True(correction.IsEmpty);
        }

        [Fact]
        public void OnlyTheLimbsThatHaveDriftedAreCorrected()
        {
            var root = new NetVector3(100f, 200f, 30f);
            RagdollPose pose = Pose();

            RagdollCorrection correction = RagdollDriver.Compute(
                pose,
                root,
                root + pose.Head,                                 // exactly where it should be
                root + pose.RightFoot + new NetVector3(1f, 0f, 0f),
                root + pose.LeftFoot);

            Assert.False(correction.Has(RagdollBones.Head));
            Assert.True(correction.Has(RagdollBones.RightFoot));
            Assert.False(correction.Has(RagdollBones.LeftFoot));

            // The foot was displaced a metre along +X, so the impulse pulls it back
            // along -X. A sign error here would push every limb further out.
            Assert.Equal(-RagdollDriver.Gain, correction.RightFoot.X, 3);
        }

        [Fact]
        public void TheSolverGetsTheFirstFramesToItself()
        {
            // The pose in hand describes a body a round-trip old. Pulling on limbs
            // mid-impact produces a fall that matches neither machine.
            Assert.False(RagdollDriver.ShouldCorrect(0));
            Assert.False(RagdollDriver.ShouldCorrect(RagdollDriver.SettleFrames - 1));
            Assert.True(RagdollDriver.ShouldCorrect(RagdollDriver.SettleFrames));
        }

        [Fact]
        public void ARagdollingPedIsHandedItsPose()
        {
            var frame = new RemotePedFrame
            {
                Flags = PlayerFlags.Ragdoll,
                Health = 150,
                Position = new NetVector3(10f, 10f, 5f),
                Ragdoll = Pose(),
            };

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 5f));

            Assert.Equal(RemotePedAction.Ragdoll, command.Action);
            Assert.Equal(Pose(), command.Ragdoll);
            Assert.False(command.HardCorrect);
        }

        [Fact]
        public void ABodyInAnotherPostcodeIsPlaced()
        {
            // Past the placement distance the two machines are no longer describing the
            // same fall, and a clamped impulse will not close that before it ends.
            var frame = new RemotePedFrame
            {
                Flags = PlayerFlags.Ragdoll,
                Health = 150,
                Position = new NetVector3(0f, 0f, 0f),
                Ragdoll = Pose(),
            };

            RemotePedCommand command = RemotePedController.Decide(
                in frame, new NetVector3(RagdollDriver.PlaceDistance + 1f, 0f, 0f));

            Assert.Equal(RemotePedAction.Ragdoll, command.Action);
            Assert.True(command.HardCorrect);
        }

        [Fact]
        public void APoseIsNotBlendedWithAbsence()
        {
            // Interpolating from "no pose" would drag the limbs out of the root on the
            // first frame of every fall and back into it on the last.
            RagdollPose pose = Pose();

            Assert.Equal(pose, RagdollPose.Lerp(RagdollPose.None, pose, 0.5f));
            Assert.Equal(RagdollPose.None, RagdollPose.Lerp(pose, RagdollPose.None, 0.5f));
        }

        [Fact]
        public void TwoPosesBlendHalfway()
        {
            var from = new RagdollPose(new NetVector3(0f, 0f, 1f), new NetVector3(0f, 0f, 0f), new NetVector3(1f, 0f, 0f));
            var to = new RagdollPose(new NetVector3(0f, 0f, 2f), new NetVector3(0f, 0f, 1f), new NetVector3(2f, 0f, 0f));

            RagdollPose middle = RagdollPose.Lerp(from, to, 0.5f);

            Assert.Equal(1.5f, middle.Head.Z, 4);
            Assert.Equal(0.5f, middle.RightFoot.Z, 4);
            Assert.Equal(1.5f, middle.LeftFoot.X, 4);
        }
    }
}
