# Developer console

> English. Русский: [ru/DEVELOPER_CONSOLE.md](ru/DEVELOPER_CONSOLE.md).

Press **F8** in game. The key is configurable — `ConsoleKey` in
`GTA V/Gtamp/client.ini` is a virtual key code (119 = F8, 192 = tilde).

```
MULTIPLAYER DEVELOPER CONSOLE   [Connected] entities: 152  players: 3  snapshot: 4471  38 ms, 0.4% loss
[11:04:22.118] [INFO]    [CLIENT]   GTAMP client 0.1.0 loaded. Press F8 for the console.
[11:04:22.412] [SUCCESS] [NETWORK]  Connected to 'Los Santos RP' as player 3 (entity #17)
[11:04:22.418] [INFO]    [SERVER]   bob joined the session.
[11:04:31.902] [WARNING] [NETWORK]  Snapshot delay: 120ms
[11:04:33.140] [ERROR]   [ENTITY]   Vehicle #152 state mismatch
[11:04:33.141] [CRITICAL][NETWORK]  Failed to deserialize entity
> _
```

## Design

The console is split in two on purpose:

- `DeveloperConsole` in `Gtamp.Client.Core` — the buffer, filters, search, command
  table and copy actions. No drawing, no GTA V.
- `ConsoleRenderer` in `Gtamp.Client.Shv` — reads the model each frame and draws
  it. No state of its own.

That split is why filtering, search, history and every command is covered by unit
tests. This is exactly the kind of logic that quietly rots when it can only be
exercised by standing in Los Santos pressing F8.

## Colours

| Role | Colour | When |
| --- | --- | --- |
| `Critical` | **Bright red on a filled red row** | Critical |
| `Error` | **Red** | Error |
| `Warning` | **Yellow** | Warning |
| `Success` | **Green** | Success |
| `Debug` | Grey | Debug |
| `Network` | Light blue | Info from the network subsystem |
| `Server` | Purple | Info from the server |
| `Client` | Green-grey | Info from the client |
| `Mod` | Orange | Info from a mod or adapter |
| `Security` | Magenta | Info from the security subsystem |

Severity beats subsystem: a network error is red, not network-blue. When you are
looking for a failure you are looking for the failure, not for which subsystem
produced it. Critical additionally gets a filled row so it is impossible to scroll
past.

## Keys

| Key | Action |
| --- | --- |
| F8 | Open/close |
| Enter | Run the line |
| Escape | Close |
| Backspace | Delete a character |
| ↑ / ↓ | Command history |
| Page Up / Page Down | Scroll |
| End | Jump to the newest line |

Game controls are disabled while the console is open, so keystrokes do not leak
into the game.

## Commands

| Command | What it does |
| --- | --- |
| `help [command]` | List commands, or explain one |
| `connect [host] [port]` | Connect; defaults come from `client.ini` |
| `disconnect` | Leave the server |
| `status` | Connection, world and replication summary |
| `players` | Players in the replicated world |
| `entity <id>` | Full replicated state of one entity |
| `diff <id>` | The server's state for an entity next to the local game's, field by field |
| `net` | Network debugger: ping, loss, bandwidth, snapshots, retransmits |
| `mods` | Detected mods and adapter status |
| `diagnostics` | Check the installation and the session |
| `bundle [what went wrong]` | Write a diagnostic folder next to the logs. Nothing is sent anywhere |
| `overlay [on\|off]` | Toggle the on-screen network readout |
| `admin <command...>` | Run a server command, if the server lets you |
| `report <text>` | Build a bug report and copy it to the clipboard |
| `say <text>` | Send a chat message |
| `filter <name>` | `all`, `info`, `debug`, `warning`, `error`, `critical`, `network`, `server`, `client`, `mod`, `security` |
| `search [text]` | Filter lines by text; no argument clears |
| `copy [message\|stack\|full] [errorId]` | Copy the last problem, or one by id |
| `clear` | Empty the buffer |
| `dev [on\|off]` | Toggle developer mode |

Developer-only (hidden and refused until `dev on`):

| Command | What it does |
| --- | --- |
| `resync` | Throw away the replicated world and request a full snapshot |
| `schema` | List registered entity types and their replicated fields |
| `reload config` | Re-read `client.ini` and apply what can change live |
| `reload adapters` | Re-scan the adapter directory for adapters added since startup |

## Search

Matches message, detail, tag, level, category and timestamp.

Two refinements that matter in practice:

- **An all-digit query is an exact error-id lookup.** Substring-matching the id
  would also hit every timestamp containing those digits, which makes
  `search 152` useless for finding error 152.
- **The timestamp is only searched when the query contains a colon.** Otherwise
  `search 1` matches every line logged in the 11th hour and drowns the result.

Filter and search combine: `filter error` then `search entity 7` gives you the
errors about entity 7 and nothing else.

## Copy

Every problem line can be copied in four shapes (master prompt section 41):

- `copy message` — the message alone
- `copy stack` — the stack trace
- `copy full` — the formatted line plus its detail
- `report <text>` — a complete bug report

Copy targets the most recent **problem** (warning or above), not the most recent
line — by the time you reach for it, the error has usually scrolled up behind
routine chatter. Pass an error id to target a specific one.

The clipboard write runs on a short-lived STA thread, because the Windows
clipboard requires one and the ScriptHookVDotNet script thread is not.

## Bug reports

`/report Vehicle #152 disappears after reconnect` produces the format from master
prompt section 43 and copies it to the clipboard, ready to paste into a tracker or
into Claude Code:

```
=== MULTIPLAYER BUG REPORT ===

BUG_ID: 8F2A1C4D9E03
DATE: 2026-08-31 11:04:33Z
SEVERITY: MEDIUM

SUBSYSTEM: Entity
GTA V VERSION: v1_0_3095_0
MULTIPLAYER VERSION: 0.1.0
RPH VERSION: 1.124.0.0
LSPDFR VERSION: 0.4.9
SCRIPTHOOKV: present

MODS:
  scripthookv 1.0.3095.0
  ...

DESCRIPTION / STEPS TO REPRODUCE / EXPECTED / ACTUAL
PLAYER:   name, ids, position, health, interior
ENTITY:   replicated count, remote peds, the first 25 entities
NETWORK:  state, server, ping, loss, packets, bytes, retransmits, snapshots, resyncs
STACK TRACE
RECENT EVENTS:  the last 10 warnings and errors
LOGS:           the last 40 lines

=== END REPORT ===
```

Nothing is sent anywhere. The report goes to the clipboard and the log file, and
the player decides what to do with it.

## The entity inspector

`entity <id>` shows what the **server** believes. `diff <id>` shows that next to
what the **game** actually has:

```
Entity #152 (Vehicle) — server vs the replicated vehicle
  field            server                          local
  = position       (241.3, -1042.7, 29.3)          (241.4, -1042.6, 29.3) (0.14 m apart)
  ≠ bodyHealth     1000                            614
  = model          0xB779A091                      0xB779A091
  ? deformation    —                               not readable
      GTA V exposes deformation only through natives that write into a vehicle, never read from one
  1 difference(s), 1 field(s) the game will not read back
```

Every symptom a player reports — "the car is in the wrong place", "he's driving a
different vehicle", "my health keeps resetting" — is a disagreement between two
states that are otherwise impossible to see at once. Showing only the replicated
state answers "what does the server think" and leaves the actual question
unanswered.

**`?` is not a match.** GTA V exposes far less for reading than for writing, and a
field that cannot be read back is marked rather than left blank — a blank in a
diff reads as agreement, which is the one answer that must never be given for
something nobody checked.

Tolerances are per field, not global: a remote ped is deliberately rendered behind
the server clock, so a small positional difference there is the interpolation
working and is annotated as such.

## The network overlay

### `selftest`

Asks the running game which replicated capabilities actually arrived, and prints
one line each. It is the only place where this project's test suite and the engine
meet: the suite proves the decisions and can prove nothing about GTA V, so until
this existed, finding out whether a ped ends up in seat -1 meant a person watching
for it and remembering what they saw.

Four outcomes, kept distinct on purpose — "no", "not tried" and "go and look" are
three different answers, and collapsing them is how a self-test becomes a green
light that means nothing:

| Mark | Meaning |
| --- | --- |
| `ok` | The game answered and the answer was right |
| `FAIL` | The game answered and the answer was wrong. This is a defect |
| `--` | Nothing to check against yet — nobody else is connected, nobody has fired |
| `look` | Cannot be judged from inside the game. A blip is on the screen or it is not, and no state says which |

Run it alone and almost everything reads `--`; that is correct, not a failure. Get
another player near you, drive, fire a weapon and run it again. The same report is
written into every `bundle`, which makes a bug report from a real session far more
useful than one without it.

`ShowPlayerBlips` and `ShowPlayerNames` in `client.ini` control the map blip and
the floating name drawn for each other player. Both default to **on**: a session in
which you cannot find or identify anybody is not a session. The blip is coloured by
what the player is doing — red when the police are after them, grey when they are
down, blue in a vehicle, yellow when badly hurt, green otherwise — and the name
fades out with distance rather than vanishing at a threshold.

`overlay on`, or `ShowNetworkOverlay=True` in `client.ini`, draws a readout in the
top-right corner:

```
GTAMP  Los Santos RP  3 player(s)
ping 38 ms   loss 0.4%
snapshots 4471 applied, 2 dropped
resyncs 0   corrections 1
entities 152   vehicles 14   objects 30
```

Colour follows the console's roles. The thresholds are the ones at which each
symptom becomes visible rather than round numbers: 150 ms of ping is where remote
players start to feel behind, 5% loss is where unreliable state updates thin out
enough to see. A resync is always red — it means a delta failed to decode, which
is a version or mod mismatch far more often than congestion.

Missing mod content is raised here as well as in `/diagnostics`, because this is
the readout that is on screen while the player is looking at the gap.

## Diagnostic bundles

`bundle <what went wrong>` writes a folder next to the logs:

```
report.txt           the full bug report
diagnostics.txt      installation and session checks
network.txt          the network readout at that moment
log.txt              the last 500 client log lines
client.ini.redacted  your configuration with secrets replaced
README.txt           what this is and what not to share
```

**Nothing is sent anywhere.** Master prompt section 47 requires that crash data
not leave the machine without permission, and the permission model here is the one
that cannot be got wrong: the framework writes files, the player decides what to
do with them. There is no upload path in this code, so there is nothing to enable
by accident.

**The identity secret is redacted, and that is not decoration.** Since
authentication landed, `client.ini` holds the private key that proves who a player
is. A bundle exists to be shared, so copying it verbatim would turn a bug report
into a handover of the player's character. The secret and the server password are
replaced with a marker that keeps their shape visible; the *public* identity stays,
because it is the single most useful identifier in a report and is not a secret.

Redaction matches on the key name, never on a substring — a player called
`IdentitySecret` should not have their name removed, and more importantly a
substring match that misses nothing still looks like it worked.

## Diagnostics

`/diagnostics` checks the installation and the live session:

```
=== DIAGNOSTICS ===
✓ GTA V                v1_0_3095_0
✓ Multiplayer          client 0.1.0
✓ ScriptHookV          installed
✓ ScriptHookVDotNet    3.6.0
✓ RAGE Plugin Hook     1.124.0.0
⚠ LSPDFR               not installed (optional)
✓ Mods                 27 detected (4 ASI, 12 scripts, 0 LSPDFR plugins)
✓ Adapters             1 active, 1 inactive
✓ Server               Los Santos RP at 203.0.113.9:27015
✓ Network              38 ms, 0.4% loss
✓ Entity schema        0xB0AC13FC
✓ Game directory       D:\Games\Grand Theft Auto V
=== END DIAGNOSTICS ===
```

An **optional** component that is absent is `⚠`, never `✗`. Every optional
component in this framework is genuinely optional, and marking its absence as a
failure would train people to ignore the failures that matter.

## Server-side console

The server has the same command surface on stdin: `status`, `players`,
`entities`, `entity <id>`, `net`, `kick`, `say`, `time`, `weather`, `save`,
`diagnostics`, `stop`. See [../DEV_COMMANDS.md](../DEV_COMMANDS.md).

Redirected stdin is read too, so `echo stop | Gtamp.Server` and a container's
console both work.
