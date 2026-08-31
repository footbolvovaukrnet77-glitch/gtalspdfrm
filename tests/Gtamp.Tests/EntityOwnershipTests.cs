using System.Linq;
using Gtamp.Server.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// End-to-end coverage of the entity lifecycle: a client creating something
    /// locally, the server adopting it, other clients seeing it, and ownership
    /// moving when the owner leaves or wanders off.
    /// </summary>
    public class EntityOwnershipTests
    {
        private const uint Adder = 0xB779A091;

        private static ServerConfig Config(float handoff = 300f) => new ServerConfig
        {
            PersistenceEnabled = false,
            SaveIntervalSeconds = 0,
            OwnershipHandoffDistance = handoff,
            OwnershipCheckIntervalSeconds = 0.2,
        };

        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        private static VehicleEntity? ServerVehicle(TestHarness harness) =>
            harness.Server.World.State.OfType<VehicleEntity>().FirstOrDefault();

        [Fact]
        public void AVehicleAClientGetsIntoIsAdoptedByTheServer()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f), 90f);

            Assert.True(
                harness.AdvanceUntil(() => ServerVehicle(harness) != null, timeoutSeconds: 5),
                "the server never adopted the vehicle");

            // The client only knows the id once the server's reply comes back.
            Assert.True(
                harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5),
                "the client never learned the entity id the server assigned");

            VehicleEntity vehicle = ServerVehicle(harness)!;
            Assert.Equal(Adder, vehicle.ModelHash);
            Assert.Equal(alice.Client.LocalPlayerId, vehicle.OwnerId);
        }

        [Fact]
        public void TheOwnersVehicleStateReachesEveryoneElse()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            int handle = alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5));

            // Alice drives; her client streams the vehicle it owns.
            alice.Bridge.Vehicles[handle].BodyHealth = 420f;
            alice.Bridge.Vehicles[handle].Flags = VehicleFlags.EngineRunning | VehicleFlags.SirenActive;

            Assert.True(
                harness.AdvanceUntil(() => ServerVehicle(harness)!.BodyHealth < 500f, timeoutSeconds: 5),
                "the owner's vehicle state never reached the server");

            Assert.True(ServerVehicle(harness)!.HasFlag(VehicleFlags.SirenActive));

            // And bob's client spawned a local vehicle for it.
            Assert.True(
                harness.AdvanceUntil(() => bob.Client.RemoteEntities.VehicleCount == 1, timeoutSeconds: 5),
                "bob never saw the vehicle");

            Assert.True(harness.AdvanceUntil(() => bob.Bridge.VehicleFrames.Count == 1, timeoutSeconds: 5));
        }

        [Fact]
        public void AdoptionIsRequestedOnceEvenThoughTheReplyBeatsTheSnapshot()
        {
            // The acceptance is reliable and the snapshot is not, so the client learns
            // its entity id before the entity appears in its replicated world. A client
            // that treated that gap as "the entity is gone" would ask again, and the
            // server would have no way to tell the retry from a second vehicle.
            using var harness = new TestHarness(Config());
            harness.Latency = 0.08;
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 8));

            harness.Advance(4.0);

            Assert.Equal(1, alice.Client.OwnedEntities.SpawnsRequested);
            Assert.Single(harness.Server.World.State.OfType<VehicleEntity>());
        }

        [Fact]
        public void AClientCannotReportStateForSomebodyElsesEntity()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => ServerVehicle(harness) != null, timeoutSeconds: 5));

            VehicleEntity vehicle = ServerVehicle(harness)!;
            float before = vehicle.BodyHealth;

            // Bob claims the vehicle is wrecked. He does not own it.
            var forged = new VehicleEntity(vehicle.Id) { BodyHealth = 1f, Position = vehicle.Position };
            var writer = new Gtamp.Shared.Net.NetWriter();
            harness.Server.Registry.Get((byte)EntityType.Vehicle).WriteFull(writer, forged);

            var update = new Gtamp.Shared.Protocol.OwnedEntityUpdateMessage
            {
                EntityId = vehicle.Id,
                State = writer.ToArray(),
            };

            bob.Client.Connection.Peer!.Send(
                Gtamp.Shared.Protocol.NetMessageType.OwnedEntityUpdate,
                update.Serialize(),
                Gtamp.Shared.Net.DeliveryMethod.ReliableOrdered);

            harness.Advance(1.0);

            Assert.Equal(before, ServerVehicle(harness)!.BodyHealth, 0);
        }

        [Fact]
        public void OwnershipMovesToTheNearestPlayerWhenTheOwnerLeaves()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => ServerVehicle(harness) != null, timeoutSeconds: 5));

            uint aliceId = alice.Client.LocalPlayerId;
            uint bobId = bob.Client.LocalPlayerId;

            alice.Client.Disconnect("test");
            harness.RemoveClient(alice);

            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Count == 1, timeoutSeconds: 5));

            VehicleEntity vehicle = ServerVehicle(harness)!;
            Assert.NotEqual(aliceId, vehicle.OwnerId);
            Assert.Equal(bobId, vehicle.OwnerId);

            // And the vehicle is still in the world: it does not evaporate with its owner.
            Assert.NotNull(ServerVehicle(harness));
        }

        [Fact]
        public void AnEntityNobodyIsNearGoesBackToTheServerRatherThanBeingDeleted()
        {
            using var harness = new TestHarness(Config(handoff: 50f));
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => ServerVehicle(harness) != null, timeoutSeconds: 5));

            // Move the vehicle far from everyone, server-side.
            VehicleEntity vehicle = ServerVehicle(harness)!;
            vehicle.Position = new NetVector3(3000f, 3000f, 30f);
            alice.Bridge.LocalVehicleHandle = 0;

            Assert.True(
                harness.AdvanceUntil(() => ServerVehicle(harness)!.OwnerId == 0, timeoutSeconds: 5),
                "the vehicle was never handed back to the server");

            Assert.NotNull(ServerVehicle(harness));
            Assert.Single(harness.Server.World.State.OfType<VehicleEntity>());
        }

        [Fact]
        public void OwnershipMovesToWhoeverIsClosest()
        {
            using var harness = new TestHarness(Config(handoff: 100f));
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => ServerVehicle(harness) != null, timeoutSeconds: 5));

            uint bobId = bob.Client.LocalPlayerId;

            // Alice gets out and the server moves her far away; bob stays put.
            alice.Bridge.LocalVehicleHandle = 0;
            Assert.True(harness.Server.TeleportPlayer(
                harness.Server.Players.Sessions[0], new NetVector3(2000f, 2000f, 30f), 0f));

            Assert.True(
                harness.AdvanceUntil(() => ServerVehicle(harness)!.OwnerId == bobId, timeoutSeconds: 8),
                "ownership never moved to the closer player");
        }

        [Fact]
        public void ThePerPlayerEntityLimitIsEnforced()
        {
            var config = Config();
            config.MaxEntitiesPerPlayer = 2;

            using var harness = new TestHarness(config);
            TestClient alice = Connected(harness, "alice");

            for (int i = 0; i < 5; i++)
            {
                alice.Bridge.LocalVehicleHandle = 0;
                harness.Advance(0.2);
                alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f + (i * 5f), -810f, 30f));
                harness.Advance(1.0);
            }

            Assert.True(harness.Server.World.State.OfType<VehicleEntity>().Count() <= 2);
            Assert.True(alice.Client.OwnedEntities.SpawnsRejected > 0, "the limit was never reported to the client");
        }

        [Fact]
        public void ASpawnOutsideTheWorldIsRefused()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            var request = new Gtamp.Shared.Protocol.EntitySpawnRequestMessage
            {
                Type = EntityType.Vehicle,
                ModelHash = Adder,
                Position = new NetVector3(999999f, 999999f, 999999f),
                RequestTag = 7,
            };

            Gtamp.Shared.Protocol.EntityEventMessage reply = harness.Server.Entities.HandleSpawnRequest(
                harness.Server.Players.Sessions[0], request, harness.Server.Players);

            Assert.Equal(Gtamp.Shared.Protocol.EntityEventKind.SpawnRejected, reply.Kind);
            Assert.Contains("outside the world", reply.Detail);
        }

        [Fact]
        public void APlayerEntityCannotBeSpawnedByAClient()
        {
            using var harness = new TestHarness(Config());
            Connected(harness, "alice");

            var request = new Gtamp.Shared.Protocol.EntitySpawnRequestMessage
            {
                Type = EntityType.Player,
                RequestTag = 1,
                Position = new NetVector3(0f, 0f, 30f),
            };

            Gtamp.Shared.Protocol.EntityEventMessage reply = harness.Server.Entities.HandleSpawnRequest(
                harness.Server.Players.Sessions[0], request, harness.Server.Players);

            Assert.Equal(Gtamp.Shared.Protocol.EntityEventKind.SpawnRejected, reply.Kind);
        }
    }

    public class DamageReplicationTests
    {
        private static readonly uint Pistol = GameHash.Joaat("WEAPON_PISTOL");

        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        [Fact]
        public void AReportedHitReducesTheTargetsHealthForEveryone()
        {
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                RespawnDelaySeconds = 60,
            });

            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            alice.Bridge.Sample.CurrentWeaponHash = Pistol;
            harness.Advance(0.3);

            PlayerEntity bobOnServer = harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!;
            int before = bobOnServer.Health;

            alice.Client.ReportDamage(bob.Client.LocalEntityId, Pistol, 45, bobOnServer.Position);

            Assert.True(
                harness.AdvanceUntil(() => bobOnServer.Health < before, timeoutSeconds: 5),
                "the hit never landed on the server");

            Assert.Equal(before - 45, bobOnServer.Health);

            // And it reaches the victim's own client as a correction.
            Assert.True(
                harness.AdvanceUntil(() => bob.Bridge.Sample.Health <= before - 45, timeoutSeconds: 5),
                "the victim's client never learned about the damage");
        }

        [Fact]
        public void AHitFromImpossiblyFarAwayIsRefused()
        {
            using var harness = new TestHarness();
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            // Move bob to the far side of the map, server-side.
            Assert.True(harness.Server.TeleportPlayer(
                harness.Server.Players.Sessions[1], new NetVector3(2000f, 4000f, 30f), 0f));
            harness.Advance(1.0);

            PlayerEntity bobOnServer = harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!;
            int before = bobOnServer.Health;

            alice.Client.ReportDamage(bob.Client.LocalEntityId, Pistol, 90, bobOnServer.Position);
            harness.Advance(1.0);

            Assert.Equal(before, bobOnServer.Health);
        }

        [Fact]
        public void AFatalHitKillsTheVictimThroughTheServersDeathPath()
        {
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                RespawnDelaySeconds = 60,
            });

            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            PlayerEntity bobOnServer = harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!;
            bobOnServer.Health = 20;
            bob.Bridge.Sample.Health = 20;
            harness.Advance(0.3);

            alice.Client.ReportDamage(bob.Client.LocalEntityId, Pistol, 80, bobOnServer.Position);

            Assert.True(
                harness.AdvanceUntil(
                    () => harness.Server.Players.Sessions[1].IsDead, timeoutSeconds: 5),
                "the fatal hit never killed the victim");

            Assert.Equal(0, bobOnServer.Health);
            Assert.True(bobOnServer.HasFlag(PlayerFlags.Dead));
        }

        [Fact]
        public void SustainedDamageIsNotUndoneByTheVictimsOwnReports()
        {
            // The victim's client keeps reporting the health it had before the hit. If
            // the server accepted that, every shot would land and then un-land.
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                RespawnDelaySeconds = 60,
            });

            harness.Latency = 0.06;

            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2, timeoutSeconds: 10));

            PlayerEntity bobOnServer = harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!;

            for (int shot = 0; shot < 4; shot++)
            {
                alice.Client.ReportDamage(bob.Client.LocalEntityId, Pistol, 30, bobOnServer.Position);
                harness.Advance(0.5);
            }

            harness.Advance(1.0);

            // Four 30-point hits on a 200-health player.
            Assert.Equal(80, harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!.Health);
        }

        [Fact]
        public void PvpCanBeSwitchedOff()
        {
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                PlayerVersusPlayer = false,
            });

            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            PlayerEntity bobOnServer = harness.Server.World.GetPlayer(bob.Client.LocalEntityId)!;
            int before = bobOnServer.Health;

            alice.Client.ReportDamage(bob.Client.LocalEntityId, Pistol, 45, bobOnServer.Position);
            harness.Advance(1.0);

            Assert.Equal(before, bobOnServer.Health);
        }
    }
}
