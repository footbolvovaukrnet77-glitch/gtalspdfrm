# Developer commands

Everything you need to build, run, test and debug the framework.

---

## Build

| Command | What it does |
| --- | --- |
| `./tools/build.sh [Debug\|Release]` | Builds every project, including the `net48` GTA V client |
| `./tools/rebuild.sh [Debug\|Release]` | Clean build from scratch — use when a stale artefact is suspected |
| `./tools/clean.sh` | Removes `bin/` and `obj/`. Leaves `server.json`, `data/` and `logs/` alone |

Windows equivalents: `tools\build.bat`, `tools\rebuild.bat`, `tools\clean.bat`.

The `net48` client builds on Linux and macOS too, through
`Microsoft.NETFramework.ReferenceAssemblies`. You do not need Windows to compile
the GTA V half.

## Test

| Command | What it does |
| --- | --- |
| `./tools/test.sh` | Runs the whole suite |
| `./tools/test.sh --filter SessionTests` | One class |
| `./tools/test.sh --filter "FullyQualifiedName~Reconnect"` | Matching tests |
| `./tools/test.sh -l "console;verbosity=detailed"` | Full per-test output |

The suite is deterministic and does not sleep: the network is virtual and time is
advanced explicitly, so a lossy 8-client convergence test runs in milliseconds and
gives the same answer every time.

## Run the server

| Command | What it does |
| --- | --- |
| `./tools/run-server.sh` | Build and start with `server.json` |
| `./tools/run-server.sh --port 27020` | Override the port |
| `./tools/run-server.sh --config /etc/gtamp/server.json` | Use another config |
| `echo stop \| ./tools/run-server.sh` | Start and shut down — useful for a smoke test |

## Server console

Type these at the running server's prompt.

| Command | What it does |
| --- | --- |
| `help` | List commands |
| `status` | Server, world and tick summary |
| `players` | Connected players with ping, loss and position |
| `entities` | Every entity the server is tracking |
| `entity <id>` | Full state of one entity |
| `net` | Per-connection counters: ping, RTT variance, packets, loss, retransmits |
| `kick <playerId>` | Disconnect a player |
| `teleport <id> <x> <y> <z> [heading]` | Move a player; holds their authority until they confirm |
| `kill <playerId>` | Kill a player |
| `respawn <playerId>` | Respawn a dead player immediately |
| `say <text>` | Broadcast a chat message as the server |
| `time <HH:MM>` | Set the world clock |
| `weather <name>` | `EXTRASUNNY`, `CLEAR`, `RAIN`, `THUNDER`, `SNOW`, ... |
| `save` | Write the world to persistence now |
| `diagnostics` | Same checks as the in-game `/diagnostics` |
| `stop` | Save and shut down cleanly |

## In-game console (F8)

| Command | What it does |
| --- | --- |
| `help [command]` | List commands, or explain one |
| `connect [host] [port]` | Connect; defaults from `client.ini` |
| `disconnect` | Leave |
| `status` | Connection, world and replication summary |
| `players` | Players in the replicated world |
| `entity <id>` | Replicated state of one entity |
| `net` | Network debugger |
| `mods` | Detected mods and adapter status |
| `diagnostics` | Check the installation and the session |
| `report <text>` | Build a bug report and copy it to the clipboard |
| `say <text>` | Chat |
| `filter <name>` | Show only matching lines |
| `search [text]` | Filter by text; no argument clears |
| `copy [message\|stack\|full] [id]` | Copy an error |
| `clear` | Empty the buffer |
| `dev [on\|off]` | Toggle developer mode |
| `resync` *(dev)* | Discard the replicated world, request a full snapshot |
| `schema` *(dev)* | List entity types and their replicated fields |

Full reference: [docs/DEVELOPER_CONSOLE.md](docs/DEVELOPER_CONSOLE.md).

## Logs

| Where | What |
| --- | --- |
| `logs/server-YYYY-MM-DD.log` | Server, next to the working directory |
| `<GTA V>/Gtamp/logs/client-YYYY-MM-DD.log` | Client |
| `<GTA V>/Gtamp/logs/startup-failure.log` | Written only if the client fails before its logger exists |
| `<GTA V>/ScriptHookVDotNet.log` | SHVDN's own log — check here first if the script never loads |

`./tools/logs.sh` tails today's server log.

## Package the client

```bash
./tools/package-client.sh Release
```

Stages `dist/client/` with the exact files a player copies into their GTA V
directory. See [docs/INSTALL.md](docs/INSTALL.md).

## Typical loops

**Change the protocol**

```bash
$EDITOR src/Gtamp.Shared/Protocol/Messages.cs
# bump ProtocolConstants.ProtocolVersion if the change is not backwards compatible
./tools/build.sh && ./tools/test.sh
```

**Change replication and check it converges**

```bash
$EDITOR src/Gtamp.Shared/World/SnapshotCodec.cs
./tools/test.sh --filter "SnapshotTests|StressTests"
```

**Change the GTA V bridge** — this is the part tests cannot cover

```bash
$EDITOR src/Gtamp.Client.Shv/Bridge/ShvGameBridge.cs
./tools/build.sh Release            # verifies it still compiles against SHVDN
./tools/package-client.sh Release
# copy to the GTA V directory, start the game, F8, connect
```
