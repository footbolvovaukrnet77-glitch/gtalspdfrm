# Architecture

## The shape of the system

```
        GTA V process (Windows)                    Server process (any OS)
  ┌────────────────────────────────┐         ┌──────────────────────────────┐
  │ ScriptHookV → ScriptHookVDotNet│         │ Gtamp.Server (net8.0)        │
  │   ┌──────────────────────────┐ │         │  ┌────────────────────────┐  │
  │   │ Gtamp.Client.Shv  net48  │ │         │  │ GameServer tick loop   │  │
  │   │  IGameBridge impl        │ │         │  │  handshake             │  │
  │   │  console renderer        │ │         │  │  validation            │  │
  │   └───────────┬──────────────┘ │         │  │  world state (FULL)    │  │
  │               │                │         │  │  replication           │  │
  │   ┌───────────▼──────────────┐ │  UDP    │  │  persistence           │  │
  │   │ Gtamp.Client.Core        │◄├─────────┤► │  anti-cheat            │  │
  │   │  connection, replicated  │ │         │  └────────────────────────┘  │
  │   │  world, interpolation,   │ │         └──────────────┬───────────────┘
  │   │  console model, Mod SDK  │ │                        │
  │   └───────────┬──────────────┘ │                 ┌──────▼──────┐
  │               │                │                 │ SQLite      │
  │   ┌───────────▼──────────────┐ │                 └─────────────┘
  │   │ Gtamp.Adapters.*  net48  │ │
  │   │  RPH, LSPDFR (reflection)│ │
  │   └──────────────────────────┘ │
  └────────────────────────────────┘
                  ▲
                  │ both sides
        ┌─────────┴──────────┐
        │ Gtamp.Shared       │  netstandard2.0
        │  protocol, entities│
        │  snapshots, world  │
        └────────────────────┘
```

## Projects

| Project | Target | Why it exists |
| --- | --- | --- |
| `Gtamp.Shared` | `netstandard2.0` | Everything both sides must agree on: wire format, entity model, snapshot codec, world state, anti-cheat rules, logging. Targets netstandard so the .NET Framework client and the .NET 8 server load the *same* assembly and cannot drift. |
| `Gtamp.Server` | `net8.0` | The authoritative coordinator. Cross-platform, no game dependency. |
| `Gtamp.Client.Core` | `netstandard2.0` | All client logic that does not touch GTA V. Testable with no game installed. |
| `Gtamp.Client.Shv` | `net48` | The ScriptHookVDotNet host. Deliberately thin — a bridge implementation, a console renderer and a tick pump. |
| `Gtamp.Adapters.Rph` | `net48` | Optional RAGE Plugin Hook integration, loaded only when RPH is present. |
| `Gtamp.Adapters.Lspdfr` | `net48` | Optional LSPDFR integration, same rule. |
| `Gtamp.Tests` | `net8.0` | xUnit. Covers everything except the ~400 lines that touch GTA V natives. |

## The three seams

The design rests on three deliberate boundaries. Each one exists to keep a
category of problem out of the rest of the system.

### 1. `IGameBridge` — the engine seam

Everything GTA V-specific is behind one interface with fourteen members. Above it
nothing knows what a `Ped` is.

This is what makes the client testable: `FakeGameBridge` in the test project
implements it in 60 lines, and the full session tests — connect, replicate, move,
disconnect, reconnect — run with no game process anywhere.

It also means a second host (ScriptHookVDotNetCore, or an RPH-hosted build) is a
new bridge implementation, not a fork.

### 2. `IDatagramTransport` — the network seam

`UdpDatagramTransport` for real use; `LoopbackNetwork` for tests, with
configurable latency, jitter, loss and reordering, driven by virtual time.

Every networking test — reliable delivery under 40% loss, snapshot convergence
under a byte budget, the lost-accept handshake retry — runs deterministically in
milliseconds because of this seam. None of them sleep.

### 3. `INetEntitySerializer` — the extensibility seam

The replication layer only ever talks to this interface. A mod-registered entity
type is indistinguishable from `PlayerEntity` to every layer above it, which is
what makes "add an entity type without rewriting the networking layer" a fact
rather than a claim. `ModSdkTests.AModDefinedEntityReplicatesThroughTheOrdinarySnapshotPath`
is the proof.

## Data flow

### Client → server (input)

```
Game frame
  → IGameBridge.SampleLocalPlayer()
  → ClientStateUpdateMessage          (unreliable, 30 Hz)
  → NetPeer                           packing, sequencing
  → UDP
  → GameServer.HandleClientStateUpdate
  → AntiCheatEngine.ValidatePlayerState
  → accepted ? write to PlayerEntity : discard (server state stands)
```

### Server → client (state)

```
Snapshot tick (20 Hz), per client
  → ReplicationPriority.Order(...)    distance and staleness scoring
  → SnapshotCodec.Write(world, baseline, budget)
  → NetPeer                           unreliable
  → UDP
  → ReplicatedWorld.TryApply          decoded against the named baseline view
  → RemotePlayerManager.Sync          into interpolation buffers
  → ApplyServerCorrection             local player snaps if it has drifted
  → RemotePlayerManager.Render        every frame, at serverTime − delay
```

### Reliable events

Chat, entity lifecycle events, server announcements and mod events go through the
same `NetPeer` on the reliable-ordered channel: retransmitted until acknowledged,
delivered in send order, exactly once.

## Threading

The server runs a single tick thread plus a stdin reader that only enqueues
strings. The client runs entirely on the ScriptHookVDotNet script thread. There is
no shared mutable state across threads and no locking on the hot path.

`LogBus` is the exception: it is lock-guarded because sinks are added from one
thread and written from another, and a clipboard write is dispatched to a
short-lived STA thread.

This is a deliberate choice. Concurrency bugs in a replication layer are
extraordinarily hard to reproduce, and a 60 Hz tick over a few dozen players does
not need parallelism.

## Failure policy

Three rules, applied everywhere:

1. **A malformed packet is never fatal.** Every decode path throws
   `NetSerializationException`, which is caught at the message boundary, logged
   under `NETWORK`/`SECURITY`, and the packet is dropped. The tick loop continues.
2. **A failing subsystem is disabled, not escalated.** An adapter that throws
   during update is removed and logged; the client keeps running. A log sink that
   throws is ignored. A failed persistence write is logged; the world stays up.
3. **An unrecoverable decode is a resync, not a disconnect.** If a client cannot
   decode a delta it asks for a full snapshot and the server sends one.

## Where the invariants live

| Invariant | Enforced in |
| --- | --- |
| The server never removes an entity because of distance | `ServerWorld` is the only place entities are added or removed, and it has no distance-aware removal at all |
| Distance affects priority only | `ReplicationPriority.Score` — the single place distance is read |
| A delta is decoded against the state it was written for | `SnapshotCodec.Apply` rejects a baseline mismatch instead of applying |
| Entity ids are never reused | `ServerWorld.AllocateEntityId` + `ReserveEntityIdsUpTo` after a restore |
| The client is never the source of truth | `GameServer.HandleClientStateUpdate` discards rejected updates |
| Mod types cannot collide with built-ins | `ModSdk.RegisterEntity` enforces the reserved id range |
