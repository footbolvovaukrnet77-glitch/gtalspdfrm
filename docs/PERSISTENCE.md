# Persistence

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

## Known limits

- **Saves are synchronous**, on the tick thread. At Phase 1 scale (a few dozen
  players, one table write each) this is microseconds. It will need to move to a
  background writer before the entity count reaches the thousands — Phase 12.
- **No migrations yet.** The schema is additive so far and the hash check catches
  incompatibility, but there is no version-stamped migration runner. Phase 5.
- **No backups.** Copy `data/world.db` yourself. Because WAL mode is on, copy
  `-wal` and `-shm` alongside it, or stop the server first.
