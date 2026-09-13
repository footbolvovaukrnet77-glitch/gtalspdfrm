using System;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Protocol
{
    /// <summary>
    /// Framing for packets sent before a session exists (the handshake) or after it
    /// has been torn down. They carry no sequence numbers and no session id:
    /// <code>u32 magic | u8 kind=0 | u8 messageType | varuint length | payload</code>
    /// </summary>
    public static class ConnectionlessPacket
    {
        public const byte Kind = 0;

        public static byte[] Write(NetMessageType type, byte[] payload)
        {
            var writer = new NetWriter(payload.Length + 16);
            writer.WriteUInt32(ProtocolConstants.Magic);
            writer.WriteByte(Kind);
            writer.WriteByte((byte)type);
            writer.WriteVarUInt((uint)payload.Length);
            writer.WriteBytes(payload, 0, payload.Length);
            return writer.ToArray();
        }

        public static bool TryRead(byte[] data, out NetMessageType type, out byte[] payload)
        {
            type = NetMessageType.None;
            payload = Array.Empty<byte>();

            try
            {
                var reader = new NetReader(data);
                if (reader.ReadUInt32() != ProtocolConstants.Magic)
                {
                    return false;
                }

                if (reader.ReadByte() != Kind)
                {
                    return false;
                }

                type = (NetMessageType)reader.ReadByte();
                payload = reader.ReadByteArray(ProtocolConstants.MaxPacketSize);
                return true;
            }
            catch (NetSerializationException)
            {
                return false;
            }
        }

        /// <summary>True when the datagram is a session packet rather than a connectionless one.</summary>
        public static bool IsSessionPacket(byte[] data)
        {
            if (data.Length < 5)
            {
                return false;
            }

            var reader = new NetReader(data);
            return reader.ReadUInt32() == ProtocolConstants.Magic && reader.ReadByte() == 1;
        }
    }
}
