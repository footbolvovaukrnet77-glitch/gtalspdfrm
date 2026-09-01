using System;
using System.Text;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Counterpart of <see cref="NetWriter"/>. Every read is bounds-checked and
    /// throws <see cref="NetSerializationException"/> rather than corrupting state:
    /// a malformed packet must never be able to crash the server tick loop.
    /// </summary>
    public sealed class NetReader
    {
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _end;
        private int _position;

        public NetReader(byte[] buffer)
            : this(buffer, 0, buffer.Length)
        {
        }

        public NetReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _start = offset;
            _end = offset + count;
            _position = offset;
        }

        public int Position => _position - _start;

        public int Remaining => _end - _position;

        public bool EndOfData => _position >= _end;

        private void Require(int count)
        {
            if (_position + count > _end)
            {
                throw new NetSerializationException(
                    $"Truncated packet: wanted {count} byte(s) at offset {_position - _start}, {_end - _position} available.");
            }
        }

        public byte ReadByte()
        {
            Require(1);
            return _buffer[_position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public short ReadInt16() => unchecked((short)ReadUInt16());

        public uint ReadUInt32()
        {
            Require(4);
            uint value = (uint)(_buffer[_position]
                                | (_buffer[_position + 1] << 8)
                                | (_buffer[_position + 2] << 16)
                                | (_buffer[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadUInt64()
        {
            ulong low = ReadUInt32();
            ulong high = ReadUInt32();
            return low | (high << 32);
        }

        public long ReadInt64() => unchecked((long)ReadUInt64());

        public float ReadSingle()
        {
            Require(4);
            byte[] bytes = ReadRawBytes(4);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToSingle(bytes, 0);
        }

        public double ReadDouble()
        {
            Require(8);
            byte[] bytes = ReadRawBytes(8);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToDouble(bytes, 0);
        }

        public uint ReadVarUInt()
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                if (shift > 28)
                {
                    throw new NetSerializationException("Varint overflows 32 bits.");
                }

                byte b = ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }

        public ulong ReadVarUInt64()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (shift > 63)
                {
                    throw new NetSerializationException("Varint overflows 64 bits.");
                }

                byte b = ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }

        public int ReadVarInt()
        {
            uint raw = ReadVarUInt();
            return unchecked((int)(raw >> 1) ^ -(int)(raw & 1));
        }

        public byte[] ReadRawBytes(int count)
        {
            Require(count);
            var result = new byte[count];
            Array.Copy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        public byte[] ReadByteArray(int maxLength = 1 << 20)
        {
            uint length = ReadVarUInt();
            if (length > maxLength)
            {
                throw new NetSerializationException($"Byte array length {length} exceeds limit {maxLength}.");
            }

            return ReadRawBytes((int)length);
        }

        public string ReadString(int maxLength = 4096)
        {
            uint length = ReadVarUInt();
            if (length == 0)
            {
                return string.Empty;
            }

            if (length > maxLength)
            {
                throw new NetSerializationException($"String length {length} exceeds limit {maxLength}.");
            }

            Require((int)length);
            string value = Encoding.UTF8.GetString(_buffer, _position, (int)length);
            _position += (int)length;
            return value;
        }

        public NetVector3 ReadQuantizedPosition()
        {
            float x = Quantize.DecodePositionAxis(ReadVarInt());
            float y = Quantize.DecodePositionAxis(ReadVarInt());
            float z = Quantize.DecodePositionAxis(ReadVarInt());
            return new NetVector3(x, y, z);
        }

        public NetVector3 ReadQuantizedVelocity()
        {
            float x = Quantize.DecodeVelocityAxis(ReadVarInt());
            float y = Quantize.DecodeVelocityAxis(ReadVarInt());
            float z = Quantize.DecodeVelocityAxis(ReadVarInt());
            return new NetVector3(x, y, z);
        }

        public NetVector3 ReadBoneOffset()
        {
            float x = Quantize.DecodeBoneOffsetAxis(ReadVarInt());
            float y = Quantize.DecodeBoneOffsetAxis(ReadVarInt());
            float z = Quantize.DecodeBoneOffsetAxis(ReadVarInt());
            return new NetVector3(x, y, z);
        }

        public float ReadAngleDegrees() => Quantize.DecodeAngleDegrees(ReadUInt16());

        public float ReadUnit() => Quantize.DecodeUnit(ReadByte());
    }
}
