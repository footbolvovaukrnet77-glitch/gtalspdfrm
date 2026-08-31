# Entity system

## Goal

Adding a new kind of networked thing — by the framework or by a mod — must not
require touching the networking layer. That is the requirement, and the test that
proves it is `ModSdkTests.AModDefinedEntityReplicatesThroughTheOrdinarySnapshotPath`:
a turret entity defined entirely in the test project replicates full state and
deltas through the ordinary snapshot path, with no change anywhere else.

## Base state

Every entity, built-in or mod-defined, carries:

| Field | Purpose |
| --- | --- |
| `Id` | Server-assigned, never reused |
| `Type` | Wire type id; ≥128 is the mod range |
| `OwnerId` | Player simulating it; 0 = server |
| `Position`, `Velocity`, `Heading` | Transform |
| `Dimension` | Instance separation; different dimensions never see each other |
| `InteriorId` | GTA V interior, 0 outdoors |
| `NetworkVersion` | Incremented on every accepted mutation |
| `LastUpdateTick` | Server tick of the last mutation |
| `CustomData` | Free-form string map owned by mods |

`CustomData` is deliberately `string → string`. A server that has never heard of a
mod can still store, replicate and persist that mod's per-entity state verbatim.
That is what lets a mod's world survive a restart on a vanilla server build.

### Entity ids are never reused

An id identifies one entity for the lifetime of the server, and
`ReserveEntityIdsUpTo` restores the allocator past the highest persisted id on
startup. A stale reference therefore resolves to "gone", never to a different
entity that happens to have inherited the number — a class of bug that is
extremely hard to diagnose from the symptoms.
`PersistenceTests.EntityIdsAreNeverReusedAfterARestart` covers it.

## Declaring a type

Fields are declared, not hand-serialised. Each declaration supplies a change test,
a writer and a reader:

```csharp
public sealed class TurretSerializer : EntitySerializer<TurretEntity>
{
    public TurretSerializer() : base(typeId: 128, typeName: "mod.turret") { }

    public override NetEntity Create(EntityId id) => new TurretEntity(id);

    protected override void DeclareFields(EntityFieldSet<TurretEntity> fields)
    {
        fields
            .Add("Ammo",
                 (a, b) => a.Ammo != b.Ammo,
                 (w, e) => w.WriteVarInt(e.Ammo),
                 (r, e) => e.Ammo = r.ReadVarInt())
            .Add("Deployed",
                 (a, b) => a.Deployed != b.Deployed,
                 (w, e) => w.WriteBool(e.Deployed),
                 (r, e) => e.Deployed = r.ReadBool());
    }
}
```

The base fields above are added automatically and always occupy the same bit
indices across every type, which is what keeps the entity inspector generic.

- **Full state** writes every field in declaration order.
- **Delta** writes a varint bitmask followed by only the changed fields.
- **Unchanged** entities produce a one-byte zero mask and are skipped entirely by
  the snapshot writer.

The limit is 64 replicated fields per type. Past that, group state into
`CustomData` or a separate entity — a single 64-field entity is a design smell,
and an unbounded mask would cost bytes on every delta.

### Change tests, not equality

Each field decides for itself what "changed" means. `Heading` uses a 0.0001°
epsilon, weather transition uses 0.004 (one quantisation step). This matters: a
float that differs only below its own wire precision would otherwise be
retransmitted forever, burning budget on a difference the receiver cannot
represent.

## Type ids

| Range | Owner |
| --- | --- |
| 0 | Unknown/invalid |
| 1–127 | Built-in: Player, Vehicle, Ped, Object, Weapon, Projectile, Pickup, Door, Mission, Marker |
| 128–255 | Mod-defined, via `IModSdk.RegisterEntity` |

`ModSdk.RegisterEntity` refuses an id inside the built-in range
(`ModSdkTests.ModEntityIdsMustStayInTheReservedRange`) and refuses any
registration after the network layer has started — a type table that changes
mid-session would desynchronise every client that already negotiated it.

## Schema hash

`EntityRegistry.ComputeSchemaHash` is an FNV-1a over the sorted type table:
every type id, type name and field name. Client and server exchange it in the
handshake.

A mismatch means the two sides disagree about field layouts, and it is reported as
a mod incompatibility at join time — not discovered later as a corrupt snapshot,
which is where an undetected layout disagreement otherwise surfaces.

## Implemented today

**`PlayerEntity`** is complete for Phase 1 and 2: player id, name, model, health,
max health, armour, movement flags (crouch, sprint, jump, fall, swim, dive, climb,
ragdoll, dead, aim, shoot, reload, melee, in-vehicle, entering-vehicle, parachute,
cover, invincible), locomotion state, current weapon, ammo, aim position, vehicle
and seat, wanted level, animation hash, dimension and interior.

The remaining items from master prompt section 9 — clothing components, props,
full inventory, scenario tasks — are **not present**. They are scheduled in
[ROADMAP.md](ROADMAP.md) rather than stubbed to empty values, because a field that
replicates nothing is worse than an absent one: it looks supported.

`VehicleEntity`, `PedEntity` and `ObjectEntity` are Phase 3. Their type ids are
already reserved so adding them does not renumber anything.
