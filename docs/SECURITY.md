# Security

## What is defensible and what is not

The server cannot see inside the client process. It cannot detect memory editing,
injected DLLs or a modified client binary, and any client-side check runs on
hardware the attacker controls.

So the framework validates **effects, not causes**. Whatever a cheat does, its
result arrives as a packet, and a packet claiming something impossible is
detectable regardless of how it was produced.

This is true of every game with a client. It is stated here rather than implied by
a long feature list.

## Two layers

### Protocol guards — always on, even at `Off`

These are not cheat detection. They are what stops a malformed or hostile packet
writing garbage into the authoritative world.

| Guard | Behaviour |
| --- | --- |
| Magic and session id | Wrong values → dropped before parsing |
| Bounds-checked decoding | Every read is checked; a truncated packet throws and the packet is dropped |
| Length limits | Strings, arrays, mod counts and dependency lists all capped |
| Finiteness | NaN or infinity in any float → update rejected |
| World bounds | Position outside ±16 km / ±2 km → update rejected |
| Packet rate | More than `maxUpdatesPerSecond` state updates in a second → throttled |
| Reliable buffer cap | 512 out-of-order messages max, so a withheld sequence cannot exhaust memory |

`AntiCheatTests.ProtocolGuardsStayOnEvenWithAntiCheatOff` asserts the first two of
these still fire at `Off`.

### Behavioural validation — level-gated

| Level | Behaviour |
| --- | --- |
| `Off` | Protocol guards only. A player can teleport freely |
| `Basic` | Violations logged |
| `Standard` (default) | Violations warned; movement, health and armour checked |
| `Strict` | Violations kick; adds god-mode detection |
| `Custom` | Per-violation actions from configuration |

Checks: speed, teleport, health maximum, health regeneration rate, armour cap,
god-mode flag, packet rate.

Actions: `Ignore`, `Log`, `Warn`, `Kick`, `Ban`. Escalation to kick or ban only
happens once a player has exceeded `violationsBeforeEscalation` (default 20), so a
single false positive never removes anyone.

## The movement budget

Movement is checked against a replenishing budget, not per update.

```
capacity  = speedLimit × burstSeconds        (default 1.5 s)
budget   += speedLimit × timeSinceLastUpdate,  clamped to capacity
distance  = |proposed − authoritative|

distance > teleportDistance   → Teleport violation
distance > budget             → SpeedHack violation
otherwise                     → budget -= distance
```

**Why not a per-update check.** The obvious implementation compares distance
against `speed × timeSinceLastUpdate`. On a jittery link two updates routinely
arrive in the same millisecond, which makes that denominator ~0 while the distance
is normal — so an honest player is flagged, their update is rejected, and because
the server's position no longer advances, *every subsequent update fails too*. The
player is wedged in place by their own network conditions.

A budget bounds sustained speed just as tightly while tolerating bursty arrival.
`AntiCheatTests.BunchedUpdatesAfterJitterAreNotMistakenForSpeedHacking` and
`SustainedImpossibleSpeedIsRejected` are the two halves of that requirement.

## Rejection is the enforcement

A rejected update is discarded. The server's own state stands, and the next
snapshot carries it to the client, which snaps to it if the drift exceeds
`correctionThreshold`.

That loop is what makes server authority real. Without the client-side correction
half, the server would "reject" a movement while the player kept walking in a world
that no longer agreed with them.

## Server-initiated moves and the authority hold

When the server moves a player itself — placing them at their persisted position
on join, or respawning them at a hospital — the client is still reporting where it
thinks it is. Those reports are in flight and describe a world that no longer
exists.

Accepting them drags the player straight back out of the position the server just
chose. Rejecting them trips the teleport check, and because the server's position
then never advances, *every* subsequent update fails: the player is wedged.

So neither. The server records the snapshot id that carries the move and ignores
that client's state updates until one arrives acknowledging it:

```
server moves the player   → PendingAuthorityHold
next snapshot allocated   → AuthorityHoldSnapshot = that id
client update arrives     → its own acknowledged id < hold ? ignore : release
```

Two details that are not optional:

- The test is against the id carried by **that update**, not the session's
  high-water mark. Snapshot acknowledgements also travel in their own unreliable
  message, so a standalone acknowledgement can overtake a state update sent
  before it; releasing on the high-water mark lets that older update through.
- The window between the move and the next snapshot has no id to acknowledge yet,
  so anything arriving in it is held unconditionally.

The hold expires after ten seconds. A client whose acknowledgement is lost
entirely would otherwise be unable to move at all, which is worse than accepting
a stale update and correcting from there.

Death is arbitrated the same way: while the server considers a player dead their
reported health is ignored outright, so a trainer's heal key cannot cut the
respawn timer short.

## Health authority

Server-arbitrated damage needs its own, narrower hold.

When the server resolves a hit, the victim's client is still reporting the health
it had before. Accepting that undoes the damage — the shot lands and then
un-lands, once per snapshot, for as long as the firefight lasts. A full authority
hold would fix it and would also freeze the victim's movement for a round trip
every time they were hit, which is worse than the bug.

So health and armour alone are held until the victim's client acknowledges the
snapshot carrying the change. Position keeps flowing throughout.

`DamageReplicationTests.SustainedDamageIsNotUndoneByTheVictimsOwnReports` fires
four 30-point hits at a 200-health player over two seconds at 60 ms latency and
asserts they end on 80.

## Damage arbitration

A hit arrives as a **claim**. The attacking client is the only party that knows it
fired, because the server cannot raycast — it has no map.

What the server does check:

| Check | Behaviour |
| --- | --- |
| Target exists and is damageable | Rejected otherwise |
| Attacker is alive, target is not already dead | Rejected otherwise |
| Self-harm | Rejected |
| PvP / NPC damage / vehicle damage rules | Rejected when the server has them off |
| Range for the reported weapon | Rejected beyond it; melee is held to arm's length |
| Damage ceiling for that weapon | **Clamped**, not rejected |
| Weapon actually held | Rejected only at Strict |

Two of those deserve their reasons stated.

**Damage is clamped rather than rejected.** A legitimate headshot or explosive can
exceed a weapon's base figure. More importantly, dropping an over-claimed hit
entirely would let a cheat *deny* damage simply by claiming too much of it.

**Weapon matching is off by default.** The shot and the attacker's last state
update are two different packets. A player who switches weapons right after firing
produces a mismatch legitimately, so enforcing it costs honest players hits. It
belongs at Strict, where the operator has accepted that trade.

**What is not checked: line of sight.** A client claiming a hit through a wall,
within range and with the right weapon, is accepted. This is the same limitation
as everywhere else in the framework — the server has no map geometry — and it is
stated rather than implied.

## Owned entities

A client that owns a vehicle proposes its state; it does not decide it. Every
update is decoded into a **fresh instance** and validated before it replaces the
live entity: decoding straight into the entity and then rejecting would leave it
half-written with attacker-chosen values, which is worse than the update the
check was meant to stop.

Checks: ownership, finite and in-bounds position, health within engine limits,
occupant count, and the same replenishing movement budget used for players, with
one speed limit covering every vehicle. Distinguishing a jet from a bicycle would
need a model table the server does not have, and getting it wrong grounds honest
pilots.

## Trust boundaries

| Data | Trusted? |
| --- | --- |
| Client position, velocity, health, armour | **No** — validated |
| Client-claimed player name | **No** — sanitised to a safe character set and length |
| Client mod manifest | **No** — advisory only; used for compatibility reporting |
| Client identity token | **Partially** — proves continuity, not identity (see below) |
| Server world state | Yes — it is the source of truth |

### The identity token is not authentication

It is a GUID in a text file. Anyone who copies it becomes that player. It solves
*continuity* — "give me my character back" — not *identity*.

Real authentication (challenge/response against a stored secret, and a ban list
keyed to something harder to forge) is Phase 10 and is not pretended to exist now.

## Administration

Roles are `Player`, `Moderator`, `Admin`, persisted per identity token. Developer
console commands are gated behind developer mode and refused otherwise
(`ConsoleTests.DeveloperCommandsAreRefusedUntilDeveloperModeIsOn`).

The server console is unauthenticated by design — it is stdin of the server
process. Anyone who can reach it already has the machine.

## Not implemented, and not claimed to be

- Authentication beyond the identity token — Phase 10
- A ban list — Phase 10
- File integrity checking of the client — see the opening section; not defensible
- Encryption of the session — Phase 12; today the protocol is plaintext UDP, so
  anyone on the path can read and forge packets. Run trusted servers, or tunnel
  through a VPN, until then.
