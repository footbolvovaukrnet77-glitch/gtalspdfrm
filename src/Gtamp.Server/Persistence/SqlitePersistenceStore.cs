using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gtamp.Shared.Security;
using Microsoft.Data.Sqlite;

namespace Gtamp.Server.Persistence
{
    /// <summary>
    /// SQLite-backed store. The schema deliberately mirrors master prompt section 34
    /// including the tables that later phases fill in, so moving to PostgreSQL later
    /// is a driver swap rather than a redesign: no SQLite-specific types are used and
    /// every statement is plain parameterised SQL.
    /// </summary>
    public sealed class SqlitePersistenceStore : IPersistenceStore
    {
        /// <summary>Schema this build writes and understands.</summary>
        public const int CurrentSchemaVersion = 3;

        private readonly string _path;
        private SqliteConnection? _connection;

        public SqlitePersistenceStore(string path)
        {
            _path = path;
        }

        public bool Enabled => true;

        public void Initialize()
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());

            _connection.Open();

            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");

            Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

            Execute(@"
CREATE TABLE IF NOT EXISTS players (
    identity_token TEXT PRIMARY KEY,
    name           TEXT NOT NULL,
    x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, heading REAL NOT NULL,
    health INTEGER NOT NULL, max_health INTEGER NOT NULL, armor INTEGER NOT NULL,
    model_hash INTEGER NOT NULL, wanted_level INTEGER NOT NULL,
    dimension INTEGER NOT NULL, interior_id INTEGER NOT NULL,
    role INTEGER NOT NULL, money INTEGER NOT NULL,
    last_seen_utc TEXT NOT NULL
);");

            Execute(@"
CREATE TABLE IF NOT EXISTS world_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    time_of_day INTEGER NOT NULL, clock_scale REAL NOT NULL,
    weather_hash INTEGER NOT NULL, next_weather_hash INTEGER NOT NULL,
    weather_transition REAL NOT NULL, blackout INTEGER NOT NULL,
    highest_entity_id INTEGER NOT NULL, schema_hash INTEGER NOT NULL,
    saved_at_utc TEXT NOT NULL
);");

            Execute(@"
CREATE TABLE IF NOT EXISTS entities (
    entity_id INTEGER PRIMARY KEY,
    type_id   INTEGER NOT NULL,
    state     BLOB NOT NULL
);");

            // Reserved for later phases. Created now so a database made by an early
            // build does not need a migration when those phases land.
            Execute("CREATE TABLE IF NOT EXISTS vehicles (entity_id INTEGER PRIMARY KEY, state BLOB NOT NULL);");
            Execute("CREATE TABLE IF NOT EXISTS peds (entity_id INTEGER PRIMARY KEY, state BLOB NOT NULL);");
            Execute("CREATE TABLE IF NOT EXISTS objects (entity_id INTEGER PRIMARY KEY, state BLOB NOT NULL);");
            Execute("CREATE TABLE IF NOT EXISTS missions (mission_id TEXT PRIMARY KEY, state BLOB NOT NULL);");
            Execute("CREATE TABLE IF NOT EXISTS inventories (owner TEXT PRIMARY KEY, state BLOB NOT NULL);");
            Execute("CREATE TABLE IF NOT EXISTS mod_state (mod_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (mod_id, key));");
            Execute("CREATE TABLE IF NOT EXISTS permissions (identity_token TEXT NOT NULL, permission TEXT NOT NULL, PRIMARY KEY (identity_token, permission));");
            Execute("CREATE TABLE IF NOT EXISTS server_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
            Execute(BansTableSql);

            RunMigrations();
        }

        /// <summary>
        /// Brings an existing database up to <see cref="CurrentSchemaVersion"/>.
        /// <para>
        /// Migrations run in order and each is responsible for being safe to apply to
        /// the version before it. The version is stored in the database rather than
        /// inferred from which tables exist: inferring works until two changes touch the
        /// same table, and then it silently does the wrong thing.
        /// </para>
        /// </summary>
        private void RunMigrations()
        {
            int version = ReadSchemaVersion();

            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The database is at schema version {version} but this build understands {CurrentSchemaVersion}. " +
                    "It was written by a newer server; downgrading would lose data, so it is refused.");
            }

            while (version < CurrentSchemaVersion)
            {
                version++;
                ApplyMigration(version);
                WriteSchemaVersion(version);
            }
        }

        private void ApplyMigration(int version)
        {
            switch (version)
            {
                case 1:
                    // The initial schema, created above by the CREATE TABLE IF NOT EXISTS
                    // statements. Recorded so later migrations have a floor to build on.
                    break;

                case 2:
                    // Entity blobs gained a dimension so a persisted entity comes back
                    // into the instance it belonged to.
                    AddColumnIfMissing("entities", "dimension", "INTEGER NOT NULL DEFAULT 0");
                    break;

                case 3:
                    // Bans, keyed by identity public key. Created here as well as in
                    // the CREATE TABLE block above, so a database made by an older
                    // build gains the table rather than failing its first ban.
                    Execute(BansTableSql);
                    break;

                default:
                    throw new InvalidOperationException($"No migration is defined for schema version {version}.");
            }
        }

        private int ReadSchemaVersion()
        {
            using SqliteCommand command = CreateCommand("SELECT version FROM schema_version LIMIT 1;");
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? reader.GetInt32(0) : 0;
        }

        private void WriteSchemaVersion(int version)
        {
            Execute("DELETE FROM schema_version;");
            using SqliteCommand command = CreateCommand("INSERT INTO schema_version (version) VALUES ($version);");
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }

        private void AddColumnIfMissing(string table, string column, string definition)
        {
            using (SqliteCommand check = CreateCommand($"PRAGMA table_info({table});"))
            using (SqliteDataReader reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }

        public void SavePlayer(PersistedPlayer player)
        {
            using SqliteCommand command = CreateCommand(@"
INSERT INTO players (identity_token, name, x, y, z, heading, health, max_health, armor,
                     model_hash, wanted_level, dimension, interior_id, role, money, last_seen_utc)
VALUES ($token, $name, $x, $y, $z, $heading, $health, $maxHealth, $armor,
        $model, $wanted, $dimension, $interior, $role, $money, $lastSeen)
ON CONFLICT(identity_token) DO UPDATE SET
    name = excluded.name, x = excluded.x, y = excluded.y, z = excluded.z, heading = excluded.heading,
    health = excluded.health, max_health = excluded.max_health, armor = excluded.armor,
    model_hash = excluded.model_hash, wanted_level = excluded.wanted_level,
    dimension = excluded.dimension, interior_id = excluded.interior_id,
    role = excluded.role, money = excluded.money, last_seen_utc = excluded.last_seen_utc;");

            command.Parameters.AddWithValue("$token", player.IdentityToken);
            command.Parameters.AddWithValue("$name", player.Name);
            command.Parameters.AddWithValue("$x", player.X);
            command.Parameters.AddWithValue("$y", player.Y);
            command.Parameters.AddWithValue("$z", player.Z);
            command.Parameters.AddWithValue("$heading", player.Heading);
            command.Parameters.AddWithValue("$health", player.Health);
            command.Parameters.AddWithValue("$maxHealth", player.MaxHealth);
            command.Parameters.AddWithValue("$armor", player.Armor);
            command.Parameters.AddWithValue("$model", player.ModelHash);
            command.Parameters.AddWithValue("$wanted", player.WantedLevel);
            command.Parameters.AddWithValue("$dimension", player.Dimension);
            command.Parameters.AddWithValue("$interior", player.InteriorId);
            command.Parameters.AddWithValue("$role", player.Role);
            command.Parameters.AddWithValue("$money", player.Money);
            command.Parameters.AddWithValue("$lastSeen", player.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public PersistedPlayer? LoadPlayer(string identityToken)
        {
            using SqliteCommand command = CreateCommand("SELECT * FROM players WHERE identity_token = $token;");
            command.Parameters.AddWithValue("$token", identityToken);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PersistedPlayer
            {
                IdentityToken = reader.GetString(reader.GetOrdinal("identity_token")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                X = (float)reader.GetDouble(reader.GetOrdinal("x")),
                Y = (float)reader.GetDouble(reader.GetOrdinal("y")),
                Z = (float)reader.GetDouble(reader.GetOrdinal("z")),
                Heading = (float)reader.GetDouble(reader.GetOrdinal("heading")),
                Health = reader.GetInt32(reader.GetOrdinal("health")),
                MaxHealth = reader.GetInt32(reader.GetOrdinal("max_health")),
                Armor = reader.GetInt32(reader.GetOrdinal("armor")),
                ModelHash = (uint)reader.GetInt64(reader.GetOrdinal("model_hash")),
                WantedLevel = (byte)reader.GetInt32(reader.GetOrdinal("wanted_level")),
                Dimension = (uint)reader.GetInt64(reader.GetOrdinal("dimension")),
                InteriorId = reader.GetInt32(reader.GetOrdinal("interior_id")),
                Role = reader.GetInt32(reader.GetOrdinal("role")),
                Money = reader.GetInt64(reader.GetOrdinal("money")),
                LastSeenUtc = DateTime.Parse(
                    reader.GetString(reader.GetOrdinal("last_seen_utc")),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            };
        }

        public void SaveWorld(PersistedWorld world)
        {
            using SqliteCommand command = CreateCommand(@"
INSERT INTO world_state (id, time_of_day, clock_scale, weather_hash, next_weather_hash,
                         weather_transition, blackout, highest_entity_id, schema_hash, saved_at_utc)
VALUES (1, $time, $scale, $weather, $nextWeather, $transition, $blackout, $highest, $schema, $savedAt)
ON CONFLICT(id) DO UPDATE SET
    time_of_day = excluded.time_of_day, clock_scale = excluded.clock_scale,
    weather_hash = excluded.weather_hash, next_weather_hash = excluded.next_weather_hash,
    weather_transition = excluded.weather_transition, blackout = excluded.blackout,
    highest_entity_id = excluded.highest_entity_id, schema_hash = excluded.schema_hash,
    saved_at_utc = excluded.saved_at_utc;");

            command.Parameters.AddWithValue("$time", world.TimeOfDaySeconds);
            command.Parameters.AddWithValue("$scale", world.ClockScale);
            command.Parameters.AddWithValue("$weather", world.WeatherHash);
            command.Parameters.AddWithValue("$nextWeather", world.NextWeatherHash);
            command.Parameters.AddWithValue("$transition", world.WeatherTransition);
            command.Parameters.AddWithValue("$blackout", world.Blackout ? 1 : 0);
            command.Parameters.AddWithValue("$highest", world.HighestEntityId);
            command.Parameters.AddWithValue("$schema", world.SchemaHash);
            command.Parameters.AddWithValue("$savedAt", world.SavedAtUtc.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public PersistedWorld? LoadWorld()
        {
            using SqliteCommand command = CreateCommand("SELECT * FROM world_state WHERE id = 1;");
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PersistedWorld
            {
                TimeOfDaySeconds = reader.GetInt32(reader.GetOrdinal("time_of_day")),
                ClockScale = (float)reader.GetDouble(reader.GetOrdinal("clock_scale")),
                WeatherHash = (uint)reader.GetInt64(reader.GetOrdinal("weather_hash")),
                NextWeatherHash = (uint)reader.GetInt64(reader.GetOrdinal("next_weather_hash")),
                WeatherTransition = (float)reader.GetDouble(reader.GetOrdinal("weather_transition")),
                Blackout = reader.GetInt32(reader.GetOrdinal("blackout")) != 0,
                HighestEntityId = (uint)reader.GetInt64(reader.GetOrdinal("highest_entity_id")),
                SchemaHash = (uint)reader.GetInt64(reader.GetOrdinal("schema_hash")),
                SavedAtUtc = DateTime.Parse(
                    reader.GetString(reader.GetOrdinal("saved_at_utc")),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            };
        }

        public void SaveEntities(IReadOnlyList<PersistedEntity> entities)
        {
            using SqliteTransaction transaction = Connection.BeginTransaction();
            using (SqliteCommand clear = Connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM entities;";
                clear.ExecuteNonQuery();
            }

            foreach (PersistedEntity entity in entities)
            {
                using SqliteCommand insert = Connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO entities (entity_id, type_id, state, dimension) VALUES ($id, $type, $state, $dimension);";
                insert.Parameters.AddWithValue("$id", entity.EntityId);
                insert.Parameters.AddWithValue("$type", entity.TypeId);
                insert.Parameters.AddWithValue("$state", entity.State);
                insert.Parameters.AddWithValue("$dimension", entity.Dimension);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public IReadOnlyList<PersistedEntity> LoadEntities()
        {
            var results = new List<PersistedEntity>();
            using SqliteCommand command = CreateCommand(
                "SELECT entity_id, type_id, state, dimension FROM entities ORDER BY entity_id;");

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PersistedEntity
                {
                    EntityId = (uint)reader.GetInt64(0),
                    TypeId = (byte)reader.GetInt32(1),
                    State = (byte[])reader.GetValue(2),
                    Dimension = (uint)reader.GetInt64(3),
                });
            }

            return results;
        }

        private const string BansTableSql = @"
CREATE TABLE IF NOT EXISTS bans (
    public_key  TEXT PRIMARY KEY,
    player_name TEXT NOT NULL,
    reason      TEXT NOT NULL,
    issued_by   TEXT NOT NULL,
    issued_at   TEXT NOT NULL,
    expires_at  TEXT
);";

        /// <summary>
        /// Replaces the whole ban list in one transaction.
        /// <para>
        /// Wholesale rather than incremental because the list is small and changes
        /// rarely, and because a partial write is the one failure mode that matters
        /// here: a ban applied in memory and lost on disk comes back the next time the
        /// server restarts, which is exactly when nobody is watching.
        /// </para>
        /// </summary>
        public void SaveBans(IReadOnlyList<BanEntry> bans)
        {
            using SqliteTransaction transaction = Connection.BeginTransaction();

            using (SqliteCommand clear = CreateCommand("DELETE FROM bans;"))
            {
                clear.Transaction = transaction;
                clear.ExecuteNonQuery();
            }

            foreach (BanEntry ban in bans)
            {
                using SqliteCommand insert = CreateCommand(
                    "INSERT INTO bans (public_key, player_name, reason, issued_by, issued_at, expires_at) " +
                    "VALUES ($key, $name, $reason, $by, $at, $expires);");

                insert.Transaction = transaction;
                insert.Parameters.AddWithValue("$key", ban.PublicKey);
                insert.Parameters.AddWithValue("$name", ban.PlayerName ?? string.Empty);
                insert.Parameters.AddWithValue("$reason", ban.Reason ?? string.Empty);
                insert.Parameters.AddWithValue("$by", ban.IssuedBy ?? "server");
                insert.Parameters.AddWithValue("$at", ban.IssuedAt.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$expires",
                    ban.ExpiresAt.HasValue
                        ? (object)ban.ExpiresAt.Value.ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value);

                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public IReadOnlyList<BanEntry> LoadBans()
        {
            var bans = new List<BanEntry>();
            using SqliteCommand command = CreateCommand(
                "SELECT public_key, player_name, reason, issued_by, issued_at, expires_at FROM bans;");

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                bans.Add(new BanEntry
                {
                    PublicKey = reader.GetString(0),
                    PlayerName = reader.GetString(1),
                    Reason = reader.GetString(2),
                    IssuedBy = reader.GetString(3),
                    IssuedAt = DateTime.Parse(
                        reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    ExpiresAt = reader.IsDBNull(5)
                        ? null
                        : DateTime.Parse(
                            reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                });
            }

            return bans;
        }

        public string Describe() => $"SQLite at {Path.GetFullPath(_path)}";

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
            SqliteConnection.ClearAllPools();
        }

        private SqliteConnection Connection =>
            _connection ?? throw new InvalidOperationException("The persistence store has not been initialised.");

        private SqliteCommand CreateCommand(string sql)
        {
            SqliteCommand command = Connection.CreateCommand();
            command.CommandText = sql;
            return command;
        }

        private void Execute(string sql)
        {
            using SqliteCommand command = CreateCommand(sql);
            command.ExecuteNonQuery();
        }
    }
}
