# GTAMP — GTA V Universal Multiplayer Framework

A server-authoritative multiplayer framework that turns single-player GTA V into a
shared world, with an entity system and mod SDK that third-party mods can extend
without patching the framework.

**Status: Phase 1 complete.** Two players share one world, movement replicates,
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
- **SQLite persistence** that survives a real process restart.
- **Optional anti-cheat**, five levels, with protocol guards that stay on even at
  `Off`.
- **F8 developer console** with colour roles, filters, search, copy-error, bug
  reports and diagnostics.
- **Mod SDK** — a mod-defined entity type replicates through the ordinary snapshot
  path with no change to the networking layer.
- **RAGE Plugin Hook and LSPDFR are genuinely optional.** Neither the client nor
  the adapters link against them; the adapters bind by reflection and report
  themselves inactive when the mod is absent.

138 automated tests, all passing, covering everything except the ~400 lines that
call GTA V natives.

## What does not work yet

Stated plainly, because a framework that hides its gaps wastes your time:

- **Remote players slide instead of walking.** Position, heading, health and
  armour are correct; gait is not. GTA V drives locomotion from its task system,
  which writing coordinates does not touch. Phase 2 — see
  [docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) §4.1.
- **No vehicles, peds or objects yet.** Player replication only. Their type ids
  are reserved; the classes do not exist, because an empty `VehicleEntity` that
  replicates nothing would look like support.
- **RPH and LSPDFR state is not replicated.** The adapters detect, report and
  register their integration points and say so at warning level. The reason is a
  real one, and it is written down:
  [RPH](docs/RPH_INTEGRATION.md), [LSPDFR](docs/LSPDFR_INTEGRATION.md).
- **The identity token is continuity, not authentication.** Phase 10.
- **The protocol is plaintext.** Run trusted servers until Phase 12.

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
| [docs/RPH_INTEGRATION.md](docs/RPH_INTEGRATION.md) | Why cross-host integration is hard and what Phase 7 will do |
| [docs/LSPDFR_INTEGRATION.md](docs/LSPDFR_INTEGRATION.md) | Why reflection, and the scope `API.Functions` permits |
| [docs/PERSISTENCE.md](docs/PERSISTENCE.md) | Schema, opaque mod blobs, restart flow |
| [docs/SECURITY.md](docs/SECURITY.md) | What is defensible, the movement budget, trust boundaries |
| [docs/DEVELOPER_CONSOLE.md](docs/DEVELOPER_CONSOLE.md) | Console, colours, commands, bug reports |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phases 1–12 and what was deliberately not faked |
| [docs/INSTALL.md](docs/INSTALL.md) | Concrete install, verify and rollback commands |
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
tests/Gtamp.Tests         net8.0          xUnit
tools/                                    build, test, run, package scripts
docs/                                     architecture and design documents
```

## Licence and legal note

This framework contains no Rockstar code or assets and does not modify any GTA V
file. It is a ScriptHookVDotNet script, in the same category as any other GTA V
mod. It does not use Rockstar's online services or netcode — see
[docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) §3 for why that matters.
