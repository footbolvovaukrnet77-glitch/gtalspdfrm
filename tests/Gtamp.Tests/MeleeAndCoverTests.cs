using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The half of melee that was missing.
    /// <para>
    /// <c>PlayerFlags.Melee</c> travelled from the first commit and was applied by
    /// nothing, for a reason recorded in ENTITY_SYSTEM.md and correct as far as it
    /// went: a melee task needs an entity to strike, only the flag was replicated, and
    /// a ped told to fight nobody swings at the air. These are about the target now
    /// travelling with it — end to end, from one client's sample to the command the
    /// other client's bridge is handed.
    /// </para>
    /// </summary>
    public class MeleeAndCoverTests
    {
        private static (TestHarness Harness, TestClient Attacker, TestClient Victim) Pair()
        {
            var harness = new TestHarness();
            TestClient attacker = harness.CreateClient("attacker");
            TestClient victim = harness.CreateClient("victim");
            attacker.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            victim.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => attacker.PlayerCount >= 2 && victim.PlayerCount >= 2));
            Assert.True(harness.AdvanceUntil(() => attacker.Bridge.Peds.Count > 0 && victim.Bridge.Peds.Count > 0));
            return (harness, attacker, victim);
        }

        private static int PedHandleFor(TestClient observer, string name)
        {
            PlayerEntity? entity = observer.FindPlayer(name);
            Assert.NotNull(entity);
            Assert.True(observer.Client.RemotePlayers.TryGet(entity!.Id, out RemotePlayer remote));
            return remote.PedHandle;
        }

        /// <summary>
        /// A third party's ped is the thing that has to be swung at, so the whole point
        /// is that the id survives the trip and comes out the other side as one of the
        /// *receiving* client's own handles.
        /// </summary>
        [Fact]
        public void AMeleeTargetTravelsAndResolvesToTheRightPedOnTheOtherClient()
        {
            var (harness, attacker, victim) = Pair();
            using (harness)
            {
                // Settle first, and this is not test hygiene — it is the thing the game
                // does too. A remote ped is destroyed and rebuilt when its model
                // changes, which happens once on every player as their real model
                // arrives behind the snapshot that made them visible, and the handle
                // changes with it. The bridge reads the melee target on the same frame
                // it reads the flag, so it never sees a stale one; a test that reads the
                // handle early does, and the first version of this one did.
                harness.Advance(1.0);

                attacker.Bridge.Sample.Flags |= PlayerFlags.Melee;
                attacker.Bridge.Sample.MeleeTargetPedHandle = PedHandleFor(attacker, "victim");

                // The victim's own client draws the attacker, so it is the victim's
                // copy of the attacker's ped that must be told who to hit — and the
                // ped it must hit is the victim's own.
                int attackerHandleOnVictim = PedHandleFor(victim, "attacker");

                Assert.True(
                    harness.AdvanceUntil(() =>
                        victim.Bridge.Peds.TryGetValue(attackerHandleOnVictim, out RemotePedCommand command)
                        && command.MeleeTargetHandle != 0),
                    "the melee target never reached the other client");

                RemotePedCommand seen = victim.Bridge.Peds[attackerHandleOnVictim];
                Assert.Equal(victim.Bridge.LocalPedHandle, seen.MeleeTargetHandle);
            }
        }

        /// <summary>
        /// Every field on this message is written twenty times a second, so one that is
        /// meaningful on almost no frame is gated on its flag. The gate is a decoding
        /// hazard as much as a bandwidth one: writer and reader have to agree about
        /// whether the bytes are there at all.
        /// </summary>
        [Fact]
        public void TheTargetIsOnTheWireOnlyWhileTheMeleeFlagIsSet()
        {
            var punching = new ClientStateUpdateMessage
            {
                Flags = PlayerFlags.Melee,
                MeleeTargetId = new EntityId(4242),
            };

            var idle = new ClientStateUpdateMessage { Flags = PlayerFlags.None, MeleeTargetId = new EntityId(4242) };

            Assert.True(punching.Serialize().Length > idle.Serialize().Length);

            ClientStateUpdateMessage back = ClientStateUpdateMessage.Deserialize(punching.Serialize());
            Assert.Equal(new EntityId(4242), back.MeleeTargetId);

            Assert.Equal(EntityId.None, ClientStateUpdateMessage.Deserialize(idle.Serialize()).MeleeTargetId);
        }

        /// <summary>
        /// A melee target is one client naming somebody else's entity, so it is checked
        /// rather than copied. An id for an entity the server has never heard of would
        /// replicate to every client, each of which would look it up, find nothing and
        /// do nothing — a lie travelling twenty times a second.
        /// </summary>
        [Fact]
        public void TheServerRefusesATargetThatNamesNothing()
        {
            var (harness, attacker, _) = Pair();
            using (harness)
            {
                attacker.Bridge.Sample.Flags |= PlayerFlags.Melee;
                harness.Advance(0.5);

                PlayerEntity onServer = harness.Server.World.GetPlayer(attacker.Client.LocalEntityId)!;
                Assert.Equal(EntityId.None, onServer.MeleeTargetId);
            }
        }

        /// <summary>A client cannot report itself as its own melee target.</summary>
        [Fact]
        public void TheServerRefusesAPlayerNamingThemselves()
        {
            var (harness, attacker, _) = Pair();
            using (harness)
            {
                harness.Advance(1.0);
                attacker.Bridge.Sample.Flags |= PlayerFlags.Melee;
                attacker.Bridge.Sample.MeleeTargetPedHandle = attacker.Bridge.LocalPedHandle;
                harness.Advance(0.5);

                PlayerEntity onServer = harness.Server.World.GetPlayer(attacker.Client.LocalEntityId)!;
                Assert.Equal(EntityId.None, onServer.MeleeTargetId);
            }
        }

        /// <summary>
        /// The old entry said cover could not be applied because the point would have
        /// to be guessed. It does not: a player in cover is standing at their cover,
        /// and their position is already replicated. This asserts the coordinate the
        /// bridge is handed is that position and not something derived from it.
        /// </summary>
        [Fact]
        public void ACoveringPlayersOwnPositionIsWhatTheCoverPointIsTakenFrom()
        {
            var frame = new RemotePedFrame
            {
                Position = new NetVector3(120.5f, -33.25f, 71f),
                Flags = PlayerFlags.InCover,
                Health = 200,
                Movement = MovementState.Idle,
            };

            RemotePedCommand command = RemotePedController.Decide(in frame, frame.Position);

            Assert.Equal(frame.Position, command.TargetPosition);
            Assert.True((command.Flags & PlayerFlags.InCover) != 0);
        }
    }
}
