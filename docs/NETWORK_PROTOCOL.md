# Network protocol

Transport: UDP. Byte order: little-endian. Protocol version: **2**
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
u16      sequence        this packet's id
u16      ack             highest packet id received from the peer
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

The request is retried every 500 ms, up to 20 attempts.

**The handshake is idempotent.** The accept is the one packet with no reliability
layer behind it — it is sent before the session exists. If it is lost, the client
retries, and the server must answer that retry rather than routing it into the
half-open session the first request created. A repeated request carrying the same
identity token and nonce gets the byte-identical accept resent. Without this a
client whose accept was dropped is stranded until it gives up; see
`SessionTests.ALostAcceptDoesNotStrandTheClient`.

The nonce also stops a late accept from an abandoned attempt being adopted as a
live session.

## Message types

| Id | Name | Delivery | Direction |
| --- | --- | --- | --- |
| `0x01` | ConnectRequest | connectionless | C → S |
| `0x02` | ConnectAccept | connectionless | S → C |
| `0x03` | ConnectReject | connectionless | S → C |
| `0x04` | Disconnect | reliable | both |
| `0x05` | KeepAlive | unreliable | both |
| `0x10` | Ping | unreliable | C → S |
| `0x11` | Pong | unreliable | S → C |
| `0x20` | ClientStateUpdate | unreliable | C → S |
| `0x21` | Snapshot | unreliable | S → C |
| `0x22` | SnapshotAck | unreliable | C → S |
| `0x23` | ResyncRequest | reliable | C → S |
| `0x30` | EntityEvent | reliable | S → C |
| `0x31` | ServerEvent | reliable | S → C |
| `0x32` | ChatMessage | reliable | both |
| `0x40` | ModManifest | reliable | both |
| `0x41` | ModCompatibilityReport | reliable | S → C |
| `0x50` | AdminCommand | reliable | C → S |
| `0x51` | SecurityNotice | reliable | S → C |
| `0xF0`–`0xFF` | reserved for the Mod SDK | either | both |

## Snapshots

```
varuint  snapshotId          non-zero, increasing per client
varuint  baselineId          0 means "full state"
varuint  tick
f64      serverTime
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

## Quantisation

| Quantity | Encoding | Range | Worst-case error |
| --- | --- | --- | --- |
| Position axis | zig-zag varint of `round(value × 512)` | ±16384 m (XY), ±2048 m (Z) | 0.98 mm |
| Velocity axis | zig-zag varint of `round(value × 128)` | ±256 m/s | 3.9 mm/s |
| Angle | `u16` of `degrees × 65536/360` | 0–360° | 0.0027° |
| Unit scalar | `u8` | 0–1 | 0.2% |

Out-of-range values are **clamped, not wrapped** — a corrupt coordinate produces a
position at the world edge, which is caught by the validator, rather than one that
silently appears somewhere plausible.

The position bound is asserted over 5,000 random samples in
`SerializationTests.QuantizedPositionStaysWithinTheDocumentedErrorBound`.

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
