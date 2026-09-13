# Developer commands

> English. Русский: [DEV_COMMANDS.ru.md](DEV_COMMANDS.ru.md).

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

## Run the bot (a second player without a second person)

Full description: [docs/BOT.md](docs/BOT.md). It decides everything the *server*
decides and nothing the *engine* decides, so it does not replace looking at a
screen — it removes the need for a second person to be looking at one.

| Command | What it does |
| --- | --- |
| `./tools/run-bot.sh` | Connect to `127.0.0.1:27015` and run every task in order |
| `./tools/run-bot.sh --task follow` | Walk after the nearest real player, so you have somebody to watch |
| `./tools/run-bot.sh --count 2 --task stand,follow,shoot,die` | Two bots, which gives each of them somebody to see, shoot and be shot by |
| `./tools/run-bot.sh --count 10 --task patrol` | Ten players at once — what the byte budget does under real density |
| `./tools/run-bot.sh --server 192.168.1.5:27015 --name Напарник` | Another server, another name |
| `./tools/run-bot.sh --help` | Every key and every task |

Exit code 0 means no task failed. Windows equivalent: `tools\run-bot.bat`.

## Run the watcher (record what breaks)

Full description: [docs/WATCHER.md](docs/WATCHER.md). It reads logs and never
touches the game. Nothing leaves the machine unless `--publish` is given, and that
refuses on a public repository until `--public-ok` is given too.

| Command | What it does |
| --- | --- |
| `./tools/run-watcher.sh` | Watch the logs and write a record whenever something breaks |
| `./tools/run-watcher.sh --screenshot` | Also grab the screen — needs the game windowed or borderless |
| `./tools/run-watcher.sh --rules` | What counts as a problem, and what each one means |
| `./tools/run-watcher.sh --publish --public-ok` | Push the records to the `diagnostics` branch, having acknowledged the repository is public |
| `./tools/run-watcher.sh --help` | Every key |

Windows equivalent: `tools\run-watcher.bat`.

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
| `ban <playerId\|fingerprint> [minutes] [reason]` | Ban an identity; 0 minutes is permanent |
| `unban <name\|fingerprint>` | Lift a ban |
| `bans` | List active bans |
| `role <playerId> <player\|moderator\|admin>` | Set what a player may do over the network |
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
| `admin <command...>` | Run a server command, if the server lets you |
| `mods` | Detected mods and adapter status |
| `diagnostics` | Check the installation and the session |
| `diff <id>` | Server state next to the local game's, field by field |
| `bundle [text]` | Write a diagnostic folder next to the logs; nothing is sent |
| `overlay [on\|off]` | Toggle the on-screen network readout |
| `report <text>` | Build a bug report and copy it to the clipboard |
| `say <text>` | Chat |
| `filter <name>` | Show only matching lines |
| `search [text]` | Filter by text; no argument clears |
| `copy [message\|stack\|full] [id]` | Copy an error |
| `clear` | Empty the buffer |
| `dev [on\|off]` | Toggle developer mode |
| `resync` *(dev)* | Discard the replicated world, request a full snapshot |
| `schema` *(dev)* | List entity types and their replicated fields |
| `reload config` *(dev)* | Re-read client.ini and apply what can change live |
| `reload adapters` *(dev)* | Re-scan the adapter directory |

Full reference: [docs/DEVELOPER_CONSOLE.md](docs/DEVELOPER_CONSOLE.md).

## Logs

| Where | What |
| --- | --- |
| `logs/server-YYYY-MM-DD.log` | Server, next to the working directory |
| `<GTA V>/Gtamp/logs/client-YYYY-MM-DD.log` | Client |
| `<GTA V>/Gtamp/logs/startup-failure.log` | Written only if the client fails before its logger exists |
| `<GTA V>/ScriptHookVDotNet.log` | SHVDN's own log — check here first if the script never loads |
| `<GTA V>/RagePluginHook.log` | RPH's log — look for `[GTAMP] RPH bridge` lines if the bridge never answers |

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
