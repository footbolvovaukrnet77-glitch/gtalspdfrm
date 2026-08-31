# RAGE Plugin Hook integration

## Status

| Capability | State |
| --- | --- |
| Detection and version reporting | **Working** |
| Publication into the mod manifest | **Working** |
| Registration points for mod authors | **Working** |
| RPH plugin enumeration | Phase 7 |
| RPH entity and event replication | Phase 7 |

## RPH is not required

The Multiplayer Core hosts on ScriptHookVDotNet. All three configurations work:

- GTA V + Multiplayer
- GTA V + Multiplayer + RPH
- GTA V + Multiplayer + RPH + LSPDFR

The adapter assembly has **no compile-time reference to RAGE Plugin Hook**. It
binds by reflection, and its `IsAvailable` check reads only the detected-mod
record. A client with no RPH installed loads the adapter file harmlessly and
reports it inactive.

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

## The chosen design (Phase 7)

```
GTA V process
├── ScriptHookVDotNet
│    └── Gtamp.Client.Shv          ← the Multiplayer Core lives here
│         └── Gtamp.Adapters.Rph   ← detection today; channel client in Phase 7
│                 ▲
│                 │  in-process shared channel
│                 │  (lock-free queues, no cross-scheduler calls)
│                 ▼
└── RAGE Plugin Hook
     └── Gtamp.RphBridge.dll       ← an RPH plugin, NOT WRITTEN YET
          runs on GameFiber, uses the Rage API legally
```

Each side stays on its own scheduler and only exchanges plain data across the
channel. `Gtamp.RphBridge.dll` — the RPH-loaded half — does not exist yet. That is
the whole of what "Phase 7" means here.

**Compromise this creates:** RPH state crosses a queue rather than being read
directly, so it is one frame stale by construction, and every piece of RPH state
worth replicating needs an explicit serialiser on the bridge side. The alternative
was thread-unsafe reflection into internals, which would work in a demo and fail
in the field.

## What the adapter does today

On a client where RPH is installed:

1. reports RPH's version, and whether RPH is actually *hosting* this process (the
   game can be launched without it even when it is installed);
2. publishes RPH's presence into the mod manifest, so the server and other
   players can see it;
3. registers the `rph.event` network event and the `rph.plugin` state key, so a
   mod author can already move their own state between clients;
4. logs, at warning level, that state replication is not implemented.

That last point is the important one. It reports honestly instead of no-op'ing,
which would look like it worked.

## For RPH plugin authors, today

You do not have to wait for Phase 7 to replicate your own state. Register a
network event through the Mod SDK and send your own payloads:

```csharp
sdk.RegisterNetworkEvent("mymod.pursuit", (sender, payload) => { /* apply */ });
sdk.SendNetworkEvent("mymod.pursuit", Serialize(pursuit));
```

That path is complete and tested today.
