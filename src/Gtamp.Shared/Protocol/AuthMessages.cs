using System;
using Gtamp.Shared.Net;
using Gtamp.Shared.Security;

namespace Gtamp.Shared.Protocol
{
    /// <summary>
    /// Server → client: prove you hold the private half of the identity you claimed.
    /// <para>
    /// Connectionless, like the rest of the handshake, and therefore unreliable. A
    /// lost challenge means the client retries its connect request, and the server
    /// must answer that retry with the <b>same</b> challenge — otherwise a proof
    /// already in flight for the first one arrives against a nonce the server has
    /// forgotten and a legitimate player is told their key is wrong.
    /// </para>
    /// </summary>
    public sealed class ConnectChallengeMessage
    {
        /// <summary>Echoed so the client can tell this challenge from a stale one.</summary>
        public uint ClientNonce { get; set; }

        public byte[] ServerNonce { get; set; } = Array.Empty<byte>();

        /// <summary>Signed along with the nonces, so a proof cannot be replayed to another server.</summary>
        public string ServerName { get; set; } = string.Empty;

        /// <summary>The server's ephemeral ECDH public point. Empty when encryption is off.</summary>
        public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteUInt32(ClientNonce);
            writer.WriteByteArray(ServerNonce);
            writer.WriteString(ServerName);
            writer.WriteByteArray(EphemeralPublicKey);
            return writer.ToArray();
        }

        public static ConnectChallengeMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ConnectChallengeMessage
            {
                ClientNonce = reader.ReadUInt32(),
                ServerNonce = reader.ReadByteArray(64),
                ServerName = reader.ReadString(128),
                EphemeralPublicKey = reader.ReadByteArray(128),
            };
        }
    }

    /// <summary>Client → server: the signature over the challenge.</summary>
    public sealed class ConnectProofMessage
    {
        public uint ClientNonce { get; set; }

        /// <summary>Base64 X‖Y. Repeated here so the server can verify without trusting its own lookup.</summary>
        public string PublicKey { get; set; } = string.Empty;

        public byte[] Signature { get; set; } = Array.Empty<byte>();

        /// <summary>The client's ephemeral ECDH public point. Empty when encryption is off.</summary>
        public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(256);
            writer.WriteUInt32(ClientNonce);
            writer.WriteString(PublicKey);
            writer.WriteByteArray(Signature);
            writer.WriteByteArray(EphemeralPublicKey);
            return writer.ToArray();
        }

        public static ConnectProofMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ConnectProofMessage
            {
                ClientNonce = reader.ReadUInt32(),
                PublicKey = reader.ReadString(256),

                // A P-256 DER signature is ~72 bytes; the cap is generous but bounded
                // so an unauthenticated peer cannot make the server allocate freely.
                Signature = reader.ReadByteArray(256),
                EphemeralPublicKey = reader.ReadByteArray(128),
            };
        }
    }

    /// <summary>
    /// Client → server: an administrative command typed in the in-game console.
    /// The server decides whether the sender may run it; the client only asks.
    /// </summary>
    public sealed class AdminCommandMessage
    {
        public string CommandLine { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(256);
            writer.WriteString(CommandLine);
            return writer.ToArray();
        }

        public static AdminCommandMessage Deserialize(byte[] payload) =>
            new AdminCommandMessage { CommandLine = new NetReader(payload).ReadString(512) };
    }

    public enum SecurityNoticeKind : byte
    {
        Information = 0,
        CommandResult = 1,
        PermissionDenied = 2,
        Warning = 3,
    }

    /// <summary>Server → client: the answer to an admin command, or an unsolicited notice.</summary>
    public sealed class SecurityNoticeMessage
    {
        public SecurityNoticeKind Kind { get; set; } = SecurityNoticeKind.Information;

        public string Text { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(1024);
            writer.WriteByte((byte)Kind);
            writer.WriteString(Text);
            return writer.ToArray();
        }

        public static SecurityNoticeMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new SecurityNoticeMessage
            {
                Kind = (SecurityNoticeKind)reader.ReadByte(),
                Text = reader.ReadString(4096),
            };
        }
    }
}
