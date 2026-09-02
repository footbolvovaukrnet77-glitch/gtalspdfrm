using System;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>GTA V ped component slots. The index is the value the natives expect.</summary>
    public enum PedComponentSlot : byte
    {
        Face = 0,
        Mask = 1,
        Hair = 2,
        Torso = 3,
        Legs = 4,
        Bag = 5,
        Shoes = 6,
        Accessory = 7,
        Undershirt = 8,
        BodyArmor = 9,
        Decal = 10,
        Top = 11,
    }

    /// <summary>GTA V ped prop slots. 3, 4 and 5 are unused by the game.</summary>
    public enum PedPropSlot : byte
    {
        Hat = 0,
        Glasses = 1,
        Ear = 2,
        Watch = 6,
        Bracelet = 7,
    }

    /// <summary>
    /// A player's clothing and props.
    /// <para>
    /// Appearance changes rarely — at spawn, and when the player changes clothes —
    /// so it is one replicated field written whole rather than a per-slot delta.
    /// The encoding is mask-driven, so a default character costs three bytes rather
    /// than the 46 a fixed layout would need.
    /// </para>
    /// </summary>
    public sealed class PedAppearance
    {
        public const int ComponentSlots = 12;
        public const int PropSlots = 8;

        /// <summary>Prop drawable meaning "nothing in this slot", matching the game's own convention.</summary>
        public const short NoProp = -1;

        private readonly ComponentVariation[] _components = new ComponentVariation[ComponentSlots];
        private readonly PropVariation[] _props = new PropVariation[PropSlots];

        public PedAppearance()
        {
            for (int i = 0; i < PropSlots; i++)
            {
                _props[i] = new PropVariation(NoProp, 0);
            }
        }

        public ComponentVariation GetComponent(int slot) => _components[slot];

        public ComponentVariation GetComponent(PedComponentSlot slot) => _components[(int)slot];

        public void SetComponent(int slot, ushort drawable, byte texture, byte palette) =>
            _components[slot] = new ComponentVariation(drawable, texture, palette);

        public void SetComponent(PedComponentSlot slot, ushort drawable, byte texture, byte palette = 0) =>
            SetComponent((int)slot, drawable, texture, palette);

        public PropVariation GetProp(int slot) => _props[slot];

        public PropVariation GetProp(PedPropSlot slot) => _props[(int)slot];

        public void SetProp(int slot, short drawable, byte texture) =>
            _props[slot] = new PropVariation(drawable, texture);

        public void SetProp(PedPropSlot slot, short drawable, byte texture = 0) =>
            SetProp((int)slot, drawable, texture);

        public void ClearProp(PedPropSlot slot) => SetProp((int)slot, NoProp, 0);

        /// <summary>True when nothing has been set; such an appearance is not applied to a ped.</summary>
        public bool IsDefault
        {
            get
            {
                for (int i = 0; i < ComponentSlots; i++)
                {
                    if (!_components[i].IsDefault)
                    {
                        return false;
                    }
                }

                for (int i = 0; i < PropSlots; i++)
                {
                    if (_props[i].Drawable != NoProp)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public PedAppearance Clone()
        {
            var clone = new PedAppearance();
            Array.Copy(_components, clone._components, ComponentSlots);
            Array.Copy(_props, clone._props, PropSlots);
            return clone;
        }

        public void CopyFrom(PedAppearance other)
        {
            Array.Copy(other._components, _components, ComponentSlots);
            Array.Copy(other._props, _props, PropSlots);
        }

        public bool ValueEquals(PedAppearance other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            for (int i = 0; i < ComponentSlots; i++)
            {
                if (!_components[i].Equals(other._components[i]))
                {
                    return false;
                }
            }

            for (int i = 0; i < PropSlots; i++)
            {
                if (!_props[i].Equals(other._props[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public void Write(NetWriter writer)
        {
            ushort componentMask = 0;
            for (int i = 0; i < ComponentSlots; i++)
            {
                if (!_components[i].IsDefault)
                {
                    componentMask |= (ushort)(1 << i);
                }
            }

            byte propMask = 0;
            for (int i = 0; i < PropSlots; i++)
            {
                if (_props[i].Drawable != NoProp)
                {
                    propMask |= (byte)(1 << i);
                }
            }

            writer.WriteUInt16(componentMask);
            for (int i = 0; i < ComponentSlots; i++)
            {
                if ((componentMask & (1 << i)) == 0)
                {
                    continue;
                }

                writer.WriteVarUInt(_components[i].Drawable);
                writer.WriteByte(_components[i].Texture);
                writer.WriteByte(_components[i].Palette);
            }

            writer.WriteByte(propMask);
            for (int i = 0; i < PropSlots; i++)
            {
                if ((propMask & (1 << i)) == 0)
                {
                    continue;
                }

                writer.WriteVarUInt((uint)_props[i].Drawable);
                writer.WriteByte(_props[i].Texture);
            }
        }

        public void Read(NetReader reader)
        {
            ushort componentMask = reader.ReadUInt16();
            for (int i = 0; i < ComponentSlots; i++)
            {
                if ((componentMask & (1 << i)) != 0)
                {
                    uint drawable = reader.ReadVarUInt();
                    if (drawable > ushort.MaxValue)
                    {
                        throw new NetSerializationException($"Component drawable {drawable} is out of range.");
                    }

                    _components[i] = new ComponentVariation((ushort)drawable, reader.ReadByte(), reader.ReadByte());
                }
                else
                {
                    _components[i] = default;
                }
            }

            byte propMask = reader.ReadByte();
            for (int i = 0; i < PropSlots; i++)
            {
                if ((propMask & (1 << i)) != 0)
                {
                    uint drawable = reader.ReadVarUInt();
                    if (drawable > short.MaxValue)
                    {
                        throw new NetSerializationException($"Prop drawable {drawable} is out of range.");
                    }

                    _props[i] = new PropVariation((short)drawable, reader.ReadByte());
                }
                else
                {
                    _props[i] = new PropVariation(NoProp, 0);
                }
            }
        }

        public readonly struct ComponentVariation : IEquatable<ComponentVariation>
        {
            public ComponentVariation(ushort drawable, byte texture, byte palette)
            {
                Drawable = drawable;
                Texture = texture;
                Palette = palette;
            }

            public ushort Drawable { get; }

            public byte Texture { get; }

            public byte Palette { get; }

            public bool IsDefault => Drawable == 0 && Texture == 0 && Palette == 0;

            public bool Equals(ComponentVariation other) =>
                Drawable == other.Drawable && Texture == other.Texture && Palette == other.Palette;

            public override bool Equals(object? obj) => obj is ComponentVariation other && Equals(other);

            public override int GetHashCode() => (Drawable << 16) | (Texture << 8) | Palette;
        }

        public readonly struct PropVariation : IEquatable<PropVariation>
        {
            public PropVariation(short drawable, byte texture)
            {
                Drawable = drawable;
                Texture = texture;
            }

            public short Drawable { get; }

            public byte Texture { get; }

            public bool IsEmpty => Drawable == NoProp;

            public bool Equals(PropVariation other) => Drawable == other.Drawable && Texture == other.Texture;

            public override bool Equals(object? obj) => obj is PropVariation other && Equals(other);

            public override int GetHashCode() => (Drawable << 8) | Texture;
        }
    }
}
