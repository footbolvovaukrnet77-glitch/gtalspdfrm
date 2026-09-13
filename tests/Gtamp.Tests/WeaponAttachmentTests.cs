using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// What is bolted to the weapon, not just which weapon it is.
    /// <para>
    /// The weapon hash names a carbine. It does not say the carbine has a suppressor,
    /// a scope and an extended clip, so a remote player carrying one appeared with a
    /// bare rifle — correct in the arbiter's eyes and wrong in everybody's.
    /// </para>
    /// </summary>
    public class WeaponAttachmentTests
    {
        private static readonly uint Rifle = GameHash.Joaat("WEAPON_CARBINERIFLE");
        private static readonly uint Suppressor = GameHash.Joaat("COMPONENT_AT_AR_SUPP_02");
        private static readonly uint Scope = GameHash.Joaat("COMPONENT_AT_SCOPE_MEDIUM");

        [Fact]
        public void AttachmentsSurviveTheEntityDelta()
        {
            var baseline = new PlayerEntity(new EntityId(4));
            var current = new PlayerEntity(new EntityId(4)) { CurrentWeaponHash = Rifle, WeaponTint = 3 };
            current.WeaponComponents.Add(Suppressor);
            current.WeaponComponents.Add(Scope);

            var serializer = new PlayerEntitySerializer();
            var writer = new NetWriter(128);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new PlayerEntity(new EntityId(4));
            serializer.ReadDelta(new NetReader(writer.ToArray()), applied);

            Assert.Equal(3, applied.WeaponTint);
            Assert.Equal(new[] { Suppressor, Scope }, applied.WeaponComponents);
        }

        [Fact]
        public void AnUnchangedWeaponCostsNothing()
        {
            var state = new PlayerEntity(new EntityId(4)) { WeaponTint = 3 };
            state.WeaponComponents.Add(Suppressor);
            var same = (PlayerEntity)state.Clone();

            var withAttachments = new NetWriter(128);
            new PlayerEntitySerializer().WriteDelta(withAttachments, state, same);

            var bare = new NetWriter(128);
            new PlayerEntitySerializer().WriteDelta(
                bare, new PlayerEntity(new EntityId(4)), new PlayerEntity(new EntityId(4)));

            Assert.Equal(bare.ToArray().Length, withAttachments.ToArray().Length);
        }

        [Fact]
        public void ACloneCarriesThemAndDoesNotShareTheList()
        {
            var entity = new PlayerEntity(new EntityId(4)) { WeaponTint = 2 };
            entity.WeaponComponents.Add(Suppressor);

            var clone = (PlayerEntity)entity.Clone();
            entity.WeaponComponents.Add(Scope);

            // A shared list would make every buffered sample show the newest
            // attachments, which is the interpolation buffer quietly not buffering.
            Assert.Equal(2, clone.WeaponTint);
            Assert.Single(clone.WeaponComponents);
        }

        [Fact]
        public void AHostileComponentCountIsRefused()
        {
            // An unbounded count is an allocation the sender gets to choose. Written
            // through the real serializer, so the test fails if the writer is ever
            // changed to cap silently instead of the reader refusing.
            var baseline = new PlayerEntity(new EntityId(4));
            var flood = new PlayerEntity(new EntityId(4));
            for (int i = 0; i <= CharacterEntity.MaxWeaponComponents; i++)
            {
                flood.WeaponComponents.Add((uint)i);
            }

            var serializer = new PlayerEntitySerializer();
            var writer = new NetWriter(256);
            serializer.WriteDelta(writer, baseline, flood);

            Assert.Throws<NetSerializationException>(
                () => serializer.ReadDelta(new NetReader(writer.ToArray()), new PlayerEntity(new EntityId(4))));
        }

        [Fact]
        public void AClientUpdateCarriesThemAndRefusesTooMany()
        {
            var sent = new ClientStateUpdateMessage { CurrentWeaponHash = Rifle, WeaponTint = 5 };
            sent.WeaponComponents.Add(Suppressor);

            ClientStateUpdateMessage received = ClientStateUpdateMessage.Deserialize(sent.Serialize());

            Assert.Equal(5, received.WeaponTint);
            Assert.Equal(new[] { Suppressor }, received.WeaponComponents);

            var flood = new ClientStateUpdateMessage();
            for (int i = 0; i <= CharacterEntity.MaxWeaponComponents; i++)
            {
                flood.WeaponComponents.Add((uint)i);
            }

            Assert.Throws<NetSerializationException>(() => ClientStateUpdateMessage.Deserialize(flood.Serialize()));
        }

        [Fact]
        public void TheCommandCarriesThemOnEveryBranch()
        {
            // Decide returns early for dead, ragdoll and in-vehicle, which is exactly
            // where a new field gets added to the walking path and forgotten.
            var components = new List<uint> { Suppressor };

            foreach (PlayerFlags flags in new[]
            {
                PlayerFlags.None, PlayerFlags.Dead, PlayerFlags.Ragdoll, PlayerFlags.InVehicle,
            })
            {
                var frame = new RemotePedFrame
                {
                    Position = new NetVector3(10f, 10f, 30f),
                    Health = flags == PlayerFlags.Dead ? 0 : 200,
                    Flags = flags,
                    CurrentWeaponHash = Rifle,
                    WeaponTint = 4,
                    WeaponComponents = components,
                };

                RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

                Assert.Equal(4, command.WeaponTint);
                Assert.Same(components, command.WeaponComponents);
            }
        }

        [Fact]
        public void NotReadIsNotTheSameAsNothingFitted()
        {
            // Null means the reporting client could not read the components; empty
            // means it read them and there are none. Collapsing the two strips the
            // suppressor off a remote player every time a read fails.
            var unread = new RemotePedFrame
            {
                Position = NetVector3.Zero, Health = 200, CurrentWeaponHash = Rifle, WeaponComponents = null,
            };
            var bare = new RemotePedFrame
            {
                Position = NetVector3.Zero,
                Health = 200,
                CurrentWeaponHash = Rifle,
                WeaponComponents = new List<uint>(),
            };

            Assert.Null(RemotePedController.Decide(in unread, NetVector3.Zero).WeaponComponents);
            Assert.NotNull(RemotePedController.Decide(in bare, NetVector3.Zero).WeaponComponents);
            Assert.Empty(RemotePedController.Decide(in bare, NetVector3.Zero).WeaponComponents!);
        }
    }
}
