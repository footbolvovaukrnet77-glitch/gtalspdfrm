using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Section 14's physics and destruction.
    /// <para>
    /// <c>ObjectFlags.Dynamic</c>, <c>ObjectFlags.Broken</c> and <c>Health</c> were
    /// declared, serialised, delta encoded and persisted, and no native was ever called
    /// for any of them. A crate the owner had pushed down a hill was a physics object on
    /// their screen and a placed one on everybody else's; a barrier somebody had
    /// smashed was intact everywhere else.
    /// </para>
    /// </summary>
    public class ObjectPhysicsTests
    {
        private static ObjectEntity RoundTrip(ObjectEntity from, ObjectEntity to)
        {
            INetEntitySerializer serializer = EntityRegistry.CreateDefault().Get((byte)EntityType.Object);
            var writer = new NetWriter(256);
            serializer.WriteDelta(writer, from, to);

            var applied = (ObjectEntity)from.Clone();
            serializer.ReadDelta(new NetReader(writer.ToArray()), applied);
            return applied;
        }

        /// <summary>
        /// A physics object given only positions is corrected by the solver on every
        /// frame it receives one, which is the twitching a replicated ragdoll used to
        /// have. Given the velocity as well it carries on moving between updates and
        /// the positions become corrections rather than instructions.
        /// </summary>
        [Fact]
        public void AMovingObjectsVelocityTravels()
        {
            var before = new ObjectEntity(new EntityId(5));
            var after = (ObjectEntity)before.Clone();
            after.Velocity = new NetVector3(3.5f, -1.25f, 0.75f);

            ObjectEntity applied = RoundTrip(before, after);

            Assert.Equal(3.5f, applied.Velocity.X, 1);
            Assert.Equal(-1.25f, applied.Velocity.Y, 1);
        }

        /// <summary>
        /// Most objects never move at all. A field written twenty times a second to say
        /// "still nought" is the kind of cost that ends up mattering on a wire already
        /// at its MTU ceiling.
        /// </summary>
        [Fact]
        public void AStationaryObjectDoesNotPayForAVelocityField()
        {
            var before = new ObjectEntity(new EntityId(5));
            var still = (ObjectEntity)before.Clone();
            var moving = (ObjectEntity)before.Clone();
            moving.Velocity = new NetVector3(1f, 0f, 0f);

            INetEntitySerializer serializer = EntityRegistry.CreateDefault().Get((byte)EntityType.Object);

            var stillWriter = new NetWriter(256);
            serializer.WriteDelta(stillWriter, before, still);

            var movingWriter = new NetWriter(256);
            serializer.WriteDelta(movingWriter, before, moving);

            Assert.True(movingWriter.Length > stillWriter.Length);
        }

        [Fact]
        public void BrokenAndHealthSurviveTheWire()
        {
            var before = new ObjectEntity(new EntityId(9));
            var after = (ObjectEntity)before.Clone();
            after.Health = 0;
            after.SetFlag(ObjectFlags.Broken, true);

            ObjectEntity applied = RoundTrip(before, after);

            Assert.Equal(0, applied.Health);
            Assert.True(applied.HasFlag(ObjectFlags.Broken));
        }

        /// <summary>
        /// Frozen and dynamic are contradictory, and the contradiction is resolvable
        /// only one way: a frozen object does not move whatever else it claims. Letting
        /// both through would hand the game a dynamic object and then forbid it to move,
        /// which on this engine means it twitches against its own solver.
        /// </summary>
        [Fact]
        public void FrozenWinsOverDynamic()
        {
            var entity = new ObjectEntity(new EntityId(3));
            entity.SetFlag(ObjectFlags.Dynamic, true);
            entity.SetFlag(ObjectFlags.Frozen, true);

            // The bridge computes `Dynamic && !Frozen`; this is that rule, stated where
            // it can be read without a game.
            bool dynamic = entity.HasFlag(ObjectFlags.Dynamic) && !entity.HasFlag(ObjectFlags.Frozen);

            Assert.False(dynamic);
        }

        [Fact]
        public void AnObjectThatIsOnlyDynamicIsSimulated()
        {
            var entity = new ObjectEntity(new EntityId(3));
            entity.SetFlag(ObjectFlags.Dynamic, true);

            Assert.True(entity.HasFlag(ObjectFlags.Dynamic) && !entity.HasFlag(ObjectFlags.Frozen));
        }
    }
}
