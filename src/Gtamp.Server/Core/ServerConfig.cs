using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

        public string BindAddress { get; set; } = "0.0.0.0";

        public int Port { get; set; } = ProtocolConstants.DefaultPort;

        public int TickRate { get; set; } = ProtocolConstants.DefaultTickRate;

        public int SnapshotRate { get; set; } = ProtocolConstants.DefaultSnapshotRate;

        /// <summary>Bytes of snapshot payload per client per snapshot. Caps outbound bandwidth.</summary>
        public int SnapshotByteBudget { get; set; } = 1024;

        /// <summary>
        /// Floor the adaptive shaper will not go below, however bad a client's link is.
        /// Below this a client stops converging on the world at all.
        /// </summary>
        public int MinimumSnapshotByteBudget { get; set; } = 256;

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

        /// <summary>Seconds a dead player waits before the server respawns them.</summary>
        public double RespawnDelaySeconds { get; set; } = 8;

        /// <summary>
        /// Seconds of anti-cheat grace granted after the server moves a player itself
        /// (join, respawn). Must comfortably exceed one network round trip, or the
        /// client's in-flight updates from before the move get flagged.
        /// </summary>
        public double ServerMoveGraceSeconds { get; set; } = 3;

        /// <summary>How many networked entities one player may own at once.</summary>
        public int MaxEntitiesPerPlayer { get; set; } = 32;

        /// <summary>
        /// Beyond this distance an entity's owner is considered too far to simulate it
        /// well, and it is handed to a closer player or back to the server.
        /// </summary>
        public float OwnershipHandoffDistance { get; set; } = 300f;

        /// <summary>
        /// How far an owner may get before the entity is taken off them, as a multiple of
        /// <see cref="OwnershipHandoffDistance"/>.
        /// <para>
        /// Taking and keeping used to use the same number, which makes the boundary a
        /// switch: a player driving past it hands every nearby entity back to the server
        /// and takes it again on the next check, forever. One real session flipped eleven
        /// vehicles eight times in fifteen minutes, and each flip destroyed and rebuilt
        /// eleven cars in the game, because a client that is granted an entity it has no
        /// handle for drops the remote copy it was drawing.
        /// </para>
        /// <para>
        /// Hysteresis is the standard answer and this is the standard shape of it: take it
        /// at 300 m, keep it out to 450 m. The gap has to be wider than a player moves
        /// between two ownership checks, or the flapping simply moves outwards.
        /// </para>
        /// </summary>
        public float OwnershipKeepFactor { get; set; } = 1.5f;

        /// <summary>The distance an existing owner is allowed to reach before losing it.</summary>
        public float OwnershipReleaseDistance =>
            OwnershipHandoffDistance * (OwnershipKeepFactor < 1f ? 1f : OwnershipKeepFactor);

        public double OwnershipCheckIntervalSeconds { get; set; } = 2;

        /// <summary>
        /// How long a finished activity stays in the world before it and the entities
        /// it owns are removed. Long enough that every client sees the ending.
        /// </summary>
        public double FinishedActivityLingerSeconds { get; set; } = 30;

        /// <summary>
        /// Require every client to prove it holds the private half of the identity it
        /// claims (see docs/SECURITY.md).
        /// <para>
        /// On by default. Security that is off by default is decoration, and the
        /// protocol version has changed anyway, so there is no older client this
        /// would newly lock out. An operator running a private server among people
        /// who already trust each other can turn it off; what they give up is that
        /// anyone who learns another player's identity string becomes that player.
        /// </para>
        /// </summary>
        public bool RequireAuthentication { get; set; } = true;

        /// <summary>
        /// Encrypt and authenticate every packet after the handshake.
        /// <para>
        /// On by default, and it depends on <see cref="RequireAuthentication"/>: the
        /// key exchange rides inside the signed challenge, so a server that does not
        /// authenticate has nothing to bind the exchange to and would be agreeing a
        /// key with whoever answered first.
        /// </para>
        /// <para>
        /// Turning it off returns to plaintext UDP, where anyone on the path reads
        /// every position and chat line and can forge packets into a live session.
        /// The switch exists for a LAN, and for someone debugging with a packet
        /// capture.
        /// </para>
        /// </summary>
        public bool EncryptSessions { get; set; } = true;

        /// <summary>Reject connections whose mod set is missing a Required server mod.</summary>
        public bool EnforceRequiredMods { get; set; } = true;

        /// <summary>
        /// Mod event names the server forwards verbatim from the sender to every
        /// other player, without interpreting the payload.
        /// <para>
        /// This is how two clients running the same mod talk on a server that knows
        /// nothing about that mod. It carries no server authority — see
        /// <c>IServerModSdk.RegisterRelay</c> — so an operator who does not want
        /// clients passing each other opaque bytes can empty this list. The defaults
        /// are the two events the shipped adapters register.
        /// </para>
        /// </summary>
        public List<string> RelayedModEvents { get; set; } = new List<string> { "lspdfr.event", "rph.event" };

        /// <summary>
        /// Weapon validation envelopes for weapons this build does not know about —
        /// a weapons mod, or a DLC weapon added after this build shipped.
        /// <para>
        /// A weapon with no profile falls back to <c>DefaultMaxDamagePerHit</c> and
        /// <c>DefaultMaxRange</c>, which is deliberately permissive so an unknown
        /// weapon still works. Listing it here tightens that: a modded taser stops
        /// being allowed to claim 250 damage, and a modded long-range rifle stops
        /// having its legitimate hits rejected at 400 m.
        /// </para>
        /// <para>
        /// These are ceilings the server enforces, never damage values — the game
        /// decides what a hit actually does. An operator sets them; a client cannot,
        /// for the obvious reason.
        /// </para>
        /// </summary>
        public List<CustomWeaponSetting> CustomWeapons { get; set; } = new List<CustomWeaponSetting>();

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

        /// <summary>
        /// Property names found in the file that this build does not recognise.
        /// <para>
        /// <c>System.Text.Json</c> ignores unmapped members, so a misspelled setting
        /// used to be silently dropped: an operator writes <c>"maxPlayerz": 64</c>,
        /// gets 32, and is told nothing. Refusing to start would be worse — it would
        /// make an older build unable to read a newer file — so they are collected and
        /// reported instead.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public List<string> UnknownKeys { get; } = new List<string>();

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

                config.UnknownKeys.AddRange(FindUnknownKeys(json));
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

        /// <summary>
        /// Top-level property names in the document that no property on this type
        /// maps to. Only the top level: a nested object belongs to whichever setting
        /// declares it, and walking into one would report a mod's own keys as ours.
        /// </summary>
        private static List<string> FindUnknownKeys(string json)
        {
            var unknown = new List<string>();

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo property in typeof(ServerConfig).GetProperties())
            {
                known.Add(property.Name);
            }

            try
            {
                using var document = JsonDocument.Parse(
                    json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return unknown;
                }

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!known.Contains(property.Name))
                    {
                        unknown.Add(property.Name);
                    }
                }
            }
            catch (JsonException)
            {
                // Unparseable here means Deserialize already threw or will; reporting
                // "unknown keys" on top of a syntax error only obscures it.
            }

            return unknown;
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

            if (MinimumSnapshotByteBudget < 256 || MinimumSnapshotByteBudget > SnapshotByteBudget)
            {
                throw new InvalidDataException(
                    "minimumSnapshotByteBudget must be between 256 and snapshotByteBudget.");
            }

            if (RespawnDelaySeconds < 0 || RespawnDelaySeconds > 600)
            {
                throw new InvalidDataException("respawnDelaySeconds must be between 0 and 600.");
            }

            if (MaxEntitiesPerPlayer < 1 || MaxEntitiesPerPlayer > 1024)
            {
                throw new InvalidDataException("maxEntitiesPerPlayer must be between 1 and 1024.");
            }

            if (OwnershipHandoffDistance < 25f)
            {
                throw new InvalidDataException("ownershipHandoffDistance must be at least 25.");
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
    /// <summary>
    /// One operator-defined weapon envelope from <c>server.json</c>. Kept as a plain
    /// settable type rather than reusing <c>WeaponProfile</c>, whose constructor
    /// computes the hash and whose properties are read-only — a shape the JSON
    /// deserialiser cannot fill.
    /// </summary>
    public sealed class CustomWeaponSetting
    {
        /// <summary>The game's weapon name, e.g. <c>WEAPON_MYMOD_RAILGUN</c>. Hashed with joaat.</summary>
        public string Name { get; set; } = string.Empty;

        public int MaxDamagePerHit { get; set; } = 100;

        public float MaxRange { get; set; } = 200f;

        public bool Melee { get; set; }

        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && MaxDamagePerHit > 0 && MaxRange > 0f;

        public WeaponProfile ToProfile() => new WeaponProfile(Name.Trim(), MaxDamagePerHit, MaxRange, Melee);
    }

}
