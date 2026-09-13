using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    public class SnapshotTests
    {
        private static readonly EntityRegistry Registry = EntityRegistry.CreateDefault();

        private static PlayerEntity Player(uint id, float x, string name = "player") => new PlayerEntity(new EntityId(id))
        {
            PlayerId = id,
            Name = name,
            Position = new NetVector3(x, 0f, 30f),
            Health = 200,
        };

        private static List<NetEntity> Order(WorldState world)
        {
            var list = new List<NetEntity>();
            foreach (NetEntity entity in world.Entities)
            {
                list.Add(entity);
            }

            return list;
        }

        [Fact]
        public void FullSnapshotReconstructsTheWorld()
        {
            var world = new WorldState { Tick = 10, ServerTime = 1.5 };
            world.Add(Player(1, 100f, "alice"));
            world.Add(Player(2, 200f, "bob"));

            SnapshotWriteResult written = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, Order(world), 1, 1024);

            SnapshotApplyResult applied = SnapshotCodec.Apply(written.Payload, EntitySnapshotView.Empty, Registry);

            Assert.True(applied.Header.IsFullSnapshot);
            Assert.Equal(2, applied.View.Count);
            Assert.Equal(10u, applied.View.Tick);
            Assert.Equal(1.5, applied.View.ServerTime);

            var alice = (PlayerEntity)applied.View.GetOrNull(new EntityId(1))!;
            Assert.Equal("alice", alice.Name);
            Assert.Equal(100f, alice.Position.X, 2);
        }

        [Fact]
        public void DeltaSnapshotCarriesOnlyWhatChanged()
        {
            var world = new WorldState { Tick = 1 };
            var alice = Player(1, 100f, "alice");
            var bob = Player(2, 200f, "bob");
            world.Add(alice);
            world.Add(bob);

            SnapshotWriteResult full = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            EntitySnapshotView baseline = full.ResultingView;

            alice.Position = new NetVector3(101f, 0f, 30f);
            world.Tick = 2;

            SnapshotWriteResult delta = SnapshotCodec.Write(world, baseline, Registry, Order(world), 2, 4096);

            Assert.Equal(1, delta.DeltaEntityCount);
            Assert.Equal(0, delta.NewEntityCount);
            Assert.True(delta.Payload.Length < full.Payload.Length);

            SnapshotApplyResult applied = SnapshotCodec.Apply(delta.Payload, baseline, Registry);
            Assert.Equal(2, applied.View.Count);
            Assert.Equal(101f, applied.View.GetOrNull(new EntityId(1))!.Position.X, 2);
            Assert.Equal(200f, applied.View.GetOrNull(new EntityId(2))!.Position.X, 2);
            Assert.Equal("bob", ((PlayerEntity)applied.View.GetOrNull(new EntityId(2))!).Name);
        }

        [Fact]
        public void RemovedEntitiesAreReplicated()
        {
            var world = new WorldState { Tick = 1 };
            world.Add(Player(1, 100f));
            world.Add(Player(2, 200f));

            SnapshotWriteResult full = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            world.Remove(new EntityId(2));

            SnapshotWriteResult delta = SnapshotCodec.Write(world, full.ResultingView, Registry, Order(world), 2, 4096);
            Assert.Single(delta.RemovedIds);

            SnapshotApplyResult applied = SnapshotCodec.Apply(delta.Payload, full.ResultingView, Registry);
            Assert.Equal(1, applied.View.Count);
            Assert.False(applied.View.Contains(new EntityId(2)));
        }

        [Fact]
        public void ANewEntityIsSentAsFullStateInsideADeltaSnapshot()
        {
            var world = new WorldState { Tick = 1 };
            world.Add(Player(1, 100f));

            SnapshotWriteResult first = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            world.Add(Player(2, 200f, "late-joiner"));

            SnapshotWriteResult second = SnapshotCodec.Write(world, first.ResultingView, Registry, Order(world), 2, 4096);
            Assert.Equal(1, second.NewEntityCount);

            SnapshotApplyResult applied = SnapshotCodec.Apply(second.Payload, first.ResultingView, Registry);
            Assert.Equal("late-joiner", ((PlayerEntity)applied.View.GetOrNull(new EntityId(2))!).Name);
        }

        [Fact]
        public void ApplyingAgainstTheWrongBaselineIsRejectedRatherThanDesynchronising()
        {
            var world = new WorldState { Tick = 1 };
            world.Add(Player(1, 100f));

            SnapshotWriteResult first = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            SnapshotWriteResult second = SnapshotCodec.Write(world, first.ResultingView, Registry, Order(world), 2, 4096);

            Assert.Throws<NetSerializationException>(() =>
                SnapshotCodec.Apply(second.Payload, EntitySnapshotView.Empty, Registry));
        }

        [Fact]
        public void ByteBudgetDefersEntitiesInsteadOfDroppingThem()
        {
            var world = new WorldState { Tick = 1 };
            for (uint i = 1; i <= 60; i++)
            {
                world.Add(Player(i, i * 10f, "player-with-a-long-name-" + i));
            }

            SnapshotWriteResult constrained = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, Registry, Order(world), 1, 512);

            Assert.True(constrained.Payload.Length <= 512, $"payload was {constrained.Payload.Length} B");
            Assert.True(constrained.DeferredCount > 0, "some entities should have been deferred");
            Assert.True(constrained.NewEntityCount > 0, "some entities should still have been written");
            Assert.Equal(60, constrained.NewEntityCount + constrained.DeferredCount);

            // The deferred ones must arrive in a later snapshot, not be lost.
            SnapshotWriteResult next = SnapshotCodec.Write(
                world, constrained.ResultingView, Registry, Order(world), 2, 512);

            Assert.True(next.NewEntityCount > 0, "the next snapshot must carry the deferred entities");
        }

        [Fact]
        public void ConvergesOnTheWholeWorldAcrossSeveralBudgetedSnapshots()
        {
            var world = new WorldState { Tick = 1 };
            for (uint i = 1; i <= 40; i++)
            {
                world.Add(Player(i, i * 10f));
            }

            EntitySnapshotView serverView = EntitySnapshotView.Empty;
            EntitySnapshotView clientView = EntitySnapshotView.Empty;

            for (uint snapshotId = 1; snapshotId <= 20; snapshotId++)
            {
                SnapshotWriteResult result = SnapshotCodec.Write(world, serverView, Registry, Order(world), snapshotId, 400);
                clientView = SnapshotCodec.Apply(result.Payload, clientView, Registry).View;
                serverView = result.ResultingView;
            }

            Assert.Equal(40, clientView.Count);
        }

        [Fact]
        public void EnvironmentIsSentOnlyWhenItChanges()
        {
            var world = new WorldState { Tick = 1 };
            world.Environment.SetTime(12, 0, 0);
            world.Add(Player(1, 100f));

            SnapshotWriteResult first = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            Assert.True(first.EnvironmentIncluded);

            SnapshotWriteResult second = SnapshotCodec.Write(world, first.ResultingView, Registry, Order(world), 2, 4096);
            Assert.False(second.EnvironmentIncluded);

            world.Environment.SetTime(18, 30, 0);
            SnapshotWriteResult third = SnapshotCodec.Write(world, second.ResultingView, Registry, Order(world), 3, 4096);
            Assert.True(third.EnvironmentIncluded);

            EntitySnapshotView view = SnapshotCodec.Apply(first.Payload, EntitySnapshotView.Empty, Registry).View;
            view = SnapshotCodec.Apply(second.Payload, view, Registry).View;
            view = SnapshotCodec.Apply(third.Payload, view, Registry).View;

            Assert.Equal(18, view.Environment.Hours);
            Assert.Equal(30, view.Environment.Minutes);
        }

        [Fact]
        public void BaselineViewsShareUnchangedEntityInstances()
        {
            var world = new WorldState { Tick = 1 };
            world.Add(Player(1, 100f));
            world.Add(Player(2, 200f));

            SnapshotWriteResult first = SnapshotCodec.Write(world, EntitySnapshotView.Empty, Registry, Order(world), 1, 4096);
            world.Get<PlayerEntity>(new EntityId(1))!.Position = new NetVector3(150f, 0f, 30f);
            SnapshotWriteResult second = SnapshotCodec.Write(world, first.ResultingView, Registry, Order(world), 2, 4096);

            // Entity 2 did not change, so the newer view must reference the same
            // object rather than a copy: that is what keeps a 64-deep history cheap.
            Assert.Same(
                first.ResultingView.GetOrNull(new EntityId(2)),
                second.ResultingView.GetOrNull(new EntityId(2)));

            Assert.NotSame(
                first.ResultingView.GetOrNull(new EntityId(1)),
                second.ResultingView.GetOrNull(new EntityId(1)));
        }

        [Fact]
        public void SnapshotHistoryEvictsOldestFirstAndAlwaysResolvesTheEmptyBaseline()
        {
            var history = new SnapshotHistory(4);
            var world = new WorldState();
            world.Add(Player(1, 0f));

            EntitySnapshotView view = EntitySnapshotView.Empty;
            for (uint i = 1; i <= 6; i++)
            {
                view = SnapshotCodec.Write(world, view, Registry, Order(world), i, 4096).ResultingView;
                history.Store(view);
            }

            Assert.Equal(4, history.Count);
            Assert.False(history.TryGet(1, out _));
            Assert.True(history.TryGet(6, out _));
            Assert.True(history.TryGet(0, out EntitySnapshotView empty));
            Assert.Equal(0u, empty.SnapshotId);
            Assert.Equal(6u, history.Latest.SnapshotId);
        }
    }
}
