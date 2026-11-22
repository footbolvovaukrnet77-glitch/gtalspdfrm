using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Keeps <see cref="EntityType"/>'s documentation honest.
    /// <para>
    /// Five of its values are ids with no class and no serializer behind them. That is
    /// legitimate — a wire format reserves ids ahead of use, and renumbering later to
    /// close a gap breaks every client that already speaks it — but only while the
    /// distinction is visible. A reader who cannot tell a reserved id from a working
    /// type will assume <c>Projectile</c> replicates something.
    /// </para>
    /// <para>
    /// So the comment is enforced rather than trusted. If someone implements one of the
    /// reserved types, this test fails and tells them to move it in the documentation;
    /// if someone removes a working one, it fails too.
    /// </para>
    /// </summary>
    public class EntityTypeTests
    {
        [Theory]
        [InlineData(EntityType.Player)]
        [InlineData(EntityType.Vehicle)]
        [InlineData(EntityType.Ped)]
        [InlineData(EntityType.Object)]
        [InlineData(EntityType.Mission)]
        public void EveryTypeDocumentedAsImplementedHasASerializer(EntityType type)
        {
            EntityRegistry registry = EntityRegistry.CreateDefault();

            Assert.True(
                registry.TryGet((byte)type, out _),
                $"{type} is documented as implemented but no serializer is registered for it.");
        }

        [Theory]
        [InlineData(EntityType.Weapon)]
        [InlineData(EntityType.Projectile)]
        [InlineData(EntityType.Pickup)]
        [InlineData(EntityType.Door)]
        [InlineData(EntityType.Marker)]
        public void EveryTypeDocumentedAsReservedHasNone(EntityType type)
        {
            EntityRegistry registry = EntityRegistry.CreateDefault();

            Assert.False(
                registry.TryGet((byte)type, out _),
                $"{type} now has a serializer. It is documented as reserved and not implemented — "
                + "move it into the implemented group in EntityType and update this test.");
        }
    }
}
