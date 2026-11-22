using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Mods
{
    /// <summary>
    /// The set of mods one side has loaded, plus the entity schema hash they
    /// produce. Exchanged during the handshake so both ends know, before any world
    /// state moves, exactly which mods each other has.
    /// </summary>
    public sealed class ModManifest
    {
        public const int MaxMods = 512;

        public List<ModDescriptor> Mods { get; } = new List<ModDescriptor>();

        /// <summary>Fingerprint of the registered entity type table (see EntityRegistry.ComputeSchemaHash).</summary>
        public uint SchemaHash { get; set; }

        public bool RagePluginHookPresent { get; set; }

        public string RagePluginHookVersion { get; set; } = string.Empty;

        public bool LspdfrPresent { get; set; }

        public string LspdfrVersion { get; set; } = string.Empty;

        public bool ScriptHookVPresent { get; set; }

        public void Write(NetWriter writer)
        {
            writer.WriteUInt32(SchemaHash);
            writer.WriteBool(RagePluginHookPresent);
            writer.WriteString(RagePluginHookVersion);
            writer.WriteBool(LspdfrPresent);
            writer.WriteString(LspdfrVersion);
            writer.WriteBool(ScriptHookVPresent);
            writer.WriteVarUInt((uint)Mods.Count);
            foreach (ModDescriptor mod in Mods)
            {
                mod.Write(writer);
            }
        }

        public static ModManifest Read(NetReader reader)
        {
            var manifest = new ModManifest
            {
                SchemaHash = reader.ReadUInt32(),
                RagePluginHookPresent = reader.ReadBool(),
                RagePluginHookVersion = reader.ReadString(64),
                LspdfrPresent = reader.ReadBool(),
                LspdfrVersion = reader.ReadString(64),
                ScriptHookVPresent = reader.ReadBool(),
            };

            uint count = reader.ReadVarUInt();
            if (count > MaxMods)
            {
                throw new NetSerializationException($"Manifest declares {count} mods; the limit is {MaxMods}.");
            }

            for (uint i = 0; i < count; i++)
            {
                manifest.Mods.Add(ModDescriptor.Read(reader));
            }

            return manifest;
        }

        public ModDescriptor? Find(string modId)
        {
            foreach (ModDescriptor mod in Mods)
            {
                if (string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase))
                {
                    return mod;
                }
            }

            return null;
        }
    }
}
