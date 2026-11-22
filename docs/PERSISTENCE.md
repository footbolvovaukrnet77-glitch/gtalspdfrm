# Persistence

> English. Русский: [ru/PERSISTENCE.md](ru/PERSISTENCE.md).

## Model

```
SERVER WORLD STATE  →  PERSISTENCE LAYER  →  SQLite
```

Saving happens on a timer (`saveIntervalSeconds`, default 60), when a player
disconnects, on the `save` console command, and on clean shutdown.

Loading happens once at startup, before the listener accepts anyone.

## Storage

SQLite by default (`data/world.db`, WAL mode). The schema uses no
SQLite-specific types and every statement is plain parameterised SQL, so moving to
PostgreSQL is a driver swap rather than a redesign.

### Tables

| Table | Status | Contents |
| --- | --- | --- |
| `players` | **Live** | Identity token, name, position, health, armour, model, wanted level, dimension, interior, role, money, last seen |
| `world_state` | **Live** | Clock, weather, blackout, highest entity id, entity schema hash, save timestamp |
| `entities` | **Live** | Opaque full-state blobs keyed by entity id and type id |
| `vehicles`, `peds`, `objects` | Created, filled in Phase 3/5 | |
| `missions` | Created, filled in Phase 6 | |
| `inventories` | Created, filled in Phase 3 | |
| `mod_state` | Created, filled in Phase 6 | Mod-scoped key/value state not attached to an entity |
| `permissions` | Created, filled in Phase 10 | |
| `server_settings` | Created, filled in Phase 10 | |

The later-phase tables are created now so a database made by an early build needs
no migration when those phases land.

## Opaque entity blobs

Non-player entities are stored as the output of their own serializer's full-state
writer, with the type id alongside. The persistence layer never interprets them.

This is what lets a **mod-defined entity survive a restart on a server that has no
compiled knowledge of that mod**. The blob goes in and comes back out; the
serializer that understands it is supplied by the mod at load time.

The `world_state.schema_hash` column guards this. If the stored hash does not
match the running build's entity schema, the field layouts have changed and the
blobs cannot be trusted: player records are still restored, stored entity blobs
are skipped, and a warning names both hashes. Silently misinterpreting a blob
would be far worse than losing it.

## Identity

Players are keyed by `IdentityToken` — a GUID generated on first run and stored in
the client's `client.ini`.

Consequences worth being explicit about:

- **Not an account system.** Anyone who copies the token is that player. Real
  authentication is Phase 10.
- **Not tied to a name.** A player can rename freely and keep their character.
- **Not tied to an IP.** Reconnecting from a different address still works.

## Restart

```
SERVER START
  → open the database
  → load world_state       clock, weather, highest entity id
  → reserve entity ids     so ids are never reused
  → load entity blobs      when the schema hash matches
  → accept connections
```

```
SHUTDOWN (Ctrl+C, or the `stop` command)
  → notify connected players
  → save every connected player
  → save world state
  → close the database
```

`PersistenceTests.APlayerGetsTheirCharacterBackAfterAServerRestart` runs this
whole cycle: connect, walk, take damage, tear the server down, start a new one
against the same file, reconnect with the same identity, and assert position,
health, armour and the world clock all came back.

## Disabling it

`"persistenceEnabled": false` in `server.json` swaps in `NullPersistenceStore`.
Every call becomes a no-op and the world is fresh on each start. Useful for test
servers; the code path is identical, so nothing else changes.

## Writes happen off the tick thread

`BackgroundPersistenceStore` wraps the real store and queues writes to a worker.
A save is a disk write, and a disk write on the simulation thread is a stall every
player feels — especially now that entities are persisted too, which makes a save
a transaction over every object in the world.

Queued writes are **coalesced**: only the newest state for each player, for the
world, and for the entity set is kept. A slow disk therefore costs freshness,
never an unbounded queue — an unbounded queue is what turns a slow disk into an
out-of-memory crash.

Two details that matter:

- **Reads see queued writes.** A player who reconnects immediately gets their own
  last save even if it is still in the queue, rather than the older copy on disk.
- **Shutdown drains the queue.** A shutdown that dropped the last save is how a
  clean restart still loses a session's worth of progress.

Reads themselves stay synchronous. They happen at startup and on join, both rare
enough that a few milliseconds does not matter, and an asynchronous read would
mean a join could not be answered in the tick that received it.

## Migrations

The schema version lives in the database, in a `schema_version` table, and
migrations run in order on open.

The version is stored rather than inferred from which tables exist. Inferring
works until two changes touch the same table, and then it silently does the wrong
thing.

Opening a database written by a **newer** build is refused rather than attempted:
downgrading would lose the columns the newer build added.

## Known limits

- **No backups.** Copy `data/world.db` yourself. Because WAL mode is on, copy
  `-wal` and `-shm` alongside it, or stop the server first.
- **No PostgreSQL driver yet.** The SQL is already portable — no SQLite-specific
  types, every statement parameterised — so this is a driver swap, not a
  redesign.
