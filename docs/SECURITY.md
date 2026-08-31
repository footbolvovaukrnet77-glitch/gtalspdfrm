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
