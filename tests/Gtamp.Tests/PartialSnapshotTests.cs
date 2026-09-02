using System.Collections.Generic;
using Gtamp.Client.Entities;
using Gtamp.Client.World;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Client.Mods;
using Gtamp.Client.Players;
using Gtamp.Server.Replication;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// A snapshot the byte budget cut short is a real but incomplete picture of the
    /// world. Everything here is about the difference between "this entity is gone"
    /// and "this entity has not arrived yet" — which the client used to conflate,
    /// destroying and rebuilding every car it had not been sent this frame.
    /// </summary>
    public class PartialSnapshotTests
    {
        private static readonly EntityRegistry Registry = EntityRegistry.CreateDefault();

        private static VehicleEntity Vehicle(uint id, float x) => new VehicleEntity(new EntityId(id))
        {
            OwnerId = 0,
            ModelHash = 0x1234u + id,
            Position = new NetVector3(x, 0f, 30f),
            EngineHealth = 1000f,
            BodyHealth = 1000f,
            PetrolTankHealth = 1000f,
        };

        private static PlayerEntity Player(uint id, float x) => new PlayerEntity(new EntityId(id))
        {
            PlayerId = id,
            Name = "p" + id,
            Position = new NetVector3(x, 0f, 30f),
            Health = 200,
        };

        private static List<NetEntity> AllOf(WorldState world)
        {
            var list = new List<NetEntity>();
            foreach (NetEntity entity in world.Entities)
            {
                list.Add(entity);
            }

            return list;
        }

        private static WorldState CrowdedWorld(int vehicles)
        {
            var world = new WorldState { Tick = 1, ServerTime = 1d };
            for (uint i = 1; i <= (uint)vehicles; i++)
            {
                world.Add(Vehicle(i, i * 10f));
            }

            return world;
        }

        /// <summary>
        /// The defect a joining player actually saw. Two full snapshots go out before
        /// the first acknowledgement can possibly arrive; both are written against
        /// nothing. They must describe the same world. They used not to: writing an
        /// entity demoted it, so the second snapshot carried the entities the first
        /// had left out and told the client the first half no longer existed.
        /// </summary>
        [Fact]
        public void TwoFullSnapshotsSentBeforeAnyAcknowledgementDescribeTheSameWorld()
        {
            WorldState world = CrowdedWorld(20);
            var state = new ClientReplicationState();
            var viewer = new NetVector3(0f, 0f, 30f);

            SnapshotWriteResult first = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry,
                ReplicationPriority.Order(world.Entities, viewer, world.Tick, state, EntityId.None),
                state.AllocateSnapshotId(), 512);
            state.RecordSent(first, world.Tick);

            // No Acknowledge() in between: the client has not answered yet, so the
            // server is still writing against nothing.
            SnapshotWriteResult second = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry,
                ReplicationPriority.Order(world.Entities, viewer, world.Tick, state, EntityId.None),
                state.AllocateSnapshotId(), 512);

            Assert.True(first.DeferredCount > 0, "the budget must be tight enough to defer, or this proves nothing");
            Assert.Equal(
                new List<EntityId>(first.ResultingView.Ids).ConvertAll(id => id.Value).ToArray(),
                new List<EntityId>(second.ResultingView.Ids).ConvertAll(id => id.Value).ToArray());
        }

        [Fact]
        public void OnceTheClientAcknowledgesTheDeferredEntitiesAreSentNext()
        {
            WorldState world = CrowdedWorld(20);
            var state = new ClientReplicationState();
            var viewer = new NetVector3(0f, 0f, 30f);

            SnapshotWriteResult first = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry,
                ReplicationPriority.Order(world.Entities, viewer, world.Tick, state, EntityId.None),
                state.AllocateSnapshotId(), 512);
            state.RecordSent(first, world.Tick);
            state.Acknowledge(first.SnapshotId);

            SnapshotWriteResult second = SnapshotCodec.Write(
                world, state.Baseline, Registry,
                ReplicationPriority.Order(world.Entities, viewer, world.Tick, state, EntityId.None),
                state.AllocateSnapshotId(), 512);

            // Everything the first snapshot could not fit is what the second leads with.
            foreach (EntityId id in first.ResultingView.Ids)
            {
                Assert.True(second.ResultingView.Contains(id), $"{id} was dropped after being acknowledged");
            }

            Assert.True(
                second.ResultingView.Count > first.ResultingView.Count,
                $"the world should be converging: {first.ResultingView.Count} then {second.ResultingView.Count}");
        }

        [Fact]
        public void ASnapshotThatDidNotFitSaysSoAndOneThatFitsSaysSo()
        {
            WorldState world = CrowdedWorld(20);

            SnapshotWriteResult cramped = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 512);
            Assert.True(cramped.DeferredCount > 0);
            Assert.False(cramped.ResultingView.IsComplete);
            Assert.False(SnapshotCodec.Apply(cramped.Payload, EntitySnapshotView.Empty, Registry).Header.DescribesWholeWorld);

            SnapshotWriteResult roomy = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 2, 64 * 1024);
            Assert.Equal(0, roomy.DeferredCount);
            Assert.True(roomy.ResultingView.IsComplete);
            Assert.True(SnapshotCodec.Apply(roomy.Payload, EntitySnapshotView.Empty, Registry).Header.DescribesWholeWorld);
        }

        /// <summary>
        /// Completeness is a property of the reconstructed world, not of one packet:
        /// a delta that fits does not make the world whole if its baseline was
        /// already missing entities.
        /// </summary>
        [Fact]
        public void AViewStaysIncompleteUntilEverythingHasActuallyArrived()
        {
            WorldState world = CrowdedWorld(20);

            SnapshotWriteResult first = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 512);
            Assert.False(first.ResultingView.IsComplete);

            // A tiny delta against that partial baseline: it fits, but the world is
            // still missing everything the first snapshot deferred.
            world.Get<VehicleEntity>(new EntityId(1))!.Position = new NetVector3(5f, 0f, 30f);
            var justOne = new List<NetEntity> { world.Get<VehicleEntity>(new EntityId(1))! };
            SnapshotWriteResult second = SnapshotCodec.Write(
                world, first.ResultingView, Registry, justOne, 2, 64 * 1024);

            Assert.Equal(0, second.DeferredCount);
            Assert.False(second.ResultingView.IsComplete);

            SnapshotWriteResult third = SnapshotCodec.Write(
                world, second.ResultingView, Registry, AllOf(world), 3, 64 * 1024);
            Assert.True(third.ResultingView.IsComplete);
        }

        [Fact]
        public void AVehicleMissingOnlyBecauseTheBudgetRanOutIsNotDestroyed()
        {
            var bridge = new FakeGameBridge();
            var log = new LogBus();
            var entities = new RemoteEntityManager(bridge, log, new MissingContentTracker(log)) { LocalPlayerId = 7 };

            WorldState world = CrowdedWorld(20);
            SnapshotWriteResult whole = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 64 * 1024);
            entities.Sync(whole.ResultingView);
            entities.Render(world.ServerTime);
            int built = bridge.Vehicles.Count;
            Assert.True(built > 0, "the first snapshot should have built the world");

            // The next snapshot is written against nothing and only half of it fits.
            SnapshotWriteResult cramped = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 2, 512);
            Assert.True(cramped.DeferredCount > 0);
            entities.Sync(cramped.ResultingView);
            entities.Render(world.ServerTime);

            Assert.Equal(built, bridge.Vehicles.Count);
        }

        [Fact]
        public void APlayerMissingOnlyBecauseTheBudgetRanOutIsNotDespawned()
        {
            var bridge = new FakeGameBridge();
            var log = new LogBus();
            var players = new RemotePlayerManager(bridge, log, new MissingContentTracker(log)) { LocalEntityId = new EntityId(99) };

            var world = new WorldState { Tick = 1, ServerTime = 1d };
            world.Add(Player(1, 10f));
            world.Add(Player(2, 20f));

            SnapshotWriteResult whole = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 64 * 1024);
            players.Sync(whole.ResultingView);
            Assert.Equal(2, players.Count);

            // Only one player fits. The other has not left the server.
            var justOne = new List<NetEntity> { world.Get<PlayerEntity>(new EntityId(1))! };
            SnapshotWriteResult cramped = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 2, 120);
            Assert.True(cramped.DeferredCount > 0, "the budget must be tight enough to defer a player");
            players.Sync(cramped.ResultingView);

            Assert.Equal(2, players.Count);
        }

        /// <summary>
        /// The streamer gives an owned entity five seconds of grace before deciding a
        /// snapshot that never carries it means the server let it go. A busy server can
        /// leave the same vehicle out of the budget for longer than that, so the grace
        /// timer must not run at all while the view is incomplete — otherwise the
        /// player hands back a car they are still driving.
        /// </summary>
        [Fact]
        public void AnOwnedVehicleIsNotHandedBackWhileTheViewIsIncomplete()
        {
            var bridge = new FakeGameBridge();
            bridge.PutLocalPlayerInVehicle(0x1B38E955u, new NetVector3(10f, 20f, 30f));

            var sent = new List<byte[]>();
            var streamer = new OwnedEntityStreamer(bridge, Registry, new LogBus())
            {
                LocalPlayerId = 1,
                Send = (type, payload, delivery) =>
                {
                    if (type == NetMessageType.EntitySpawnRequest)
                    {
                        sent.Add(payload);
                    }
                },
            };

            streamer.RegisterLocalVehicleIfNeeded(EntitySnapshotView.Empty, 100d);
            streamer.HandleEntityEvent(new EntityEventMessage
            {
                Kind = EntityEventKind.SpawnAccepted,
                EntityId = new EntityId(7),
                RequestTag = EntitySpawnRequestMessage.Deserialize(sent[0]).RequestTag,
            });
            Assert.Equal(1, streamer.OwnedCount);

            // A crowded world that does not contain #7, sent under a budget too tight
            // to hold it: incomplete, and it stays incomplete far longer than the grace.
            var world = new WorldState { Tick = 1, ServerTime = 1d };
            for (uint i = 100; i < 120; i++)
            {
                world.Add(Vehicle(i, i * 10f));
            }

            EntitySnapshotView cramped = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 512).ResultingView;
            Assert.False(cramped.IsComplete);

            // Two passes far enough apart to outlast the grace several times over.
            // Without the guard the second one hands the vehicle back.
            streamer.Stream(cramped, 100d, 0d);
            streamer.Stream(cramped, 100d + (OwnedEntityStreamer.MissingEntityGrace * 4d), 0d);

            Assert.Equal(1, streamer.OwnedCount);

            // A complete view that still does not carry it does let it go, so this has
            // not become "an owned entity is never released".
            EntitySnapshotView whole = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 2, 64 * 1024).ResultingView;
            Assert.True(whole.IsComplete);

            double t = 200d;
            streamer.Stream(whole, t, 0d);
            Assert.Equal(1, streamer.OwnedCount);
            streamer.Stream(whole, t + (OwnedEntityStreamer.MissingEntityGrace * 2d), 0d);

            Assert.Equal(0, streamer.OwnedCount);
        }

        /// <summary>
        /// The client's own history evicted the baseline the server was still writing
        /// against, and then could not decode anything.
        /// <para>
        /// It happens whenever the client falls behind: the server keeps encoding
        /// against the last snapshot it heard acknowledged, the client comes back and
        /// applies the backlog, and every applied snapshot stores a view. After
        /// `SnapshotHistory` of them the baseline every one of those snapshots names
        /// falls out of the ring, and the rest of the backlog is undecodable — one
        /// resync, and a run of dropped snapshots exactly as long as the overrun.
        /// </para>
        /// </summary>
        [Fact]
        public void TheBaselineTheServerIsStillWritingAgainstSurvivesACatchUpBurst()
        {
            var world = new WorldState { Tick = 1, ServerTime = 1d };
            world.Add(Vehicle(1, 10f));
            world.Add(Vehicle(2, 20f));

            var client = new ReplicatedWorld(Registry);

            SnapshotWriteResult full = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 64 * 1024);
            Assert.True(client.TryApply(full.Payload, out _, out string firstError), firstError);

            // The client's acknowledgement never reaches the server, so every snapshot
            // in the burst is written against snapshot 1. More of them arrive than the
            // history is deep.
            int burst = ProtocolConstants.SnapshotHistory + 8;
            for (uint i = 0; i < burst; i++)
            {
                world.Tick++;
                world.ServerTime += 0.05d;
                world.Get<VehicleEntity>(new EntityId(1))!.Position = new NetVector3(10f + i, 0f, 30f);

                SnapshotWriteResult delta = SnapshotCodec.Write(
                    world, full.ResultingView, Registry, AllOf(world), 2 + i, 64 * 1024);

                Assert.True(
                    client.TryApply(delta.Payload, out _, out string error),
                    $"snapshot {2 + i} of {burst} was rejected: {error}");
            }

            Assert.Equal(0, client.SnapshotsDropped);
        }

        /// <summary>
        /// The fix must not become "never remove anything". A complete snapshot that
        /// leaves an entity out still means it is gone.
        /// </summary>
        [Fact]
        public void AVehicleTheServerReallyRemovedIsStillDestroyed()
        {
            var bridge = new FakeGameBridge();
            var log = new LogBus();
            var entities = new RemoteEntityManager(bridge, log, new MissingContentTracker(log)) { LocalPlayerId = 7 };

            WorldState world = CrowdedWorld(4);
            SnapshotWriteResult whole = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, AllOf(world), 1, 64 * 1024);
            entities.Sync(whole.ResultingView);
            entities.Render(world.ServerTime);
            int built = bridge.Vehicles.Count;
            Assert.Equal(4, built);

            world.Remove(new EntityId(2));
            SnapshotWriteResult afterRemoval = SnapshotCodec.Write(
                world, whole.ResultingView, Registry, AllOf(world), 2, 64 * 1024);
            Assert.True(afterRemoval.ResultingView.IsComplete);
            entities.Sync(afterRemoval.ResultingView);
            entities.Render(world.ServerTime);

            Assert.Equal(3, bridge.Vehicles.Count);
        }
    }
}
