using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Posture flags reaching the bridge at all.
    /// <para>
    /// Eighteen flags are sampled and replicated. Until this, the command handed to
    /// the bridge carried five of them — dead, ragdoll, in-vehicle, aiming, shooting
    /// — and the rest had nowhere to arrive: crouch, cover, climb, parachute and the
    /// others travelled the whole way and stopped one layer short of the ped. Two are
    /// applied now and the remainder are named in docs/ENTITY_SYSTEM.md instead of
    /// quietly going nowhere.
    /// </para>
    /// </summary>
    public class PostureFlagTests
    {
        private static RemotePedFrame Frame(PlayerFlags flags) => new RemotePedFrame
        {
            Position = new NetVector3(10f, 10f, 30f),
            Heading = 90f,
            Health = 200,
            Flags = flags,
        };

        [Fact]
        public void TheFlagsReachTheCommand()
        {
            RemotePedCommand command = RemotePedController.Decide(
                Frame(PlayerFlags.Crouching | PlayerFlags.Reloading), new NetVector3(10f, 10f, 30f));

            Assert.True((command.Flags & PlayerFlags.Crouching) != 0);
            Assert.True((command.Flags & PlayerFlags.Reloading) != 0);
        }

        [Fact]
        public void ADeadPlayersFlagsStillTravel()
        {
            // The action is what changes; the posture is not thrown away because of it.
            RemotePedCommand command = RemotePedController.Decide(
                Frame(PlayerFlags.Dead | PlayerFlags.Crouching), new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Dead, command.Action);
            Assert.True((command.Flags & PlayerFlags.Crouching) != 0);
        }

        [Fact]
        public void ARagdollingPlayersFlagsStillTravel()
        {
            RemotePedCommand command = RemotePedController.Decide(
                Frame(PlayerFlags.Ragdoll | PlayerFlags.Falling), new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Ragdoll, command.Action);
            Assert.True((command.Flags & PlayerFlags.Falling) != 0);
        }

        [Fact]
        public void ASeatedPlayersFlagsStillTravel()
        {
            RemotePedCommand command = RemotePedController.Decide(
                Frame(PlayerFlags.InVehicle | PlayerFlags.Reloading), new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.InVehicle, command.Action);
            Assert.True((command.Flags & PlayerFlags.Reloading) != 0);
        }

        [Fact]
        public void AWalkingPlayersFlagsStillTravel()
        {
            // The ordinary path, and the one the other four are exceptions to.
            RemotePedFrame frame = Frame(PlayerFlags.Crouching);
            frame.Movement = MovementState.Walk;
            frame.Velocity = new NetVector3(1.5f, 0f, 0f);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(5f, 10f, 30f));

            Assert.Equal(RemotePedAction.Walk, command.Action);
            Assert.True((command.Flags & PlayerFlags.Crouching) != 0);
        }
        /// <summary>
        /// Fire is one of the few states GTA V both answers and accepts:
        /// <c>IS_ENTITY_ON_FIRE</c> reads it, <c>START_ENTITY_FIRE</c> and
        /// <c>STOP_ENTITY_FIRE</c> write it. Most of the flags beside it are one or the
        /// other, which is why several are replicated and deliberately not applied.
        /// </summary>
        [Fact]
        public void BurningTravelsToTheCommandLikeAnyOtherPosture()
        {
            RemotePedCommand command = RemotePedController.Decide(
                Frame(PlayerFlags.OnFire), new NetVector3(10f, 10f, 30f));

            Assert.True((command.Flags & PlayerFlags.OnFire) != 0);
        }

        /// <summary>
        /// A duplicated bit makes two unrelated states the same state. `VehicleFlags`
        /// has had this check for a while and `PlayerFlags` — which is larger and older
        /// — had none, so a hand-written shift could collide in silence.
        /// </summary>
        [Fact]
        public void EveryPlayerFlagHasADistinctBit()
        {
            var seen = new System.Collections.Generic.Dictionary<uint, string>();
            foreach (PlayerFlags flag in System.Enum.GetValues(typeof(PlayerFlags)))
            {
                if (flag == PlayerFlags.None)
                {
                    continue;
                }

                uint bit = (uint)flag;
                Assert.False(
                    seen.ContainsKey(bit),
                    $"{flag} shares bit {bit} with {(seen.TryGetValue(bit, out string? other) ? other : "?")}");
                seen[bit] = flag.ToString();
            }

            Assert.Equal(19, seen.Count);
        }

        /// <summary>
        /// And every one of them survives the wire. A flag added past the width of the
        /// field that carries it would arrive as zero, which reads exactly like a state
        /// that is simply false.
        /// </summary>
        [Fact]
        public void TheWholeFlagSetSurvivesTheWire()
        {
            PlayerFlags all = PlayerFlags.None;
            foreach (PlayerFlags flag in System.Enum.GetValues(typeof(PlayerFlags)))
            {
                all |= flag;
            }

            var baseline = new PlayerEntity(new EntityId(3));
            var current = new PlayerEntity(new EntityId(3)) { Flags = all };

            var serializer = new PlayerEntitySerializer();
            var writer = new Gtamp.Shared.Net.NetWriter(128);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new PlayerEntity(new EntityId(3));
            serializer.ReadDelta(new Gtamp.Shared.Net.NetReader(writer.ToArray()), applied);

            Assert.Equal(all, applied.Flags);
        }

    }
}
