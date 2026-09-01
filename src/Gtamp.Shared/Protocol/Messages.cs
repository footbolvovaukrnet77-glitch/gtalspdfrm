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

        /// <summary>Weapon tint and fitted components. See <see cref="CharacterEntity"/>.</summary>
        public byte WeaponTint { get; set; }

        public List<uint> WeaponComponents { get; } = new List<uint>();

        public NetVector3 AimPosition { get; set; }

        public int InteriorId { get; set; }

        public uint AnimationHash { get; set; }

        /// <summary>
        /// Limb positions while ragdolling. Written only when
        /// <see cref="PlayerFlags.Ragdoll"/> is set, because this message is sent in
        /// full twenty times a second rather than as a delta: an unconditional pose
        /// would cost every client 360 bytes a second to say "not falling".
        /// </summary>
        public RagdollPose Ragdoll { get; set; }

        /// <summary>Clothing and props, as the client currently has them.</summary>
        public PedAppearance Appearance { get; } = new PedAppearance();

        /// <summary>
        /// Increments with every update this client sends. Echoed back in the
        /// snapshot header so the client can tell which of its reports the server had
        /// seen when it wrote that snapshot.
        /// </summary>
        public uint UpdateSequence { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteVarUInt(ClientTick);
            writer.WriteDouble(ClientTime);
            writer.WriteVarUInt(AcknowledgedSnapshotId);
            writer.WriteVarUInt(UpdateSequence);
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
            writer.WriteByte(WeaponTint);
            writer.WriteVarUInt((uint)WeaponComponents.Count);
            foreach (uint component in WeaponComponents)
            {
                writer.WriteUInt32(component);
            }

            writer.WriteQuantizedPosition(AimPosition);
            writer.WriteVarInt(InteriorId);
            writer.WriteUInt32(AnimationHash);

            // Gated on the flag, which the reader has already decoded by this point.
            if ((Flags & PlayerFlags.Ragdoll) != 0)
            {
                Ragdoll.Write(writer);
            }

            Appearance.Write(writer);
            return writer.ToArray();
        }

        /// <summary>
        /// Reads the tint and the component list. The count is bounded here for the
        /// same reason as on the entity: an unbounded count is an allocation a hostile
        /// client gets to choose.
        /// </summary>
        private static byte ReadWeaponAttachments(NetReader reader, out List<uint> components)
        {
            byte tint = reader.ReadByte();
            uint count = reader.ReadVarUInt();
            if (count > CharacterEntity.MaxWeaponComponents)
            {
                throw new NetSerializationException(
                    $"A client claims {count} weapon components; the limit is {CharacterEntity.MaxWeaponComponents}.");
            }

            components = new List<uint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                components.Add(reader.ReadUInt32());
            }

            return tint;
        }

        public static ClientStateUpdateMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            var message = new ClientStateUpdateMessage
            {
                ClientTick = reader.ReadVarUInt(),
                ClientTime = reader.ReadDouble(),
                AcknowledgedSnapshotId = reader.ReadVarUInt(),
                UpdateSequence = reader.ReadVarUInt(),
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
                WeaponTint = ReadWeaponAttachments(reader, out List<uint> components),
                AimPosition = reader.ReadQuantizedPosition(),
                InteriorId = reader.ReadVarInt(),
                AnimationHash = reader.ReadUInt32(),
            };

            message.WeaponComponents.AddRange(components);

            if ((message.Flags & PlayerFlags.Ragdoll) != 0)
            {
                message.Ragdoll = RagdollPose.Read(reader);
            }

            message.Appearance.Read(reader);
            return message;
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
        PlayerDied = 5,
        PlayerRespawned = 6,
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

    /// <summary>
    /// A client asking the server to adopt something it created locally — the
    /// vehicle it just got into, a prop a mod spawned.
    /// <para>
    /// The client cannot invent an entity id: ids are the server's, and a client
    /// that could choose its own could collide with another player's or overwrite an
    /// existing entity. So it describes what it made and correlates the answer
    /// through <see cref="RequestTag"/>.
    /// </para>
    /// </summary>
    public sealed class EntitySpawnRequestMessage
    {
        public EntityType Type { get; set; } = EntityType.Vehicle;

        public uint ModelHash { get; set; }

        public NetVector3 Position { get; set; }

        public float Heading { get; set; }

        public uint Dimension { get; set; }

        /// <summary>Client-chosen correlation id, echoed in the reply.</summary>
        public uint RequestTag { get; set; }

        /// <summary>Full state written by the entity's own serializer, or empty.</summary>
        public byte[] State { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(256);
            writer.WriteByte((byte)Type);
            writer.WriteUInt32(ModelHash);
            writer.WriteQuantizedPosition(Position);
            writer.WriteAngleDegrees(Heading);
            writer.WriteVarUInt(Dimension);
            writer.WriteUInt32(RequestTag);
            writer.WriteByteArray(State);
            return writer.ToArray();
        }

        public static EntitySpawnRequestMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new EntitySpawnRequestMessage
            {
                Type = (EntityType)reader.ReadByte(),
                ModelHash = reader.ReadUInt32(),
                Position = reader.ReadQuantizedPosition(),
                Heading = reader.ReadAngleDegrees(),
                Dimension = reader.ReadVarUInt(),
                RequestTag = reader.ReadUInt32(),
                State = reader.ReadByteArray(ProtocolConstants.MaxPacketSize),
            };
        }
    }

    /// <summary>
    /// The owning client's report of an entity it simulates.
    /// <para>
    /// The payload is written by the entity type's own serializer, so a mod-defined
    /// entity streams through this path with no protocol change. The server still
    /// validates it — ownership grants the right to propose, not to decide.
    /// </para>
    /// </summary>
    public sealed class OwnedEntityUpdateMessage
    {
        public EntityId EntityId { get; set; }

        /// <summary>
        /// Snapshot the delta was written against, or 0 for full state.
        /// <para>
        /// The baseline is a snapshot the client has <em>applied</em>, which means the
        /// server sent it and still holds it in that client's history. That is what
        /// makes delta compression safe over an unreliable channel: both sides can name
        /// the same starting point, so a lost update cannot silently desynchronise the
        /// chain the way a "delta against my previous send" scheme would.
        /// </para>
        /// </summary>
        public uint BaselineSnapshotId { get; set; }

        public byte[] State { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            var writer = new NetWriter(State.Length + 12);
            writer.WriteVarUInt(EntityId.Value);
            writer.WriteVarUInt(BaselineSnapshotId);
            writer.WriteByteArray(State);
            return writer.ToArray();
        }

        public static OwnedEntityUpdateMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new OwnedEntityUpdateMessage
            {
                EntityId = new EntityId(reader.ReadVarUInt()),
                BaselineSnapshotId = reader.ReadVarUInt(),
                State = reader.ReadByteArray(ProtocolConstants.MaxPacketSize),
            };
        }
    }

    public enum EntityReleaseKind : byte
    {
        /// <summary>Give up ownership; the server reassigns it.</summary>
        ReleaseOwnership = 0,

        /// <summary>Destroy the entity outright.</summary>
        Destroy = 1,
    }

    public sealed class EntityReleaseRequestMessage
    {
        public EntityId EntityId { get; set; }

        public EntityReleaseKind Kind { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(16);
            writer.WriteVarUInt(EntityId.Value);
            writer.WriteByte((byte)Kind);
            return writer.ToArray();
        }

        public static EntityReleaseRequestMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new EntityReleaseRequestMessage
            {
                EntityId = new EntityId(reader.ReadVarUInt()),
                Kind = (EntityReleaseKind)reader.ReadByte(),
            };
        }
    }

    public enum EntityEventKind : byte
    {
        SpawnAccepted = 0,
        SpawnRejected = 1,
        OwnershipGranted = 2,
        OwnershipRevoked = 3,
        Destroyed = 4,
    }

    /// <summary>Reliable notification about one entity's lifecycle or ownership.</summary>
    public sealed class EntityEventMessage
    {
        public EntityEventKind Kind { get; set; }

        public EntityId EntityId { get; set; }

        /// <summary>Echo of the spawn request's tag, 0 when this is not a spawn reply.</summary>
        public uint RequestTag { get; set; }

        public string Detail { get; set; } = string.Empty;

        public byte[] Serialize()
        {
            var writer = new NetWriter(128);
            writer.WriteByte((byte)Kind);
            writer.WriteVarUInt(EntityId.Value);
            writer.WriteUInt32(RequestTag);
            writer.WriteString(Detail);
            return writer.ToArray();
        }

        public static EntityEventMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new EntityEventMessage
            {
                Kind = (EntityEventKind)reader.ReadByte(),
                EntityId = new EntityId(reader.ReadVarUInt()),
                RequestTag = reader.ReadUInt32(),
                Detail = reader.ReadString(256),
            };
        }
    }

    /// <summary>
    /// A hit one client believes it landed. It is a <em>claim</em>: the server
    /// decides whether it happened, how much it was worth, and whether it killed.
    /// </summary>
    public sealed class DamageReportMessage
    {
        public EntityId TargetId { get; set; }

        public uint WeaponHash { get; set; }

        public int Damage { get; set; }

        public NetVector3 HitPosition { get; set; }

        /// <summary>GTA V bone index, for hit-location logic.</summary>
        public short HitBone { get; set; }

        public bool IsMelee { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(64);
            writer.WriteVarUInt(TargetId.Value);
            writer.WriteUInt32(WeaponHash);
            writer.WriteVarInt(Damage);
            writer.WriteQuantizedPosition(HitPosition);
            writer.WriteInt16(HitBone);
            writer.WriteBool(IsMelee);
            return writer.ToArray();
        }

        public static DamageReportMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new DamageReportMessage
            {
                TargetId = new EntityId(reader.ReadVarUInt()),
                WeaponHash = reader.ReadUInt32(),
                Damage = reader.ReadVarInt(),
                HitPosition = reader.ReadQuantizedPosition(),
                HitBone = reader.ReadInt16(),
                IsMelee = reader.ReadBool(),
            };
        }
    }

    /// <summary>
    /// One shot fired, travelling up from the shooter and back down to everyone near
    /// enough to see it.
    /// <para>
    /// <b>This carries no damage and never will.</b> The hit is arbitrated from
    /// <see cref="DamageReportMessage"/> against the server's own world; this message
    /// exists so the shot is *visible*. Letting a rendered bullet also deal damage
    /// would mean the same trigger pull is counted once by the arbiter and again by
    /// every client that drew it.
    /// </para>
    /// </summary>
    public sealed class WeaponShotMessage
    {
        /// <summary>
        /// Who fired. Left empty on the way up — the server fills it in from the
        /// session, because a client that names its own shooter can name someone else.
        /// </summary>
        public EntityId ShooterId { get; set; }

        public uint WeaponHash { get; set; }

        /// <summary>Muzzle position when the round left the barrel.</summary>
        public NetVector3 Origin { get; set; }

        /// <summary>Where the round ended: the impact point, or the aim point when it hit nothing.</summary>
        public NetVector3 Impact { get; set; }

        public byte[] Serialize()
        {
            var writer = new NetWriter(32);
            writer.WriteVarUInt(ShooterId.Value);
            writer.WriteUInt32(WeaponHash);
            writer.WriteQuantizedPosition(Origin);
            writer.WriteQuantizedPosition(Impact);
            return writer.ToArray();
        }

        public static WeaponShotMessage Deserialize(byte[] payload)
        {
            var reader = new NetReader(payload);
            return new WeaponShotMessage
            {
                ShooterId = new EntityId(reader.ReadVarUInt()),
                WeaponHash = reader.ReadUInt32(),
                Origin = reader.ReadQuantizedPosition(),
                Impact = reader.ReadQuantizedPosition(),
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
