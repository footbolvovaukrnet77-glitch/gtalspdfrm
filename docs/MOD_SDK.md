# Mod SDK

> English. Русский: [ru/MOD_SDK.md](ru/MOD_SDK.md).

## What it is for

A mod should be able to describe its own entities, state and events, and have them
replicate through the same machinery the framework uses for players — without
patching the framework.

## The section-21 mapping

The master prompt lists fifteen `Register*` names. Here is what each one actually
is today. As of Phase 9 all of them are implemented; nothing in this table is a
stub that silently does nothing, and nothing throws a "not yet" any more.

| Prompt name | Status | Notes |
| --- | --- | --- |
| `RegisterEntity()` | **Implemented** | Registers a networked entity type; returns its wire id. The overload taking a mod name records who owns the type, so `entity <id>` and every bug report can name the mod behind an entity instead of leaving the reader to guess |
| `RegisterSerializer()` | **Implemented** | Alias of `RegisterEntity` |
| `RegisterVehicle()` | **Implemented** | `RegisterEntity` with a readable name at the call site |
| `RegisterPed()` | **Implemented** | as above |
| `RegisterObject()` | **Implemented** | as above |
| `RegisterComponent()` | **Implemented as `RegisterState`** | Components are string-keyed state on an entity |
| `RegisterState()` | **Implemented** | Declares a `CustomData` key so `/entity` can describe it |
| `RegisterNetworkEvent()` | **Implemented** | Routed by name over `ModEvent` (`0x2A`); see [Relayed events](#relayed-events) for reaching other clients |
| `RegisterOwner()` | **Implemented as `NetEntity.OwnerId`** | Ownership is a field, not a registration |
| `RegisterDimension()` | **Implemented** | Allocates a dimension id so two mods cannot collide |
| `RegisterInterior()` | **Implemented** | Names an interior id so it survives a restart |
| `RegisterRPC()` | **Implemented** | Registers a procedure the other side can call and get an answer from |
| `RegisterMission()` | **Implemented** | Registers the local half of an activity: blips, markers, UI |
| `RegisterCustomWeapon()` | **Implemented** | Names a weapon locally; the envelope it is arbitrated against is a server-side registration — see [Custom weapons](#custom-weapons) |
| `RegisterDeserializer()` | **Not separate** | A serializer declares both directions |

Where a member was not yet implemented it threw with a phase number rather than
no-op'ing, and that was deliberate: a registration that appears to succeed and
then never fires is far harder to debug than a failure at load time naming the
reason. The same rule still governs anything added later.

## Defining an entity

```csharp
public sealed class TurretEntity : NetEntity
{
    public TurretEntity(EntityId id) : base(id, (EntityType)128) { }

    public int Ammo { get; set; }
    public bool Deployed { get; set; }

    public override NetEntity Clone()
    {
        var clone = new TurretEntity(Id) { Ammo = Ammo, Deployed = Deployed };
        CopyBaseTo(clone);        // never forget this: it copies transform and CustomData
        return clone;
    }
}
```

Register it during adapter initialisation, before any connection:

```csharp
public void Initialize(IModSdk sdk, ModEnvironment environment)
{
    sdk.RegisterMod(new ModDescriptor
    {
        Id = "my-mod",
        Name = "My Mod",
        Version = "1.0.0",
        Requirement = ModNetworkRequirement.Optional,
    });

    // Naming the mod is optional and worth doing: without it an entity of this
    // type is reported as unattributed, and a bug report from a player running six
    // mods says nothing about which one put it there.
    sdk.RegisterEntity(new TurretSerializer(), "TurretMod");
}
```

From that point a `TurretEntity` replicates exactly like a player: full state on
first sight, delta afterwards, priority-ordered, budget-aware, persisted as an
opaque blob.

## Network events

```csharp
sdk.RegisterNetworkEvent("turret.fire", (senderPlayerId, payload) =>
{
    // runs on the client tick thread
});

sdk.SendNetworkEvent("turret.fire", payload, reliable: true);
```

Events are routed **by name**, not by an id assigned in registration order.

That distinction matters. Assigning ids in registration order means the two sides
only agree while they register in the same order — and a mod that adds one event
on the client silently renumbers every later event on that side alone, so
`turret.fire` starts arriving at the handler for `turret.reload`. Names cost a few
bytes per event and remove both that coupling and the sixteen-event ceiling.

Names are case-insensitive, so two mods cannot disagree over capitalisation.
Sending an unregistered event throws rather than silently dropping.

### Where a client's event goes

By default, **to the server and no further**. `SendNetworkEvent` from a client
reaches a server-side handler registered with
`IServerModSdk.RegisterNetworkEvent`, and stops there. That is the right default:
the server can validate, rate-limit, or act on the event with its own authority.

### Relayed events

A server that knows nothing about your mod has no handler to register, and
without one two clients running the same mod cannot talk at all. For that case
the server can forward an event verbatim:

```csharp
// server side
sdk.RegisterRelay("mymod.pursuit");
```

or, for an operator with no server-side mod, by name in `server.json`:

```json
"relayedModEvents": [ "lspdfr.event", "rph.event" ]
```

The server does not parse the payload and cannot validate it, so **a relayed
event carries no authority**. It is one client telling the others something, with
the server acting as the postbox. The receiving handler gets the origin player's
id as `senderPlayerId`, and `0` when the event came from the server itself.

Anything a mod needs the server to vouch for — a score, a spawn, a permission —
must go through a server-side handler or an RPC instead. An operator who does not
want clients passing each other opaque bytes empties `relayedModEvents`; mods that
rely on relaying then stop crossing between clients, which is the intended effect
and not a fault.

## Remote procedure calls

```csharp
// Server side
serverSdk.RegisterRpc("bank.balance", (session, payload) => Encode(BalanceOf(session)));

// Client side
sdk.CallServerRpc("bank.balance", request, result =>
{
    if (!result.Success)
    {
        // result.Error says why: no handler, the handler threw, a timeout,
        // or the connection dropped
        return;
    }

    Show(Decode(result.Payload));
});
```

It works in both directions: a client registers with `RegisterRPC` and the server
calls it with `CallClientRpc`.

Calls **always complete** — with an answer, with the remote handler's error, or
with a timeout. A call that can hang forever is a mod bug that presents as a
frozen game, so the timeout is not optional, and every outstanding call is failed
immediately when the connection drops rather than being left to time out.

Handlers are synchronous by design. Both sides run mod code on a single thread —
the game's script thread and the server's tick thread — so an asynchronous handler
would need a scheduler that does not exist, and would invite mods to do slow work
in the middle of a frame.

## Activities

An activity is a mission, callout, job, race or anything else with objectives and
participants. **It is an entity.** That is the whole design: it replicates,
persists and appears in the entity inspector through exactly the same machinery as
a vehicle, with no mission-specific networking anywhere.

Server side declares and runs it:

```csharp
serverSdk.RegisterActivity(
    new ActivityDefinition("traffic-stop", "Traffic stop") { TimeLimitSeconds = 600 }
        .WithObjective(1, "Pull the vehicle over")
        .WithObjective(2, "Speak to the driver")
        .WithObjective(3, "Resolve the stop"));

ActivityEntity? stop = serverSdk.Activities.Start("traffic-stop", playerId, now);
serverSdk.Activities.SetObjectiveState(stop.Id, 1, ObjectiveState.Completed, now);
```

Objectives advance one at a time; the activity finishes when they are all
resolved, when its time limit expires, or when the mod says so. Anything still
outstanding is marked skipped, so a client rendering the objective list never
shows a live objective on a finished activity.

Client side reacts:

```csharp
sdk.RegisterMission("traffic-stop", new MyCalloutHandler());
```

The handler is driven by **diffing replicated state**, not by a stream of events.
A client that missed a snapshot still ends up in the right place, and one that
joins mid-activity is told about it as if it had just started. A handler that
throws is logged and contained; it cannot take the session down.

Entities attached to an activity are destroyed with it, so a mod does not have to
track its own suspects and props to clean them up.

## Custom state

```csharp
sdk.RegisterState("lspdfr.callout", "the callout this entity belongs to");
sdk.SetState(entity, "lspdfr.callout", "traffic-stop");
```

Writing an undeclared key throws. The declaration is what lets `/entity` describe
the value and what stops two mods quietly fighting over the same key.

State written this way is replicated with the entity's next delta and persisted
with it, including on a server that has never heard of the mod.

## Custom weapons

Combat is arbitrated on the server, so a weapon has two halves and only one of
them is authoritative.

**Server side — the half that decides.**

```csharp
serverSdk.RegisterWeapon(new WeaponProfile("WEAPON_MYMOD_RAILGUN", maxDamagePerHit: 400, maxRange: 1000f));
```

or, for an operator with no server-side mod, in `server.json`:

```json
"customWeapons": [
  { "name": "WEAPON_MYMOD_RAILGUN", "maxDamagePerHit": 400, "maxRange": 1000, "melee": false }
]
```

These are **validation ceilings, not damage values**. The game decides what a hit
actually does; the profile bounds what a client is allowed to claim.

**Client side — the half that names it.**

```csharp
sdk.RegisterCustomWeapon("WEAPON_MYMOD_RAILGUN", new WeaponProfile("WEAPON_MYMOD_RAILGUN", 400, 1000f));
```

This maps the joaat hash back to the name so `/entity`, the console and bug
reports read `WEAPON_MYMOD_RAILGUN` instead of `0x3F2A91C4`. It grants no
envelope, and it cannot: a client that could declare its own weapon's damage
ceiling could declare any ceiling it liked.
`ContentNegotiationTests.AClientCannotWidenTheEnvelopeItsOwnHitsAreCheckedAgainst`
pins that.

**Registering neither is not fatal.** An unprofiled weapon falls back to
`defaultMaxDamagePerHit` (250) and `defaultMaxRange` (400 m), which is permissive
rather than blocking — a modded weapon works out of the box. What it loses is
accuracy in both directions: a modded taser is allowed to claim 250 damage, and a
modded long-range rifle has its legitimate hits rejected past 400 m.

## Mod content the other player does not have

Models travel as hashes and resolve against locally installed assets, so a player
without your vehicle mod has nothing to create. That case is reported rather than
left silent:

| Entity | What the other client does |
| --- | --- |
| Vehicle, object | Nothing is shown. A different car is worse than an absence, because it looks correct |
| Player | A default body is used, and the record is marked `substituted` |

Each unresolvable hash is recorded once — creation is retried every frame — and
appears in `/diagnostics`, `/mods` and the bug report. See
[ENGINE_ANALYSIS.md](ENGINE_ANALYSIS.md) §4.4.

Nothing here installs anything. The framework cannot ship assets and does not
pretend to; what it does is make "your friend's car is invisible" a line of text
instead of a mystery.

## Mod requirement levels

| Level | Meaning | Effect when a peer lacks it |
| --- | --- | --- |
| `ClientOnly` | Purely local (textures, graphics) | Never compared |
| `Optional` | Replicates but degrades cleanly | Reported, never blocks |
| `Required` | Both sides need it or entities will not decode | Blocks the connection when `enforceRequiredMods` is on |

Only `Required` can refuse a join. Missing an optional mod produces a report entry
the player can see in `/diagnostics`, and the session continues — the master
prompt is explicit that a missing mod must not take the world down.

## Writing an adapter

```csharp
public sealed class MyAdapter : IModAdapter
{
    public string Id => "my-mod";
    public string DisplayName => "My Mod";

    // MUST only read the environment record. See the rule below.
    public bool IsAvailable(ModEnvironment e) => e.Mods.Exists(m => m.Id == "my-mod");

    public void Initialize(IModSdk sdk, ModEnvironment environment) { }
    public void Update(double now) { }
    public void Shutdown() { }
    public string DescribeStatus() => "ok";
}
```

Build it as `Gtamp.Adapters.<Name>.dll` and drop it in `GTA V/Gtamp/Adapters/`.

### The one hard rule

**The constructor and `IsAvailable` must not touch any type from the mod being
adapted.**

Adapter assemblies are loaded before the mod's presence is confirmed. On .NET
Framework, referencing a missing assembly only fails when a method that uses it is
JIT-compiled — so as long as those two members stay clean, an adapter for a mod
you do not have loads harmlessly and reports itself inactive.

This is what makes "RAGE Plugin Hook is optional" a fact rather than a claim.

### Failure containment

- An adapter that throws during `Initialize` is logged and skipped; the others
  still load.
- An adapter that throws during `Update` is shut down and removed; the client
  keeps running.

Both are covered by tests. A third-party adapter cannot take the session down.
