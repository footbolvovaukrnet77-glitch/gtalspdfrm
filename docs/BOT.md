> English. Русский: [ru/BOT.md](ru/BOT.md).

# The headless bot

`Gtamp.Bot` is a second player without a second person: the real client, the real
protocol and the real server, with the game underneath replaced by a simulation.

It exists because half of this framework had never been run. Eight rows of
`selftest` — remote peds, shots seen, hits reported, seating, explosions, respawn —
read `--` from the first commit to the thirty-seventh defect, not because they were
hard but because every one of them needs two connections and there was one person
testing.

## What it is, exactly

Everything above `IGameBridge` is the shipping client. `SimulatedGameBridge` answers
the client's questions about "the game" from `BotBody`, and records every call the
client makes to draw somebody else instead of drawing it. So:

| Question | Can the bot answer it? |
| --- | --- |
| Is another player replicated, and continuously? | Yes |
| Does the server arbitrate a shot, a hit, a death? | Yes |
| Does a reconnect restore position, health, vehicles? | Yes |
| Does the byte budget hold up with ten players? | Yes |
| Does a ped play a crouch animation? | **No** |
| Does a car fall through the map? | **No** |
| Is the camera where a person expects? | **No** |

The line is exactly where you would expect it: the bot decides everything the
**server** decides, and nothing the **engine** decides. It does not replace a person
looking at a screen; it removes the need for a second one.

## Running it

```
tools\run-bot.bat --task follow
tools/run-bot.sh  --task follow
```

The bot connects to `127.0.0.1:27015` by default and runs every task in order.
`--help` lists the keys. The ones that matter:

| Key | What it does |
| --- | --- |
| `--server <host:port>` | Where to connect |
| `--name <name>` | What the bot is called. With `--count` above 1 a number is appended |
| `--count <N>` | How many bots to run in one process (1–64) |
| `--task <a,b,c>` | Which tasks, in that order |
| `--at <x,y,z>` | Where to appear |
| `--identities <dir>` | Where bot keys live, so the server recognises them between runs |
| `--verbose` | The whole client log, not just the bot's own events |

Two bots are usually what you want, because the tasks that matter need somebody to
look at, shoot at and be shot by:

```
tools/run-bot.sh --count 2 --task stand,follow,shoot,die
```

## The tasks

| Task | What it checks |
| --- | --- |
| `stand` | Stand still and keep existing for everybody else |
| `patrol` | Walk a square changing gait — the movement budget and the posture flags |
| `drive` | Claim a vehicle, have the server adopt it, drive a route |
| `follow` | Walk after the nearest real player and never lose them |
| `shoot` | Fire at a player, claim the hits, and see whether damage arrives |
| `die` | Be killed **by somebody else** and wait for the server to bring you back |
| `reconnect` | Leave and return, and check what the server remembered |

Each ends in one of four verdicts. `ok` and `FAIL` mean what they say. `смотр` means
it ran but a person has to judge the answer. `--` means **skipped**: what the task
needed was not there. A task that needed another player and found none has not
failed, and reporting it as a failure would be the same lie as reporting an untested
thing as working, pointing the other way.

## What it found on its first run

Two, and one of them was in the bot.

**A model hash of zero was reported as missing content.** Between the moment the
server creates a player entity and the moment that player's first state update
lands, the entity carries model hash 0. Every other client asked the streamer
whether it had model `0x00000000`, was told no, and warned that a mod was missing —
and `0x00000000` went into the bug report's `MISSING CONTENT` list, where it names
nothing anybody could install. Zero is the absence of a value, not a model nobody
has. It had been there since players were first replicated and had never been seen,
because it takes two clients to see it.

**The first `die` task passed on evidence that proved nothing.** It set its own
health to zero and called the server's reply a respawn. It was not: the server is
authoritative about health, so it corrected the lie straight back, and the task
reported a respawn that had never happened. The claimed zero was then recorded, so
the next `reconnect` restored a player with no health — which the bot reported as a
server defect. It was not that either. A client cannot kill itself, and that is
correct; the task now waits to be shot, because the only death worth testing is one
the server ruled on.

## Two defects it found and I did not fix

Both came out of a live server log with timestamps, and neither is fixed, because I
could not reproduce either in a test and a change to the death path that nobody can
demonstrate is worse than the defect it is aimed at.

**A respawned player is killed again by their own corpse still in flight.**

```
17:49:13.860  Финал2 respawned at Pillbox Hill Medical Center.
17:49:13.943  Финал2 died at (298.6, -584, 43.26)      <- 83 ms later
17:49:16.969  Финал2 joined — restored from persistence
17:49:17.066  Финал2 died at (298.6, -584, 43.26)      <- 97 ms after joining
```

The server refills health and teleports the player, and the next update arrives from
a client that left before the snapshot did and still says health 0. `ApplyHealth`
reads that as a fresh death report. The mechanism that should prevent it exists —
`HoldHealthAuthority` — but `Respawn` holds *position* authority and not health, and
the death report is checked *before* the hold rather than after. That reasoning is
probably right and it is still only reasoning: a test built on the loopback harness
with 40 ms of latency passes with the change and without it, so it demonstrates
nothing, and it was deleted rather than kept as decoration.

**A player sits at zero health that the server does not consider a death.** In a
later run the server refused every state update from one bot for twelve seconds
running — `HealthHack — gained 20 health in 50 ms` at 20 ms intervals — which means
the server held that player at 0 health while the client reported 20, with no death
logged. A rejected update does not advance the server's state, so the next one is
rejected for the same reason: the same permanent lock-out shape as the anti-cheat
teleport defect fixed earlier on this branch, in a different field.

What would settle both is a reproduction that starts from the live path rather than
the harness: two bots, scripted to fight until one dies, with the server's own log as
the oracle. The bot makes that possible for the first time. It has not been written.

## An open question the bot raised and did not answer

Two bots firing at each other with 30-damage claims, 38 rounds each, moved health
from 200 to 20 and no further. That is roughly six claims applied out of
thirty-eight. Whether the rest were correctly rejected — the hit rate limiter, the
range check, the damage envelope — or wrongly dropped is **not established**, and the
number is written here rather than explained.
