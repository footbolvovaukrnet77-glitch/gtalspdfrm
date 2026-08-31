# Security

> English. Русский: [ru/SECURITY.md](ru/SECURITY.md).

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

### Identity is a keypair, and the private half never moves

Each installation holds an ECDSA P-256 keypair, generated on first run and stored
in `client.ini`. The **public** half is the identity: it is what the server knows
the player by, what bans are keyed to, and what persistence stores a character
against. The **private** half signs a per-connection challenge and is never
transmitted, so nothing an eavesdropper can capture lets them become that player.

```
CLIENT                                        SERVER
  ConnectRequest (identity = public key) ────►
                                              validate version, name, password, mods
                                              check the ban list
  ◄──── ConnectChallenge (random nonce)
  sign(clientNonce ‖ serverNonce ‖ serverName)
  ConnectProof (public key, signature) ──────►
                                              verify against the claimed key
  ◄──── ConnectAccept
```

The nonces and the server name are all inside the signed bytes: the client nonce
binds the proof to one connect attempt, the server nonce makes it unreplayable
against a later one, and the server name stops a proof captured on one server
being replayed to another.

**P-256 rather than Ed25519**, which would be the better curve, because the client
runs on .NET Framework 4.8 inside GTA V's CLR and Ed25519 is not available there.

**What this does not solve.** First contact is trust-on-first-use: the first time
a public key is seen it is simply enrolled, so an active attacker who is on the
path at that moment can register a key of their own — as a *new* player, with a
new character. They cannot take over an existing identity, which requires a
private key that has never been on the wire.

**Losing the secret loses the character**, exactly as losing the old identity
token did. An unreadable `IdentitySecret` produces a new identity *and a warning
saying so*, rather than a silent fresh start that looks like server-side data
loss.

`RequireAuthentication` in `server.json` is on by default. Turning it off accepts
any identity string a client claims — the pre-Phase-10 behaviour — and is the
operator's call to make for a private server among people who already trust each
other.

## Session encryption

Every packet after the handshake is encrypted and authenticated.

**Key agreement.** Each connection generates a fresh ECDH P-256 keypair on both
sides. The two ephemeral public keys are placed inside the bytes the identity key
already signs during the challenge, so the one signature that proves who the
client is also binds the key exchange to them — unauthenticated ECDH agrees a key
with whoever is on the other end, which on a public network is whoever got there
first.

Ephemeral rather than derived from the identity keys, so that obtaining a player's
private key lets an attacker impersonate them from then on but **not** decrypt
sessions recorded before. That is forward secrecy, and it costs one key generation
per join.

**AES-CBC with HMAC-SHA256, encrypt-then-MAC — not AES-GCM.** GCM would be the
obvious choice and does not exist on .NET Framework 4.8, which is what the client
runs on inside GTA V's CLR. Five keys are derived from the one shared secret:
cipher and MAC keys per direction, plus an IV key. Sharing a key between
encryption and authentication, or between directions, is the classic way to turn a
sound construction into an unsound one.

**The header stays readable and is authenticated.** The receiver needs the session
id to find the peer and the sequence number to derive the IV before it can decrypt
anything, so those cannot be encrypted. The MAC covers them, so rewriting a
sequence number invalidates the packet rather than redirecting it.

**The IV is derived, not transmitted:** one AES block over the direction byte and
the packet sequence, the same construction CTR mode uses for its keystream blocks.
At a 1200-byte MTU, twenty snapshots a second and thirty-two players, the sixteen
bytes an explicit IV would cost per packet is real bandwidth. The tag is truncated
to 16 bytes — the same 128 bits GCM offers by default.

**Verified as encrypted, not reported as encrypted.**
`EncryptionTests.ASessionIsActuallyEncryptedOnTheWire` watches every datagram on
the virtual wire and asserts a known plaintext never appears in it, with
`WithEncryptionOffTheSameTrafficIsReadable` as the control — without that control a
canary that never appears would prove nothing.

**What it still does not protect.** The connectionless handshake packets
themselves: the connect request, the challenge and the accept are readable, because
there is no key until they have been exchanged. A passive observer therefore learns
that a client joined, its name and its public identity. Everything after that is
opaque.

`encryptSessions` in `server.json` is on by default and depends on
`requireAuthentication`: without a signed challenge there is nothing to bind the
key exchange to. Turning it off returns to plaintext UDP and exists for a LAN, or
for someone debugging with a packet capture.

## Bans

Keyed by identity public key. Not by name, which the player chooses and changes in
a text file, and not by address, which is shared by everyone behind one router and
changed by reconnecting a home line — banning either is a combination of trivially
evaded and hitting people who did nothing.

An entry carries a reason, who issued it, and an optional expiry; expiry is
applied on lookup rather than by a sweep, because a timed ban that outlives its
window for want of a sweep is the same bug as one that never expires. Bans are
written to the database synchronously, unlike every other write here, because a
ban issued moments before a crash is exactly the one that must survive it.

**What a ban does not stop:** generating a fresh keypair and coming back as a new
player. Nothing available to a server with no account system can stop that, and
this does not pretend otherwise. What it buys is that evading costs the evader
everything they had — the new identity is a new character with nothing in it.

## Administration

Roles are `Player`, `Moderator`, `Admin`, persisted per identity.

| Role | May |
| --- | --- |
| `Player` | nothing over the network |
| `Moderator` | inspect, announce, kick, ban, move/kill/respawn players |
| `Admin` | all of that, plus the world clock and weather, saving, roles and shutdown |

A moderator cannot change roles, including their own: a moderator who can promote
themselves is an admin with extra steps.

Admin commands arrive over the network as `AdminCommand` (`0x50`) and are answered
with `SecurityNotice` (`0x51`). **Authorisation happens on the server and only
there** — the client sends a string and is told what happened, so a modified
client gains nothing by pretending to be an admin. The in-game `admin <command>`
is deliberately *not* gated behind developer mode: developer mode is a local
switch that gates nothing an attacker could not flip, and the only gate that means
anything is the server's.

Permissions are declared per command, and **a command with no entry needs the
highest permission**. A table where forgetting to add a command makes it public is
worse than no table at all, because it reads as though it is protecting something.

The stdin console runs the same command table with no authorisation, by design —
it is the server process's own input. Anyone who can reach it already has the
machine.

## Not implemented, and not claimed to be

- **Accounts.** Identity is a key on a machine, not a login. There is no password
  reset, no "same character on two computers" without copying the secret, and no
  central registry.
- **Protection against the first-contact substitution** described above. Solving
  it needs either session encryption or an out-of-band way to publish keys.
- **File integrity checking of the client** — see the opening section; not
  defensible.
- **Encryption of the handshake itself.** The connect request, challenge and
  accept are connectionless and precede any key, so they are readable. A passive
  observer learns that somebody joined, under what name, with what public
  identity — and nothing they said afterwards.
- **Replay protection beyond the packet level.** A captured packet only decrypts
  at the sequence it was sent from, and the reliability layer already suppresses
  duplicates, but there is no separate anti-replay window.
