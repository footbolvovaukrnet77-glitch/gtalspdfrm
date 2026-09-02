using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Xunit;

namespace Gtamp.Tests
{
    public class AppearanceTests
    {
        [Fact]
        public void ADefaultAppearanceCostsThreeBytes()
        {
            var writer = new NetWriter();
            new PedAppearance().Write(writer);

            // Two bytes of component mask plus one of prop mask. A fixed layout would
            // be 46 bytes on every player, on every full snapshot.
            Assert.Equal(3, writer.Length);
        }

        [Fact]
        public void ADefaultAppearanceIsRecognisedAsDefault()
        {
            Assert.True(new PedAppearance().IsDefault);

            var dressed = new PedAppearance();
            dressed.SetComponent(PedComponentSlot.Torso, 12, 3);
            Assert.False(dressed.IsDefault);
        }

        [Fact]
        public void ComponentsAndPropsRoundTrip()
        {
            var original = new PedAppearance();
            original.SetComponent(PedComponentSlot.Face, 4, 1, 2);
            original.SetComponent(PedComponentSlot.Torso, 300, 5);
            original.SetComponent(PedComponentSlot.Legs, 41, 2);
            original.SetComponent(PedComponentSlot.BodyArmor, 1, 0);
            original.SetProp(PedPropSlot.Hat, 8, 3);
            original.SetProp(PedPropSlot.Glasses, 5, 0);

            var writer = new NetWriter();
            original.Write(writer);

            var restored = new PedAppearance();
            restored.Read(new NetReader(writer.ToArray()));

            Assert.True(original.ValueEquals(restored));
            Assert.Equal(300, restored.GetComponent(PedComponentSlot.Torso).Drawable);
            Assert.Equal(5, restored.GetComponent(PedComponentSlot.Torso).Texture);
            Assert.Equal(2, restored.GetComponent(PedComponentSlot.Face).Palette);
            Assert.Equal(8, restored.GetProp(PedPropSlot.Hat).Drawable);
            Assert.Equal(3, restored.GetProp(PedPropSlot.Hat).Texture);
            Assert.True(restored.GetProp(PedPropSlot.Watch).IsEmpty);
        }

        [Fact]
        public void ClearingAPropRoundTripsAsEmptyRatherThanAsDrawableZero()
        {
            var appearance = new PedAppearance();
            appearance.SetProp(PedPropSlot.Hat, 0, 0);

            var writer = new NetWriter();
            appearance.Write(writer);

            var restored = new PedAppearance();
            restored.Read(new NetReader(writer.ToArray()));

            // Drawable 0 is a real hat, not "no hat".
            Assert.False(restored.GetProp(PedPropSlot.Hat).IsEmpty);
            Assert.Equal(0, restored.GetProp(PedPropSlot.Hat).Drawable);

            appearance.ClearProp(PedPropSlot.Hat);
            writer.Reset();
            appearance.Write(writer);
            restored.Read(new NetReader(writer.ToArray()));

            Assert.True(restored.GetProp(PedPropSlot.Hat).IsEmpty);
        }

        [Fact]
        public void ReadingOverwritesEverySlotSoStaleStateCannotSurvive()
        {
            var restored = new PedAppearance();
            restored.SetComponent(PedComponentSlot.Hair, 9, 9, 9);
            restored.SetProp(PedPropSlot.Hat, 4, 4);

            var writer = new NetWriter();
            new PedAppearance().Write(writer);
            restored.Read(new NetReader(writer.ToArray()));

            Assert.True(restored.IsDefault);
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var original = new PedAppearance();
            original.SetComponent(PedComponentSlot.Top, 10, 1);

            PedAppearance clone = original.Clone();
            original.SetComponent(PedComponentSlot.Top, 99, 9);

            Assert.Equal(10, clone.GetComponent(PedComponentSlot.Top).Drawable);
        }

        [Fact]
        public void AnOutOfRangeDrawableIsRejectedRatherThanTruncated()
        {
            var writer = new NetWriter();
            writer.WriteUInt16(1);            // component slot 0 present
            writer.WriteVarUInt(1_000_000);   // drawable far beyond ushort

            Assert.Throws<NetSerializationException>(() =>
                new PedAppearance().Read(new NetReader(writer.ToArray())));
        }

        [Fact]
        public void AppearanceIsPartOfThePlayerEntityAndItsDelta()
        {
            var serializer = new PlayerEntitySerializer();
            var baseline = new PlayerEntity(new EntityId(1)) { Name = "alice" };
            var current = (PlayerEntity)baseline.Clone();
            current.Appearance.SetComponent(PedComponentSlot.Torso, 15, 2);

            Assert.True(serializer.HasChanges(baseline, current));

            var writer = new NetWriter();
            serializer.WriteDelta(writer, baseline, current);

            var restored = (PlayerEntity)baseline.Clone();
            serializer.ReadDelta(new NetReader(writer.ToArray()), restored);

            Assert.Equal(15, restored.Appearance.GetComponent(PedComponentSlot.Torso).Drawable);
        }

        [Fact]
        public void CloningAPlayerCopiesTheAppearanceRatherThanSharingIt()
        {
            var player = new PlayerEntity(new EntityId(1));
            player.Appearance.SetComponent(PedComponentSlot.Legs, 7, 1);

            var clone = (PlayerEntity)player.Clone();
            player.Appearance.SetComponent(PedComponentSlot.Legs, 99, 9);

            Assert.Equal(7, clone.Appearance.GetComponent(PedComponentSlot.Legs).Drawable);
        }
    }
}
