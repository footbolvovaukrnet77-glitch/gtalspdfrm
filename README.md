# GTAMP — GTA V Universal Multiplayer Framework

> English. Русский: [README.ru.md](README.ru.md).

[![CI](https://github.com/footbolvovaukrnet77-glitch/gtalspdfrm/actions/workflows/ci.yml/badge.svg)](https://github.com/footbolvovaukrnet77-glitch/gtalspdfrm/actions/workflows/ci.yml)

A server-authoritative multiplayer framework that turns single-player GTA V into a
shared world, with an entity system and mod SDK that third-party mods can extend
without patching the framework.

**Status: phases 1 to 12 complete.** Two players share one world, movement replicates,
and a player who disconnects and comes back receives the world as it is *now* —
including their own character, across a server restart.

```
                 ┌────────────────────────────────────────────┐
  GTA V + SHVDN  │  Client A  ──┐                             │
                 └──────────────┼──────────────┐              │
                                │              ▼              │
                                │      ┌───────────────┐      │
                                └─────►│  GTAMP Server │◄─────┘
  GTA V + SHVDN  ┌──────────────┐      │  full world   │
                 │  Client B  ──┼─────►│  state        │
                 └──────────────┘      └───────┬───────┘
                                               ▼
                                          SQLite
```

## What works today

- **Server-authoritative world state.** The server keeps every entity, always.
  Distance changes how *often* an entity is sent to a client, never whether the
  server knows about it. There is no distance-based removal path anywhere in the
  server.
- **Custom UDP protocol** with reliable and unreliable delivery, packet-level
  acknowledgement, retransmission, ordered delivery and RTT estimation — verified
  under 40% packet loss with latency, jitter and reordering.
- **Delta-compressed snapshots** encoded against per-client baseline views, so a
  delta is always decoded against exactly the state it was written for.
- **Reconnect and resync.** A returning player gets a full snapshot of the current
  world plus their persisted character.
- **Animated remote players.** Peds are driven through GTA V's task system at the
  right gait, with ragdolling limbs pulled toward where they are on their owner's
  machine and corpses left where they fell.
- **Clothing and props** replicated in three bytes when default.
- **Vehicles, NPCs and objects** with server-assigned identities, client-driven
  simulation and ownership that migrates when the owner leaves or drives away.
- **Server-arbitrated combat** with per-weapon range and damage envelopes.
- **Fragmentation** for reliable messages up to 256 KB, and **adaptive bandwidth
  shaping** that backs off on loss and creeps back on a clean link.
- **Server-arbitrated death and respawn**, with an authority hold so a
  server-initiated move is not dragged back by the client's in-flight updates.
- **SQLite persistence** that survives a real process restart, including
  vehicles and objects, written off the tick thread and versioned by migration.
- **Optional anti-cheat**, five levels, with protocol guards that stay on even at
  `Off`.
- **F8 developer console** with colour roles, filters, search, copy-error, bug
  reports and diagnostics.
- **An entity inspector that shows both sides.** `diff <id>` puts the server's
  state for an entity next to what the game actually has, field by field, and
  marks anything GTA V will not read back rather than leaving it blank. Plus an
  on-screen network readout and a `bundle` command that writes a shareable
  diagnostic folder — with the identity secret redacted, and nothing sent
  anywhere.
- **Mod SDK** — a mod-defined entity type replicates through the ordinary snapshot
  path with no change to the networking layer, plus RPC in both directions and a
  universal activity system for missions, callouts and jobs.
- **RAGE Plugin Hook and LSPDFR are genuinely optional.** Neither the client nor
  the adapters link against them; the adapters report themselves inactive when
  the mod is absent.
- **A bridge into RAGE Plugin Hook.** RPH and ScriptHookVDotNet are two hosts in
  one process with no supported path between them, so `Gtamp.RphBridge.dll` runs
  under RPH and talks to the client over an in-process channel — bytes under a
  topic name, bounded and non-blocking, so neither scheduler can block the other.
  It publishes RPH's live plugin list into the mod manifest.
- **LSPDFR state shared between players.** Availability for calls, the running
  callout **with its name and acceptance state**, a traffic stop, and an active
  pursuit with whether it has been called in and is still running — read from
  LSPDFR's `API.Functions` surface on the bridge and relayed to the other players by
  a server that has never heard of LSPDFR. Every probe name is checked against the
  assembly's public metadata, not just the shipped XML documentation: the XML lists
  only members with doc comments, and trusting it alone had already produced one
  wrong answer. Callout *logic* is not shared and cannot be; see
  [docs/LSPDFR_INTEGRATION.md](docs/LSPDFR_INTEGRATION.md).

- **Mod content negotiation.** Models travel as hashes, so a player without your
  vehicle mod has nothing to create. That is reported — once per hash, in
  `/diagnostics`, `/mods` and the bug report — rather than left as an entity that
  silently never appears. Custom weapons get a server-side validation envelope, an
  operator-editable one in `server.json`, and a client-side name so the console
  stops printing bare hashes.
- **Encrypted, authenticated sessions.** A player proves ownership of an ECDSA
  P-256 identity key during the handshake, and the same signature binds an
  ephemeral ECDH exchange, so every session packet after it is AES-256-CBC with
  an HMAC-SHA256 tag under per-direction keys. Forward-secret: the ephemeral keys
  die with the session. Proven by a test that plants a canary in a chat message
  and requires it to be absent from the wire — with a control test that requires
  the same canary to be findable when encryption is off.

522 automated tests, all passing, covering everything except the ScriptHookVDotNet
host layer and the two plugin-host bridges, which need a running game.

Every push and pull request runs the build, the suite and a documentation check
on `ubuntu-latest` ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). The
build step passes `-warnaserror`, so the zero-warning claim is enforced rather
than asserted, and the whole solution — the `net48` client included — compiles on
Linux without a Windows runner. Running the client still needs Windows and GTA V,
and CI does not pretend otherwise.

## What does not work yet

Stated plainly, because a framework that hides its gaps wastes your time:

- **Ped locomotion is implemented but never watched.** The decision logic —
  gait, when to correct, ragdoll and death handling — is unit-tested, and the
  bridge drives the game's task system rather than writing coordinates. Whether
  it *looks* right in Los Santos has not been verified and cannot be from a build
  machine. Expect the thresholds to need tuning.
- **Vehicles interpolate but do not predict.** A non-owned vehicle is replayed
  behind the server clock rather than simulated forward, so it lags its owner's
  view by the interpolation delay.
- **Vehicle body deformation is not replicated.** GTA V exposes deformation only
  through natives that write into a vehicle, never read from one.
- **The RPH bridge has never run inside a real game.** It compiles against
  RagePluginHook 1.124.0 and its channel half is covered by tests, but RPH's
  loader and `GameFiber` timing cannot be exercised without Windows, GTA V and
  RPH. Same for whether each LSPDFR probe binds against a given LSPDFR release —
  which is why unbound probes are counted and reported rather than assumed to
  work.
- **An arbitrary RPH plugin's internal state cannot be replicated, and callout
  logic cannot be shared.** These are limits of what RPH and LSPDFR expose, not
  deferred work, and they are written down rather than promised:
  [RPH](docs/RPH_INTEGRATION.md), [LSPDFR](docs/LSPDFR_INTEGRATION.md).
- **A missing mod is reported, not fixed.** The framework has no game files and
  neither does the server, so it can tell you which model is missing and which
  entity wanted it; installing it is still yours to do.
- **Identity is a keypair, not an account.** The private half never leaves the
  machine, so nobody who watches a handshake can become you — but there is no
  login, no password reset, and copying `IdentitySecret` moves the character.
- **Encryption protects the wire, not the operator.** Sessions are encrypted and
  authenticated, but the server legitimately holds the session keys and sees
  everything you send it. Packet sizes and timing stay visible to an observer,
  and the connectionless handshake legs are plaintext by construction.

## Quick start

```bash
# Build and test
./tools/build.sh Release
./tools/test.sh

# Run a server
./tools/run-server.sh

# Stage the client for a GTA V install
./tools/package-client.sh Release
```

Then copy `dist/client/scripts/*` into `<GTA V>\scripts\` and `dist/client/Gtamp/`
into `<GTA V>\`, start the game, press **F8** and type `connect`.

Full walkthrough, including prerequisites and how to uninstall:
[docs/INSTALL.md](docs/INSTALL.md).

## Documentation

| Document | What is in it |
| --- | --- |
| [docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) | **Read this first.** What GTA V actually permits, every named limitation, and the workaround chosen for each |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Projects, the three seams, data flow, failure policy |
| [docs/NETWORK_PROTOCOL.md](docs/NETWORK_PROTOCOL.md) | Wire format, reliability, handshake, snapshots, quantisation bounds |
| [docs/WORLD_STATE.md](docs/WORLD_STATE.md) | The full-state rule and how it is enforced structurally |
| [docs/ENTITY_SYSTEM.md](docs/ENTITY_SYSTEM.md) | Entity model, field declarations, type ids, schema hashing |
| [docs/MOD_SDK.md](docs/MOD_SDK.md) | Writing a mod or an adapter; the section-21 API mapping |
| [docs/RPH_INTEGRATION.md](docs/RPH_INTEGRATION.md) | The two-host problem, the in-process channel, and the RPH limit that is not a roadmap item |
| [docs/LSPDFR_INTEGRATION.md](docs/LSPDFR_INTEGRATION.md) | What is read, how it travels, and why callout logic cannot be shared |
| [docs/PERSISTENCE.md](docs/PERSISTENCE.md) | Schema, opaque mod blobs, restart flow |
| [docs/SECURITY.md](docs/SECURITY.md) | What is defensible, the movement budget, trust boundaries |
| [docs/DEVELOPER_CONSOLE.md](docs/DEVELOPER_CONSOLE.md) | Console, colours, commands, bug reports |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phases 1–12 and what was deliberately not faked |
| [docs/INSTALL.md](docs/INSTALL.md) | Concrete install, verify and rollback commands |
| [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md) | Every project read, its licence, what was learned from it and what was deliberately not taken |
| [DEV_COMMANDS.md](DEV_COMMANDS.md) | Every build, test, run and console command |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Symptom → cause → fix |

## Requirements

**Building:** .NET SDK 8.0+. Builds on Linux, macOS and Windows — including the
`net48` GTA V client.

**Server:** .NET 8 runtime. Any OS.

**Client:** GTA V, ScriptHookV, ScriptHookVDotNet 3. RAGE Plugin Hook and LSPDFR
are optional.

## Repository layout

```
src/Gtamp.Shared          netstandard2.0  protocol, entities, snapshots, world
src/Gtamp.Server          net8.0          authoritative server
src/Gtamp.Client.Core     netstandard2.0  client logic, no GTA V dependency
src/Gtamp.Client.Shv      net48           ScriptHookVDotNet host
src/Gtamp.Adapters.Rph    net48           optional RPH integration
src/Gtamp.Adapters.Lspdfr net48           optional LSPDFR integration
src/Gtamp.RphBridge       net48           RPH-loaded plugin; the other half of the bridge
tests/Gtamp.Tests         net8.0          xUnit
tools/                                    build, test, run, package scripts
docs/                                     architecture and design documents
```

## Licence and legal note

This framework contains no Rockstar code or assets and does not modify any GTA V
file. It is a ScriptHookVDotNet script, in the same category as any other GTA V
mod. It does not use Rockstar's online services or netcode — see
[docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) §3 for why that matters.
