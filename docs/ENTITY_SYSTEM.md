# Entity system

> English. Русский: [ru/ENTITY_SYSTEM.md](ru/ENTITY_SYSTEM.md).

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

Clothing components and props were added in Phase 2. `PedAppearance` carries the
twelve component slots (drawable, texture, palette) and eight prop slots, encoded
behind presence masks so a default character costs three bytes rather than the 46
a fixed layout would need. It is one replicated field written whole, because
appearance changes at spawn and when a player changes clothes — not per frame.

### Every posture flag, and what actually applies it

Eighteen flags are sampled from the local player and replicated. That is not the
same as eighteen flags being *used*, and the difference is exactly the kind of gap
that hides for a whole project: the state travels, stores and prints correctly and
never reaches the ped. So the whole set is listed, applied or not.

| Flag | Applied by | Notes |
| --- | --- | --- |
| `Dead` | `RemotePedController` → `DriveDead` | Placed, not tasked; the game's death animation owns it |
| `Ragdoll` | `DriveRagdoll` + `RagdollDriver` | Head and both feet corrected with impulses |
| `InVehicle` | `SeatRemotePedInVehicle`, once per seat change | Placed at the reported position only when the local game has no such vehicle to sit in |
| `Aiming` | `TASK_AIM_GUN_AT_COORD` | Real aim target from the shooter's camera |
| `Shooting` | `WeaponShot` relay | The rounds themselves, counted from the clip |
| `Reloading` | `TASK_RELOAD_WEAPON`, once per reload | Re-issuing it every frame makes the ped fumble the magazine forever |
| `Crouching` | `SET_PED_STEALTH_MOVEMENT` | GTA V has no ped crouch; what a player sees as crouching is stealth movement |
| `Sprinting` | — | Superseded by `MovementState`, which the gait selection already uses. The flag is kept because a mod may want it |
| `Jumping`, `Climbing` | **not applied** | Both are transitions a second or two long. By the time one arrives the player has finished it, and `TASK_JUMP` on a ped that is being walked to a coordinate fights the task it is already running |
| `Falling` | **not applied** | The local ped falls on its own when there is nothing under it. Forcing it would double up with the game's own physics |
| `Swimming`, `Diving` | **not applied** | A ped tasked into water swims by itself. What is missing is the *dive*, which needs the underwater task set and is Phase 9 work |
| `Melee` | **not applied** | A melee task needs a target entity, and the target is not replicated — only the flag. Issuing it without one makes the ped swing at the air in a random direction |
| `InCover` | **not applied** | Cover is a position in the world, not a state of the ped. `TASK_STAY_IN_COVER` needs a cover point the receiving client would have to find for itself, and a wrong guess pins the ped to the wrong wall |
| `Parachuting` | **not applied** | Needs the parachute prop, its own task set and a canopy state machine. Scheduled with the wider animation work |
| `EnteringVehicle` | **not applied** | A ped is seated outright; the entry animation is not played, so a player appears in a car rather than climbing into it |
| `Invincible` | server-side only | Read by the anti-cheat as a god-mode signal. Never applied to a remote ped — a remote ped is already invincible, because its health comes from the server |

Six applied, one superseded, one server-side, ten not applied. The ten are a real
gap and are stated as one; each is here because applying it badly is visibly worse
than not applying it, not because it was overlooked.

### The ragdoll pose

A ragdoll flag says a body is falling. It does not say where the body ends up, and
that turns out to be a different question: physics solvers are not deterministic
across machines, so two copies handed the same starting state diverge within a few
frames. A player who lands face-down against a wall on their own screen lands on
their back in the road on everyone else's. Replicating the root position hides
this rather than fixing it — the root keeps up while everything attached to it is
somewhere else.

`RagdollPose` carries three bone positions — head, right foot, left foot — as
offsets from the character's root, and only while the ragdoll flag is set.
`RagdollDriver` turns them into Euphoria impulses proportional to the error
between the reported position and the local one.

Four decisions worth stating, because each one costs something:

- **Three bones, not the skeleton.** A ped has over eighty bones; all of them would
  cost more per falling player than the rest of this protocol combined. Head and
  feet pin both ends of the body, which is what a root position cannot express. The
  price is that arms, spine and head rotation are not replicated at all — they are
  whatever the local solver produced.
- **Impulses, not placement.** Writing bone positions into a running solver is what
  makes replicated ragdolls twitch: the solver re-derives them from its own
  constraints on the next step. An impulse leaves it in charge. The price is that
  the pose is an approach, not a state — a limb pinned under geometry cannot be
  pulled through it, and a client with a long RTT sees a body that lags and settles
  rather than one that matches frame for frame.
- **Offsets, not world positions.** Cheaper on the wire (±8 m at 7.8 mm instead of
  ±16 km at 2 mm), and correct when the receiving side's root has been interpolated
  or corrected. A world bone position paired with a root that has moved describes a
  body pulled apart.
- **A settle delay and a give-up distance.** The first ten frames of a fall are left
  to the local solver, because the pose in hand describes a body a round trip old
  and pulling on limbs mid-impact matches neither machine. Past eight metres of root
  error the two copies are no longer describing the same fall, and the ped is placed
  outright — one visible teleport, against a body that would otherwise stay in the
  wrong street until it stood up.

The technique is the one [RAGECOOP-V](https://github.com/RAGECOOP/RAGECOOP-V) (MIT)
arrived at. The implementation, the constants and the two safeguards above are
ours; no code was copied. Like everything else in this layer it is unit-tested for
the decision and **not visually verified in the game**.

The remaining items from master prompt section 9 — full inventory, scenario
tasks — are **not present**. They are scheduled in
[ROADMAP.md](ROADMAP.md) rather than stubbed to empty values, because a field that
replicates nothing is worse than an absent one: it looks supported.

**`CharacterEntity`** carries everything a player and a networked NPC have in
common — body, weapon, vehicle seat, appearance — declared once in
`CharacterFields`. Two hand-maintained copies of fifteen field declarations would
drift, and a drift between them is a silent decode corruption rather than a
compile error.

**`VehicleEntity`** carries 27 replicated fields. Several are deliberately
grouped: the six paint indices as one `Colors` field, livery and wheel type as
`Styling`, plate text and type as `Plate`, RPM and gear as `Drivetrain`. They are
set together in practice, and each separate field would cost a mask bit and its
own presence on every delta that touched any of them. The nineteen vehicle
booleans are one `VehicleFlags` bitfield for the same reason — three bytes as a
varint instead of nineteen fields.

Doors and tyres pack two states per index into one 16-bit word: bits 0-7 open or
burst, bits 8-15 broken or punctured.

**`PedEntity`** adds behaviour flags, alert level, task and scenario hashes, a
combat target, a relationship group and a free-form group id, so a mod can keep a
set of peds together without inventing its own registry.

**Its behaviour fields are replicated and not applied.** `Behaviour`, `AlertLevel`,
`TaskHash`, `ScenarioHash`, `CombatTargetId`, `RelationshipGroupHash` and `GroupId`
cross the wire and nothing in the game layer reads them. That is not an oversight
being hidden: an NPC's *decisions* belong to the server, and the server has no AI
that produces them, so there is nothing to apply yet. They are listed here rather
than left looking supported — a field that describes nothing is worse than an
absent one, which is the same rule the reserved entity ids follow.

`RemoteEntityManager` creates and drives a ped for each one, through the same
`RemotePedController` a player's ped uses — the gait selection, the correction
thresholds, the ragdoll pose and the death handling are the same problem, and a
second copy of that logic would be a second set of bugs. One difference, and it is
deliberate: an NPC whose model is missing is **substituted**, because an NPC is
scenery with a role and a crowd with a hole in it is worse than a crowd wearing
the wrong jacket. A player is never substituted — you have to be able to
recognise who you are looking at.

Until this existed the type was registered, serialised, delta encoded, persisted,
replicated and accepted by the damage arbiter, and no client created anything for
it. A spawned NPC appeared in the world state and in every diagnostic and was
invisible in the game.

**`ObjectEntity`** adds model, rotation, health, flags and attachment to another
entity.
