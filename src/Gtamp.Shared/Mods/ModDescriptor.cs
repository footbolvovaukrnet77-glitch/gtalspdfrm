using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Mods
{
    /// <summary>
    /// How much a mod needs from the network layer. Drives what the server does
    /// when a client does not have it (master prompt section 4: a missing mod must
    /// not automatically break the whole session).
    /// </summary>
    public enum ModNetworkRequirement : byte
    {
        /// <summary>Purely local (a texture pack, a graphics tweak). Never checked.</summary>
        ClientOnly = 0,

        /// <summary>Replicates state but degrades cleanly if the peer lacks it.</summary>
        Optional = 1,

        /// <summary>Both sides must have a compatible build or entities will not decode.</summary>
        Required = 2,
    }

    public enum ModCompatibility : byte
    {
        Compatible = 0,
        Missing = 1,
        WrongVersion = 2,
        HashMismatch = 3,
        PartiallyCompatible = 4,
        Unsupported = 5,
    }

    /// <summary>Identity of a single installed modification.</summary>
    public sealed class ModDescriptor
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = "0.0.0";

        /// <summary>Content hash of the mod's primary file, used to spot silent edits.</summary>
        public string Hash { get; set; } = string.Empty;

        public ModNetworkRequirement Requirement { get; set; } = ModNetworkRequirement.Optional;

        public List<string> Dependencies { get; } = new List<string>();

        public void Write(NetWriter writer)
        {
            writer.WriteString(Id);
            writer.WriteString(Name);
            writer.WriteString(Version);
            writer.WriteString(Hash);
            writer.WriteByte((byte)Requirement);
            writer.WriteVarUInt((uint)Dependencies.Count);
            foreach (string dependency in Dependencies)
            {
                writer.WriteString(dependency);
            }
        }

        public static ModDescriptor Read(NetReader reader)
        {
            var descriptor = new ModDescriptor
            {
                Id = reader.ReadString(128),
                Name = reader.ReadString(128),
                Version = reader.ReadString(64),
                Hash = reader.ReadString(128),
                Requirement = (ModNetworkRequirement)reader.ReadByte(),
            };

            uint count = reader.ReadVarUInt();
            if (count > 64)
            {
                throw new NetSerializationException($"Mod '{descriptor.Id}' declares {count} dependencies; the limit is 64.");
            }

            for (uint i = 0; i < count; i++)
            {
                descriptor.Dependencies.Add(reader.ReadString(128));
            }

            return descriptor;
        }

        public override string ToString() => $"{Id} {Version}";
    }

    /// <summary>Per-mod verdict returned to the client after the handshake.</summary>
    public sealed class ModCompatibilityEntry
    {
        public string ModId { get; set; } = string.Empty;

        public ModCompatibility Status { get; set; }

        public string Detail { get; set; } = string.Empty;

        public bool BlocksConnection { get; set; }

        public void Write(NetWriter writer)
        {
            writer.WriteString(ModId);
            writer.WriteByte((byte)Status);
            writer.WriteString(Detail);
            writer.WriteBool(BlocksConnection);
        }

        public static ModCompatibilityEntry Read(NetReader reader) => new ModCompatibilityEntry
        {
            ModId = reader.ReadString(128),
            Status = (ModCompatibility)reader.ReadByte(),
            Detail = reader.ReadString(512),
            BlocksConnection = reader.ReadBool(),
        };
    }
}
