# GTA V engine analysis — what is actually possible

> English. Русский: [ru/ENGINE_ANALYSIS.md](ru/ENGINE_ANALYSIS.md).

This is the analysis the master prompt asks for before any code: what GTA V lets a
multiplayer framework do, where the hard limits are, and which of them are
avoidable. Everything else in the design follows from this document.

Nothing here is aspirational. Where a limit exists it is named, located at the
layer it occurs, and paired with the workaround that was chosen and the cost that
workaround carries.

---

## 1. Integration points

GTA V exposes no official modding or server API. Everything runs through
community hooks:

| Layer | What it gives you | Language | Required? |
| --- | --- | --- | --- |
| **ScriptHookV** (Alexander Blade) | Calls into the game's ~5,000 native functions; loads `.asi` plugins | C++ | **Yes** — everything else sits on it |
| **ScriptHookVDotNet 3** | Managed scripts with a tick loop, key events and a wrapper over the natives | C# (.NET Framework 4.8) | **Yes** for this client |
| **RAGE Plugin Hook** | An alternative host that launches the game itself and loads its own plugins on `GameFiber`s | C# (.NET Framework) | No |
| **LSPDFR** | A law-enforcement gamemode built as an RPH plugin | C# | No |

The framework targets ScriptHookVDotNet as its host. That choice is what makes RPH
optional: SHVDN loads whether or not RPH launched the game.

**Limit — .NET Framework 4.8, not .NET 8.** ScriptHookVDotNet 3 loads assemblies
into the game's CLR, which is .NET Framework. The client therefore cannot use
`System.Text.Json`, `Span<T>`-based APIs or anything else .NET-Core-only. This is
why the shared code targets `netstandard2.0` and the client config is INI rather
than JSON.

## 2. There is no server build of GTA V

This is the single most important fact in the design.

Rockstar ships no dedicated server binary. Every GTA V multiplayer project —
FiveM, alt:V, RAGE MP, and this one — runs a *coordinator* process that holds
state and arbitrates, not a second copy of the game. The coordinator cannot:

- run RAGE's physics solver;
- raycast against the map, or know where the ground is at a coordinate;
- run ped AI, pathfinding or the task system;
- resolve a vehicle collision;
- stream or query an interior.

**Consequence.** "Server authority" cannot mean "the server simulates the world
and clients render it". It means:

> The server is authoritative over **state, identity, lifecycle and validation**.
> The client is authoritative over **its own local simulation**, which the server
> then accepts or rejects.

The rejection path is what makes this authority real rather than nominal:
a rejected update is discarded, the server's own state stands, and the next
snapshot carries that state back to the client, which snaps to it
(`MultiplayerClient.ApplyServerCorrection`).

This split is applied consistently:

| Server-authoritative outright | Client-simulated, server-validated |
| --- | --- |
| Player identity and session | Movement and position |
| Entity ids, ownership and lifecycle | Vehicle physics |
| Health, armour and damage arbitration | Ragdoll and collisions |
| Inventory and ammo counts | Animation blending |
| World clock and weather | Local camera and controls |
| Mission and world flags | |
| Persistence | |

## 3. Why not use GTA V's own netcode

GTA V has a complete network layer: `NETWORK_*` natives, network object ids,
network scenes. It is unusable here.

- It is bound to Rockstar's session and matchmaking services. A session cannot be
  created without them.
- Its object ownership and migration model is opaque and undocumented, and it
  disagrees with any external authority you try to impose.
- Using it against Rockstar's services in single player risks the player's
  account.

Every serious project reaches the same conclusion. The alternative is what this
framework does: **replicate over our own protocol and represent other players as
ordinary local peds.** Each client spawns a normal, non-networked ped for every
other player and drives it from replicated state. The game never knows it is in a
multiplayer session.

**Cost.** Everything the native netcode would have given for free — ped
locomotion blending, vehicle interpolation, damage propagation, sound
attribution — has to be rebuilt. That is the bulk of Phases 2 to 4.

## 4. Named limitations

### 4.1 Remote player locomotion — *implemented in Phase 2, not visually verified*

- **What is limited:** a ped driven purely by coordinates is positionally correct
  but plays an idle animation while it moves. It slides.
- **Why:** GTA V drives locomotion from the ped's *task system*, not from its
  coordinates. Writing `SET_ENTITY_COORDS_NO_OFFSET` every frame moves the ped
  without telling the animation system anything.
- **Layer:** the game engine's animation/task system, reached through
  ScriptHookVDotNet.
- **Options:** (a) write coordinates and accept sliding; (b) drive
  `TASK_GO_STRAIGHT_TO_COORD` and let the game animate, at the cost of the ped
  lagging behind and taking its own route; (c) hybrid — task-driven locomotion
  with coordinate correction when error exceeds a threshold, plus explicit
  animation state (crouch, sprint, ragdoll, swim) from the replicated
  `PlayerFlags` and `MovementState`.
- **Chosen:** (c). `RemotePedController` picks the gait from `MovementState`,
  refined by the replicated velocity so a stale flag does not leave a ped
  sprinting on the spot or standing still while it drifts. The bridge issues
  `TASK_GO_STRAIGHT_TO_COORD` at the matching move-blend ratio, re-issuing only
  when the destination moves more than 0.75 m — re-tasking every frame restarts
  the animation and produces a ped that jitters in place. Beyond 8 m of error the
  ped is placed outright, because walking that off takes seconds of visibly
  running through scenery.
- **Cost:** two costs, both real. Tasking gives up exact positional agreement
  between the correction points, so a remote ped is approximately, not exactly,
  where the server says. And the thresholds above are reasoned, not measured:
  the decision logic is unit-tested but nobody has watched the result in Los
  Santos, so expect them to need tuning.

### 4.2 Vehicle physics — *Phase 3*

- **What is limited:** two clients will not agree exactly on where a vehicle is.
- **Why:** RAGE's vehicle physics is deterministic only given identical inputs,
  timestep and collision state. Across two machines at different frame rates, with
  different mods loaded, none of those hold.
- **Layer:** the physics solver, which is not reachable or replaceable.
- **Options:** full server simulation (impossible, §2); lockstep (unusable at
  internet latency); **ownership + correction** — one client owns a vehicle's
  simulation, the server validates and replicates, other clients interpolate.
- **Chosen:** ownership + correction, which is what `NetEntity.OwnerId` exists
  for.
- **Cost:** the owner's view is authoritative-feeling and everyone else sees a
  slightly stale vehicle. Collisions between two owned vehicles are resolved
  differently on each machine and need a tie-break rule.

### 4.3 Interiors, MLO and IPL — *Phase 3*

- **What is limited:** the server cannot verify that a client actually has an
  interior loaded, nor stream one to it.
- **Why:** IPL and MLO content is client-side asset data. A client without the
  map mod simply has nothing there.
- **Chosen:** replicate the *interior id* and *dimension* on every entity (both
  fields exist on `NetEntity` today) and let each client resolve them locally.
  Mismatches are reported through the mod-compatibility report rather than
  papered over.
- **Cost:** two players can stand in "the same" custom interior and see different
  geometry. The framework can detect the mismatch and warn; it cannot fix it
  without server-side mod distribution, which is explicitly out of scope for v1.

### 4.4 Mod content — *implemented in Phase 9*

- **What is limited:** a mod-added vehicle, ped or weapon that one client has and
  another does not.
- **Why:** models are resolved by hash against locally installed assets. There is
  no way to stream an asset the player does not have, and no way for the server to
  supply one — it has no game files either.
- **Chosen:** hashes travel on the wire, never indices, and a hash the client
  cannot resolve is **reported rather than papered over**.
  `IGameBridge.GetModelAvailability` separates "still streaming" from "not
  installed", because collapsing them is what turns a missing mod into an entity
  that is retried sixty times a second and never appears.
  `MissingContentTracker` records each unresolvable hash once, counts the entities
  that wanted it, and surfaces it through `/diagnostics`, `/mods` and the bug
  report.
- **Substitution policy, and why it differs by type:** a vehicle or object is not
  substituted. Showing a different car than the one somebody is driving corrupts
  every judgement a viewer makes about it, and unlike an absence it looks correct.
  A player *is* substituted with a default body, because an invisible teammate is
  worse than one wearing the wrong clothes — and the record says `substituted` so
  the fallback is not mistaken for success.
- **Cost:** a missing mod produces a visible, explained gap for that one client,
  not a desynchronised world for everyone. What it does not do is fix anything: the
  player still has to install the mod.

### 4.5 Anti-cheat — *Phase 10*

- **What is limited:** the server cannot detect memory editing, injected DLLs, or
  a modified client.
- **Why:** it has no visibility into the client process, and any client-side
  check runs on hardware the attacker controls.
- **Chosen:** validate *effects*, not *causes*. Impossible movement, impossible
  health, impossible packet rates and out-of-bounds positions are all detectable
  server-side regardless of how they were produced.
- **Cost:** a cheat that stays within plausible bounds is undetectable. This is
  true of every game and is stated rather than implied.

### 4.6 Ped and traffic AI — *Phase 3*

- **What is limited:** ambient traffic and pedestrians cannot be made identical
  across clients.
- **Why:** they are spawned by the game's own population system from a seed the
  framework does not control.
- **Chosen:** suppress ambient population in the shared world and replicate only
  peds the framework or a mod explicitly creates. That is what "the server knows
  about every ped" can actually mean.
- **Cost:** an emptier world unless a mod repopulates it deliberately.

## 5. What this makes possible

With those limits accepted, the following are fully achievable and are what the
roadmap builds:

- A shared world where every player, vehicle, ped, object and mission entity has
  a server-assigned identity that survives disconnects and server restarts.
- Full server-side world state, with distance affecting only replication
  priority — never whether the server keeps an entity.
- Deterministic reconnect and resync: a returning player gets the world as it is
  now, not as they left it.
- An entity system a mod can extend without touching the networking layer.
- Optional integrations that stay genuinely optional, because nothing links
  against them.

## 6. Ownership model

Every entity has an `OwnerId`: the player whose client simulates it, or 0 for the
server.

- Ownership grants the right to *propose* state, not to *decide* it.
- The server validates every proposal and may reject it.
- Ownership migrates when the owner disconnects or moves too far away for their
  simulation to be meaningful (Phase 3).
- Ownership is not visibility: every client receives every entity, at a rate set
  by priority.

## 7. Reading order

1. This document — what is possible.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — how the pieces fit.
3. [NETWORK_PROTOCOL.md](NETWORK_PROTOCOL.md) — the wire format.
4. [WORLD_STATE.md](WORLD_STATE.md) and [ENTITY_SYSTEM.md](ENTITY_SYSTEM.md).
5. [ROADMAP.md](ROADMAP.md) — what is built and what is next.
