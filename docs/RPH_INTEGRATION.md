# RAGE Plugin Hook integration

> English. Русский: [ru/RPH_INTEGRATION.md](ru/RPH_INTEGRATION.md).

## Status

| Capability | State |
| --- | --- |
| Detection and version reporting | **Working** |
| Publication into the mod manifest | **Working** |
| Registration points for mod authors | **Working** |
| In-process channel to an RPH-loaded plugin | **Working** |
| RPH plugin enumeration, live from RPH | **Working** |
| LSPDFR state observation (see [LSPDFR_INTEGRATION.md](LSPDFR_INTEGRATION.md)) | **Working** |
| Replicating an arbitrary RPH plugin's internal state | **Not possible** — see below |

The last row is not a roadmap entry. It is a limit of what RPH exposes, and it is
described rather than promised.

## RPH is not required

The Multiplayer Core hosts on ScriptHookVDotNet. All three configurations work:

- GTA V + Multiplayer
- GTA V + Multiplayer + RPH
- GTA V + Multiplayer + RPH + LSPDFR

`Gtamp.Adapters.Rph.dll` has **no compile-time reference to RAGE Plugin Hook** —
after Phase 7 it does not name a single `Rage` type. Its `IsAvailable` check reads
only the detected-mod record, so a client with no RPH installed loads the adapter
file harmlessly and reports it inactive.

## The technical problem, stated plainly

RPH and ScriptHookVDotNet are two separate hosts loading .NET code into the same
GTA V process. There is no supported path between them.

- RPH exposes its API to assemblies **it** loads, not to arbitrary code in the
  process.
- RPH work runs on its own `GameFiber` scheduler. Touching `Rage` types from the
  ScriptHookVDotNet script thread is unsafe even when the assembly resolves,
  because RPH's own objects assume fiber affinity.
- There is no public registry of loaded RPH plugins reachable from outside RPH.

**Layer:** host isolation inside the game process. Not the game engine, not the
network — the boundary between two .NET plugin hosts.

## Options considered

| Option | Verdict |
| --- | --- |
| Reflect into RPH internals from the SHVDN thread | Rejected. Thread-unsafe against `GameFiber`, and binds to unversioned internals that change every RPH release |
| Host the whole client under RPH instead of SHVDN | Rejected. Makes RPH mandatory, which contradicts the requirement |
| Two assemblies, one per host, sharing an in-process channel | **Chosen** |
| Out-of-process IPC (named pipe) between them | Rejected. Same process, so a shared channel is strictly simpler and lower latency |

## The design as built

```
GTA V process
├── ScriptHookVDotNet
│    └── Gtamp.Client.Shv           ← the Multiplayer Core lives here
│         ├── Gtamp.Adapters.Rph    ← core side of the channel
│         └── Gtamp.Adapters.Lspdfr ← core side, LSPDFR topics
│                 ▲
│                 │  in-process channel: bytes under a topic name
│                 │  bounded, non-blocking, no cross-scheduler calls
│                 ▼
└── RAGE Plugin Hook
     └── Gtamp.RphBridge.dll        ← an RPH plugin, runs on a GameFiber
          uses the Rage API legally, observes LSPDFR by reflection
```

### The rendezvous

`Gtamp.Shared.Interop.InProcessChannel` is the seam. Two hosts in one process may
load **different copies of the same assembly**, in which case a type from one is
not the same type to the other and any shared object is uncastable. So the channel
shares only framework types:

- `AppDomain.CurrentDomain.GetData/SetData` holding `ConcurrentQueue<byte[]>`;
- `string.Intern` on a fixed key as the one lock object both sides can name
  without agreeing on a type;
- a hand-rolled frame, `[u8 version][u16 topicLength][topic utf8][payload]`,
  because only the byte layout is guaranteed to agree.

Both queues are **bounded and non-blocking**. Neither side may block the other:
RPH work is on a `GameFiber` and core work is on the script thread, and a blocking
call from one into the other deadlocks the game. A full queue drops its oldest
message and counts it in `InProcessEndpoint.Dropped`, which costs freshness rather
than the frame.

### Fan-out

Two adapters want messages off the same channel — RPH wants the handshake and the
plugin list, LSPDFR wants the state events. `InProcessChannel.OpenCoreSide()`
returns a view onto *one* queue pair, not a private one, so two adapters each
draining it would steal each other's messages. `Gtamp.Client.Mods.BridgeLink` owns
the endpoint and fans messages out by topic; adapters subscribe and never touch
the endpoint directly. Handler exceptions are counted, not rethrown, so one broken
adapter cannot stop the messages another is waiting for.

### Topics

| Topic | Direction | Payload |
| --- | --- | --- |
| `gtamp.describe` | core → bridge | empty; "tell me everything you can see" |
| `gtamp.hello` | bridge → core | `state=running;bridge=…;rph=…;lspdfr=…`, or `state=stopped` |
| `gtamp.rph.plugins` | bridge → core | `Name\|Version;Name\|Version;…` |
| `gtamp.lspdfr.event` | bridge → core | `key=value;key=value` — only what changed |
| `gtamp.mod.payload` | either | opaque bytes belonging to a mod |

**Compromise this creates:** RPH state crosses a queue rather than being read
directly, so it is one poll interval stale by construction (50 ms on the bridge
side), and every piece of state worth replicating needs an explicit encoder on the
bridge side. The alternative was thread-unsafe reflection into internals, which
would work in a demo and fail in the field.

## What the bridge does

`src/Gtamp.RphBridge` is an ordinary RPH plugin: `[assembly: Plugin(...)]`,
`Main()` on a `GameFiber`, `Finally()` on shutdown.

1. Opens the plugin side of the channel and announces itself with its own version,
   RPH's version, and LSPDFR's version if present.
2. Every 5 seconds, enumerates the assemblies RPH has loaded and reports each
   one's `PluginAttribute` name and version. That attribute is a **public
   surface**, not an RPH internal, which is why this is safe across RPH releases.
3. Every 50 ms, polls LSPDFR through `LspdfrObserver` and publishes changes.
4. Answers `gtamp.describe` with the complete picture, so a client that connects
   later is not left with only the changes it missed.
5. On shutdown sends `state=stopped`, so the core stops reporting stale state
   instead of waiting for a timeout.

If the bridge never answers within 10 seconds, the RPH adapter says so at warning
level and names both causes: the game was not started through RPH, or the bridge
is not in RPH's `Plugins` folder.

## The limit that is not a roadmap item

**RPH gives a plugin no way to enumerate or drive another plugin's objects.**
There is no registry of another plugin's entities, no event bus, no state
interface. So there is nothing generic to read: "replicate RPH plugins" is not a
feature that can be built from outside those plugins.

What is possible, and is implemented:

- the *fact* of a plugin being loaded, with its name and version, published into
  the mod manifest so the server and other players can see it;
- LSPDFR specifically, because it publishes a documented `API.Functions` surface;
- anything a plugin chooses to send itself, over `rph.event`.

## For RPH plugin authors

Register a network event through the Mod SDK and send your own payloads:

```csharp
sdk.RegisterNetworkEvent("mymod.pursuit", (senderPlayerId, payload) => { /* apply */ });
sdk.SendNetworkEvent("mymod.pursuit", Serialize(pursuit));
```

For that event to reach the *other players* rather than only the server, its name
must be in the server's `relayedModEvents` list (see
[MOD_SDK.md](MOD_SDK.md#relayed-events)). The server forwards the bytes verbatim
without interpreting them, so a relayed event carries **no server authority** — it
is one client telling the others something. Anything that must be vouched for
needs a server-side handler or an RPC instead.

## Installing the bridge

`tools/package-client.sh` (or `.bat`) stages it into
`dist/client/RagePluginHook-plugins/`. Copy that folder's contents into
`GTA V/Plugins/`. Without it the RPH and LSPDFR adapters still load and still
report what is installed; they simply have no live state to report.

## What is not verified here

The bridge compiles against `RagePluginHook` 1.124.0 and its channel half is
covered by tests. **It has not been run inside a real GTA V process with RPH
loaded** — that requires Windows, a licensed copy of GTA V and RPH itself, none of
which exist on the machine this was built on. The parts that could be verified
were: framing, bounded queues, fan-out by topic, handler isolation, the handshake,
plugin-list parsing into the manifest, the timeout when no bridge answers, and the
end-to-end path of an LSPDFR state change reaching another player. What remains
unverified is RPH's own loader behaviour and `GameFiber` timing.
