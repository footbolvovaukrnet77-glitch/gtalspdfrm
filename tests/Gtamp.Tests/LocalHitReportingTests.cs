using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The path from "the game says I hit somebody" to "the server agrees they were
    /// hit".
    /// <para>
    /// The defect these close is the largest of the set: <c>ReportDamage</c> existed,
    /// was correct, was covered by five tests — and was called by nothing but those
    /// tests. Nothing in the ScriptHookVDotNet layer ever noticed the local player
    /// hitting anyone, so the combat arbiter, the weapon envelopes, the kill feed and
    /// the whole death-and-respawn path were reachable only from the suite. In a real
    /// game no player could damage another.
    /// </para>
    /// </summary>
    public class LocalHitReportingTests
    {
        private static readonly uint Pistol = GameHash.Joaat("WEAPON_PISTOL");

        private static (TestHarness Harness, TestClient Shooter, TestClient Victim) Duel()
        {
            var harness = new TestHarness();
            TestClient shooter = harness.CreateClient("shooter");
            TestClient victim = harness.CreateClient("victim");
            shooter.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            victim.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => shooter.PlayerCount >= 2 && victim.PlayerCount >= 2));
            Assert.True(harness.AdvanceUntil(() => shooter.Bridge.Peds.Count > 0));
            return (harness, shooter, victim);
        }

        private static int PedHandleFor(TestClient observer, string name)
        {
            PlayerEntity? entity = observer.FindPlayer(name);
            Assert.NotNull(entity);
            Assert.True(observer.Client.RemotePlayers.TryGet(entity!.Id, out RemotePlayer remote));
            return remote.PedHandle;
        }

        [Fact]
        public void AHitTheGameScoredReachesTheServer()
        {
            var (harness, shooter, victim) = Duel();
            using (harness)
            {
                PlayerEntity? victimOnServer = harness.Server.World.GetPlayer(victim.Client.LocalEntityId);
                Assert.NotNull(victimOnServer);
                int before = victimOnServer!.Health;

                shooter.Bridge.PendingHits.Add(new LocalHitSample
                {
                    PedHandle = PedHandleFor(shooter, "victim"),
                    WeaponHash = Pistol,
                    Damage = 40,
                    HitPosition = victimOnServer.Position,
                    HitBone = -1,
                });

                Assert.True(harness.AdvanceUntil(
                    () => harness.Server.World.GetPlayer(victim.Client.LocalEntityId)?.Health < before));

                Assert.Equal(1, shooter.Client.HitsReported);
            }
        }

        [Fact]
        public void AHitOnAPedNobodyOwnsIsNotReported()
        {
            // A ped left over from a player who has already left. The hit is real and
            // there is nobody to attribute it to, which is not the same as a hit worth
            // inventing a victim for.
            var (harness, shooter, _) = Duel();
            using (harness)
            {
                shooter.Bridge.PendingHits.Add(new LocalHitSample
                {
                    PedHandle = 999999,
                    WeaponHash = Pistol,
                    Damage = 40,
                });

                harness.Advance(0.5d);

                Assert.Equal(0, shooter.Client.HitsReported);
            }
        }

        [Fact]
        public void EnoughHitsKillAndTheServerSaysSo()
        {
            // The whole path, end to end: the arbiter, the health, the death and the
            // server event. None of it was reachable from the game before.
            var (harness, shooter, victim) = Duel();
            using (harness)
            {
                for (int i = 0; i < 12; i++)
                {
                    // Re-read every round: a ped is rebuilt when its model arrives, and
                    // the handle it had before that is somebody else's or nobody's.
                    shooter.Bridge.PendingHits.Add(new LocalHitSample
                    {
                        PedHandle = PedHandleFor(shooter, "victim"),
                        WeaponHash = Pistol,
                        Damage = 45,
                        HitBone = -1,
                    });

                    harness.Advance(0.2d);

                    if (harness.Server.World.GetPlayer(victim.Client.LocalEntityId)?.Health <= 0)
                    {
                        break;
                    }
                }

                PlayerEntity? dead = harness.Server.World.GetPlayer(victim.Client.LocalEntityId);
                Assert.NotNull(dead);
                Assert.Equal(0, dead!.Health);
                Assert.True(dead.HasFlag(PlayerFlags.Dead));
            }
        }

        [Fact]
        public void APedHandleResolvesBackToItsPlayer()
        {
            var (harness, shooter, _) = Duel();
            using (harness)
            {
                PlayerEntity? entity = shooter.FindPlayer("victim");
                Assert.NotNull(entity);
                Assert.True(shooter.Client.RemotePlayers.TryGet(entity!.Id, out RemotePlayer expected));

                Assert.True(shooter.Client.RemotePlayers.TryGetByPedHandle(expected.PedHandle, out RemotePlayer found));
                Assert.Equal(expected.EntityId, found.EntityId);

                // Handle 0 is "no ped", not "the first player in the dictionary".
                Assert.False(shooter.Client.RemotePlayers.TryGetByPedHandle(0, out _));
            }
        }

        [Fact]
        public void TheBridgeIsAskedForHitsEveryFrame()
        {
            // A hit is an event. Sampling it at the state rate would drop most of a
            // burst, exactly as it would for shots.
            var (harness, shooter, _) = Duel();
            using (harness)
            {
                var seen = new List<int>();

                for (int i = 0; i < 3; i++)
                {
                    shooter.Bridge.PendingHits.Add(new LocalHitSample
                    {
                        PedHandle = PedHandleFor(shooter, "victim"), WeaponHash = Pistol, Damage = 5, HitBone = -1,
                    });

                    harness.Advance(1d / 60d, 1d / 60d);
                    seen.Add(shooter.Client.HitsReported);
                }

                Assert.Equal(new[] { 1, 2, 3 }, seen);
            }
        }
        /// <summary>
        /// A hit smaller than the client's health-correction threshold must still
        /// reach the player it hit.
        /// <para>
        /// Every test above this one measured the victim's health <em>on the server</em>,
        /// and every one of them fired for 40 or 45 damage. Both halves of that are why
        /// this went unseen for twelve phases: the server was always right, and the
        /// numbers were always big enough to cross the threshold that decides whether
        /// the client is told. A pistol round is about twelve. Twelve is below the
        /// threshold, so the arbiter accepted the hit, the server lowered the health,
        /// held authority over it — and the victim's own game was never told, so the
        /// player took no damage at all until enough hits had stacked up on the server
        /// to cross twenty in one go.
        /// </para>
        /// </summary>
        [Fact]
        public void ASmallHitReachesTheVictimsOwnGameAndNotOnlyTheServer()
        {
            var (harness, shooter, victim) = Duel();
            using (harness)
            {
                PlayerEntity? victimOnServer = harness.Server.World.GetPlayer(victim.Client.LocalEntityId);
                Assert.NotNull(victimOnServer);
                int beforeOnServer = victimOnServer!.Health;
                int beforeInGame = victim.Bridge.Sample.Health;

                shooter.Bridge.PendingHits.Add(new LocalHitSample
                {
                    PedHandle = PedHandleFor(shooter, "victim"),
                    WeaponHash = Pistol,
                    Damage = 12,
                    HitPosition = victimOnServer.Position,
                    HitBone = -1,
                });

                Assert.True(
                    harness.AdvanceUntil(
                        () => harness.Server.World.GetPlayer(victim.Client.LocalEntityId)?.Health < beforeOnServer),
                    "the server never applied the hit");

                // The half that was never asserted: the victim's game.
                Assert.True(
                    harness.AdvanceUntil(() => victim.Bridge.Sample.Health < beforeInGame),
                    $"the server took the damage but the victim's game still reads {victim.Bridge.Sample.Health}");
            }
        }
    }
}