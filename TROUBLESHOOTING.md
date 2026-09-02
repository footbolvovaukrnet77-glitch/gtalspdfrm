# Troubleshooting

> English. Русский: [TROUBLESHOOTING.ru.md](TROUBLESHOOTING.ru.md).

Each entry: the symptom, how to confirm the cause, and the fix.

---

## Server will not start

**`Could not bind UDP 0.0.0.0:27015`**

Another process holds the port, or the address is not local to this machine.

```bash
# Linux
sudo ss -ulpn | grep 27015
# Windows
netstat -ano -p udp | findstr 27015
```

Fix: stop the other process, or `./tools/run-server.sh --port 27020`.

**`Could not load 'server.json'`**

The file has a syntax error. The message names the problem. Delete it and restart
to get a fresh default, or fix the JSON — trailing commas and comments are
allowed, unquoted keys are not.

**`maxPlayers must be between 1 and 256` (or similar)**

Configuration validation. The message names the setting and its valid range.

---

## Client will not connect

Work down this list in order.

**1. Is the script even loaded?**

Check `<GTA V>/ScriptHookVDotNet.log`. If `Gtamp.Client.Shv.dll` is not mentioned,
the script never loaded — see "Script does not load" below.

**2. Does the client log show an attempt?**

`<GTA V>/Gtamp/logs/client-*.log` should show `Connecting to <address>...`.
If not, `connect` was never run — press F8 and type `connect`.

**3. `no reply from <address> after 20 attempts`**

Nothing is answering on that UDP port.

- Is the server running? Check `status` on the server console.
- Is the address right? `ServerAddress` in `client.ini`.
- **Is the port open as UDP?** This is the usual cause. A TCP rule will not work.
- On a home connection, is the router port-forward UDP as well?

Confirm reachability from the client machine:

```bash
# Linux/macOS
nc -zuv <server> 27015
# Windows
Test-NetConnection -ComputerName <server> -Port 27015 -InformationLevel Detailed
```

**4. `wrong server password`**

`ServerPassword` in `client.ini` must match `password` in `server.json`.

**5. `server is full`**

Raise `maxPlayers` or wait.

**6. `protocol version mismatch`**

The client and server were built from different commits. Rebuild both from the
same source and redeploy the client DLLs.

**7. `required mods are missing or incompatible`**

The message names each blocking mod and why. Install the named mod at the named
version, or set `"enforceRequiredMods": false` on the server to downgrade
requirements to warnings.

---

## Nothing appears in game

**Before anything else:** when the client loads it now shows a green notification —
`GTAMP <version> loaded. Press F8 for the console.` — in the top left, a few seconds
after the game reaches single player.

That one notification separates the two questions worth separating first, because it
is drawn by the same native text machinery the console uses:

| What you see | What it means |
| --- | --- |
| The notification appears | The client loaded **and** it can draw. If the console still does not open, the problem is the key or the console itself |
| No notification, game running normally | Either the client did not load — see "Script does not load" — or it loaded and cannot draw. `<GTA V>/Gtamp/logs/client-*.log` tells you which: if it has a `loaded` line, drawing is the problem |
| A red `GTAMP failed to start` notification | The client threw during startup. `<GTA V>/Gtamp/logs/startup-failure.log` has the exception |

---

## Script does not load

`<GTA V>/ScriptHookVDotNet.log` is the first place to look — **and the first thing to
check is whether that file exists at all.** Everything below assumes it does. If it
does not, the problem is underneath this framework: the ScriptHookVDotNet layer never
ran, so nothing here was ever asked to load, and copying our files again will not
change that.

The presence of `<GTA V>/ScriptHookV.log` splits it in two:

| What is there | What it means | Where to look |
| --- | --- | --- |
| Neither log | ScriptHookV itself never loaded | The four causes below |
| `ScriptHookV.log` but no SHVDN log | ScriptHookV runs; its `.asi` did not | `ScriptHookVDotNet.asi`, `ScriptHookVDotNet2.dll` and `ScriptHookVDotNet3.dll` must all be in the GTA V root, beside `GTA5.exe` |

When neither log exists, in the order worth checking:

1. **The wrong edition of the game.** ScriptHookV supports GTA V **Legacy** only. On
   Enhanced it will never load and no amount of installing changes that. The launcher
   names the edition. A working LSPDFR install is good evidence of Legacy, since
   LSPDFR is Legacy-only too.
2. **An antivirus deleted the files.** ScriptHookV is an injector and is quarantined
   routinely, usually in silence. `dir` the GTA V folder: if `ScriptHookV.dll` or
   `dinput8.dll` are absent after you copied them, that is the answer.
3. **The downloaded files are blocked.** Windows marks files that came from the
   internet and the loader may refuse them. In an elevated PowerShell:
   `Get-ChildItem -Path '<GTA V>\*' -Include *.dll,*.asi | Unblock-File`.
4. **Another mod owns the ASI loader.** `dinput8.dll` is one of several names a
   loader can take; another mod shipping `dsound.dll` or `version.dll` can win.

Note that RAGE Plugin Hook proves nothing here either way: RPH is its own loader and
does not need ScriptHookV, so a working LSPDFR setup can sit on a machine where
ScriptHookV has never loaded once.

Once the log exists:

| Symptom | Cause | Fix |
| --- | --- | --- |
| No mention of the DLL | Wrong folder | It goes in `<GTA V>\scripts\`, not the root |
| `Aborted script Gtamp.Client.Shv.GtampScript` with `SHVDN.NativeMemory..cctor()` in the trace | ScriptHookVDotNet does not support this build of GTA V | Not a defect in this framework — see [ScriptHookVDotNet cannot read this game build](#scripthookvdotnet-cannot-read-this-game-build) below. Builds of this client before that check existed died here on the first text draw, which is the moment the console opens, so it read as "the console does not open" |
| `Could not load file or assembly 'Gtamp.Client.Core'` | Only one DLL was copied | Copy all three: `Gtamp.Client.Shv.dll`, `Gtamp.Client.Core.dll`, `Gtamp.Shared.dll` |
| Nothing loads at all after a game update | ScriptHookV lags GTA V updates | Wait for a ScriptHookV release matching the new build |
| `<GTA V>/Gtamp/logs/startup-failure.log` exists | The client threw before its logger existed | The file has the exception. Pressing the console key also says so in-game now: a key that silently does nothing cannot be told apart from the wrong key |

---

## ScriptHookVDotNet cannot read this game build

The client says this on the first frame, in the log and as a red notification, and
refuses to connect:

```
ScriptHookVDotNet 3.6.0.0 cannot read game build 1.0.3889.0. It locates the game's
data by scanning for byte patterns, and on a build it does not know that scan fails,
so every call that reads or changes the world — spawning a ped, reading a vehicle,
checking that an entity still exists — throws instead of answering.
```

**This is not a defect in this framework, and nothing in this framework can work
around it.** ScriptHookVDotNet does not ask the game where its data lives; it searches
the game's compiled code for byte patterns and derives the addresses. When the game
updates, those patterns move, the search returns nothing, and `SHVDN.NativeMemory`
fails to initialise. From then on every member built on it throws
`TypeInitializationException` — for good, for the rest of the session, on first touch.

Thirty-eight of the ScriptHookVDotNet members this client calls are in that group,
including the ones there is no substitute for:

- `World.CreatePed`, `World.CreateVehicle`, `World.CreatePropNoOffset` — no remote
  player can be spawned at all
- `Ped.Exists`, `Vehicle.Exists`, `Prop.Exists`, `Entity.FromHandle` — nothing can be
  checked before it is used
- `Ped.CurrentVehicle`, `Vehicle.Driver` — who is in which car
- `Vehicle.CurrentRPM`, `.ThrottlePower`, `.BrakePower`, `.SteeringAngle`,
  `.FuelLevel`, `.CurrentGear`, the indicator lights — the whole vehicle telemetry
- `Entity.ForwardVector`, `GameplayCamera.Direction` — where a shot is aimed

The console and the logs are unaffected: this client draws them through
`Function.Call` and pins its own strings.

**A string argument is the trap.** `Function.Call` itself is safe on a build
ScriptHookVDotNet does not know — it goes through ScriptHookV's own invoker. But handing
it a `string` is not a free conversion: `InputArgument`'s implicit operator pins the text
through `SHVDN.ScriptDomain.PinString`, which calls `NativeMemory.StringToCoTaskMemUTF8`.
`Function.Call<string>` comes back the same way. So the identical native call succeeds
carrying numbers and throws carrying text, and every text native carries text. This
client pins its own strings and passes pointers, so none of its drawing depends on that
layer.

**The fix**, and the only one:

1. Read the game build from `<GTA V>/ScriptHookV.log`, first line —
   `game version is VER_1_0_3889_0`.
2. Download a nightly from
   https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases that lists
   support for it. The stable releases lag the game by months; the nightly mirror is
   where support for a new build lands first, and it needs no GitHub account.
3. Replace `ScriptHookVDotNet.asi`, `ScriptHookVDotNet2.dll` and
   `ScriptHookVDotNet3.dll` in the GTA V directory with the ones from it.
4. Restart the game. The message is gone and `connect` works.

Why the client refuses to connect rather than trying: a session in which the network
half works and the game half throws looks connected — the player list fills, the ping
is fine, and nothing else in the world ever happens. Refusing at the door is the
honest answer, and it is the one place this failure can still be explained in words.

---

## The game crashes on startup with an RPH crash report

`RagePluginHook.log` and the `.rcrm` crash report name it:

```
GTAMP RPH Bridge: UNHANDLED EXCEPTION DURING GAME FIBER TICK
System.IO.FileNotFoundException: Could not load file or assembly
'Gtamp.Shared, Version=0.1.0.0' ...
at Gtamp.RphBridge.EntryPoint.Main()
```

`Gtamp.RphBridge.dll` was copied into `<GTA V>\Plugins\` **without
`Gtamp.Shared.dll`**. RAGE Plugin Hook resolves a plugin's dependencies from its own
plugins folder and never from `<GTA V>\scripts\`, so the copy in `scripts\` does not
count. Copy the whole contents of `dist/client/RagePluginHook-plugins/`, both files.

Builds from before this was found took the game down with them: RPH treats an unhandled
exception on a game fiber as fatal, and the plugin's own `try` could not catch it —
the JIT resolves a method's type references on the way *into* the method, so the failure
happened before the first instruction ran. The entry point is now a wrapper that names
no other assembly, so the handler is always in place; a missing dependency now writes a
line naming the file and the folder, and the game keeps running with RPH state
unreplicated.

---

## Hundreds of "Requesting a resync" in one millisecond, then a timeout

Fixed. If a build still does this, it predates the fix.

One snapshot arrived whose baseline the client no longer held — normal, and a resync is
the right answer. The request then **cleared the snapshot history**, which is what every
snapshot already queued behind it needed, so each of those failed too and asked again.
In one real session that was 184 identical requests inside a single millisecond, three
bursts, each followed by silence and `Connection timed out` fifteen seconds later.

The client now asks once and waits up to two seconds for the answer, and keeps its view
instead of deleting every remote player while it waits. `net` reports both numbers:

```
resyncs         2 requested / 37 suppressed while one was outstanding
```

A large suppressed count is not a fault — it is snapshots that would each have asked
again. A large *requested* count means the baseline keeps going missing, which is worth
reporting.

---

## The session goes quiet and times out with nothing in the log

A peer whose ordered channel has stalled keeps answering pings and delivers nothing
reliable, so from outside it is indistinguishable from an ordinary timeout. The reliable
stream is ordered: a message that never arrives blocks every message behind it for the
life of the connection.

Both ends now say so instead — `The connection can no longer deliver: the ordered
channel stalled with N message(s) waiting for sequence S` — and drop the session at the
moment it becomes undeliverable rather than fifteen seconds later with no reason given.

---

## LSPDFR crashes when you go on duty

Fixed, and the fix is a deliberate compromise rather than a repair.

Going on duty is a model change, and so is a model the server holds for you. But
`SET_PLAYER_MODEL` does not dress the existing ped — it **destroys it and builds
another**. Anything still holding the old one has an invalid handle. In one real session
the server's model was applied 0.4 seconds after LSPDFR began building its on-duty
character, and LSPDFR died inside `Persona.FromExistingPed` on `Rage.Ped.get_IsFemale`,
taking the game with it:

```
Rage.Exceptions.InvalidHandleableException: the specified Rage.Ped is invalid
at Rage.Ped.get_IsFemale()
at ...Engine\Scripting\Entities\CPedCache.cs:line 33
at ...Mod\Character\CharacterManager.cs:line 169
```

**On an LSPDFR install the client no longer applies a server-set model at all.** LSPDFR
owns the player character; a co-op framework rebuilding it underneath is the framework
overstepping. The client says so once per session and counts it in `net` under
`models ... given up on`.

**What that costs you, stated plainly:** other players see the model the server holds for
you, and this screen keeps the one LSPDFR chose. A saved appearance does not come back on
connect. Everything else — position, health, vehicles, combat — is unaffected.

This is only reachable when the server actually sets a model: a restored save, an admin
`model` command, or a mod handing out a skin. An ordinary session never does.

---

## `diagnostics` says LSPDFR is not installed when it is

Fixed. The scan looked for `LSPD First Response.dll` in the **game root**. LSPDFR is a
RAGE Plugin Hook plugin and lives in `<GTA V>\Plugins\`, which is where
`RagePluginHook.log` names it on every start. A machine demonstrably running LSPDFR was
told it had none, and the `lspdfr` adapter never activated. Both locations are checked
now, RPH's first.

---

## RPH or LSPDFR conflict

**Symptom:** the game starts through RPH but the multiplayer console never opens.

The client hosts on ScriptHookVDotNet, which loads whether or not RPH launched the
game — but only if SHVDN is installed. Confirm `ScriptHookVDotNet3.dll` is in the
GTA V root.

**Symptom:** `/mods` shows RPH as installed but "not hosting".

You launched `GTA5.exe` directly rather than `RAGEPluginHook.exe`. Detection is
correct; RPH simply is not in the process.

**Symptom:** `/diagnostics` says `waiting for the RPH bridge`, or, after ten
seconds, `the RPH bridge never answered`.

The RPH half of the integration is a separate assembly that RPH loads itself. Two
causes, and the warning in the client log names both:

1. The game was not started through `RAGEPluginHook.exe`. RPH is installed but not
   in the process, so there is nothing to answer.
2. `Gtamp.RphBridge.dll` is not in `GTA V\Plugins\`. It does **not** go in
   `GTA V\scripts\` — that folder belongs to ScriptHookVDotNet, and RPH will
   never look there. Re-run `tools/package-client.sh` and copy
   `dist/client/RagePluginHook-plugins/*` into `GTA V\Plugins\`.

Check `RagePluginHook.log` for `[GTAMP] RPH bridge` lines; if none appear, RPH
never loaded the plugin. `Gtamp.Shared.dll` must sit next to it in `Plugins\`,
because RPH resolves a plugin's dependencies from its own folder.

**Symptom:** the bridge is connected but LSPDFR state never changes.

`/diagnostics` shows the LSPDFR line with a reflection-miss count. A non-zero
count means probes failed to bind by name — usually an LSPDFR update that renamed
a method on `API.Functions`. The client log names each missed probe. Nothing is
silently assumed to work; that is the point of counting them.

If the miss count is zero and the state still never changes, check that you are
actually on duty: every probe reads the player's LSPDFR state, and off duty most
of them legitimately return nothing.

**Symptom:** LSPDFR state does not reach the other players.

The server forwards `lspdfr.event` between clients only when its name is in
`relayedModEvents` in `server.json`. It is there by default; an operator may have
removed it deliberately, in which case the state stays local — that is the switch
working, not a fault.

**Symptom:** LSPDFR callout *scripts* do not run for the other players.

Expected, and not a roadmap item. What crosses the wire is who is on duty, who has
a callout running and which one, who is in a pursuit or a traffic stop — the
observable facts. The peds and vehicles a callout spawns replicate normally, so
you do see each other's suspects and units. The callout's own objectives and
completion state exist only on the machine running it, because LSPDFR exposes no
way to drive another player's callout. See
[docs/LSPDFR_INTEGRATION.md](docs/LSPDFR_INTEGRATION.md).

---

## Mod conflict

**Symptom:** `/diagnostics` shows adapters failed.

The adapter directory has a DLL that is not a valid adapter, or an adapter threw
during initialisation. The client log has the exception. A failed adapter never
stops the rest of the client.

**Symptom:** a mod's entities do not appear for other players.

Check `/mods` on both clients. If the server reports the mod as `Missing`,
`WrongVersion` or `HashMismatch`, the two installs disagree. Only `Required` mods
block a join; the rest are reported and the session continues.

---

## Entity desync

**Symptom:** `/entity <id>` shows values that do not match what you see in game.

Remember `/entity` shows the **replicated, server-authoritative** state. If the
game disagrees, the server is right and the client has drifted.

- Run `net`. High `resyncs` means deltas are failing to decode — check for a
  version or mod mismatch.
- High `snapshots dropped` with low `applied` means snapshots are arriving out of
  order or too late — check ping and loss.
- Run `resync` (developer mode) to force a full snapshot and see if it corrects.

**Symptom:** your own character keeps snapping back.

The server is rejecting your movement. Check the server log for
`SECURITY` lines naming `SpeedHack` or `Teleport`. Causes, in order of likelihood:

1. A trainer or teleport mod is moving you faster than the anti-cheat allows.
2. `antiCheat` is set to `Strict` on a high-latency connection.
3. `correctionThreshold` in `client.ini` is too small for your ping.

---

## Remote players slide instead of walking

Task-driven locomotion landed in Phase 2: `RemotePedController` picks the gait and
the bridge drives GTA V's task system rather than writing coordinates. If peds
still slide, it is one of these:

- **The ped is being hard-corrected constantly.** Run `net`. A remote ped is
  placed outright beyond 8 m of error, and on a link with heavy loss or a large
  interpolation delay that threshold can be crossed every snapshot. Raise
  `InterpolationDelay` in `client.ini`.
- **The thresholds need tuning for your game.** The gait selection and the 0.75 m
  re-task distance are reasoned rather than measured — nobody has watched the
  result in Los Santos. This is stated in
  [docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) §4.1 and in the README.
- **The ped is ragdolling or dead.** Both are deliberately handed to physics and
  not corrected, so a corpse stays where it fell.

---

## Packet loss and high ping

`net` in the in-game console, or `net` on the server console.

| Reading | Meaning |
| --- | --- |
| `packet loss` above ~5% | The link is genuinely lossy. Reliable traffic still arrives; unreliable state updates thin out |
| `ping` above ~250 ms | Remote players will feel behind. Raise `InterpolationDelay` in `client.ini` to trade responsiveness for smoothness |
| `retransmits` climbing fast | Heavy loss. The reliability layer is working, but chat and events will feel laggy |
| `resyncs` above 0 | Snapshots are failing to decode. Not a bandwidth problem — check versions and mods |

Raising `snapshotByteBudget` sends more per snapshot; lowering it sends less and
converges more slowly. It never changes what the server keeps.

---

## Snapshot mismatch

**`baseline snapshot N is no longer in history`**

The client stalled long enough for its baseline to age out of the 64-snapshot
history. It requests a resync automatically and recovers. Frequent occurrences
mean the client is hitching — check frame times, not the network.

**`Entity #N uses type id T, which this build has no serializer for`**

The server is running a mod that registers an entity type the client does not
have. Run `/mods` and `/diagnostics`; install the mod, or ask the server operator
to mark it `Required` so the mismatch is caught at join instead of mid-session.

---

## Persistence and database errors

**`World save failed`** in the server log

The exception follows the message. Usual causes: the disk is full, or `data/` is
not writable by the server process.

**Players are not restored after a restart**

- Is `persistenceEnabled` true?
- Did the server shut down cleanly? `stop` or Ctrl+C save; SIGKILL does not.
  Losing at most `saveIntervalSeconds` of state is the trade-off.
- Did `IdentityToken` in `client.ini` change? It is the key. A regenerated token
  is a new player.

**`Saved world was written with entity schema X but this build produces Y`**

The entity field layout changed since the save. Player records are still restored;
stored entity blobs are skipped, because misinterpreting them would be worse than
losing them. Expected after a change to any entity's fields.

---

## Crash

1. `<GTA V>/Gtamp/logs/client-*.log` — the last lines before the crash.
2. `<GTA V>/ScriptHookVDotNet.log` — SHVDN's view.
3. In game, if it is still running: F8 → `report <what happened>` → paste.

The client catches exceptions per frame and per adapter, so a crash that takes the
game down is usually *not* in managed framework code — suspect ScriptHookV, a
game update, or another ASI plugin. Test by moving the three `Gtamp.*.dll` files
out of `scripts\` and seeing whether the crash persists.

---

## Getting help

Press F8 and run:

```
/report <what you were doing when it went wrong>
```

That produces a complete report — versions, mods, position, entity state, network
counters, recent errors and the last 40 log lines — and copies it to the
clipboard. Nothing is sent anywhere; you decide where it goes.
