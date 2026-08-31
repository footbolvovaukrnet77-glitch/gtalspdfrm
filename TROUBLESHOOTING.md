# Troubleshooting

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

## Script does not load

`<GTA V>/ScriptHookVDotNet.log` is the first place to look.

| Symptom | Cause | Fix |
| --- | --- | --- |
| No mention of the DLL | Wrong folder | It goes in `<GTA V>\scripts\`, not the root |
| `Could not load file or assembly 'Gtamp.Client.Core'` | Only one DLL was copied | Copy all three: `Gtamp.Client.Shv.dll`, `Gtamp.Client.Core.dll`, `Gtamp.Shared.dll` |
| Nothing loads at all after a game update | ScriptHookV lags GTA V updates | Wait for a ScriptHookV release matching the new build |
| `Gtamp/logs/startup-failure.log` exists | The client threw before its logger existed | The file has the exception |

---

## RPH or LSPDFR conflict

**Symptom:** the game starts through RPH but the multiplayer console never opens.

The client hosts on ScriptHookVDotNet, which loads whether or not RPH launched the
game — but only if SHVDN is installed. Confirm `ScriptHookVDotNet3.dll` is in the
GTA V root.

**Symptom:** `/mods` shows RPH as installed but "not hosting".

You launched `GTA5.exe` directly rather than `RAGEPluginHook.exe`. Detection is
correct; RPH simply is not in the process.

**Symptom:** LSPDFR callouts do not replicate.

Expected. Callout, pursuit and police-AI replication is Phase 8 — the adapter says
so at warning level on startup. See
[docs/LSPDFR_INTEGRATION.md](docs/LSPDFR_INTEGRATION.md) for why.

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

**This is a known Phase 1 limitation, not a bug.** Remote peds are moved by
writing coordinates; GTA V drives locomotion animation from its task system, which
coordinates alone do not touch. Task-driven locomotion is Phase 2. See
[docs/ENGINE_ANALYSIS.md](docs/ENGINE_ANALYSIS.md) §4.1.

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
