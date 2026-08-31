using System.Collections.Generic;
using Gtamp.Server.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Master prompt sections 58-60: many players, spread across the whole map, with
    /// the server keeping full world state and only the wire being optimised.
    /// </summary>
    public class StressTests
    {
        /// <summary>Corners of the GTA V map, so no two of these are within streaming range.</summary>
        private static readonly NetVector3[] MapSpread =
        {
            new NetVector3(215f, -810f, 30f),      // Los Santos, Legion Square
            new NetVector3(1960f, 3740f, 32f),     // Sandy Shores
            new NetVector3(-275f, 6635f, 7f),      // Paleto Bay
            new NetVector3(-2350f, 3250f, 32f),    // Fort Zancudo
            new NetVector3(2540f, 4265f, 39f),     // Grapeseed
        };

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(20)]
        public void PlayersAllConvergeOnTheSameWorld(int playerCount)
        {
            using var harness = new TestHarness();

            var clients = new List<TestClient>();
            for (int i = 0; i < playerCount; i++)
            {
                TestClient client = harness.CreateClient("player" + i);
                clients.Add(client);
                client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            }

            bool converged = harness.AdvanceUntil(
                () =>
                {
                    foreach (TestClient client in clients)
                    {
                        if (client.PlayerCount != playerCount)
                        {
                            return false;
                        }
                    }

                    return true;
                },
                timeoutSeconds: 20);

            Assert.True(converged, $"{playerCount} clients never converged on a {playerCount}-player world");
            Assert.Equal(playerCount, harness.Server.Players.Count);

            foreach (TestClient client in clients)
            {
                Assert.Equal(playerCount - 1, client.Client.RemotePlayers.Count);
                Assert.Equal(0, client.Client.ResyncsRequested);
            }
        }

        [Fact]
        public void DistanceNeverRemovesAnEntityFromTheServerWorld()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            // Scatter server-owned entities across the whole map, far outside any
            // streaming range of the single connected player.
            for (int i = 0; i < 100; i++)
            {
                NetVector3 corner = MapSpread[i % MapSpread.Length];
                harness.Server.World.Spawn(new PlayerEntity(harness.Server.World.AllocateEntityId())
                {
                    Name = "npc" + i,
                    Position = new NetVector3(corner.X + i, corner.Y + i, corner.Z),
                    Health = 200,
                });
            }

            int expected = harness.Server.World.EntityCount;
            Assert.Equal(101, expected);

            harness.Advance(20.0);

            // The invariant: the server still knows about every one of them.
            Assert.Equal(expected, harness.Server.World.EntityCount);

            // And the client converged on all of them despite the byte budget,
            // because distance changes priority, never inclusion.
            Assert.Equal(expected, alice.Client.ReplicatedWorld.EntityCount);
        }

        [Fact]
        public void ABudgetedSnapshotStreamStaysWithinItsBandwidthAllowance()
        {
            var config = new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                SnapshotRate = 20,
                SnapshotByteBudget = 512,
            };

            using var harness = new TestHarness(config);
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            for (int i = 0; i < 200; i++)
            {
                harness.Server.World.Spawn(new PlayerEntity(harness.Server.World.AllocateEntityId())
                {
                    Name = "npc" + i,
                    Position = new NetVector3(i * 5f, i * 5f, 30f),
                });
            }

            harness.Advance(30.0);

            Assert.Equal(201, alice.Client.ReplicatedWorld.EntityCount);

            // 20 snapshots per second at 512 bytes is the declared ceiling; the
            // measured rate must sit under it with room for packet overhead.
            double seconds = harness.Now;
            double bytesPerSecond = alice.Client.Connection.Peer!.Stats.BytesReceived / seconds;
            Assert.True(
                bytesPerSecond < 20 * 512 * 1.5,
                $"inbound rate {bytesPerSecond:0} B/s exceeded the snapshot budget");
        }

        [Fact]
        public void ManyPlayersMovingAtOnceStayConsistent()
        {
            using var harness = new TestHarness(seed: 4242);
            harness.Latency = 0.05;
            harness.Network.Jitter = 0.03;
            harness.PacketLoss = 0.05;

            var clients = new List<TestClient>();
            for (int i = 0; i < 8; i++)
            {
                TestClient client = harness.CreateClient("player" + i);
                clients.Add(client);
                client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            }

            Assert.True(
                harness.AdvanceUntil(() => clients.TrueForAll(c => c.PlayerCount == 8), timeoutSeconds: 25),
                "clients never converged");

            // Everyone walks at once for three seconds.
            for (int step = 0; step < 90; step++)
            {
                foreach (TestClient client in clients)
                {
                    NetVector3 position = client.Bridge.Sample.Position;
                    client.Bridge.Sample.Position = new NetVector3(position.X + 0.2f, position.Y, position.Z);
                }

                harness.Advance(1d / 30d, 1d / 30d);
            }

            harness.Advance(2.0);

            foreach (TestClient client in clients)
            {
                Assert.True(client.Client.IsConnected);
                Assert.Equal(8, client.PlayerCount);

                // Nobody was corrected: honest movement must never trip the anti-cheat.
                Assert.Equal(0, client.Client.CorrectionsApplied);
            }
        }
    }
}
