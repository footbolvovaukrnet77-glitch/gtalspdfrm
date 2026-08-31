# Mod SDK

## What it is for

A mod should be able to describe its own entities, state and events, and have them
replicate through the same machinery the framework uses for players — without
patching the framework.

## The section-21 mapping

The master prompt lists fifteen `Register*` names. Here is what each one actually
is today. Nothing in this table is a stub that silently does nothing: the three
that are not implemented throw `NotSupportedException` naming the phase they land
in.

| Prompt name | Status | Notes |
| --- | --- | --- |
| `RegisterEntity()` | **Implemented** | Registers a networked entity type; returns its wire id |
| `RegisterSerializer()` | **Implemented** | Alias of `RegisterEntity` |
| `RegisterVehicle()` | **Implemented** | `RegisterEntity` with a readable name at the call site |
| `RegisterPed()` | **Implemented** | as above |
| `RegisterObject()` | **Implemented** | as above |
| `RegisterComponent()` | **Implemented as `RegisterState`** | Components are string-keyed state on an entity |
| `RegisterState()` | **Implemented** | Declares a `CustomData` key so `/entity` can describe it |
| `RegisterNetworkEvent()` | **Implemented** | Allocates a message id in the `0xF0`–`0xFF` range |
| `RegisterOwner()` | **Implemented as `NetEntity.OwnerId`** | Ownership is a field, not a registration |
| `RegisterDimension()` | **Implemented** | Allocates a dimension id so two mods cannot collide |
| `RegisterInterior()` | **Implemented** | Names an interior id so it survives a restart |
| `RegisterRPC()` | **Implemented** | Registers a procedure the other side can call and get an answer from |
| `RegisterMission()` | **Implemented** | Registers the local half of an activity: blips, markers, UI |
| `RegisterCustomWeapon()` | **Phase 9** | Throws; needs the wider weapon work |
| `RegisterDeserializer()` | **Not separate** | A serializer declares both directions |

Throwing rather than no-op'ing is deliberate. A registration that appears to
succeed and then never fires is far harder to debug than a failure at load time
that names the reason.

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

    sdk.RegisterEntity(new TurretSerializer());
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
