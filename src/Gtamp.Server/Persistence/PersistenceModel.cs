using System;
using System.Collections.Generic;
using Gtamp.Shared.Security;

namespace Gtamp.Server.Persistence
{
    public sealed class PersistedPlayer
    {
        public string IdentityToken { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        public float Heading { get; set; }

        public int Health { get; set; } = 200;

        public int MaxHealth { get; set; } = 200;

        public int Armor { get; set; }

        public uint ModelHash { get; set; }

        public byte WantedLevel { get; set; }

        public uint Dimension { get; set; }

        public int InteriorId { get; set; }

        public int Role { get; set; }

        public long Money { get; set; }

        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class PersistedWorld
    {
        public int TimeOfDaySeconds { get; set; }

        public float ClockScale { get; set; } = 30f;

        public uint WeatherHash { get; set; }

        public uint NextWeatherHash { get; set; }

        public float WeatherTransition { get; set; }

        public bool Blackout { get; set; }

        public uint HighestEntityId { get; set; }

        public uint SchemaHash { get; set; }

        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A non-player entity stored as its own serializer's full-state blob. Keeping
    /// the blob opaque is what lets a mod-defined entity persist across a restart on
    /// a server that has no compiled knowledge of that mod's fields.
    /// </summary>
    public sealed class PersistedEntity
    {
        public uint EntityId { get; set; }

        public byte TypeId { get; set; }

        public byte[] State { get; set; } = Array.Empty<byte>();

        public uint Dimension { get; set; }
    }

    /// <summary>
    /// Persistence contract. Every method is synchronous and called from the tick
    /// loop's save phase, so implementations must be fast or buffer internally.
    /// </summary>
    public interface IPersistenceStore : IDisposable
    {
        bool Enabled { get; }

        void Initialize();

        void SavePlayer(PersistedPlayer player);

        PersistedPlayer? LoadPlayer(string identityToken);

        void SaveWorld(PersistedWorld world);

        PersistedWorld? LoadWorld();

        void SaveEntities(IReadOnlyList<PersistedEntity> entities);

        IReadOnlyList<PersistedEntity> LoadEntities();

        /// <summary>Replaces the stored ban list wholesale. Bans change rarely and are small.</summary>
        void SaveBans(IReadOnlyList<BanEntry> bans);

        IReadOnlyList<BanEntry> LoadBans();

        string Describe();
    }

    /// <summary>Used when persistence is switched off. Every call is a no-op.</summary>
    public sealed class NullPersistenceStore : IPersistenceStore
    {
        public bool Enabled => false;

        public void Initialize()
        {
        }

        public void SavePlayer(PersistedPlayer player)
        {
        }

        public PersistedPlayer? LoadPlayer(string identityToken) => null;

        public void SaveWorld(PersistedWorld world)
        {
        }

        public PersistedWorld? LoadWorld() => null;

        public void SaveEntities(IReadOnlyList<PersistedEntity> entities)
        {
        }

        public IReadOnlyList<PersistedEntity> LoadEntities() => Array.Empty<PersistedEntity>();

        public void SaveBans(IReadOnlyList<BanEntry> bans)
        {
        }

        public IReadOnlyList<BanEntry> LoadBans() => Array.Empty<BanEntry>();

        public string Describe() => "disabled";

        public void Dispose()
        {
        }
    }
}
