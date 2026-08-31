# Roadmap

> English. Русский: [ru/ROADMAP.md](ru/ROADMAP.md).

Status as of the Phase 12 commit. "Working" means implemented *and* covered by a
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

## Phase 9 — Other mods ✅ complete

| Item | State |
| --- | --- |
| Custom weapons, authoritative half | ✅ `IServerModSdk.RegisterWeapon` and a `customWeapons` list in `server.json`, so an operator needs no C# |
| Custom weapons, client half | ✅ `RegisterCustomWeapon` names a weapon locally. It grants no envelope and cannot — a client that could set its own damage ceiling would set any ceiling |
| Content negotiation for mod models | ✅ `GetModelAvailability` separates "streaming" from "not installed"; `MissingContentTracker` records each unresolvable hash once |
| Reporting it where a player looks | ✅ `/diagnostics`, `/mods` and the bug report |
| ASI, script and LSPDFR-plugin detection | ✅ since Phase 1; the manifest comparison at join reports Missing, WrongVersion and HashMismatch |
| Shipping the assets a client is missing | ⛔ **not possible** — the framework has no game files and neither does the server. It can name what is missing, not supply it |

Two bugs surfaced while building this, both user-visible and both fixed:

- **A remote player's ped was never rebuilt when their model changed.** GTA V
  cannot change a ped's model in place, and the ped is created from the first
  snapshot while the model arrives in the first state update — which lands after
  it. So every remote player wore a default body for the whole session.
  `AppearanceReplicationTests.APedIsRebuiltWhenThePlayersModelChanges`.
- **An unresolvable model was retried every frame in silence.** Sixty attempts a
  second, no log line, no diagnostic, and an entity that never appeared.

## Phase 10 — Security ✅ complete

| Item | State |
| --- | --- |
| Real authentication | ✅ ECDSA P-256 keypair per installation; the private half never leaves the machine and the server verifies a signed per-connection challenge |
| Ban list | ✅ keyed by identity public key, with reasons and expiry, persisted synchronously and checked before anything else in the handshake |
| Permissions | ✅ `Player` / `Moderator` / `Admin`, declared per command, default deny for anything unlisted |
| Admin commands over the network | ✅ `AdminCommand` / `SecurityNotice`, authorised on the server and running the same command table as the stdin console |
| Accounts, password reset, a central registry | ⛔ **out of scope** — identity is a key on a machine, not a login |
| Protection against first-contact key substitution | ⏸ needs session encryption (Phase 12) or an out-of-band way to publish keys |

Two defects surfaced while building it:

- **The handshake became four connectionless legs and needed every one of them to
  be idempotent.** A fresh challenge on a retry invalidates a proof already in
  flight; a proof arriving after the accept was lost has to be answerable with the
  stored accept, exactly as the request path already was.
- **The client corrected itself on snapshots that predated its own report.** The
  server had accepted the change; the snapshot in flight still carried the old
  value; measured against the report they are indistinguishable. The client snapped
  back to a value the server had already accepted and then reported the reverted
  one, losing the change for good. Snapshots now echo the client update sequence
  they account for, so the two cases are told apart exactly rather than guessed at.

## Phase 11 — Developer tools ✅ complete

| Item | State |
| --- | --- |
| Entity inspector, server vs live local state | ✅ `diff <id>`, field by field, with a per-field tolerance and an explicit mark for anything the game will not read back |
| Diagnostic bundle | ✅ `bundle`, writing report, diagnostics, network readout, recent log and a **redacted** config. Nothing is sent anywhere; there is no upload path in the code at all |
| Network debugger overlay | ✅ `ShowNetworkOverlay` was a setting nothing read. It now draws ping, loss, snapshot health, resyncs and missing content, coloured by the thresholds at which each becomes visible |
| Config reload | ✅ `reload config` re-reads `client.ini` and applies what can change live, naming what needs a reconnect instead of ignoring it |
| Adapter re-scan | ✅ `reload adapters` picks up an adapter added since startup |
| Replacing an adapter that is already loaded | ⛔ **not possible** — .NET Framework cannot unload an assembly without unloading its AppDomain, and that AppDomain belongs to ScriptHookVDotNet, not to this code |

**The bundle redacts the identity secret, and that is load-bearing.** Since Phase 10
`client.ini` holds a private key. A bundle is written to be shared, so copying it
verbatim would turn "here is my bug report" into "here is my character".
`DeveloperToolsTests.TheBundleNeverContainsTheIdentitySecret` asserts the secret
and the server password appear in none of the files, while the public identity —
the most useful single identifier in a report — stays.

## Phase 12 — Optimisation and encryption ✅ complete

| Item | State |
| --- | --- |
| Session encryption | ✅ signed ephemeral ECDH P-256 per connection, AES-CBC with HMAC-SHA256 encrypt-then-MAC, derived IV, 16-byte tag. Verified by watching the wire, with a control |
| Stress beyond 32 players | ✅ the convergence test now runs at 32 and 64 |
| Allocation budget on the hot path | ✅ measured rather than claimed: ~26 KB per server tick at 16 players with encryption on, guarded at 512 KB so a regression trips the test |
| Per-entity baselines | ⏸ **not done, and not pretended.** The per-client view history is the memory cost, and reworking the one path every correctness guarantee runs through — without being able to profile the real game — trades a measurable risk for an unmeasured gain |
| Database throughput | ⏸ writes are already batched into one transaction and coalesced off the tick thread; nothing measured says this is the bottleneck |

**On "allocation-free".** The phrase was in the plan and is not in the result,
because it is the kind of claim that is easy to make and impossible to check
later. What replaced it is a number: `StressTests.TheSnapshotPathStaysWithinItsAllocationBudget`
measures server-thread allocation over 200 ticks with 16 players connected and
fails if it grows by an order of magnitude. A budget that fails loudly is worth
more than an adjective.

**On the stress figures.** The suite runs on virtual time, so 64 players
converging proves the protocol and the world state are *correct* at that size. It
says nothing about whether a real server keeps up at 64 — that needs a real
machine and real clients, and is not claimed here.

---

## Continuous integration ✅ in place

`.github/workflows/ci.yml` runs on every push and every pull request:

| Step | What it guards |
| --- | --- |
| `dotnet build -c Release -warnaserror` | The zero-warning claim. Without `-warnaserror` it decays the first time a warning lands that nobody scrolls up far enough to see |
| `dotnet test -c Release` | All 360 tests, with the `.trx` uploaded as an artifact so a failure is readable without re-running anything |
| `python3 tools/check-docs.py` | Dead relative links, `#anchors` naming headings that no longer exist, and any document that lost its counterpart in the other language |

The whole solution compiles on `ubuntu-latest`, the `net48` client included,
because `Microsoft.NETFramework.ReferenceAssemblies` supplies the .NET Framework
4.8 reference assemblies. No Windows runner is needed to *build*.

**What CI still does not cover, and cannot.** Running the client, RPH's loader
and `GameFiber` timing, and whether each LSPDFR probe binds against a given
LSPDFR release. Those need Windows, GTA V, RPH and LSPDFR on the runner. A green
CI badge on this repository means the code compiles clean and the suite passes —
not that the mod works in the game.

**CI found a real defect on its first run**, which is the argument for having it.
The build failed on `ubuntu-latest` with six errors in `EntityInspector.cs` that
had never appeared locally: `error CS9273: In language version 14.0, 'field' is a
keyword within a property accessor`. Nothing was wrong with the machine — the
toolchain was floating in two places at once. `Directory.Build.props` said
`<LangVersion>latest</LangVersion>`, and with no `global.json` the SDK resolver
picks the *highest* one installed. The runner carries the .NET 10 SDK, so `latest`
meant C# 14 there and C# 12 here, and in C# 14 `field` became a contextual keyword
inside a property accessor — where two `foreach` loops happened to use it as an
ordinary variable name.

Fixed in three places, because one would have left the trap set:

| Fix | Why not just this one |
| --- | --- |
| The two loop variables renamed to `entry` | Correct, but the next identifier to collide with a new keyword would break the build again |
| `<LangVersion>12.0</LangVersion>`, pinned | Stops the language drifting, but leaves the SDK itself unpinned |
| `global.json` pinning the 8.0 band, `rollForward: latestFeature` | Fails immediately and legibly on a machine without an 8.0.x SDK, rather than building against a newer compiler and then aborting at `dotnet test` — the tests target `net8.0` and need the runtime the 8.0.x SDK carries |

The failure was **reproduced before it was fixed**: the .NET 10 SDK was installed
locally, it produced the same six errors byte for byte, and the fix was then
verified against both SDKs — build and 360 tests clean on 8.0.424, build clean on
10.0.400.

This is the sixth defect the project has found in itself, and the only one no
amount of local testing would have surfaced: the bug was in the assumption that
*my* SDK is *the* SDK.

**The documentation checker is itself checked.** It was run against a
deliberately broken tree — a link to a missing file, an anchor naming a heading
that does not exist, and a Russian document removed — and reported all three and
exited non-zero. A checker that only ever passes is indistinguishable from one
that checks nothing.

---

## Deliberately not done

Things that could have been faked and were not:

- **Visual verification of ped locomotion.** The controller is tested; how it
  looks in the game is not, and cannot be from here.
- **`RegisterRPC`, `RegisterMission`, `RegisterCustomWeapon`.** Until they were
  built they threw with a phase number rather than no-op'ing. A registration that
  appears to work and never fires is worse than a loud failure. All fifteen names
  from master prompt section 21 are implemented as of Phase 9.
- **Client-declared weapon envelopes.** `RegisterCustomWeapon` names a weapon and
  stops there. Letting the client supply the damage ceiling its own hits are
  checked against would have looked like a richer API and been worth nothing.
- **A generic "replicate any RPH plugin" feature.** RPH exposes no way to
  enumerate or drive another plugin's objects, so there is nothing to read
  generically. The bridge publishes the plugin list and LSPDFR's documented API;
  anything else a plugin must send itself.
- **Shared LSPDFR callout logic.** `API.Functions` reports whether a callout is
  running, not the decisions inside it. The observable facts cross the wire; the
  simulation does not, and the documentation says so instead of implying more.
- **Running the RPH bridge in a real game.** It compiles and its channel half is
  tested, but RPH's loader and `GameFiber` timing need Windows, GTA V and RPH.
  Marked unverified rather than reported as working.
- **Vehicle deformation.** Not sampled, because the engine will not give it up —
  stated rather than approximated with something that would look right and be
  wrong.
- **Line-of-sight checking on hits.** The server has no map, so a hit claimed
  through a wall within range is accepted. Detecting it would need geometry the
  server does not have.
