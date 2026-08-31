# Roadmap

Status as of the Phase 1 commit. "Working" means implemented *and* covered by a
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

## Phase 4 — Networking depth

| Item | Notes |
| --- | --- |
| Client-side prediction for the local player | Today the local player is not predicted; it is simulated locally and corrected |
| Vehicle prediction and reconciliation | Non-owned vehicles interpolate; they do not predict |
| Ownership handover of an existing local entity | A client granted ownership of somebody else's vehicle has no local handle for it yet |
| Animation and scenario replication | `AnimationHash` is on the wire and unused |
| Per-entity baselines | Replaces the per-client view history; needed before entity counts reach the thousands |
| Packet fragmentation | Today a single message must fit in one datagram |
| Bandwidth shaping per client | Today the budget is global |

## Phase 5 — Persistence depth

| Item | Notes |
| --- | --- |
| Background save writer | Saves are on the tick thread today |
| Schema migrations | The hash check catches incompatibility but nothing migrates |
| PostgreSQL driver | The SQL is already portable |
| Inventory and money | Tables exist and are empty |

## Phase 6 — Mod SDK depth

| Item | Notes |
| --- | --- |
| `RegisterRPC` | Throws today, naming this phase |
| `RegisterMission` | Needs the network activity system |
| Universal activity system | Missions, objectives, checkpoints, timers, rewards — not LSPDFR-specific |
| Server-side mod adapters | Adapters are client-side only today |

## Phase 7 — RAGE Plugin Hook

Blocked on writing `Gtamp.RphBridge.dll`, the RPH-loaded half of the in-process
channel. See [RPH_INTEGRATION.md](RPH_INTEGRATION.md) for why an SHVDN-side
adapter cannot reach RPH state directly.

## Phase 8 — LSPDFR

Callouts, pursuits, suspects, arrests, police units. Depends on Phase 7's channel.
See [LSPDFR_INTEGRATION.md](LSPDFR_INTEGRATION.md) for the scope that
`API.Functions` actually permits.

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
