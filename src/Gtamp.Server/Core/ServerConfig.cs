using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;

namespace Gtamp.Server.Core
{
    /// <summary>
    /// Everything an operator can set, matching master prompt section 32. Written
    /// back to disk on first run so the file is self-documenting rather than
    /// something the operator has to guess at.
    /// </summary>
    public sealed class ServerConfig
    {
        public string ServerName { get; set; } = "GTAMP Server";

        public int MaxPlayers { get; set; } = 32;

        public string Password { get; set; } = string.Empty;

        public bool Public { get; set; }

        public string BindAddress { get; set; } = "0.0.0.0";

        public int Port { get; set; } = ProtocolConstants.DefaultPort;

        public int TickRate { get; set; } = ProtocolConstants.DefaultTickRate;

        public int SnapshotRate { get; set; } = ProtocolConstants.DefaultSnapshotRate;

        /// <summary>Bytes of snapshot payload per client per snapshot. Caps outbound bandwidth.</summary>
        public int SnapshotByteBudget { get; set; } = 1024;

        /// <summary>Seconds between persistence saves. 0 disables periodic saving.</summary>
        public double SaveIntervalSeconds { get; set; } = 60;

        public string DatabasePath { get; set; } = "data/world.db";

        public bool PersistenceEnabled { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AntiCheatLevel AntiCheat { get; set; } = AntiCheatLevel.Standard;

        public bool FriendlyFire { get; set; } = true;

        public bool PlayerVersusPlayer { get; set; } = true;

        public bool VehicleDamage { get; set; } = true;

        public bool NpcDamage { get; set; } = true;

        /// <summary>In-game seconds per real second. 30 matches single-player GTA V.</summary>
        public float ClockScale { get; set; } = 30f;

        /// <summary>Starting time of day, "HH:MM" or empty to restore from persistence.</summary>
        public string StartTime { get; set; } = "12:00";

        /// <summary>Weather name applied at startup, empty to restore from persistence.</summary>
        public string StartWeather { get; set; } = "EXTRASUNNY";

        /// <summary>
        /// How long a disconnected player's body stays in the world. 0 removes it
        /// immediately; the player's saved state is retained either way and restored
        /// on reconnect.
        /// </summary>
        public double KeepDisconnectedBodySeconds { get; set; }

        /// <summary>Reject connections whose mod set is missing a Required server mod.</summary>
        public bool EnforceRequiredMods { get; set; } = true;

        public string LogDirectory { get; set; } = "logs";

        public bool VerboseNetworkLogging { get; set; }

        public double TickIntervalSeconds => 1d / Math.Max(1, TickRate);

        public double SnapshotIntervalSeconds => 1d / Math.Max(1, SnapshotRate);

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static ServerConfig LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                ServerConfig? config = JsonSerializer.Deserialize<ServerConfig>(json, SerializerOptions);
                if (config == null)
                {
                    throw new InvalidDataException($"'{path}' did not contain a server configuration object.");
                }

                config.Validate();
                return config;
            }

            var created = new ServerConfig();
            created.Save(path);
            return created;
        }

        public void Save(string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }

        public void Validate()
        {
            if (MaxPlayers < 1 || MaxPlayers > 256)
            {
                throw new InvalidDataException("maxPlayers must be between 1 and 256.");
            }

            if (Port < 1 || Port > 65535)
            {
                throw new InvalidDataException("port must be between 1 and 65535.");
            }

            if (TickRate < 10 || TickRate > 240)
            {
                throw new InvalidDataException("tickRate must be between 10 and 240.");
            }

            if (SnapshotRate < 1 || SnapshotRate > TickRate)
            {
                throw new InvalidDataException("snapshotRate must be between 1 and tickRate.");
            }

            if (SnapshotByteBudget < 256 || SnapshotByteBudget > 64 * 1024)
            {
                throw new InvalidDataException("snapshotByteBudget must be between 256 and 65536.");
            }
        }

        public bool TryParseStartTime(out int hours, out int minutes)
        {
            hours = 12;
            minutes = 0;
            if (string.IsNullOrWhiteSpace(StartTime))
            {
                return false;
            }

            string[] parts = StartTime.Split(':');
            return parts.Length == 2
                   && int.TryParse(parts[0], out hours)
                   && int.TryParse(parts[1], out minutes)
                   && hours is >= 0 and < 24
                   && minutes is >= 0 and < 60;
        }
    }
}
