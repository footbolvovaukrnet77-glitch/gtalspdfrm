# LSPDFR integration

> English. Русский: [ru/LSPDFR_INTEGRATION.md](ru/LSPDFR_INTEGRATION.md).

## Status

| Capability | State |
| --- | --- |
| Detection and version reporting | **Working** |
| Callout plugin enumeration | **Working** |
| Registration points (state keys, network event) | **Working** |
| Live LSPDFR state of the local player, read from the process | **Working** |
| That state replicated to the other players on the server | **Working** |
| Callout scripts, suspect AI and pursuit behaviour shared between players | **Not possible** — see below |

The last row is not a roadmap entry. It is a limit of what LSPDFR exposes, and it
is described rather than promised.

## LSPDFR is not required

LSPDFR is an RPH plugin, so it inherits everything in
[RPH_INTEGRATION.md](RPH_INTEGRATION.md) and adds one problem of its own.

The adapter lives in its own assembly, loaded only when LSPDFR is detected. The
Multiplayer Core has no knowledge of it. Nothing in the core references anything
police-related — that separation is the point of the adapter model.

## Why nothing references LSPDFR at compile time

LSPDFR ships no redistributable SDK. `LSPD First Response.dll` is installed with
the mod and its licence does not permit redistributing it, so **no assembly in
this repository can reference it** — there is no package to reference, and
vendoring the DLL would be a licence violation.

**The cost, stated:** everything is bound by name at runtime, so a rename inside
LSPDFR turns into a runtime miss rather than a compile error. That is why every
missed binding is recorded and reported through `/diagnostics` instead of being
ignored or throwing.

## Where the reading actually happens

Not in `Gtamp.Adapters.Lspdfr`. LSPDFR is on RPH's `GameFiber`, so reading it from
the ScriptHookVDotNet script thread would be unsafe even if the assembly resolved.
The reading is done by `LspdfrObserver` inside `Gtamp.RphBridge.dll`, which RPH
loads, and the result crosses the in-process channel as text.

```
LSPD First Response.dll
        ▲  reflection, on RPH's GameFiber
        │
Gtamp.RphBridge.dll  ──gtamp.lspdfr.event──►  Gtamp.Adapters.Lspdfr
                          (in-process)              │
                                                    │ lspdfr.event
                                                    ▼
                                                 server (relay)
                                                    │
                                                    ▼
                                        other players' adapters
```

### What the observer polls

Public static methods on `LSPD_First_Response.Mod.API.Functions` — the documented
surface, never internals:

| Key | Bound to | Shape |
| --- | --- | --- |
| `onDuty` | `OnOnDutyStateChanged` event, or `IsPlayerAvailableForCalls()` | event when its delegate shape matches, poll otherwise |
| `onDuty.source` | — | `event` or `poll`, so the reader knows which |
| `callout.running` | `IsCalloutRunning()` | parameterless |
| `pullover.active` | `IsPlayerPerformingPullover()` | parameterless |
| `pursuit.active` | `GetActivePursuit()` | parameterless, returns a handle |
| `callout.name` | `GetCalloutFriendlyName(handle)` | derived from `GetCurrentCallout()` |
| `callout.state` | `GetCalloutAcceptanceState(handle)` | derived from `GetCurrentCallout()` |
| `pursuit.calledIn` | `IsPursuitCalledIn(handle)` | derived from `GetActivePursuit()` |
| `pursuit.running` | `IsPursuitStillRunning(handle)` | derived from `GetActivePursuit()` |

Every name here is checked against the public metadata of
`LSPD_First_Response.dll` 0.4.9695.26411 — the same surface reflection sees at
runtime, which is how this class binds anyway.

**Checking the XML documentation alone was not enough, and getting that wrong is
worth recording.** An earlier pass used only `LSPD_First_Response.XML` and
concluded that four of the six original probe names did not exist. Two of those
four were real:

| Name | XML | Assembly |
| --- | --- | --- |
| `GetPlayerState` | absent | **absent** — removed correctly |
| `IsPlayerAvailable` | absent | **absent** — removed correctly |
| `GetCurrentCallout` | absent | **present**, returns `LHandle` — removed in error |
| `GetCurrentPullover` | absent | **present**, returns `LHandle` — removed in error |

The XML lists only members carrying a doc comment. That caveat had been written
down at the time and the wrong conclusion was still drawn from it, because absence
of evidence was treated as evidence of absence. The assembly settles it.

**This is what makes the callout's name reachable.** `GetCurrentCallout()` takes no
arguments and returns the handle `GetCalloutFriendlyName` wants — and LSPDFR's own
documentation calls that method "the friendly name representation of a callout
that is used for LSPDFR Sync". So the name of the callout a player is on now
crosses the wire, not merely the fact that some callout is running.

**Handles are passed back, never opened.** `GetActivePursuit()` returns an
`LHandle`, which is opaque and stays that way — the observer reports only that
there is one, and hands it straight back to LSPDFR for the questions LSPDFR can
answer about it. That is what turns "a pursuit is happening" into "a pursuit is
happening, it has been called in, and it is still running". A null handle is
reported as `none` rather than called through, because calling through a null
handle is how a quiet moment becomes an exception on every poll.

**An ambiguous overload is left unbound.** Two single-argument overloads of one
name cannot be told apart without naming LSPDFR's types, and picking one would
hand LSPDFR the wrong argument type. The probe is reported missing instead.

Anything that fails to bind is listed in `MissingProbes` and reported, so an
LSPDFR update that renames a method produces a visible diagnostic rather than
silence.

**This is now tested.** The observer moved from the RPH bridge into
`Gtamp.Shared` — it touches no `Rage` type, only `System.Reflection` — which made
it ordinary reflection over a type, and a type is something a test can supply.
`LspdfrBindingTests` exercises binding, the handle plumbing, the null-handle case,
a throwing getter, a sparse build, an ambiguous overload and LSPDFR being absent
entirely, against stand-in types that mirror the documented signatures. What that
proves is this side of the boundary; it does not prove how the real LSPDFR
behaves.

**Polling for most of it, one event where the event is safe.** LSPDFR's event
delegates almost all name LSPDFR's own types — `LHandle`, `Ped`, `Persona` — which
cannot be named at compile time here. Binding to those would mean emitting a
matching delegate at runtime, and that fails in the worst way available:
subscribing appears to succeed and no event ever fires, mid-callout. Polling a
parameterless method either returns a value or visibly fails to bind.

**`OnOnDutyStateChanged` is the exception, and the only one.** Its delegate is
`void(bool)` — verified from the assembly as
`OnDutyStateChangedEventHandler.Invoke(Boolean)` — so it names no LSPDFR type and
binds with an ordinary `Delegate.CreateDelegate`. On duty is therefore *exact*
rather than sampled, and a change that happens and reverts inside one poll
interval is still seen. The delegate's shape is checked before binding rather than
assumed; a build whose shape differs falls back to the poll and says so, because
`onDuty.source` reports `event` or `poll`.

**The subscription is undone on shutdown.** `LspdfrObserver` is `IDisposable` and
the bridge disposes it in its exit point. A handler left attached keeps the
observer alive inside LSPDFR and goes on being called after the bridge has
stopped — the shape of a crash on plugin reload, and RPH reloads plugins.

The remaining cost is real: up to 50 ms of latency on everything except on duty,
and no access to information LSPDFR exposes only through an event *argument*, such
as the reason a pursuit ended.

## How the state travels

The observer sends **only what changed**, as `key=value;key=value`. The adapter
merges it into the local state, and forwards the same bytes to the server through
the `lspdfr.event` network event. The server relays them verbatim to every other
player — it does not parse them and has never heard of LSPDFR — and each receiving
adapter merges them into a per-player state map keyed by the sender's player id.

Two consequences worth being explicit about:

- **An unchanged value costs nothing.** A poll that finds the same pursuit still
  running produces no packet and no log line.
- **A relayed event carries no server authority.** It is one client's client
  telling the others what its own LSPDFR reported. An operator who does not want
  clients passing each other opaque bytes can empty `relayedModEvents` in
  `server.json`; the local state still works, it simply does not leave the
  machine.

## The limit that is not a roadmap item

**Callout logic cannot be shared.** `API.Functions` exposes *whether* a callout is
running — not the decisions inside it, and there is no supported way to drive
another player's LSPDFR into the same callout state. Building that would mean
binding to LSPDFR internals by name, producing an integration that works against
one LSPDFR build and breaks on the next, silently, in the middle of a callout.

So what crosses the wire is the observable facts, not the simulation:

- whether the player is available for calls;
- whether a callout is running, **its friendly name, and its acceptance state**;
- whether the player is performing a traffic stop;
- whether there is an active pursuit, whether it has been called in, and whether
  it is still running.

**One thing this list used to claim and no longer does.** `GetPlayerState` does
not exist in the assembly, so "the reported player state" described a probe that
could never have bound. `IsPlayerAvailable` does not exist either; the on-duty
line above is `IsPlayerAvailableForCalls()`, which answers the question another
officer actually cares about — can this one take a call — rather than
approximating it.

**What is still out of reach.** The decisions inside a callout: its objectives,
its dialogue, its branch state. The API exposes a name and an acceptance state,
which is a label on the simulation, not the simulation. Two officers see each
other's callout *name*; they do not share the callout's script, and no supported
API would let them.

**A note on the callout name, because it is free text.** It comes from whichever
third-party callout plugin the player installed, and the state string is
`key=value;key=value`. Separators and control characters in a name are replaced
with spaces and the name is capped at 64 characters, because every other probe
returns a bool, an enum or a handle and this is the first value that could break
the format from the outside.

**What players still see of each other's callouts.** The peds and vehicles a
callout spawns replicate normally — as peds and vehicles, through the ordinary
entity system, owned by the client that spawned them. Two officers on one server
see each other's suspects, each other's police units, and each other's pursuit
traffic. They do not share callout scripts, so the callout's own objectives,
dialogue and completion state exist only on the machine running it.

## Registration points

Declared by the adapter so mod authors can use them and so `/entity` can describe
them:

| Key | Meaning |
| --- | --- |
| `lspdfr.callout` | Identifier of the callout an entity belongs to |
| `lspdfr.role` | `suspect`, `victim`, `witness`, `officer` |
| `lspdfr.pursuit` | Identifier of the pursuit an entity is part of |
| `lspdfr.arrested` | Set when a suspect has been arrested |

These are entity `CustomData`: they travel with the entity in every delta and are
stored verbatim, even on a server that has never heard of LSPDFR. A callout script
that marks the ped it spawned as its suspect gets replication and persistence for
free.

Plus the `lspdfr.event` network event, which is the channel described above and is
also open to any mod that wants to send its own payloads under that name.

## What is not verified here

The reflection targets are named from LSPDFR's documented `API.Functions` surface.
**They have not been executed against a real LSPDFR install** — that requires
Windows, GTA V, RPH and LSPDFR, none of which exist on the machine this was built
on. What was verified: the payload format, the merge that suppresses unchanged
values, the fan-out on the channel, the relay through the server, per-player
attribution of the received state, and the operator switch that turns relaying
off. What is unverified is whether each probe binds against any given LSPDFR
release — which is exactly why unbound probes are counted and reported instead of
being assumed to work.
