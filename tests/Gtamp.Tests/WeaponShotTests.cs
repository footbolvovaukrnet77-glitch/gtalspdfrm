using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Server.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Gunfire, from the clip that emptied to the tracer somebody else sees.
    /// <para>
    /// The defect behind these: <see cref="PlayerFlags.Shooting"/> was sampled from
    /// the local player, replicated, stored and printed — and on the receiving side
    /// it was folded into "aiming". Every remote player pointed a weapon and never
    /// fired it: no flash, no tracer, no report, no impact, while the server's
    /// arbiter scored their hits.
    /// </para>
    /// </summary>
    public class WeaponShotTests
    {
        private static readonly uint Rifle = GameHash.Joaat("WEAPON_CARBINERIFLE");

        [Fact]
        public void TheFlagAloneWouldReportSixShotsForEveryRoundFired()
        {
            // A rifle at 600 rounds a minute holds the shooting flag for six frames per
            // round at 60 fps. Counting frames instead of ammunition is what fills the
            // uplink with a burst that was never fired.
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);

            int reported = 0;
            for (int frame = 0; frame < 6; frame++)
            {
                reported += detector.Observe(Rifle, 29, shooting: true);
            }

            Assert.Equal(1, reported);
        }

        [Fact]
        public void AFirstSightingReportsNothing()
        {
            // Otherwise a session opens with a burst the size of the clip.
            var detector = new ShotDetector();

            Assert.Equal(0, detector.Observe(Rifle, 30, shooting: true));
        }

        [Fact]
        public void AReloadIsNotGunfire()
        {
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);
            detector.Observe(Rifle, 12, true);

            // Clip back up to full: rounds went in, not out.
            Assert.Equal(0, detector.Observe(Rifle, 30, shooting: false));
        }

        [Fact]
        public void ADrainedClipWithTheTriggerUpIsNotGunfire()
        {
            // A script or a trainer that empties the clip should not make the player
            // fire a burst at whatever they happened to be facing.
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);

            Assert.Equal(0, detector.Observe(Rifle, 10, shooting: false));
        }

        [Fact]
        public void ABurstInsideOneFrameIsReportedWhole()
        {
            // Three rounds between two frames is what a fast weapon on a slow frame
            // looks like, and the flag cannot express it at all.
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);

            Assert.Equal(3, detector.Observe(Rifle, 27, shooting: true));
        }

        [Fact]
        public void AnImplausibleJumpIsClipped()
        {
            var detector = new ShotDetector();
            detector.Observe(Rifle, 100, false);

            Assert.Equal(ShotDetector.MaxRoundsPerFrame, detector.Observe(Rifle, 0, shooting: true));
        }

        [Fact]
        public void SwitchingWeaponsStartsAFreshCount()
        {
            // The new weapon's clip has nothing to do with the old one's, and
            // subtracting one from the other invents a burst on every weapon change.
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);

            uint pistol = GameHash.Joaat("WEAPON_PISTOL");
            Assert.Equal(0, detector.Observe(pistol, 12, shooting: true));
            Assert.Equal(1, detector.Observe(pistol, 11, shooting: true));
        }

        [Fact]
        public void AResetForgetsTheBaseline()
        {
            var detector = new ShotDetector();
            detector.Observe(Rifle, 30, false);
            detector.Reset();

            Assert.Equal(0, detector.Observe(Rifle, 5, shooting: true));
        }

        [Fact]
        public void OnlyHitscanWeaponsAreEchoed()
        {
            // A rocket is an entity that flies. Drawing it as an instant line from
            // muzzle to impact shows everyone an explosion arriving at light speed.
            Assert.True(ShotDetector.IsHitscan(WeaponClass.Hitscan));
            Assert.False(ShotDetector.IsHitscan(WeaponClass.Projectile));
            Assert.False(ShotDetector.IsHitscan(WeaponClass.None));
        }

        [Fact]
        public void AShotSurvivesTheWire()
        {
            var sent = new WeaponShotMessage
            {
                ShooterId = new EntityId(9),
                WeaponHash = Rifle,
                Origin = new NetVector3(100.5f, -200.25f, 30f),
                Impact = new NetVector3(140f, -180f, 31.5f),
            };

            WeaponShotMessage received = WeaponShotMessage.Deserialize(sent.Serialize());

            Assert.Equal(sent.ShooterId, received.ShooterId);
            Assert.Equal(sent.WeaponHash, received.WeaponHash);
            Assert.True(NetVector3.Distance(sent.Origin, received.Origin) < 0.005f);
            Assert.True(NetVector3.Distance(sent.Impact, received.Impact) < 0.005f);
        }

        [Fact]
        public void OneRoundFiredIsOneRoundDrawnSomewhereElse()
        {
            using var harness = new TestHarness();
            TestClient shooter = harness.CreateClient("shooter");
            TestClient watcher = harness.CreateClient("watcher");
            shooter.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => watcher.PlayerCount >= 2 && shooter.PlayerCount >= 2));

            // A ped has to exist for the watcher before a shot can come out of it.
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.Peds.Count > 0));

            shooter.Bridge.PendingShot = new LocalShotSample
            {
                Rounds = 1,
                WeaponHash = Rifle,
                Origin = new NetVector3(215f, -810f, 31.5f),
                Impact = new NetVector3(235f, -810f, 31.5f),
            };

            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.ShotsPlayed.Count > 0));

            var drawn = watcher.Bridge.ShotsPlayed[0];
            Assert.Equal(Rifle, drawn.Weapon);
            Assert.True(NetVector3.Distance(new NetVector3(235f, -810f, 31.5f), drawn.Impact) < 0.005f);
            Assert.Equal(1, shooter.Client.ShotsFired);
            Assert.Equal(1, watcher.Client.ShotsSeen);

            // The shooter never draws their own shot: the game already did.
            Assert.Empty(shooter.Bridge.ShotsPlayed);
        }

        [Fact]
        public void AShooterCannotPutAMuzzleFlashInSomebodyElsesHands()
        {
            using var harness = new TestHarness();
            TestClient shooter = harness.CreateClient("shooter");
            TestClient watcher = harness.CreateClient("watcher");
            shooter.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => watcher.PlayerCount >= 2 && shooter.PlayerCount >= 2));
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.Peds.Count > 0));

            shooter.Bridge.PendingShot = new LocalShotSample
            {
                Rounds = 1,
                WeaponHash = Rifle,
                Origin = new NetVector3(215f, -810f, 31.5f),
                Impact = new NetVector3(235f, -810f, 31.5f),
            };

            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.ShotsPlayed.Count > 0));

            // The client sends no shooter id at all, and the server stamps the session's
            // own entity. The ped the watcher drew from must be the shooter's.
            PlayerEntity? shooterEntity = watcher.FindPlayer("shooter");
            Assert.NotNull(shooterEntity);
            Assert.True(watcher.Client.RemotePlayers.TryGet(shooterEntity!.Id, out RemotePlayer remote));
            Assert.Equal(remote.PedHandle, watcher.Bridge.ShotsPlayed[0].Handle);
        }

        [Fact]
        public void AShotFromTheOtherSideOfTheMapIsNotRelayed()
        {
            using var harness = new TestHarness();
            TestClient shooter = harness.CreateClient("shooter");
            TestClient watcher = harness.CreateClient("watcher");
            shooter.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => watcher.PlayerCount >= 2 && shooter.PlayerCount >= 2));
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.Peds.Count > 0));

            // Walked, not teleported: the anti-cheat would reject a jump, and rightly.
            harness.Walk(watcher, GameServer.ShotRelayRange + 150f);

            shooter.Bridge.PendingShot = new LocalShotSample
            {
                Rounds = 1,
                WeaponHash = Rifle,
                Origin = shooter.Bridge.Sample.Position,
                Impact = shooter.Bridge.Sample.Position + new NetVector3(20f, 0f, 0f),
            };

            harness.Advance(1d);

            // Dropped for replication only. The world still holds both players — the
            // filter never touches server state.
            Assert.Empty(watcher.Bridge.ShotsPlayed);
            Assert.Equal(2, harness.Server.World.EntityCount);
        }

        [Fact]
        public void AMuzzleClaimedAcrossTheStreetIsDropped()
        {
            // The origin decides where the flash is drawn. A client is free to lie
            // about it, so the server checks it against the shooter's own position
            // rather than relaying whatever arrives.
            using var harness = new TestHarness();
            TestClient shooter = harness.CreateClient("shooter");
            TestClient watcher = harness.CreateClient("watcher");
            shooter.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => watcher.PlayerCount >= 2 && shooter.PlayerCount >= 2));
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.Peds.Count > 0));

            NetVector3 elsewhere = shooter.Bridge.Sample.Position + new NetVector3(GameServer.MaxMuzzleOffset + 5f, 0f, 0f);
            shooter.Bridge.PendingShot = new LocalShotSample
            {
                Rounds = 1,
                WeaponHash = Rifle,
                Origin = elsewhere,
                Impact = elsewhere + new NetVector3(20f, 0f, 0f),
            };

            harness.Advance(1d);

            Assert.Empty(watcher.Bridge.ShotsPlayed);
        }

        [Fact]
        public void TheBudgetClearsAMinigunAndStopsAFlood()
        {
            // The ceiling has to sit above the fastest weapon in the game, which is why
            // over-budget is dropped rather than counted as a violation.
            var budget = new ShotBudget();
            double now = 0d;
            int relayed = 0;

            for (int i = 0; i < 60; i++)
            {
                now += 1d / 60d;
                if (budget.TryTake(now))
                {
                    relayed++;
                }
            }

            Assert.Equal(60, relayed);
            Assert.Equal(0, budget.Dropped);
        }

        [Fact]
        public void AFloodInsideOneFrameIsCutOffAtTheBurst()
        {
            var budget = new ShotBudget();
            int relayed = 0;

            // Ten thousand shots claimed at the same instant: no time has passed, so
            // only the burst allowance is available.
            for (int i = 0; i < 10000; i++)
            {
                if (budget.TryTake(1d))
                {
                    relayed++;
                }
            }

            Assert.Equal((int)ShotBudget.Burst, relayed);
            Assert.Equal(10000 - (int)ShotBudget.Burst, budget.Dropped);
        }
    }
}
