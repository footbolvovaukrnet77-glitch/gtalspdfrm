using System;
using Gtamp.Client.Core;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Which mod put an entity type in the world.
    /// <para>
    /// Master prompt section 44 asks the entity inspector to name the mod and the
    /// adapter behind an entity. It named neither, and nothing recorded either — so
    /// the answer to &#34;what put a type nobody claims in my world&#34; was nothing at all, which is
    /// the least useful thing a diagnostic can say in a framework whose whole premise
    /// is third-party mods.
    /// </para>
    /// </summary>
    public class EntityAttributionTests
    {
        [Fact]
        public void ATypeAModRegisteredNamesTheMod()
        {
            var registry = EntityRegistry.CreateDefault();
            registry.Register(new TurretSerializer(), "TrafficPolicyMod");

            Assert.Equal("TrafficPolicyMod", registry.OwnerOf(TurretEntity.WireTypeId));
        }

        /// <summary>
        /// And the framework's own types report nothing, which is the answer rather
        /// than a hole: an unclaimed type came from here, not from a mod.
        /// </summary>
        [Fact]
        public void TheFrameworksOwnTypesAreUnattributed()
        {
            var registry = EntityRegistry.CreateDefault();

            Assert.Null(registry.OwnerOf((byte)EntityType.Player));
            Assert.Null(registry.OwnerOf((byte)EntityType.Vehicle));
        }

        /// <summary>
        /// A mod that does not name itself still registers. Refusing would break every
        /// mod written against the older signature to gain a diagnostic, which is the
        /// wrong trade; it is reported as unattributed instead.
        /// </summary>
        [Fact]
        public void AModThatDoesNotNameItselfStillRegisters()
        {
            var registry = EntityRegistry.CreateDefault();
            registry.Register(new TurretSerializer());

            Assert.True(registry.TryGet(TurretEntity.WireTypeId, out INetEntitySerializer serializer));
            Assert.Equal("mod.turret", serializer.TypeName);
            Assert.Null(registry.OwnerOf(TurretEntity.WireTypeId));
        }

        [Fact]
        public void TheSdkPassesTheModNameThrough()
        {
            var registry = EntityRegistry.CreateDefault();
            var sdk = new ModSdk(registry, new Gtamp.Shared.Diagnostics.LogBus(), (name, payload, reliable) => true);

            byte assigned = sdk.RegisterEntity(new TurretSerializer(), "TrafficPolicyMod");

            Assert.Equal(TurretEntity.WireTypeId, assigned);
            Assert.Equal("TrafficPolicyMod", registry.OwnerOf(assigned));
        }
    }
}
