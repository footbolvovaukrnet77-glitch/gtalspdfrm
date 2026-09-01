using System;
using System.IO;
using Gtamp.Server.Core;
using Gtamp.Server.Persistence;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class PersistenceTests : IDisposable
    {
        private readonly string _databasePath;

        public PersistenceTests()
        {
            _databasePath = Path.Combine(
                Path.GetTempPath(), "gtamp-tests", Guid.NewGuid().ToString("N") + ".db");
        }

        public void Dispose()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_databasePath);
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }

                foreach (string sidecar in new[] { _databasePath + "-wal", _databasePath + "-shm" })
                {
                    if (File.Exists(sidecar))
                    {
                        File.Delete(sidecar);
                    }
                }

                if (directory != null && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }

        [Fact]
        public void PlayersRoundTripThroughSqlite()
        {
            using var store = new SqlitePersistenceStore(_databasePath);
            store.Initialize();

            var player = new PersistedPlayer
            {
                IdentityToken = "token-1",
                Name = "alice",
                X = 1234.5f,
                Y = -678.25f,
                Z = 30.5f,
                Heading = 271.5f,
                Health = 150,
                MaxHealth = 200,
                Armor = 75,
                ModelHash = 0xDEADBEEF,
                WantedLevel = 3,
                Dimension = 4,
                InteriorId = 9876,
                Role = 2,
                Money = 25000,
            };

            store.SavePlayer(player);

            PersistedPlayer? loaded = store.LoadPlayer("token-1");
            Assert.NotNull(loaded);
            Assert.Equal("alice", loaded!.Name);
            Assert.Equal(1234.5f, loaded.X, 2);
            Assert.Equal(-678.25f, loaded.Y, 2);
            Assert.Equal(150, loaded.Health);
            Assert.Equal(75, loaded.Armor);
            Assert.Equal(0xDEADBEEFu, loaded.ModelHash);
            Assert.Equal(3, loaded.WantedLevel);
            Assert.Equal(4u, loaded.Dimension);
            Assert.Equal(9876, loaded.InteriorId);
            Assert.Equal(2, loaded.Role);
            Assert.Equal(25000, loaded.Money);

            Assert.Null(store.LoadPlayer("no-such-token"));
        }

        [Fact]
        public void SavingTheSamePlayerTwiceUpdatesRatherThanDuplicates()
        {
            using var store = new SqlitePersistenceStore(_databasePath);
            store.Initialize();

            store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice", Health = 200 });
            store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice", Health = 40 });

            Assert.Equal(40, store.LoadPlayer("t")!.Health);
        }

        [Fact]
        public void WorldStateRoundTrips()
        {
            using var store = new SqlitePersistenceStore(_databasePath);
            store.Initialize();

            store.SaveWorld(new PersistedWorld
            {
                TimeOfDaySeconds = 3600 * 21,
                ClockScale = 30f,
                WeatherHash = GameHash.Joaat("THUNDER"),
                HighestEntityId = 512,
                SchemaHash = 0xABCD1234,
            });

            PersistedWorld? loaded = store.LoadWorld();
            Assert.NotNull(loaded);
            Assert.Equal(3600 * 21, loaded!.TimeOfDaySeconds);
            Assert.Equal(GameHash.Joaat("THUNDER"), loaded.WeatherHash);
            Assert.Equal(512u, loaded.HighestEntityId);
            Assert.Equal(0xABCD1234u, loaded.SchemaHash);
        }

        [Fact]
        public void OpaqueEntityBlobsRoundTrip()
        {
            using var store = new SqlitePersistenceStore(_databasePath);
            store.Initialize();

            store.SaveEntities(new[]
            {
                new PersistedEntity { EntityId = 1, TypeId = 2, State = new byte[] { 1, 2, 3 } },
                new PersistedEntity { EntityId = 5, TypeId = 128, State = new byte[] { 9 } },
            });

            var loaded = store.LoadEntities();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(1u, loaded[0].EntityId);
            Assert.Equal(128, loaded[1].TypeId);
            Assert.Equal(new byte[] { 9 }, loaded[1].State);

            // A second save replaces the previous set rather than appending to it.
            store.SaveEntities(new[] { new PersistedEntity { EntityId = 7, TypeId = 1, State = new byte[] { 0 } } });
            Assert.Single(store.LoadEntities());
        }

        [Fact]
        public void APlayerGetsTheirCharacterBackAfterAServerRestart()
        {
            // A real keypair, because the server now asks the returning player to
            // prove they hold it rather than taking their word for a token.
            using var key = Gtamp.Shared.Security.IdentityKey.Create();
            string identity = key.ExportPrivateBlob();
            float finalX;

            var firstConfig = new ServerConfig
            {
                ServerName = "restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using (var harness = new TestHarness(firstConfig, new SqlitePersistenceStore(_databasePath)))
            {
                TestClient alice = harness.CreateClient("alice", identity);
                alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
                Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

                harness.Walk(alice, metres: 60f);
                harness.Advance(0.5);

                alice.Bridge.Sample.Health = 88;
                alice.Bridge.Sample.Armor = 44;
                harness.Advance(0.5);

                PlayerEntity onServer = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
                finalX = onServer.Position.X;
                Assert.InRange(finalX, 273f, 277f);

                harness.Server.World.State.Environment.SetTime(23, 30, 0);
            }

            // The server process is gone. A new one starts against the same database.
            var secondConfig = new ServerConfig
            {
                ServerName = "restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using var restarted = new TestHarness(secondConfig, new SqlitePersistenceStore(_databasePath));

            // The world clock came back from persistence, not from the config default.
            Assert.Equal(23, restarted.Server.World.State.Environment.Hours);
            Assert.Equal(30, restarted.Server.World.State.Environment.Minutes);

            TestClient aliceAgain = restarted.CreateClient("alice", identity);
            aliceAgain.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(restarted.AdvanceUntil(() => aliceAgain.PlayerCount >= 1));

            PlayerEntity restoredPlayer = restarted.Server.World.GetPlayer(aliceAgain.Client.LocalEntityId)!;
            Assert.Equal(finalX, restoredPlayer.Position.X, 1);
            Assert.Equal(88, restoredPlayer.Health);
            Assert.Equal(44, restoredPlayer.Armor);
            Assert.True(aliceAgain.Client.Connection.Accept!.Restored);
        }

        /// <summary>
        /// The stars come back too.
        /// <para>
        /// The wanted level was written to the database, read back on connect, and
        /// applied to nothing at all. The returning player's client — a fresh session at
        /// zero — reported that zero before the snapshot carrying the restored value had
        /// even been written, and the server took it. Three stars went in and none came
        /// out, and every layer in between reported success.
        /// </para>
        /// </summary>
        [Fact]
        public void APlayersWantedLevelSurvivesARestartAndReachesTheirGame()
        {
            using var key = Gtamp.Shared.Security.IdentityKey.Create();
            string identity = key.ExportPrivateBlob();

            var firstConfig = new ServerConfig
            {
                ServerName = "wanted-restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using (var harness = new TestHarness(firstConfig, new SqlitePersistenceStore(_databasePath)))
            {
                TestClient alice = harness.CreateClient("alice", identity);
                alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
                Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

                harness.Advance(1d);
                alice.Bridge.Sample.WantedLevel = 3;
                harness.Advance(1d);

                Assert.Equal(3, harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!.WantedLevel);
            }

            var secondConfig = new ServerConfig
            {
                ServerName = "wanted-restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using var restarted = new TestHarness(secondConfig, new SqlitePersistenceStore(_databasePath));

            TestClient aliceAgain = restarted.CreateClient("alice", identity);
            aliceAgain.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(restarted.AdvanceUntil(() => aliceAgain.PlayerCount >= 1));

            // Her game starts clean, as a fresh session does — and by the time the
            // connection is up the server has already told it otherwise, and keeps
            // telling it: the level is held against her client's reports until it has
            // seen the change.
            Assert.True(restarted.AdvanceUntil(() => aliceAgain.Bridge.LocalWantedLevel == 3));
            restarted.Advance(1d);

            Assert.Equal(3, restarted.Server.World.GetPlayer(aliceAgain.Client.LocalEntityId)!.WantedLevel);
        }

        /// <summary>
        /// The model comes back too, and reaches the game rather than the database only.
        /// <para>
        /// A restored model was written into the player's entity on connect and
        /// overwritten by their client's first update — which reports whatever character
        /// single player happened to leave the game as. Other players saw the restored
        /// model for the fraction of a second it survived.
        /// </para>
        /// </summary>
        [Fact]
        public void APlayersModelSurvivesARestartAndReachesTheirGame()
        {
            using var key = Gtamp.Shared.Security.IdentityKey.Create();
            string identity = key.ExportPrivateBlob();
            const uint Skin = 0x9C9EFFD8u;

            var firstConfig = new ServerConfig
            {
                ServerName = "model-restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using (var harness = new TestHarness(firstConfig, new SqlitePersistenceStore(_databasePath)))
            {
                TestClient alice = harness.CreateClient("alice", identity);
                alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
                Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

                harness.Advance(1d);
                alice.Bridge.Sample.ModelHash = Skin;
                harness.Advance(1d);

                Assert.Equal(Skin, harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!.ModelHash);
            }

            var secondConfig = new ServerConfig
            {
                ServerName = "model-restart-test",
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
                StartTime = "12:00",
            };

            using var restarted = new TestHarness(secondConfig, new SqlitePersistenceStore(_databasePath));

            // A fresh session, as a new game is: her client reports the default model
            // until it is told otherwise.
            TestClient aliceAgain = restarted.CreateClient("alice", identity);
            aliceAgain.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(restarted.AdvanceUntil(() => aliceAgain.PlayerCount >= 1));

            Assert.True(restarted.AdvanceUntil(() => aliceAgain.Bridge.LocalModelHash == Skin));
            restarted.Advance(1d);

            Assert.Equal(Skin, restarted.Server.World.GetPlayer(aliceAgain.Client.LocalEntityId)!.ModelHash);
        }

        [Fact]
        public void EntityIdsAreNeverReusedAfterARestart()
        {
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                store.SaveWorld(new PersistedWorld { HighestEntityId = 5000, SchemaHash = 0 });
            }

            var config = new ServerConfig
            {
                PersistenceEnabled = true,
                DatabasePath = _databasePath,
                SaveIntervalSeconds = 0,
            };

            using var harness = new TestHarness(config, new SqlitePersistenceStore(_databasePath));
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            Assert.True(
                alice.Client.LocalEntityId.Value > 5000,
                $"entity id {alice.Client.LocalEntityId} reused a range from before the restart");
        }

        [Fact]
        public void ADisabledStoreIsAValidNoOp()
        {
            using var store = new NullPersistenceStore();
            store.Initialize();
            store.SavePlayer(new PersistedPlayer { IdentityToken = "t" });

            Assert.False(store.Enabled);
            Assert.Null(store.LoadPlayer("t"));
            Assert.Null(store.LoadWorld());
            Assert.Empty(store.LoadEntities());
        }
    }
}
