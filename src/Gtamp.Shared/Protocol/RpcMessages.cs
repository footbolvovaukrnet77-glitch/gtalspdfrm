using System;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Protocol
{
    public sealed class ModRpcRequestMessage
    {
        /// <summary>Correlation id, unique per caller.</summary>
        public uint CallId { get; set; }

        public string Name { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(Payload.Length + 32);
            writer.WriteUInt32(CallId);
            writer.WriteString(Name);
            writer.WriteByteArray(Payload);
            return writer.ToArray();
        }

        public static ModRpcRequestMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ModRpcRequestMessage
            {
                CallId = reader.ReadUInt32(),
                Name = reader.ReadString(128),
                Payload = reader.ReadByteArray(NetPeer.MaxFragmentedMessage),
            };
        }
    }

    /// <summary>A mod-defined event, carrying its own name so both sides agree on routing.</summary>
    public sealed class ModEventMessage
    {
        public string Name { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(Payload.Length + 32);
            writer.WriteString(Name);
            writer.WriteByteArray(Payload);
            return writer.ToArray();
        }

        public static ModEventMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ModEventMessage
            {
                Name = reader.ReadString(128),
                Payload = reader.ReadByteArray(NetPeer.MaxFragmentedMessage),
            };
        }
    }

    public sealed class ModRpcResponseMessage
    {
        public uint CallId { get; set; }

        public bool Success { get; set; }

        /// <summary>Why it failed, when it did. Empty on success.</summary>
        public string Error { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(Payload.Length + 32);
            writer.WriteUInt32(CallId);
            writer.WriteBool(Success);
            writer.WriteString(Error);
            writer.WriteByteArray(Payload);
            return writer.ToArray();
        }

        public static ModRpcResponseMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ModRpcResponseMessage
            {
                CallId = reader.ReadUInt32(),
                Success = reader.ReadBool(),
                Error = reader.ReadString(512),
                Payload = reader.ReadByteArray(NetPeer.MaxFragmentedMessage),
            };
        }
    }
}
