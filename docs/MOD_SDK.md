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
| `RegisterRPC()` | **Phase 6** | Throws. Use `RegisterNetworkEvent` for now |
| `RegisterMission()` | **Phase 6** | Throws; needs the activity system |
| `RegisterCustomWeapon()` | **Phase 9** | Throws; needs the combat system |
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
byte id = sdk.RegisterNetworkEvent("turret.fire", (senderPlayerId, payload) =>
{
    // runs on the client tick thread
});

sdk.SendNetworkEvent("turret.fire", payload, reliable: true);
```

Ids come from `0xF0`–`0xFF` — sixteen per session. Sending an unregistered event
throws rather than silently dropping.

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
