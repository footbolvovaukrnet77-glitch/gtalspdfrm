using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Gtamp.Server.Core;
using Gtamp.Server.Persistence;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class SchemaMigrationTests : IDisposable
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(), "gtamp-tests", Guid.NewGuid().ToString("N") + ".db");

        public void Dispose()
        {
            foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // A leftover temp file is not worth failing a test over.
                }
            }
        }

        [Fact]
        public void AFreshDatabaseIsStampedWithTheCurrentSchemaVersion()
        {
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                store.SaveWorld(new PersistedWorld { TimeOfDaySeconds = 100 });
            }

            // Re-opening runs the migration path again; it must be a no-op.
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                Assert.Equal(100, store.LoadWorld()!.TimeOfDaySeconds);
            }
        }

        [Fact]
        public void ReopeningAnExistingDatabaseKeepsItsData()
        {
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice", Money = 1234 });
                store.SaveEntities(new[]
                {
                    new PersistedEntity { EntityId = 5, TypeId = 2, State = new byte[] { 1, 2 }, Dimension = 3 },
                });
            }

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                Assert.Equal(1234, store.LoadPlayer("t")!.Money);

                IReadOnlyList<PersistedEntity> entities = store.LoadEntities();
                Assert.Single(entities);
                Assert.Equal(3u, entities[0].Dimension);
            }
        }
    }

    public class BackgroundPersistenceTests
    {
        /// <summary>A store that records what it was asked to do and how slowly it did it.</summary>
        private sealed class RecordingStore : IPersistenceStore
        {
            private readonly int _writeDelayMilliseconds;

            public RecordingStore(int writeDelayMilliseconds = 0)
            {
                _writeDelayMilliseconds = writeDelayMilliseconds;
            }

            public bool Enabled => true;

            public int PlayerWrites { get; private set; }

            public int WorldWrites { get; private set; }

            public int EntityWrites { get; private set; }

            public PersistedPlayer? LastPlayer { get; private set; }

            public PersistedWorld? LastWorld { get; private set; }

            public void Initialize()
            {
            }

            public void SavePlayer(PersistedPlayer player)
            {
                Delay();
                PlayerWrites++;
                LastPlayer = player;
            }

            public PersistedPlayer? LoadPlayer(string identityToken) => LastPlayer;

            public void SaveWorld(PersistedWorld world)
            {
                Delay();
                WorldWrites++;
                LastWorld = world;
            }

            public PersistedWorld? LoadWorld() => LastWorld;

            public void SaveEntities(IReadOnlyList<PersistedEntity> entities)
            {
                Delay();
                EntityWrites++;
            }

            public IReadOnlyList<PersistedEntity> LoadEntities() => Array.Empty<PersistedEntity>();

            public void SaveBans(IReadOnlyList<Gtamp.Shared.Security.BanEntry> bans)
            {
                Bans = bans;
            }

            public IReadOnlyList<Gtamp.Shared.Security.BanEntry> Bans { get; private set; } =
                System.Array.Empty<Gtamp.Shared.Security.BanEntry>();

            public IReadOnlyList<Gtamp.Shared.Security.BanEntry> LoadBans() => Bans;

            public string Describe() => "recording";

            public void Dispose()
            {
            }

            private void Delay()
            {
                if (_writeDelayMilliseconds > 0)
                {
                    Thread.Sleep(_writeDelayMilliseconds);
                }
            }
        }

        [Fact]
        public void WritesReachTheUnderlyingStore()
        {
            var inner = new RecordingStore();
            using var store = new BackgroundPersistenceStore(inner, new LogBus());
            store.Initialize();

            store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice" });
            store.SaveWorld(new PersistedWorld { TimeOfDaySeconds = 42 });
            store.Flush();

            Assert.Equal(1, inner.PlayerWrites);
            Assert.Equal(1, inner.WorldWrites);
            Assert.Equal(42, inner.LastWorld!.TimeOfDaySeconds);
        }

        [Fact]
        public void RedundantWritesAreCoalescedRatherThanQueued()
        {
            // A slow disk must cost freshness, not an unbounded queue: an unbounded
            // queue turns a slow disk into an out-of-memory crash.
            var inner = new RecordingStore(writeDelayMilliseconds: 20);
            using var store = new BackgroundPersistenceStore(inner, new LogBus());
            store.Initialize();

            for (int i = 0; i < 200; i++)
            {
                store.SaveWorld(new PersistedWorld { TimeOfDaySeconds = i });
            }

            store.Flush();

            Assert.True(inner.WorldWrites < 200, $"{inner.WorldWrites} writes reached the disk; they were not coalesced");
            Assert.True(store.WritesCoalesced > 0);

            // Whatever did reach the disk, the newest state must be there.
            Assert.Equal(199, inner.LastWorld!.TimeOfDaySeconds);
        }

        [Fact]
        public void AQueuedWriteIsVisibleToAReadBeforeItReachesTheDisk()
        {
            var inner = new RecordingStore(writeDelayMilliseconds: 50);
            using var store = new BackgroundPersistenceStore(inner, new LogBus());
            store.Initialize();

            store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice", Money = 999 });

            // A player who reconnects immediately must see their own last save, even if
            // it is still sitting in the queue.
            PersistedPlayer? loaded = store.LoadPlayer("t");
            Assert.NotNull(loaded);
            Assert.Equal(999, loaded!.Money);
        }

        [Fact]
        public void NothingQueuedIsLostOnShutdown()
        {
            var inner = new RecordingStore();

            var store = new BackgroundPersistenceStore(inner, new LogBus());
            store.Initialize();
            store.SavePlayer(new PersistedPlayer { IdentityToken = "t", Name = "alice", Money = 500 });
            store.SaveWorld(new PersistedWorld { TimeOfDaySeconds = 7 });
            store.Dispose();

            Assert.Equal(1, inner.PlayerWrites);
            Assert.Equal(1, inner.WorldWrites);
            Assert.Equal(500, inner.LastPlayer!.Money);
        }

        [Fact]
        public void AFailingWriteDoesNotKillTheWriter()
        {
            var inner = new ThrowingStore();
            using var store = new BackgroundPersistenceStore(inner, new LogBus());
            store.Initialize();

            store.SaveWorld(new PersistedWorld());

            // Whether the failing write is picked up by the worker or by this thread's
            // Flush is a race, and which one sees the exception is an implementation
            // detail. What matters is that persistence still works afterwards.
            try
            {
                store.Flush();
            }
            catch (IOException)
            {
                // Expected when this thread happened to be the one that drained it.
            }

            // The next write still gets through: one bad save must not silently
            // disable persistence for the rest of the session.
            inner.ShouldThrow = false;
            store.SaveWorld(new PersistedWorld { TimeOfDaySeconds = 11 });

            Assert.True(
                SpinWait.SpinUntil(
                    () =>
                    {
                        try
                        {
                            store.Flush();
                        }
                        catch (IOException)
                        {
                            return false;
                        }

                        return inner.LastWorld?.TimeOfDaySeconds == 11;
                    },
                    TimeSpan.FromSeconds(5)),
                "persistence stayed broken after one failed write");
        }

        private sealed class ThrowingStore : IPersistenceStore
        {
            public bool ShouldThrow { get; set; } = true;

            public PersistedWorld? LastWorld { get; private set; }

            public bool Enabled => true;

            public void Initialize()
            {
            }

            public void SavePlayer(PersistedPlayer player)
            {
            }

            public PersistedPlayer? LoadPlayer(string identityToken) => null;

            public void SaveWorld(PersistedWorld world)
            {
                if (ShouldThrow)
                {
                    throw new IOException("the disk is full");
                }

                LastWorld = world;
            }

            public PersistedWorld? LoadWorld() => LastWorld;

            public void SaveEntities(IReadOnlyList<PersistedEntity> entities)
            {
            }

            public IReadOnlyList<PersistedEntity> LoadEntities() => Array.Empty<PersistedEntity>();

            public void SaveBans(IReadOnlyList<Gtamp.Shared.Security.BanEntry> bans)
            {
                Bans = bans;
            }

            public IReadOnlyList<Gtamp.Shared.Security.BanEntry> Bans { get; private set; } =
                System.Array.Empty<Gtamp.Shared.Security.BanEntry>();

            public IReadOnlyList<Gtamp.Shared.Security.BanEntry> LoadBans() => Bans;

            public string Describe() => "throwing";

            public void Dispose()
            {
            }
        }
    }

    public class EntityPersistenceTests : IDisposable
    {
        private const uint Adder = 0xB779A091;

        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(), "gtamp-tests", Guid.NewGuid().ToString("N") + ".db");

        public void Dispose()
        {
            foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private ServerConfig Config() => new ServerConfig
        {
            PersistenceEnabled = true,
            DatabasePath = _databasePath,
            SaveIntervalSeconds = 0,
        };

        [Fact]
        public void AVehicleSurvivesAServerRestart()
        {
            EntityId vehicleId;
            float bodyHealth;

            using (var harness = new TestHarness(Config(), new SqlitePersistenceStore(_databasePath)))
            {
                TestClient alice = harness.CreateClient("alice");
                alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
                Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

                int handle = alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(300f, -900f, 30f), 45f);
                Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5));

                alice.Bridge.Vehicles[handle].BodyHealth = 512f;
                harness.Advance(1.0);

                VehicleEntity onServer = harness.Server.World.State.OfType<VehicleEntity>().First();
                vehicleId = onServer.Id;
                bodyHealth = onServer.BodyHealth;
                Assert.Equal(512f, bodyHealth, 0);
            }

            using var restarted = new TestHarness(Config(), new SqlitePersistenceStore(_databasePath));

            VehicleEntity? restored = restarted.Server.World.State.OfType<VehicleEntity>().FirstOrDefault();
            Assert.NotNull(restored);
            Assert.Equal(vehicleId, restored!.Id);
            Assert.Equal(bodyHealth, restored.BodyHealth, 0);
            Assert.Equal(Adder, restored.ModelHash);
            Assert.Equal(300f, restored.Position.X, 1);

            // Nobody is simulating it until somebody turns up near it.
            Assert.Equal(0u, restored.OwnerId);
        }

        [Fact]
        public void ARestoredEntityKeepsItsIdSoNothingReusesIt()
        {
            using (var harness = new TestHarness(Config(), new SqlitePersistenceStore(_databasePath)))
            {
                TestClient alice = harness.CreateClient("alice");
                alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
                Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

                alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(300f, -900f, 30f));
                Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5));
                harness.Advance(0.5);
            }

            using var restarted = new TestHarness(Config(), new SqlitePersistenceStore(_databasePath));

            EntityId restoredId = restarted.Server.World.State.OfType<VehicleEntity>().First().Id;
            EntityId freshId = restarted.Server.World.AllocateEntityId();

            Assert.True(freshId.Value > restoredId.Value, "a new entity reused a persisted id");
        }

        [Fact]
        public void EntitiesAreSkippedWhenTheFieldLayoutHasChanged()
        {
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                store.Initialize();
                store.SaveWorld(new PersistedWorld { HighestEntityId = 50, SchemaHash = 0xDEADBEEF });
                store.SaveEntities(new[]
                {
                    new PersistedEntity { EntityId = 10, TypeId = (byte)EntityType.Vehicle, State = new byte[] { 1, 2, 3 } },
                });
            }

            // The stored schema hash does not match this build's, so misinterpreting
            // the blob is refused rather than attempted.
            using var harness = new TestHarness(Config(), new SqlitePersistenceStore(_databasePath));
            Assert.Empty(harness.Server.World.State.OfType<VehicleEntity>());
        }
    }
}
