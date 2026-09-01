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
| `callout.running` | `IsCalloutRunning()` | parameterless |
| `pullover.active` | `IsPlayerPerformingPullover()` | parameterless |
| `pursuit.active` | `GetActivePursuit()` | parameterless, returns a handle |
| `pursuit.calledIn` | `IsPursuitCalledIn(handle)` | derived from `GetActivePursuit()` |
| `pursuit.running` | `IsPursuitStillRunning(handle)` | derived from `GetActivePursuit()` |

Every name here was checked against `LSPD_First_Response.XML`, the API
documentation LSPDFR ships beside its assembly.

**That check deleted four of the original six probes.** `IsPlayerAvailable`,
`GetCurrentCallout`, `GetCurrentPullover` and `GetPlayerState` do not exist in the
documented API — they were plausible names, bound by name, and every one of them
could only ever have landed in `MissingProbes`. Nothing lied: unbound probes were
always counted and reported. But four of six slots were noise in the one list an
operator reads to find out what is genuinely unavailable, and two thirds of the
feature was never going to work. They are gone.

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

**Why polling rather than event subscription.** LSPDFR's event hooks take
delegates whose signatures use LSPDFR's own types. Those types cannot be named at
compile time (see above), and a delegate synthesised by reflection to match them
breaks silently the moment a signature changes — subscribing appears to succeed
and no event ever fires. Polling a parameterless method either returns a value or
visibly fails to bind. The cost is up to 50 ms of latency and no access to
event-only information such as the reason a pursuit ended.

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

- whether a callout is running;
- whether the player is performing a traffic stop;
- whether there is an active pursuit, whether it has been called in, and whether
  it is still running.

**Three things this list used to claim and no longer does.** Checking the probe
names against the documentation removed them rather than adding them, which is
worth stating plainly:

- **The callout's name.** `GetCalloutFriendlyName(LHandle)` exists, and its own
  documentation says it is "the friendly name representation of a callout that is
  used for LSPDFR Sync" — so it is exactly the right method. It needs a callout
  `LHandle`, and the shipped documentation contains **no parameterless way to
  obtain the handle of the callout currently running**. Handles reach a plugin
  through callout lifecycle events, which is a delegate-synthesis problem, not a
  lookup. So the name is readable in principle and not reachable by polling, and
  the framework does not pretend otherwise.
- **Who is on duty.** There is no polling method for it at all: on-duty is
  published as the events `OnOnDutyStateChanged` and
  `PlayerWentOnDutyFinishedSelection`. `IsPlayerAvailable`, which the observer
  used to ask for, does not exist. On-duty state is therefore no longer reported.
- **"The reported player state."** `GetPlayerState` does not exist. The phrase
  described a probe that could never have bound.

Absence from the documentation is not proof of absence from the assembly — it
lists only members carrying doc comments — so an undocumented parameterless
accessor may well exist. But an unverified name is a guess, and guessing is what
put four dead probes in the list in the first place.

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
