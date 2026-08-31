# Roadmap

Status as of the Phase 8 commit. "Working" means implemented *and* covered by a
test that would fail if it broke.

---

## Phase 1 — Core ✅ complete

| Item | State |
| --- | --- |
| GTA V integration via ScriptHookVDotNet 3 | ✅ builds against the real SHVDN API |
| Client/server bootstrap | ✅ |
| UDP transport with reliable and unreliable delivery | ✅ tested under 40% loss, latency, jitter and reordering |
| Handshake, session ids, player ids | ✅ idempotent under accept loss |
| Full server world state | ✅ no distance-based removal anywhere |
| Snapshot replication with delta compression | ✅ baseline-view model |
| Reconnect and resync | ✅ returning player gets the world as it is now |
| SQLite persistence | ✅ survives a real process restart |
| Anti-cheat with five levels | ✅ |
| F8 developer console | ✅ filters, search, copy, bug reports, diagnostics |
| Mod SDK and adapter model | ✅ a mod-defined entity replicates end to end |
| Build and run scripts | ✅ |
| Documentation | ✅ |

**Acceptance path from master prompt section 57, all green:** server starts →
A connects → B connects → A sees B → B sees A → movement synchronised → A
disconnects → B keeps playing → A reconnects → A receives the *current* world.

---

## Phase 2 — Players ✅ complete

| Item | State |
| --- | --- |
| Task-driven remote locomotion | ✅ `RemotePedController` decides gait and when to correct; the bridge tasks the ped. **Not visually verified** — see the caveat below |
| Ragdoll replication | ✅ handed to physics, not corrected while ragdolling |
| Clothing components and props | ✅ 12 components, 8 prop slots, mask-encoded (3 bytes when default) |
| Aim pose | ✅ real aim target from the gameplay camera, applied with `TASK_AIM_GUN_AT_COORD` |
| Death and respawn | ✅ server-arbitrated; nearest-hospital respawn; a dead client cannot heal itself |
| Interior tracking | ✅ read from `GET_INTERIOR_FROM_ENTITY` and replicated |
| Server-initiated moves | ✅ authority hold, so a teleport or respawn is not dragged back by in-flight client updates |
| Scenario and animation tasks | ⏸ `AnimationHash` is replicated and still unused by the bridge — moved to Phase 9 with the wider animation work |

**Caveat that matters.** The locomotion *decision* is unit-tested — 13 tests over
gait selection, correction thresholds, stale flags, ragdoll and death precedence.
Whether the resulting ped *looks* right in Los Santos cannot be tested here, and
has not been. Expect the correction distance and re-task threshold to need tuning
against the real game.

## Phase 3 — World entities ✅ complete

| Item | State |
| --- | --- |
| `VehicleEntity` | ✅ 27 replicated fields: physics, drivetrain, health, doors, windows, tyres, paint, livery, plate, extras, neon, mods, occupants, trailer |
| `PedEntity` | ✅ networked NPCs sharing the character field set with players |
| `ObjectEntity` | ✅ props, including attachment to another entity |
| Client-created entities | ✅ spawn request, server-assigned ids, correlated replies |
| Owned-entity streaming | ✅ the owner reports through the entity's own serializer, so a mod type streams with no protocol change |
| Ownership and migration | ✅ on disconnect, on distance, and back to the server when nobody is near |
| Weapons and combat | ✅ server-arbitrated damage with per-weapon range and damage envelopes |
| Physics correction | ⏸ vehicles interpolate; prediction and reconciliation are Phase 4 |

**Not replicated, and why.** Body deformation and per-wheel suspension. GTA V
exposes deformation only through natives that write into a vehicle, never read
from one, so a faithful copy cannot be sampled at all. Body health plus the door,
window and tyre states carry as much of it as the engine will give up.

## Phase 4 — Networking depth ✅ complete

| Item | State |
| --- | --- |
| Packet fragmentation | ✅ reliable messages up to 256 KB split and reassembled; unreliable fragmentation is refused on purpose |
| Delta-compressed owner streams | ✅ owners delta against a snapshot the server still holds, so a lost update costs freshness rather than the chain |
| Adaptive bandwidth shaping | ✅ per client, additive increase and multiplicative decrease from measured loss |
| Client-side prediction for the local player | ⏸ **not applicable as usually meant** — see below |
| Vehicle prediction and reconciliation | ⏸ non-owned vehicles interpolate and extrapolate up to 250 ms; they do not predict |
| Ownership handover of an existing local entity | ⏸ a client granted ownership of somebody else's vehicle has no local handle for it |
| Per-entity baselines | ⏸ moved to Phase 12; the per-client view history is the memory cost to beat |

**Why local-player prediction is not the usual thing here.** In a conventional
architecture the server simulates movement and the client predicts what the server
will say. Here the server cannot simulate movement at all — it has no physics and
no map. GTA V *is* the simulation, running on the client. So there is nothing to
predict: the client already has the authoritative-feeling result immediately, and
the server's role is to accept or correct it. Building a "prediction" layer on top
would be a re-implementation of movement that could only ever be less accurate
than the game already running underneath it.

## Phase 5 — Persistence depth ✅ complete

| Item | State |
| --- | --- |
| Entity persistence | ✅ every non-player entity saved as its own serializer's blob and restored by id |
| Background save writer | ✅ writes off the tick thread, coalesced so a slow disk costs freshness not memory |
| Schema migrations | ✅ versioned in the database, refuses to open one written by a newer build |
| Money | ✅ persisted per identity |
| Item inventory | ⏸ a gameplay system, not a framework one. `CustomData` already carries it per entity and persists with it |
| PostgreSQL driver | ⏸ the SQL is already portable; only the driver is missing |

## Phase 6 — Mod SDK depth ✅ complete

| Item | State |
| --- | --- |
| Universal activity system | ✅ activities are entities, so they replicate, persist and appear in the inspector with no mission-specific networking |
| `RegisterMission` | ✅ registers the local half — blips, markers, UI — driven from replicated state |
| `RegisterRPC` | ✅ both directions, with timeouts, handler errors and failure on disconnect |
| Server-side mod SDK | ✅ entity types, activities, RPC handlers and events, without writing against `GameServer` internals |
| Mod events routed by name | ✅ replaced id-by-registration-order, which only worked while both sides registered identically |
| `RegisterCustomWeapon` | ⏸ Phase 9, with the wider weapon work |

## Phase 7 — RAGE Plugin Hook ✅ complete

| Item | State |
| --- | --- |
| In-process channel between the two plugin hosts | ✅ bytes under a topic name, bounded and non-blocking, so neither scheduler can block the other |
| `Gtamp.RphBridge.dll`, the RPH-loaded half | ✅ RPH plugin on a `GameFiber`, 50 ms poll, answers `describe`, reports `state=stopped` on shutdown |
| Fan-out to more than one adapter | ✅ `BridgeLink` routes by topic; two adapters on one channel no longer steal each other's messages |
| Adapter free of RPH types | ✅ `Gtamp.Adapters.Rph` names no `Rage` type at all |
| Live RPH plugin list in the mod manifest | ✅ read from each assembly's `PluginAttribute`, a public surface rather than an RPH internal |
| Missing-bridge reporting | ✅ named at warning level after 10 s, with both causes |
| Replicating an arbitrary RPH plugin's internal state | ⛔ **not possible** — RPH exposes no way to enumerate or drive another plugin's objects. A plugin sends its own state over `rph.event` |

Not verified on real RPH: no Windows, no GTA V, no RPH on the build machine. See
[RPH_INTEGRATION.md](RPH_INTEGRATION.md) for exactly what was and was not tested.

## Phase 8 — LSPDFR ✅ complete

| Item | State |
| --- | --- |
| Live LSPDFR state of the local player | ✅ `LspdfrObserver` on the bridge, six probes on the documented `API.Functions` surface |
| Unbound probes reported rather than assumed | ✅ counted and surfaced through `/diagnostics` |
| That state replicated to the other players | ✅ forwarded as `lspdfr.event`, relayed by the server, attributed per player |
| Change suppression | ✅ only what actually changed is sent, so a steady pursuit costs no packets |
| Operator switch | ✅ `relayedModEvents` in `server.json`; emptying it stops clients passing each other opaque bytes |
| Callout entities visible to other players | ✅ already, through the ordinary entity system — a callout's peds and vehicles are peds and vehicles |
| Sharing callout scripts, suspect AI, pursuit behaviour | ⛔ **not possible** — `API.Functions` exposes whether a callout runs, not the decisions inside it, and nothing can drive another player's LSPDFR into a callout state |

The two ⛔ rows are limits of what RPH and LSPDFR expose, not deferred work. They
are described in the integration documents rather than promised for a later
phase.

## Phase 9 — Other mods

ScriptHookV/ASI observation, .NET script adapters, custom vehicles, peds, weapons,
maps, MLO and DLC content negotiation.

## Phase 10 — Security

Real authentication, ban lists, permissions and admin commands over the network.
The identity token is continuity, not identity — see [SECURITY.md](SECURITY.md).

## Phase 11 — Developer tools

Entity inspector comparing server state against live local state, crash-report
bundles, richer network debugger overlay, module hot-reload.

## Phase 12 — Optimisation

Per-entity baselines, allocation-free hot paths, session encryption, database
throughput, and stress testing beyond 32 players.

---

## Deliberately not done

Things that could have been faked and were not:

- **Visual verification of ped locomotion.** The controller is tested; how it
  looks in the game is not, and cannot be from here.
- **`RegisterRPC`, `RegisterMission`, `RegisterCustomWeapon`.** These throw with a
  phase number rather than no-op'ing. A registration that appears to work and
  never fires is worse than a loud failure.
- **RPH and LSPDFR state replication.** The adapters detect, report and register
  their integration points, and log at warning level that replication is not
  implemented.
- **Vehicle deformation.** Not sampled, because the engine will not give it up —
  stated rather than approximated with something that would look right and be
  wrong.
- **Line-of-sight checking on hits.** The server has no map, so a hit claimed
  through a wall within range is accepted. Detecting it would need geometry the
  server does not have.
