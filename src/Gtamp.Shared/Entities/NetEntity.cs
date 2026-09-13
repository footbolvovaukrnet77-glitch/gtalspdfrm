using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Base class for everything the server tracks. The fields declared here are
    /// the ones the replication layer, the anti-cheat and the entity inspector can
    /// rely on for <em>any</em> entity, including mod-defined ones.
    /// </summary>
    public abstract class NetEntity
    {
        protected NetEntity(EntityId id, EntityType type)
        {
            Id = id;
            Type = type;
        }

        public EntityId Id { get; }

        public EntityType Type { get; }

        /// <summary>Player id currently simulating this entity. 0 means the server owns it.</summary>
        public uint OwnerId { get; set; }

        public NetVector3 Position { get; set; }

        public NetVector3 Velocity { get; set; }

        /// <summary>Yaw in degrees, 0..360.</summary>
        public float Heading { get; set; }

        /// <summary>Instance/bucket separation. Entities in different dimensions never see each other.</summary>
        public uint Dimension { get; set; }

        /// <summary>GTA V interior id, or 0 outdoors.</summary>
        public int InteriorId { get; set; }

        /// <summary>Incremented by the server on every accepted mutation. Used to detect state mismatch.</summary>
        public uint NetworkVersion { get; set; }

        /// <summary>Server tick of the last accepted mutation.</summary>
        public uint LastUpdateTick { get; set; }

        /// <summary>
        /// Free-form key/value state owned by mods. Kept as strings so an unknown
        /// mod's data can still be stored, replicated and persisted verbatim by a
        /// server that has never heard of that mod.
        /// </summary>
        public Dictionary<string, string> CustomData { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public abstract NetEntity Clone();

        protected void CopyBaseTo(NetEntity target)
        {
            target.OwnerId = OwnerId;
            target.Position = Position;
            target.Velocity = Velocity;
            target.Heading = Heading;
            target.Dimension = Dimension;
            target.InteriorId = InteriorId;
            target.NetworkVersion = NetworkVersion;
            target.LastUpdateTick = LastUpdateTick;
            target.CustomData.Clear();
            foreach (KeyValuePair<string, string> pair in CustomData)
            {
                target.CustomData[pair.Key] = pair.Value;
            }
        }
    }
}
