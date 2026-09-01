# Network protocol

> English. Русский: [ru/NETWORK_PROTOCOL.md](ru/NETWORK_PROTOCOL.md).

Transport: UDP. Byte order: little-endian. Protocol version: **5**
(`ProtocolConstants.ProtocolVersion`); a mismatch is rejected during the
handshake with a readable message.

## Packet framing

Every datagram starts with the magic `0x504D5447` (`"GTMP"`) and a kind byte.
The magic is a cheap filter so stray traffic is discarded before parsing.

### Connectionless (kind = 0)

Used before a session exists, and only for the handshake.

```
u32      magic
u8       kind = 0
u8       messageType
varuint  length
bytes    payload
```

### Session (kind = 1)

```
u32      magic
u8       kind = 1
u32      sessionId
u16      sequence        this packet's id; 1..65535, never 0
u16      ack             highest packet id received from the peer, or 0 for none
u32      ackBits         bitfield: bit i acknowledges (ack - 1 - i)
message* until end of datagram
```

Message framing inside a packet:

```
u8       flags           bit0 = reliable
u16      reliableSeq     present only when reliable
u8       messageType
varuint  length
bytes    payload
```

`MaxPacketSize` is 1200 bytes — small enough to survive the smallest MTU on the
public internet without fragmentation. A single message may carry at most
`MaxPacketSize - 32` bytes; anything larger is rejected at the send call rather
than silently truncated, and higher layers are expected to split their own
payloads (the snapshot encoder does).

## Reliability

Acknowledgement is **packet-level**. Each sent packet records which reliable
message ids it carried; when that packet is acknowledged, every reliable message
inside it is retired at once. An unacknowledged packet re-queues exactly its own
messages.

- **Retransmission timeout** uses the Jacobson/Karels estimator — the same one
  TCP uses — clamped to [50 ms, 1 s].
- **Ordered delivery**: the receiver buffers out-of-order reliable messages and
  releases them in sequence. The buffer is capped at 512 entries so a hostile
  peer cannot exhaust memory by withholding one sequence number.
- **Duplicate suppression**: a reliable message whose sequence is below the
  delivery watermark is dropped. Retransmissions that cross their own
  acknowledgement are therefore free.
- **Loss accounting**: a packet that falls out of the 32-packet acknowledgement
  window without being acknowledged is counted lost exactly once. This is the
  figure the network debugger shows.

### Packet sequence 0 is reserved

A peer's remote sequence starts at zero, so its very first packet has nothing to
acknowledge. Sending `ack = 0` in that packet, with 0 also being a valid packet
id, means the other side reads it as an acknowledgement of its own first packet.

That is not cosmetic. The acknowledged packet's reliable messages are retired
from the retransmission list and never sent again; because delivery is ordered,
every later reliable message then queues behind a sequence number that will never
arrive, and the channel wedges — silently, permanently, for the life of the
connection. The shape that triggers it is completely ordinary: the server queues
a join notification the instant it accepts a client, before the client's peer
exists to receive it, and the client's first state update carries `ack = 0` back
before the retransmission timer fires.

So packet ids start at 1 and skip 0 on wraparound, and `ack = 0` means "I have not
received anything from you yet" and acknowledges nothing. The cost is one ack bit
every 65536 packets, which shows up as a single spurious retransmission.

Regression test:
`ReliabilityTests.AReliableMessageSentBeforeThePeerExistsIsStillDelivered`.

### Acknowledgement delay

An acknowledgement waits up to `NetPeer.MaxAckDelay` (20 ms) for outbound traffic
to piggyback on. Both ends normally send several times per second, so
acknowledgements ride along for free; the delay only produces a standalone packet
when a peer is otherwise silent.

Without this, a peer with nothing to send holds acknowledgements until the
one-second keep-alive, which inflates the measured RTT by an order of magnitude
and delays every retransmission decision. That was a real bug, caught by
`ReliabilityTests.RoundTripTimeIsEstimatedFromAcknowledgements`.

## Handshake

```
CLIENT                                          SERVER
  │  ConnectRequest (connectionless)              │
  │  ─ protocol version, client version           │
  │  ─ player name, identity token, password      │
  │  ─ client nonce                               │
  │  ─ mod manifest + entity schema hash          │
  │ ────────────────────────────────────────────► │
  │                                               │ validate version
  │                                               │ validate name, password
  │                                               │ compare mod manifests
  │                                               │ restore or create the player
  │  ConnectAccept (connectionless)               │
  │  ─ session id, player id, player entity id    │
  │  ─ echoed nonce, tick/snapshot rates          │
  │  ─ server time, per-mod compatibility report  │
  │ ◄──────────────────────────────────────────── │
  │                                               │
  │  ═════ session packets from here on ═════     │
  │  ClientStateUpdate ──────────────────────────►│  30 Hz, unreliable
  │ ◄────────────────────────────────── Snapshot  │  20 Hz, unreliable
```

When the server requires authentication — the default — two more legs sit in the
middle:

```
  │  ConnectRequest ────────────────────────────► │
  │                                               │ ban check, then a random nonce
  │                                               │ and a fresh ECDH P-256 keypair
  │ ◄──────────── ConnectChallenge                │
  │  ─ serverNonce, serverName                    │
  │  ─ server ephemeral key (64 bytes)            │
  │                                               │
  │  sign(clientNonce ‖ serverNonce               │
  │       ‖ serverName ‖ both ephemerals)         │
  │  ConnectProof ──────────────────────────────► │ verify against the claimed key
  │  ─ identity public key, signature             │ then ECDH → shared secret
  │  ─ client ephemeral key (64 bytes)            │
  │ ◄──────────── ConnectAccept                   │
```

**Both ephemeral public keys are inside the signed bytes.** The one signature
that proves the client owns its identity key therefore also binds the key
exchange: an attacker who swaps either ephemeral key invalidates the signature
rather than quietly ending up in the middle of the session. That is why the
challenge carries the server's ephemeral key rather than sending it later — the
client has to hold it before it can sign.

The request is retried every 500 ms, up to 32 attempts — raised from 20 because
four connectionless legs need two round trips through a lossy link where two
needed one.

**Every leg is idempotent, and it has to be.** A lost challenge is answered by
re-issuing the *identical* nonce for the same client nonce: a fresh one would
invalidate a proof already in flight and tell a legitimate player their key is
wrong. A lost proof is resent by the client's retry timer — which resends the
proof rather than restarting the handshake, so a challenge that did arrive is not
thrown away. A lost accept is resent when the proof arrives again, which is why
the proof path can answer with a stored accept as well as the request path can.

**The handshake is idempotent.** The accept is the one packet with no reliability
layer behind it — it is sent before the session exists. If it is lost, the client
retries, and the server must answer that retry rather than routing it into the
half-open session the first request created. A repeated request carrying the same
identity token and nonce gets the byte-identical accept resent. Without this a
client whose accept was dropped is stranded until it gives up; see
`SessionTests.ALostAcceptDoesNotStrandTheClient`.

The nonce also stops a late accept from an abandoned attempt being adopted as a
live session.

## Session encryption

Once the proof verifies, both sides hold the same ECDH shared secret, and the
session is encrypted from the first session packet onward. `encryptSessions` in
`server.json` is on by default; turning it off gives a plaintext session, and the
server says so in its log rather than degrading quietly.

The construction is **encrypt-then-MAC**: AES-256-CBC for confidentiality,
HMAC-SHA256 truncated to 16 bytes for authenticity, over independent keys.

```
                    ECDH P-256 shared secret
                               │
       ┌──────────┬────────────┼────────────┬──────────┐
   c2s cipher  c2s MAC    s2c cipher    s2c MAC     IV key
```

Five keys, each `HMAC-SHA256(secret, label ‖ counter)` under a distinct
label/counter pair (`gtamp-c2s-v1`, `gtamp-s2c-v1`, `gtamp-iv-v1`). Separate
directions mean a captured client packet cannot be replayed back at the client,
and a separate MAC key means the cipher key is never also used as a MAC key. The
first draft of `Derive` computed the same HMAC twice and would have made the
cipher key equal the MAC key — a mistake worth naming, because nothing observable
would have failed.

An encrypted session packet keeps its header in the clear and seals everything
after it:

```
u32      magic            ┐
u8       kind = 1         │
u32      sessionId        ├─ cleartext header — authenticated, not encrypted
u16      sequence         │
u16      ack              │
u32      ackBits          ┘
bytes    ciphertext         AES-256-CBC over the whole message block
16 bytes tag                HMAC-SHA256(macKey, header ‖ ciphertext)[0..16]
```

**The header stays readable on purpose.** The reliability layer needs the
sequence and acknowledgement fields to route a packet before an IV can be
derived, and the demultiplexer needs the session id to know which keys to try.
Readable is not unprotected: those bytes are inside the MAC, so rewriting a
sequence number invalidates the packet instead of redirecting a captured one
elsewhere in the stream.

**The IV is derived, not transmitted.** It is one AES-ECB block over the
direction byte and the packet sequence under the IV key. That saves 16 bytes on
every packet and cannot be desynchronised by loss, because the receiver already
has the sequence from the header. Per-packet overhead is therefore 16 bytes of
tag plus CBC padding, and the `MaxPacketSize` accounting subtracts it *before* a
message is admitted into a packet — so enabling encryption never pushes a packet
that fit the MTU over it.

**A packet that fails authentication is dropped whole** and counted, never
partially parsed; that is the entire point of carrying a MAC. Decryption returns
false rather than throwing, because a forged or corrupt packet is an ordinary
event on a public network, not an exceptional one.

### What this protects, and what it does not

| Protected | Not protected |
| --- | --- |
| Message contents against a passive eavesdropper | Traffic analysis — packet sizes and timing stay visible |
| Tampering with any byte, header fields included | The connectionless handshake legs, plaintext by construction |
| A man in the middle without the client's identity private key | The server operator, who legitimately holds the session keys |
| Replay of a packet into the opposite direction | Anything on a machine already compromised locally |

Forward secrecy comes from the ephemeral keypairs: generated per connection and
discarded with the session, so recovering an identity private key later does not
decrypt a recorded session.

`EncryptionTests` proves this rather than asserting it. It plants a canary string
in a chat message, watches the virtual wire for those exact bytes, and requires
them to be absent — paired with a control test that turns encryption off and
requires the same canary to be *findable*. Without the control, a test that never
saw the wire at all would pass for the wrong reason.

**Platform limit, stated plainly.** `ECDiffieHellman` does not exist in
`netstandard2.0`. `Gtamp.Shared` multi-targets `netstandard2.0;net48;net8.0`
because of it, and the `netstandard2.0` build of `EphemeralKeyExchange` throws
`PlatformNotSupportedException` instead of falling back to something weaker. A
host that can only load the `netstandard2.0` assembly cannot open an encrypted
session at all — it fails loudly rather than silently running in the clear. The
shipped client and server both run on `net48`, so this path is not reached in
practice.

## Message types

| Id | Name | Delivery | Direction |
| --- | --- | --- | --- |
| `0x01` | ConnectRequest | connectionless | C → S |
| `0x02` | ConnectAccept | connectionless | S → C |
| `0x03` | ConnectReject | connectionless | S → C |
| `0x04` | Disconnect | reliable | both |
| `0x05` | KeepAlive | unreliable | both |
| `0x06` | Fragment | reliable | both |
| `0x07` | ConnectChallenge | connectionless | S → C |
| `0x08` | ConnectProof | connectionless | C → S |
| `0x10` | Ping | unreliable | C → S |
| `0x11` | Pong | unreliable | S → C |
| `0x20` | ClientStateUpdate | unreliable | C → S |
| `0x21` | Snapshot | unreliable | S → C |
| `0x22` | SnapshotAck | unreliable | C → S |
| `0x23` | ResyncRequest | reliable | C → S |
| `0x24` | EntitySpawnRequest | reliable | C → S |
| `0x25` | OwnedEntityUpdate | unreliable | C → S |
| `0x26` | EntityReleaseRequest | reliable | C → S |
| `0x27` | DamageReport | reliable | C → S |
| `0x28` | ModRpcRequest | reliable | both |
| `0x29` | ModRpcResponse | reliable | both |
| `0x2A` | ModEvent | either | both |
| `0x30` | EntityEvent | reliable | S → C |
| `0x31` | ServerEvent | reliable | S → C |
| `0x32` | ChatMessage | reliable | both |
| `0x33` | WeaponShot | unreliable | both |
| `0x40` | ModManifest | **reserved, never sent** | — |
| `0x41` | ModCompatibilityReport | **reserved, never sent** | — |
| `0x50` | AdminCommand | reliable | C → S |
| `0x51` | SecurityNotice | reliable | S → C |
| `0xF0`–`0xFF` | reserved for the Mod SDK | either | both |

**Two ids in that table are reserved and neither is ever sent.** Mod negotiation
happens inside the handshake, because it has to be settled before a client is
admitted rather than after: the manifest rides in `ConnectRequest` and the
compatibility report rides in `ConnectAccept`. The ids are kept because a wire
format reserves ahead of use and renumbering later to close a gap breaks every
client already speaking it — and `NetMessageTypeTests` enforces the classification,
so an id that starts being sent fails the build until the table is corrected.

### ModEvent (`0x2A`)

```
string   name            routed by name, not by a registration-order id
varuint  senderPlayerId  server → client only; 0 means the server itself
bytes    payload         opaque; neither side interprets it
```

Routing by name rather than by an id assigned in registration order is
deliberate: two clients that load their mods in a different order would otherwise
route each other's events to the wrong handler, and it removes the ceiling on how
many events a mod may register.

`senderPlayerId` exists because the server can forward a client's event to the
other players (see [MOD_SDK.md](MOD_SDK.md#relayed-events)). A relayed event that
lost its origin would be useless for anything per-player. On the client-to-server
leg the field is ignored — the sender is the session the packet arrived on, and a
client-supplied value would be worth nothing.

## Snapshots

```
varuint  snapshotId          non-zero, increasing per client
varuint  baselineId          0 means "full state"
varuint  tick
f64      serverTime
varuint  ackClientUpdate     newest client update this snapshot accounts for
u8       flags               bit0 = environment block present
[environment]
varuint  removedCount
varuint  removedEntityId *
varuint  entityCount
  varuint  entityId
  u8       entryFlags        bit0 = full state
  [u8      typeId]           present only for full state
  fields                     full: every field; delta: varuint mask + changed fields
```

Snapshot ids are **per client**, not global: each client acknowledges its own
stream and the server encodes against whatever that client last confirmed.

### Why baseline views instead of one current world

A delta must be decoded against exactly the state it was written against. While
an acknowledgement is in flight the server keeps encoding against an older
baseline, so the client cannot simply apply deltas to a single mutable world —
the older baseline has to still exist.

Both sides therefore keep a short history of immutable `EntitySnapshotView`s
(64 by default), and every delta names the view it was written against. Applying
a delta against the wrong baseline is **rejected**, not attempted:
`SnapshotCodec.Apply` throws and the client requests a resync.

Views share entity references for everything that did not change, so a 64-deep
history costs one dictionary per snapshot rather than 64 deep copies
(`SnapshotTests.BaselineViewsShareUnchangedEntityInstances`).

### Byte budget

Each snapshot has a hard byte budget (`snapshotByteBudget`, default 1024). The
encoder walks entities in priority order and writes what fits. Anything that does
not fit is **deferred to the next snapshot**, never dropped — its baseline state
simply carries forward on the client.

This is the mechanism that lets replication be optimised without the server world
state ever being reduced. `StressTests.DistanceNeverRemovesAnEntityFromTheServerWorld`
scatters 100 entities across the whole map, far outside any streaming range of the
only connected player, and asserts the client converges on all of them.

### Why the snapshot echoes a client update sequence

Every `ClientStateUpdate` carries an incrementing `updateSequence`, and each
snapshot echoes the newest one the server had processed when it wrote that
snapshot.

Without it the client cannot tell two situations apart, and they need opposite
responses:

| The snapshot disagrees with what I reported because… | Correct response |
| --- | --- |
| the server **rejected** it (anti-cheat, a respawn, an authority hold) | snap to the server |
| the server **had not seen it yet** when it wrote this snapshot | do nothing |

Treating the second as the first is a self-inflicted rubber-band, and a
particularly nasty one: the client snaps back to a value the server has *already
accepted*, then reports the reverted value, and the change is lost for good. The
echo makes the distinction exact — the client keeps a short history of what it
reported, indexed by sequence, and judges each snapshot against the report that
snapshot actually answers.

Regression test:
`CorrectionTests.AnAcceptedChangeIsNotUndoneByASnapshotThatPredatesIt`.

## Orientation

Orientation travels as three angles — pitch, roll and heading — because that is
what the game hands over and what it takes back, and the round trip is exact. The
wire format was never the problem.

Interpolating those three *independently* was. Pitch, roll and yaw are not
independent axes: blending each on its own passes through orientations on no path
between the two ends, so a car rolling onto its roof swings its nose through the
turn on the way, and an entity pitched near vertical loses an axis outright —
gimbal lock arriving in the interpolator rather than in the format.

`NetQuaternion.LerpEuler` converts both ends to quaternions, spherically
interpolates and converts back. `FromEuler` and `ToEuler` are exact inverses by
construction, which is the property that matters: every sample endpoint is
bit-identical to what the game reported, and any disagreement with the engine's
own axis order is confined to the frames strictly between two samples.

Peds keep the cheaper single-angle blend. A ped has one axis that matters and no
pitch or roll to couple it to.

## Gunshots

A shot is an event, not a state, so it does not travel on the snapshot. The
shooting flag on `PlayerFlags` says a weapon is being fired; it cannot say how
often, and a rifle at 600 rounds a minute holds it for six frames per round at 60
fps. Rounds are counted from the clip instead — it falls by exactly one per shot —
and each one is sent as its own `WeaponShot`.

Unreliable in both directions, deliberately: a muzzle flash retransmitted after
its bullet has already been arbitrated is worse than a missing one.

Three things the server does not take on trust:

| Claim | What the server does |
| --- | --- |
| Who fired | Overwritten from the session. A client that names its own shooter can name somebody else. |
| Where the muzzle was | Dropped if further than `GameServer.MaxMuzzleOffset` (10 m) from the shooter's own position. |
| How often | A token bucket, `ShotBudget`: 80 rounds/second sustained, 20 burst. Over-budget shots are dropped and **not** counted as a violation — the ceiling is only a factor of two above a minigun, and counting there would eventually kick a player for owning one. |

Relay is distance-filtered to `GameServer.ShotRelayRange` (250 m), measured from
the shooter's *server* position. This filters replication only: no entity leaves
the world at any distance, and `WeaponShotTests.AShotFromTheOtherSideOfTheMapIsNotRelayed`
asserts both — no shot drawn, both players still in the world.

**The relayed bullet carries no damage and never will.** The hit is arbitrated
from `DamageReport` against the server's own world; a rendered bullet that also
wounded would count one trigger pull once per client that drew it. The receiving
client fires it with damage 0.

**Projectiles are not echoed.** A rocket or a grenade is an entity that flies, and
drawing it as an instant line from muzzle to impact would show every player an
explosion arriving at the speed of light. Weapon groups `Thrown` and `Heavy` are
excluded — which also excludes the railgun, a hitscan weapon in the same group as
the rocket launcher. Drawing a rocket wrongly is the worse error.

## Quantisation

| Quantity | Encoding | Range | Worst-case error |
| --- | --- | --- | --- |
| Position axis | zig-zag varint of `round(value × 512)` | ±16384 m (XY), ±2048 m (Z) | 0.98 mm |
| Velocity axis | zig-zag varint of `round(value × 128)` | ±256 m/s | 3.9 mm/s |
| Angle | `u16` of `degrees × 65536/360` | 0–360° | 0.0027° |
| Unit scalar | `u8` | 0–1 | 0.2% |
| Bone offset axis | zig-zag varint of `round(value × 128)` | ±8 m | 3.9 mm |

Out-of-range values are **clamped, not wrapped** — a corrupt coordinate produces a
position at the world edge, which is caught by the validator, rather than one that
silently appears somewhere plausible.

The position bound is asserted over 5,000 random samples in
`SerializationTests.QuantizedPositionStaysWithinTheDocumentedErrorBound`.

## Fragmentation

A reliable message larger than the per-message budget is split into fragments,
each an ordinary reliable message:

```
u16 groupId | u8 index | u8 count | u8 innerType | chunk
```

Ordering and retransmission come free from the reliable channel, so reassembly
needs no acknowledgement scheme of its own. The receiver buffers fragments until
the set is complete, then delivers the reassembled message under its inner type.

Limits, both of them memory bounds: 256 KB per message and 8 concurrent fragment
sets. A peer that opens more sets than it finishes has its oldest dropped.

**Unreliable fragmentation is refused, not offered.** Losing any one fragment
loses the whole message, so the effective loss rate is multiplied by the fragment
count — a 5-fragment message on a 10% link arrives 59% of the time. Anything big
enough to need splitting is worth sending reliably, and the send call says so
rather than letting the caller discover it in production.

## Owner state streams

The client that owns an entity reports it at the client update rate. The payload
is written by the entity type's own serializer, so a mod-defined type streams with
no protocol change.

Updates are **delta-compressed against a snapshot the client has applied**, named
by id. That baseline is one the server sent and still holds in that client's
history, so both sides name the same starting point.

This is what makes delta compression safe on an unreliable channel. A "delta
against whatever I sent last" scheme silently desynchronises the moment one update
is dropped — the receiver applies the next delta to the wrong base and has no way
to know. Here a lost update costs one frame of freshness, and a baseline that has
aged out of history is simply ignored, with the client rebasing on its next
applied snapshot.

## Adaptive bandwidth

Each client's snapshot budget moves with what its link is carrying: cut to 75% on
loss above 8%, crept back up by 10% of the maximum per clean second, floored at a
configurable minimum. Additive increase, multiplicative decrease — the same shape
TCP uses, and for the same reason.

It changes only how much is sent per snapshot. It never changes what the server
keeps and never drops an entity permanently: a smaller budget defers more entities
to later snapshots, so a congested client converges more slowly and still
converges.

## Interpolation timeline

Remote players are rendered a fixed delay behind the client's **estimate** of the
server clock, not behind the last snapshot's timestamp.

The estimate advances with every frame and is nudged towards the authoritative
value when a snapshot arrives — gradually for small differences, snapped for large
ones (a stall, or a fresh connection). Correcting hard on every snapshot would make
the render timeline jump with network jitter, which shows up as remote players
twitching.

Driving the timeline straight from the last snapshot instead would make it a 20 Hz
staircase, so a remote ped would step once per snapshot however fast the game
renders — which defeats interpolating at all.
`SessionTests.RemotePlayersAreInterpolatedAtFrameRateNotAtSnapshotRate` measures
this directly: 16 distinct rendered positions per second with the staircase, over
40 with the estimated clock.

`InterpolationDelay` (default 120 ms, roughly two snapshot intervals plus a jitter
margin) trades responsiveness against smoothness. Lower it and remote players
stutter; raise it and they lag further behind their real position.

## Resync

A client requests a resync when it cannot decode a delta:

- the named baseline has aged out of its history (a long stall);
- a delta arrived for an entity absent from the baseline;
- the payload names an entity type this build has no serializer for.

The server clears that client's replication state and sends a full snapshot. The
session is not dropped — a decode failure is recoverable, and treating it as fatal
would turn a hiccup into a disconnect.
