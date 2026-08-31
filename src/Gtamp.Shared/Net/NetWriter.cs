using System;
using System.Text;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Little-endian, growable binary writer with varint support.
    /// Deliberately allocation-light: buffers are pooled by the caller and the
    /// writer is reset rather than reconstructed on every packet.
    /// </summary>
    public sealed class NetWriter
    {
        private byte[] _buffer;
        private int _position;

        public NetWriter(int capacity = 1200)
        {
            _buffer = new byte[Math.Max(16, capacity)];
            _position = 0;
        }

        public int Length => _position;

        public byte[] Buffer => _buffer;

        public void Reset() => _position = 0;

        public byte[] ToArray()
        {
            var result = new byte[_position];
            Array.Copy(_buffer, result, _position);
            return result;
        }

        private void Ensure(int extra)
        {
            if (_position + extra <= _buffer.Length)
            {
                return;
            }

            int capacity = _buffer.Length * 2;
            while (capacity < _position + extra)
            {
                capacity *= 2;
            }

            Array.Resize(ref _buffer, capacity);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_position++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
        }

        public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

        public void WriteUInt32(uint value)
        {
            Ensure(4);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
        }

        public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

        public void WriteUInt64(ulong value)
        {
            WriteUInt32((uint)(value & 0xFFFFFFFF));
            WriteUInt32((uint)(value >> 32));
        }

        public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

        public void WriteSingle(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            WriteBytes(bytes, 0, 4);
        }

        public void WriteDouble(double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            WriteBytes(bytes, 0, 8);
        }

        /// <summary>LEB128 unsigned varint. 1 byte for values &lt; 128.</summary>
        public void WriteVarUInt(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        public void WriteVarUInt64(ulong value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        /// <summary>Zig-zag encoded signed varint, so small negatives stay small.</summary>
        public void WriteVarInt(int value) => WriteVarUInt(unchecked((uint)((value << 1) ^ (value >> 31))));

        public void WriteBytes(byte[] value, int offset, int count)
        {
            Ensure(count);
            Array.Copy(value, offset, _buffer, _position, count);
            _position += count;
        }

        public void WriteByteArray(byte[]? value)
        {
            if (value == null)
            {
                WriteVarUInt(0);
                return;
            }

            WriteVarUInt((uint)value.Length);
            WriteBytes(value, 0, value.Length);
        }

        public void WriteString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteVarUInt(0);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value!);
            WriteVarUInt((uint)bytes.Length);
            WriteBytes(bytes, 0, bytes.Length);
        }

        public void WriteQuantizedPosition(NetVector3 value)
        {
            WriteVarInt(Quantize.EncodePositionAxis(value.X, Quantize.WorldExtentXY));
            WriteVarInt(Quantize.EncodePositionAxis(value.Y, Quantize.WorldExtentXY));
            WriteVarInt(Quantize.EncodePositionAxis(value.Z, Quantize.WorldExtentZ));
        }

        public void WriteQuantizedVelocity(NetVector3 value)
        {
            WriteVarInt(Quantize.EncodeVelocityAxis(value.X));
            WriteVarInt(Quantize.EncodeVelocityAxis(value.Y));
            WriteVarInt(Quantize.EncodeVelocityAxis(value.Z));
        }

        public void WriteAngleDegrees(float degrees) => WriteUInt16(Quantize.EncodeAngleDegrees(degrees));

        public void WriteUnit(float value) => WriteByte(Quantize.EncodeUnit(value));
    }
}
