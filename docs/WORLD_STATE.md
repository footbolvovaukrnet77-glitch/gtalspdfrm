# World state

> English. Русский: [ru/WORLD_STATE.md](ru/WORLD_STATE.md).

## The rule

> **SERVER WORLD STATE = FULL. CLIENT NETWORK REPLICATION = OPTIMISED.**

The server keeps every entity, always. Distance, streaming range and bandwidth
affect *how often* an entity is sent to a given client, and never *whether* the
server knows about it.

This is the framework's central constraint and it is enforced structurally, not by
convention: `ServerWorld` is the only class permitted to add or remove entities,
and it contains no distance-aware removal path at all. There is no
`RemoveDistant`, no `CullByRange`, no streaming eviction. An entity leaves the
world when it is genuinely destroyed and at no other time.

## Why it matters

Five players in Los Santos, Sandy Shores, Paleto Bay, Fort Zancudo and Grapeseed
are in one world. A proximity-only design would quietly become five disconnected
single-player games that occasionally notice each other. Every consequence people
actually care about — a pursuit that started in Sandy Shores still running when
you arrive, a vehicle you parked in Paleto still being there, a mission that keeps
progressing while you drive across the map — depends on the server continuing to
simulate and remember entities nobody is currently standing next to.

`StressTests.DistanceNeverRemovesAnEntityFromTheServerWorld` is the regression
test: 100 entities scattered across the map corners, one connected player, and
after 20 seconds both the server *and* that player know about all 101.

## Composition

```
WorldState
├── Tick               monotonic simulation counter
├── ServerTime         seconds since server start; the timeline clients interpolate on
├── Environment        clock, weather, wind, blackout
└── Entities           EntityId → NetEntity, no distance filter of any kind
```

### Environment

| Field | Notes |
| --- | --- |
| `TimeOfDaySeconds` | 0–86399, advanced every tick by `ClockScale` |
| `ClockScale` | in-game seconds per real second; 30 matches single-player GTA V |
| `WeatherHash` / `NextWeatherHash` | joaat hashes, so a weather mod needs no protocol change |
| `WeatherTransition` | 0–1 blend |
| `WindSpeed` / `WindDirection` | |
| `Blackout` | |

The environment is carried in the snapshot header and only when it has changed
(`SnapshotTests.EnvironmentIsSentOnlyWhenItChanges`). It is tiny and every client
needs it regardless of where they are standing.

The client applies the clock only when it has drifted more than 20 in-game
seconds — writing it every frame makes the sky flicker.

## Replication priority

Priority is the *only* place distance is read.

```
score(entity, viewer) =
    never sent before        → +∞                      (a joining client converges fast)
    otherwise                → proximity × typeWeight × 1000 + staleness

    proximity  = 1                      if distance ≤ 150 m
                 150 / distance         beyond that
    typeWeight = 4 for players, 1 otherwise
    staleness  = ticks since this entity was last sent to this client
```

Two properties follow from the shape of this function:

- **Nearby things update often.** Proximity dominates for anything in view.
- **Distant things still update.** Staleness grows without bound, so an entity
  nobody is near eventually outranks a nearby idle one and gets sent. Nothing
  starves.

## Server tick

```
Tick(now):
  1. receive datagrams          route by endpoint; connectionless first
  2. process session messages   validate and apply client input
  3. advance world              tick counter, server time, world clock
  4. snapshots (at snapshot rate, per client)
  5. reap timed-out sessions
  6. flush peers
  7. persist (at save interval)
```

Time is passed in rather than read from a clock, so the whole server can be driven
deterministically from a test. `TestHarness` does exactly that: virtual time,
virtual network, no sleeping anywhere in the suite.

## Disconnect and reconnect

When a player disconnects:

1. their state is written to persistence;
2. their body leaves the world (configurable via `keepDisconnectedBodySeconds`,
   default 0 — a frozen ghost standing in the street is worse than an absence);
3. **their saved state does not leave.** It is keyed by identity token.

When they come back:

1. the identity token is recognised;
2. a new entity is created and initialised from the saved record — position,
   health, armour, model, wanted level, dimension, interior;
3. the client receives a **full snapshot of the world as it is now**.

That last point is the requirement from the master prompt and it is what the
baseline-view model gives for free: a reconnecting client has no history, so its
baseline is `Empty`, so the first snapshot is a full one.
`SessionTests.AReconnectingPlayerReceivesTheCurrentWorldNotAnOldOne` asserts the
returning player sees where the other player is *now*, and the world clock as it
is *now*, not as either was when they left.
