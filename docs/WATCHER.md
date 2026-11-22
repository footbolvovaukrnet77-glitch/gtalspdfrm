> English. Русский: [ru/WATCHER.md](ru/WATCHER.md).

# The watcher

`Gtamp.Watcher` reads the logs GTA V, ScriptHookV, ScriptHookVDotNet, RAGE Plugin
Hook and GTAMP write, and when one of them says something went wrong it records
what happened, what was happening around it, and which files show it.

It is a reader. It never touches the game, injects nothing, and cannot be the
reason a session breaks — which is the point of a tool whose job is to be believed
about what broke.

```
tools\run-watcher.bat                 record only; nothing leaves the machine
tools\run-watcher.bat --screenshot    also grab the screen
tools\run-watcher.bat --rules         what counts as a problem, and why
tools\run-watcher.bat --help          every key
```

## Nothing is sent anywhere by default

There is no channel from a player's machine to whoever is reading these. The only
automatic route is the git repository, and taking it is a decision, not a step: a
push puts the files where the repository can be read, and if the repository is
public that means everybody, permanently.

So `--publish` is off unless asked for, and when `origin` points at GitHub it
**refuses** until `--public-ok` is also given. That is not a warning to click past;
the run stops and prints what would be published. Without `--publish` the records
sit on disk and the person who made them decides what to do with them.

`diagnostics/` is in `.gitignore` so incidents never ride along with ordinary work
by accident. Publishing overrides that with `git add --force`, which is the one
place the default is meant to be overridden — so the only way these files reach a
commit is somebody passing the flag.

## What is taken out before anything is written

The redactor runs on every line of every file the watcher collects, whether or not
that incident will ever be published — because the moment a file is written is not
the moment somebody decides to share it, and a redactor that runs only on the
publishing path is one that will one day be skipped.

| Taken out | Left in |
| --- | --- |
| `IdentitySecret` — the private key that proves who a player is | `PlayerName`, which is what makes a report matchable to a session |
| `IdentityToken` — the public half, still a stable cross-server identifier | `127.0.0.1`, `0.0.0.0` and LAN addresses: a local test is all address |
| `ServerPassword` | Entity ids, snapshot numbers, positions, timings |
| The Windows account name inside paths | Mod names and versions |
| Any routable IPv4 — somebody else's server is theirs to give out | |

Three kinds of file are never copied at all, redacted or not: `client.ini` (the
private key lives in it — the bundle's `client.ini.redacted` is the copy to take),
`server.json` (may hold the password), and `*.db` (the persisted world, including
every player's identity token). Trusting the redactor to blank a secret works today
and breaks the day a setting is renamed; refusing the file does not.

**A screenshot cannot be redacted.** It may carry a player name, the overlay's
server address, and whatever else was on the screen. Screenshots are off unless
asked for, and are worth asking for: some defects have no text at all — a car under
the map, a camera in the air, a character behind glass.

## What counts as a problem

Every rule names a defect this project has actually had, or a line the client
prints precisely because somebody has to see it. That is the bar, and it is not
rhetorical: **the first version of the ScriptHookVDotNet rule matched the bare word
`NativeMemory` and called it "the session ended"**. That word is in the first ten
lines of every healthy SHVDN log. Measured across ten of the user's real logs it
hit all ten while nothing was wrong, and `TypeInitializationException` hit only the
three that had actually failed. A rule that fires on a healthy session teaches the
reader to ignore the file, and a file nobody reads is worse than no file. The rule
now matches the exception.

The same measurement decided the de-duplication. One real thirty-minute log holds
995 `Requesting a resync` lines: one incident per line would bury the session that
caused them. The same problem within five minutes is the same problem — except for
the fatal ones, which are rare and each of which is its own event, so those repeat
after thirty seconds.

Run `--rules` for the current list with what each one means.

## Known limitations, stated rather than discovered

**Exclusive fullscreen produces a black screenshot.** A copy of the desktop shows
GTA V only when the game is windowed or borderless; in exclusive fullscreen the
game owns the display. The watcher detects a uniform image and reports "the capture
came out empty — put the game in borderless windowed" rather than writing a file
that looks like evidence and is not. Getting round this from outside the process
would mean hooking DirectX, which is a different kind of program.

**It only knows what the logs say.** A defect nobody logs is invisible to it. That
is the trade for a tool that cannot itself break the game.
