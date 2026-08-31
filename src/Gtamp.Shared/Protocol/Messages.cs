using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Protocol
{
    /// <summary>First packet of the handshake. Sent connectionless and retried until answered.</summary>
    public sealed class ConnectRequestMessage
    {
        public ushort ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

        public string ClientVersion { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

        /// <summary>
        /// Stable per-installation secret. It is what lets a reconnecting player be
        /// recognised as the same person and get their character back rather than a
        /// fresh one (master prompt section 25).
        /// </summary>
        public string IdentityToken { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>Nonce echoed in the accept, so a stale accept cannot be replayed.</summary>
        public uint ClientNonce { get; set; }

        public ModManifest Manifest { get; set; } = new ModManifest();

        public byte[] Serialize()
        {
            var writer = new NetWriter(512);
            writer.WriteUInt16(ProtocolVersion);
            writer.WriteString(ClientVersion);
            writer.WriteString(PlayerName);
            writer.WriteString(IdentityToken);
            writer.WriteString(Password);
            writer.WriteUInt32(ClientNonce);
            Manifest.Write(writer);
            return writer.ToArray();
        }

        public static ConnectRequestMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ConnectRequestMessage
            {
                ProtocolVersion = reader.ReadUInt16(),
                ClientVersion = reader.ReadString(64),
                PlayerName = reader.ReadString(ProtocolConstants.MaxPlayerNameLength),
                IdentityToken = reader.ReadString(128),
                Password = reader.ReadString(128),
                ClientNonce = reader.ReadUInt32(),
                Manifest = ModManifest.Read(reader),
            };
        }
    }

    public sealed class ConnectAcceptMessage
    {
        public uint SessionId { get; set; }

        public uint PlayerId { get; set; }

        public EntityId PlayerEntityId { get; set; }

        public uint ClientNonce { get; set; }

        public int TickRate { get; set; } = ProtocolConstants.DefaultTickRate;

        public int SnapshotRate { get; set; } = ProtocolConstants.DefaultSnapshotRate;

        public int ClientUpdateRate { get; set; } = ProtocolConstants.DefaultClientUpdateRate;

        public double ServerTime { get; set; }

        public string ServerName { get; set; } = string.Empty;

        public string ServerVersion { get; set; } = string.Empty;

        /// <summary>True when the player was recognised from a previous session and restored.</summary>
        public bool Restored { get; set; }

        public List<ModCompatibilityEntry> ModReport { get; } = new List<ModCompatibilityEntry>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(512);
            writer.WriteUInt32(SessionId);
            writer.WriteVarUInt(PlayerId);
            writer.WriteVarUInt(PlayerEntityId.Value);
            writer.WriteUInt32(ClientNonce);
            writer.WriteVarUInt((uint)TickRate);
            writer.WriteVarUInt((uint)SnapshotRate);
            writer.WriteVarUInt((uint)ClientUpdateRate);
            writer.WriteDouble(ServerTime);
            writer.WriteString(ServerName);
            writer.WriteString(ServerVersion);
            writer.WriteBool(Restored);
            writer.WriteVarUInt((uint)ModReport.Count);
            foreach (ModCompatibilityEntry entry in ModReport)
            {
                entry.Write(writer);
            }

            return writer.ToArray();
        }

        public static ConnectAcceptMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            var message = new ConnectAcceptMessage
            {
                SessionId = reader.ReadUInt32(),
                PlayerId = reader.ReadVarUInt(),
                PlayerEntityId = new EntityId(reader.ReadVarUInt()),
                ClientNonce = reader.ReadUInt32(),
                TickRate = (int)reader.ReadVarUInt(),
                SnapshotRate = (int)reader.ReadVarUInt(),
                ClientUpdateRate = (int)reader.ReadVarUInt(),
                ServerTime = reader.ReadDouble(),
                ServerName = reader.ReadString(128),
                ServerVersion = reader.ReadString(64),
                Restored = reader.ReadBool(),
            };

            uint count = reader.ReadVarUInt();
            if (count > ModManifest.MaxMods)
            {
                throw new NetSerializationException($"Compatibility report has {count} entries; the limit is {ModManifest.MaxMods}.");
            }

            for (uint i = 0; i < count; i++)
            {
                message.ModReport.Add(ModCompatibilityEntry.Read(reader));
            }

            return message;
        }
    }

    public sealed class ConnectRejectMessage
    {
        public DisconnectReason Reason { get; set; }

        public string Message { get; set; } = string.Empty;

        public uint ClientNonce { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(256);
            writer.WriteByte((byte)Reason);
            writer.WriteString(Message);
            writer.WriteUInt32(ClientNonce);
            return writer.ToArray();
        }

        public static ConnectRejectMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ConnectRejectMessage
            {
                Reason = (DisconnectReason)reader.ReadByte(),
                Message = reader.ReadString(512),
                ClientNonce = reader.ReadUInt32(),
            };
        }
    }

    /// <summary>
    /// The client's own body state, sent at <see cref="ProtocolConstants.DefaultClientUpdateRate"/> Hz.
    /// This is an <em>input</em> to the server, never authoritative: the server
    /// validates it and only then writes it into the world (master prompt section 6).
    /// </summary>
    public sealed class ClientStateUpdateMessage
    {
        public uint ClientTick { get; set; }

        public double ClientTime { get; set; }

        /// <summary>Highest snapshot id the client has successfully applied. Doubles as the delta baseline ack.</summary>
        public uint AcknowledgedSnapshotId { get; set; }

        public NetVector3 Position { get; set; }

        public NetVector3 Velocity { get; set; }

        public float Heading { get; set; }

        public int Health { get; set; }

        public int Armor { get; set; }

        public PlayerFlags Flags { get; set; }

        public MovementState Movement { get; set; }

        public uint ModelHash { get; set; }

        public uint CurrentWeaponHash { get; set; }

        public int Ammo { get; set; }

        public NetVector3 AimPosition { get; set; }

        public int InteriorId { get; set; }

        public uint AnimationHash { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteVarUInt(ClientTick);
            writer.WriteDouble(ClientTime);
            writer.WriteVarUInt(AcknowledgedSnapshotId);
            writer.WriteQuantizedPosition(Position);
            writer.WriteQuantizedVelocity(Velocity);
            writer.WriteAngleDegrees(Heading);
            writer.WriteVarInt(Health);
            writer.WriteVarInt(Armor);
            writer.WriteVarUInt((uint)Flags);
            writer.WriteByte((byte)Movement);
            writer.WriteUInt32(ModelHash);
            writer.WriteUInt32(CurrentWeaponHash);
            writer.WriteVarInt(Ammo);
            writer.WriteQuantizedPosition(AimPosition);
            writer.WriteVarInt(InteriorId);
            writer.WriteUInt32(AnimationHash);
            return writer.ToArray();
        }

        public static ClientStateUpdateMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ClientStateUpdateMessage
            {
                ClientTick = reader.ReadVarUInt(),
                ClientTime = reader.ReadDouble(),
                AcknowledgedSnapshotId = reader.ReadVarUInt(),
                Position = reader.ReadQuantizedPosition(),
                Velocity = reader.ReadQuantizedVelocity(),
                Heading = reader.ReadAngleDegrees(),
                Health = reader.ReadVarInt(),
                Armor = reader.ReadVarInt(),
                Flags = (PlayerFlags)reader.ReadVarUInt(),
                Movement = (MovementState)reader.ReadByte(),
                ModelHash = reader.ReadUInt32(),
                CurrentWeaponHash = reader.ReadUInt32(),
                Ammo = reader.ReadVarInt(),
                AimPosition = reader.ReadQuantizedPosition(),
                InteriorId = reader.ReadVarInt(),
                AnimationHash = reader.ReadUInt32(),
            };
        }
    }

    public sealed class ChatMessage
    {
        public uint PlayerId { get; set; }

        public string SenderName { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(256);
            writer.WriteVarUInt(PlayerId);
            writer.WriteString(SenderName);
            writer.WriteString(Text);
            return writer.ToArray();
        }

        public static ChatMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ChatMessage
            {
                PlayerId = reader.ReadVarUInt(),
                SenderName = reader.ReadString(ProtocolConstants.MaxPlayerNameLength),
                Text = reader.ReadString(512),
            };
        }
    }

    public enum ServerEventKind : byte
    {
        PlayerJoined = 0,
        PlayerLeft = 1,
        PlayerReconnected = 2,
        Announcement = 3,
        WorldReset = 4,
    }

    public sealed class ServerEventMessage
    {
        public ServerEventKind Kind { get; set; }

        public uint PlayerId { get; set; }

        public string Text { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteByte((byte)Kind);
            writer.WriteVarUInt(PlayerId);
            writer.WriteString(Text);
            return writer.ToArray();
        }

        public static ServerEventMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ServerEventMessage
            {
                Kind = (ServerEventKind)reader.ReadByte(),
                PlayerId = reader.ReadVarUInt(),
                Text = reader.ReadString(512),
            };
        }
    }

    public sealed class DisconnectMessage
    {
        public DisconnectReason Reason { get; set; }

        public string Text { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteByte((byte)Reason);
            writer.WriteString(Text);
            return writer.ToArray();
        }

        public static DisconnectMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new DisconnectMessage
            {
                Reason = (DisconnectReason)reader.ReadByte(),
                Text = reader.ReadString(512),
            };
        }
    }

    public sealed class TimeSyncMessage
    {
        public double ClientTime { get; set; }

        public double ServerTime { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(24);
            writer.WriteDouble(ClientTime);
            writer.WriteDouble(ServerTime);
            return writer.ToArray();
        }

        public static TimeSyncMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new TimeSyncMessage
            {
                ClientTime = reader.ReadDouble(),
                ServerTime = reader.ReadDouble(),
            };
        }
    }

    /// <summary>
    /// Sent by the client when it cannot decode a delta (baseline expired, unknown
    /// entity type). The server answers with a full snapshot instead of dropping
    /// the connection — this is the whole resync path from section 25.
    /// </summary>
    public sealed class ResyncRequestMessage
    {
        public string Reason { get; set; } = string.Empty;

        public uint LastAppliedSnapshotId { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteString(Reason);
            writer.WriteVarUInt(LastAppliedSnapshotId);
            return writer.ToArray();
        }

        public static ResyncRequestMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new ResyncRequestMessage
            {
                Reason = reader.ReadString(256),
                LastAppliedSnapshotId = reader.ReadVarUInt(),
            };
        }
    }

    public sealed class SnapshotAckMessage
    {
        public uint SnapshotId { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(8);
            writer.WriteVarUInt(SnapshotId);
            return writer.ToArray();
        }

        public static SnapshotAckMessage Deserialize(byte[] payload) =>
            new SnapshotAckMessage { SnapshotId = new NetReader(payload).ReadVarUInt() };
    }
}
