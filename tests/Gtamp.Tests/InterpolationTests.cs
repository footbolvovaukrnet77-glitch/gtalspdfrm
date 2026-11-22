using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class InterpolationTests
    {
        private static PlayerEntity State(float x, float heading = 0f, int health = 200) =>
            new PlayerEntity(new EntityId(1))
            {
                Position = new NetVector3(x, 0f, 30f),
                Velocity = new NetVector3(10f, 0f, 0f),
                Heading = heading,
                Health = health,
            };

        [Fact]
        public void WithNoSamplesThereIsNothingToRender()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            Assert.False(player.TrySample(0, out _));
        }

        [Fact]
        public void ASingleSampleIsUsedAsIs()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(1.0, State(100f));

            Assert.True(player.TrySample(1.0, out RemotePedFrame frame));
            Assert.Equal(100f, frame.Position.X, 3);
        }

        [Fact]
        public void PositionsAreInterpolatedBetweenTheStraddlingSamples()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(1.0, State(100f));
            player.Push(2.0, State(200f));

            Assert.True(player.TrySample(1.5, out RemotePedFrame frame));
            Assert.Equal(150f, frame.Position.X, 1);

            Assert.True(player.TrySample(1.25, out frame));
            Assert.Equal(125f, frame.Position.X, 1);
        }

        [Fact]
        public void HeadingsInterpolateTheShortWayRound()
        {
            Assert.Equal(0f, RemotePlayer.LerpAngle(350f, 10f, 0.5f), 1);
            Assert.Equal(355f, RemotePlayer.LerpAngle(350f, 10f, 0.25f), 1);
            Assert.Equal(180f, RemotePlayer.LerpAngle(170f, 190f, 0.5f), 1);

            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(1.0, State(0f, heading: 350f));
            player.Push(2.0, State(0f, heading: 10f));

            Assert.True(player.TrySample(1.5, out RemotePedFrame frame));

            // Must land near 0/360, never spin through 180.
            float heading = frame.Heading;
            Assert.True(heading < 1f || heading > 359f, $"heading interpolated the long way: {heading}");
        }

        [Fact]
        public void RenderingBeforeTheOldestSampleHoldsTheOldestState()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(5.0, State(100f));
            player.Push(6.0, State(200f));

            Assert.True(player.TrySample(1.0, out RemotePedFrame frame));
            Assert.Equal(100f, frame.Position.X, 1);
        }

        [Fact]
        public void ShortExtrapolationUsesVelocityAndLongExtrapolationHolds()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(1.0, State(100f));
            player.Push(2.0, State(200f));

            // 100 ms past the newest sample: extrapolate along the last velocity.
            Assert.True(player.TrySample(2.1, out RemotePedFrame extrapolated));
            Assert.Equal(201f, extrapolated.Position.X, 1);

            // A full second past it: refuse to guess and hold the last known state.
            Assert.True(player.TrySample(3.0, out RemotePedFrame held));
            Assert.Equal(200f, held.Position.X, 1);
        }

        [Fact]
        public void OutOfOrderSamplesAreIgnored()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(2.0, State(200f));
            player.Push(1.0, State(100f));

            Assert.Equal(1, player.SampleCount);
            Assert.Equal(2.0, player.NewestSampleTime);
        }

        [Fact]
        public void TheSampleBufferIsBounded()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            for (int i = 0; i < 200; i++)
            {
                player.Push(i * 0.05, State(i));
            }

            Assert.True(player.SampleCount <= 16, $"buffer grew to {player.SampleCount} samples");
        }

        [Fact]
        public void SamplesAreSnapshottedSoLaterMutationCannotRewriteHistory()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            PlayerEntity live = State(100f);
            player.Push(1.0, live);

            live.Position = new NetVector3(999f, 0f, 30f);

            Assert.True(player.TrySample(1.0, out RemotePedFrame frame));
            Assert.Equal(100f, frame.Position.X, 1);
        }

        [Fact]
        public void NonPositionFieldsComeFromTheNewerSample()
        {
            var player = new RemotePlayer(new EntityId(1), 1, "alice");
            player.Push(1.0, State(100f, health: 200));
            player.Push(2.0, State(200f, health: 50));

            Assert.True(player.TrySample(1.5, out RemotePedFrame frame));

            // Health is not a continuous quantity; interpolating it would show damage
            // that never happened. The newer authoritative value wins.
            Assert.Equal(50, frame.Health);
        }
    }
}
