using System.Collections.Generic;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Core
{
    /// <summary>
    /// Everything the multiplayer core needs from GTA V, and nothing else.
    /// <para>
    /// This interface is the single seam between engine-independent logic and the
    /// ScriptHookVDotNet layer. Keeping it narrow is what makes the client testable
    /// on a machine with no game installed, and it is also what would let a second
    /// host (a ScriptHookVDotNetCore build, or an RPH-hosted build) reuse the whole
    /// client without touching the networking code.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether a model hash can be turned into an entity on this client right now.
    /// <para>
    /// The three states are not interchangeable. <see cref="Loading"/> is normal and
    /// resolves on a later frame; <see cref="Unavailable"/> never will, because the
    /// asset is not installed. Collapsing them into one "not yet" is how a missing
    /// mod becomes an entity that silently never appears.
    /// </para>
    /// </summary>
    public enum ModelAvailability : byte
    {
        /// <summary>The hash names no model this client has. A mod is missing.</summary>
        Unavailable = 0,

        /// <summary>Known, streaming in. Retry on a later frame.</summary>
        Loading = 1,

        Available = 2,
    }

    public interface IGameBridge
    {
        /// <summary>Game build string, e.g. "1.0.3095.0". Reported in bug reports.</summary>
        string GameVersion { get; }

        /// <summary>
        /// Whether this client can resolve a model hash. Called before creating a
        /// replicated entity so an asset the player does not have is reported once
        /// rather than retried forever.
        /// </summary>
        ModelAvailability GetModelAvailability(uint modelHash);

        /// <summary>True once the player is in a controllable state (not loading, not in a cutscene).</summary>
        bool IsPlayerReady { get; }

        LocalPlayerSample SampleLocalPlayer();

        /// <summary>
        /// Applies a server correction to the local player. The server only ever
        /// corrects; it does not drive normal local movement.
        /// </summary>
        void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor);

        /// <summary>
        /// Sets the local player's wanted level, because the server decided it: a
        /// restored save, an admin command, a mod. Not called for a wanted level the
        /// client itself reported — the local game owns that one.
        /// </summary>
        void SetLocalWantedLevel(int level);

        /// <summary>
        /// Changes the local player's own model, because the server decided it: a
        /// restored save, an admin command, a mod handing out a skin.
        /// <para>
        /// Returns false when it cannot be done this frame rather than forcing it: the
        /// model may still be streaming in, and the game builds a new ped for the
        /// player, which it will not do sanely while they are in a vehicle or dead. The
        /// caller retries, and gives up loudly rather than silently.
        /// </para>
        /// </summary>
        bool TrySetLocalPlayerModel(uint modelHash);

        /// <summary>
        /// Sets the local player's maximum health to the server's value.
        /// <para>
        /// The client reads its maximum but never reports it: the ceiling is the
        /// server's to decide, and a client that named its own would be naming the
        /// number the anti-cheat measures it against. So it travels one way, and a
        /// client whose game disagrees — a mod raising it is common in an LSPDFR
        /// install — is brought into line rather than flagged for it.
        /// </para>
        /// </summary>
        void SetLocalMaxHealth(int maxHealth);

        /// <summary>Creates a ped representing another player. Returns a handle, or 0 on failure.</summary>
        int CreateRemotePed(uint modelHash, NetVector3 position, float heading);

        /// <summary>
        /// Drives one remote ped for this frame. The decision of *what* to do lives in
        /// <see cref="RemotePedController"/>; this only executes it.
        /// </summary>
        void ApplyRemotePedCommand(int handle, in RemotePedCommand command);

        /// <summary>Applies clothing and props. Called only when the appearance actually changes.</summary>
        void ApplyRemotePedAppearance(int handle, PedAppearance appearance);

        /// <summary>
        /// Puts a networked NPC into the relationship group the server gave it, which
        /// is what decides whether every other ped on this machine — and the local
        /// player's own targeting — treats it as hostile.
        /// <para>
        /// A hash of 0 means "no opinion": the ped goes back to the group a remote ped
        /// is created in, which is the local player's own. Called only when the value
        /// changes; the group is a property of the ped, not a per-frame command.
        /// </para>
        /// </summary>
        void SetRemotePedRelationshipGroup(int handle, uint relationshipGroupHash);

        /// <summary>
        /// Puts a replicated character in the room of the interior it is actually in,
        /// or takes it out of one.
        /// <para>
        /// GTA V culls by room. A ped the engine believes is outdoors while it stands
        /// inside a building is drawn through the wall; one it believes is in the wrong
        /// room disappears where it should be visible. Until this existed every
        /// replicated ped was outdoors as far as the engine was concerned, whatever the
        /// world said, and <c>InteriorId</c> was sampled and applied to nothing.
        /// </para>
        /// <para>
        /// The interior is derived here from <paramref name="position"/> rather than
        /// replicated: an interior handle is a runtime number that need not match
        /// between machines, and a room key is a hash of a name that does.
        /// </para>
        /// </summary>
        void SetRemotePedRoom(int handle, uint roomKey, NetVector3 position);

        /// <summary>
        /// Tells a networked NPC to do what the server says it is doing: fight, flee,
        /// surrender, be arrested, or play a scenario.
        /// <para>
        /// The decision is <see cref="Gtamp.Client.Entities.NpcIntentDirector"/>'s and
        /// is unit-tested; this is the native call. The server owns the intent and the
        /// game owns the execution, because the server has no navigation mesh and a ped
        /// whose every footstep came from one would walk through walls.
        /// </para>
        /// </summary>
        /// <param name="targetHandle">The ped to fight or flee from, or 0.</param>
        void ApplyNpcIntent(int handle, Gtamp.Client.Entities.NpcIntent intent, int targetHandle, uint scenarioHash);

        /// <summary>
        /// Rounds the local player fired since the last call, with the geometry of the
        /// last of them. Called every frame, unlike <see cref="SampleLocalPlayer"/>:
        /// a shot is an event and the send rate would swallow most of them.
        /// </summary>
        LocalShotSample SampleLocalShots();

        /// <summary>
        /// Hits the local player has landed on other players' peds since the last
        /// call, appended to <paramref name="into"/>.
        /// <para>
        /// The engine's own hit detection answers this — it already knows about
        /// bullets, melee, vehicles and explosions, and re-deriving any of that from a
        /// ray would be worse at all four. The bridge reads what the game recorded and
        /// puts the ped's health back where the server says it should be.
        /// </para>
        /// </summary>
        void SampleLocalHits(List<LocalHitSample> into);

        /// <summary>
        /// Draws one shot fired by somebody else — the tracer, the muzzle flash and
        /// the impact. It deals no damage: the hit is arbitrated by the server from a
        /// separate report, and a rendered bullet that also wounded would count the
        /// same trigger pull twice.
        /// </summary>
        void PlayRemoteShot(int pedHandle, uint weaponHash, NetVector3 origin, NetVector3 impact);

        /// <summary>
        /// Where the ped currently is in the game. The controller needs this to decide
        /// between tasking it to walk and correcting it outright.
        /// </summary>
        bool TryGetRemotePedPosition(int handle, out NetVector3 position);

        /// <summary>
        /// Draws the map blip and the floating name for one remote player. Called
        /// every frame, because the name is drawn per frame and the blip's colour
        /// changes with what the player is doing.
        /// </summary>
        void ApplyPlayerMarker(int pedHandle, in PlayerMarker marker);

        void DestroyRemotePed(int handle);

        bool IsRemotePedValid(int handle);

        // --- vehicles ------------------------------------------------------

        /// <summary>Creates a vehicle representing a replicated one. Returns a handle, or 0 on failure.</summary>
        int CreateRemoteVehicle(uint modelHash, NetVector3 position, float heading);

        /// <summary>Drives one replicated vehicle for this frame.</summary>
        /// <summary>
        /// Drives one replicated vehicle for this frame. <paramref name="trailerHandle"/>
        /// is the local handle of the trailer it is towing, or 0.
        /// </summary>
        void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame, int trailerHandle, int attachedToHandle);

        /// <summary>
        /// The vehicle this one is physically attached to — lifted by a Cargobob, on a
        /// tow truck's hook, strapped to a flatbed — as a game handle, or 0.
        /// <para>
        /// Distinct from a trailer, which has its own hitch and its own native.
        /// <c>VehicleEntity.AttachedToId</c> has existed on the wire since Phase 3 and
        /// was read from nothing and applied to nothing, so a car being towed was
        /// attached on the tower's screen and drifting free on everybody else's.
        /// </para>
        /// </summary>
        int GetVehicleAttachedTo(int handle);

        /// <summary>Applies paint, livery, mods and plate. Called only when they change.</summary>
        void ApplyRemoteVehicleAppearance(int handle, VehicleEntity state);

        /// <summary>Reads a vehicle this client owns, so its state can be reported to the server.</summary>
        bool TryReadVehicle(int handle, VehicleEntity into);

        void DestroyRemoteVehicle(int handle);

        bool IsRemoteVehicleValid(int handle);

        /// <summary>
        /// Handle of the vehicle the local player is currently in, or 0. This is how a
        /// client notices it has something worth registering with the server.
        /// </summary>
        int GetLocalPlayerVehicleHandle();

        /// <summary>
        /// The local player's own ped, as a game handle, or 0 when there is none.
        /// <para>
        /// Needed because the one player every client is certain to be asked about is
        /// the one it is not drawing. When somebody punches you, your client has to
        /// tell their ped to swing at *your* ped — and yours is the local player, which
        /// is in no remote-player list anywhere. Resolving a replicated id to a handle
        /// without this returns nothing in exactly the most common case.
        /// </para>
        /// </summary>
        int GetLocalPlayerPedHandle();

        /// <summary>
        /// Stops GTA V spawning ambient traffic of its own, for this frame only.
        /// <para>
        /// Per-frame because the natives that do it are: the game resets the density
        /// multipliers every frame, so suppression is a thing you keep saying rather
        /// than a thing you set. Called on every client that is not the traffic source
        /// for its area, so that the only cars on the street are the ones the source
        /// spawned and the server replicated.
        /// </para>
        /// <para>
        /// Pedestrians are deliberately not suppressed. They are not adopted either,
        /// so suppressing them would empty the pavements rather than share them — a
        /// city with no people in it is further from one world, not closer.
        /// </para>
        /// </summary>
        void SuppressAmbientTrafficThisFrame();

        /// <summary>
        /// Collects the ambient vehicles near the local player: the ones the game
        /// spawned by itself, which this client may hand to the server.
        /// <para>
        /// Ordered nearest first, so a cap takes the cars the player can actually see
        /// rather than an arbitrary subset of the street.
        /// </para>
        /// </summary>
        void SampleAmbientVehicles(List<int> into, float radius);

        /// <summary>
        /// Stops GTA V spawning ambient pedestrians of its own, for this frame only.
        /// <para>
        /// Separate from the traffic call and separately switchable, because the two
        /// were shipped a commit apart and because a server may reasonably want one and
        /// not the other: a city with shared cars and local people is coherent, and one
        /// with shared cars and NO people is not.
        /// </para>
        /// </summary>
        void SuppressAmbientPedsThisFrame();

        /// <summary>
        /// Collects the ambient pedestrians near the local player: the ones the game
        /// spawned by itself and this client may hand to the server. Never includes the
        /// local player, and never a ped this client is already drawing on the server's
        /// behalf.
        /// </summary>
        void SampleAmbientPeds(List<int> into, float radius);

        /// <summary>
        /// Reads one ped this client owns, so its state can be offered or streamed.
        /// <para>
        /// The counterpart of <see cref="TryReadVehicle"/>, and it did not exist
        /// because until now every networked ped was created by the server and never
        /// read back from a game.
        /// </para>
        /// </summary>
        bool TryReadPed(int handle, PedEntity into);

        /// <summary>Model hash of a local vehicle handle, or 0 when the handle is not valid.</summary>
        uint GetVehicleModel(int handle);

        /// <summary>
        /// Draws the explosion of a replicated vehicle that has just been destroyed.
        /// Called once, on the frame the destruction is first seen — never for a wreck
        /// that was already a wreck when this client arrived.
        /// </summary>
        void PlayVehicleExplosion(int vehicleHandle);

        /// <summary>
        /// Shoves an entity this client is drawing, so that the leap happens here too
        /// rather than only on the machine that caused it.
        /// <para>
        /// The handle may be a vehicle, an object or a ped; the caller has already
        /// resolved which. A handle of 0 means this client has not built the thing and
        /// the push is simply not applied — there is nothing here to move.
        /// </para>
        /// </summary>
        void ApplyEntityImpulse(int handle, NetVector3 impulse, bool isExplosion);

        // --- objects -------------------------------------------------------

        int CreateRemoteObject(uint modelHash, NetVector3 position, float heading);

        /// <summary>
        /// Applies one replicated object. <paramref name="attachParentHandle"/> is the
        /// local handle of the entity it is attached to, resolved by the caller, or 0
        /// when it is attached to nothing this client has.
        /// </summary>
        void ApplyRemoteObject(int handle, ObjectEntity state, int attachParentHandle);

        void DestroyRemoteObject(int handle);

        bool IsRemoteObjectValid(int handle);

        // --- world ---------------------------------------------------------

        void SetWeather(uint weatherHash, uint nextWeatherHash, float transition);

        void SetClock(int hours, int minutes, int seconds);

        /// <summary>
        /// Wind speed in metres per second and its direction in degrees. Replicated
        /// because wind moves foliage, rain and cloth: two players standing in the
        /// same storm should not see it blowing opposite ways.
        /// </summary>
        void SetWind(float speed, float directionDegrees);

        /// <summary>
        /// City-wide artificial lights. A blackout is a world event every player has
        /// to see the same way or the map stops matching itself.
        /// </summary>
        void SetBlackout(bool blackout);

        /// <summary>
        /// Switches the world's map files on and off to match what the server says is
        /// loaded.
        /// <para>
        /// Section 16 lists IPL and map add-ons. Whether a player <em>has</em> a map
        /// file is mod negotiation's problem; whether it is switched <em>on</em> is a
        /// property of the world, and it was the half nobody carried — a mod that
        /// opened an interior opened it on the machine that ran the mod, and everybody
        /// else walked into a wall where the door was.
        /// </para>
        /// <para>
        /// A name this client does not have produces nothing, which is the same outcome
        /// as the mod not being installed and is reported by the missing-content
        /// tracker rather than here.
        /// </para>
        /// </summary>
        void SetActiveMapFiles(IReadOnlyList<string> ipls);

        void ShowNotification(string text);

        void ShowSubtitle(string text, int durationMilliseconds);
    }

    /// <summary>One frame of local player state, read from the game and sent to the server.</summary>
    public struct LocalPlayerSample
    {
        public NetVector3 Position;
        public NetVector3 Velocity;
        public float Heading;
        public int Health;
        public int MaxHealth;
        public int Armor;
        public uint ModelHash;
        public PlayerFlags Flags;
        public MovementState Movement;
        public uint CurrentWeaponHash;
        public int Ammo;

        /// <summary>Weapon tint index and the components fitted to the weapon in hand.</summary>
        public byte WeaponTint;

        /// <summary>Null when the bridge could not read them this frame.</summary>
        public List<uint>? WeaponComponents;
        public NetVector3 AimPosition;

        /// <summary>
        /// Game-side handle of the ped the local player is swinging at, or 0. Reported
        /// as a handle for the same reason a hit is: the bridge knows handles and the
        /// client owns the map from a handle back to a replicated id.
        /// </summary>
        public int MeleeTargetPedHandle;
        public int InteriorId;

        /// <summary>GTA V's room key for where the player is standing, 0 outdoors.</summary>
        public uint RoomKey;

        /// <summary>
        /// The local player's wanted level. Replicated so other players can see who
        /// the police are after; never applied to anybody else's game, because a
        /// wanted level is a property of a player and not of the ped standing in for
        /// them.
        /// </summary>
        public byte WantedLevel;
        public uint AnimationHash;

        /// <summary>
        /// Limb positions, read only while the local player is ragdolling and
        /// <see cref="RagdollPose.None"/> otherwise. Reading three bones costs three
        /// natives, so it is not paid for on the frames where nobody is falling.
        /// </summary>
        public RagdollPose Ragdoll;

        /// <summary>Clothing and props. Null when the bridge could not read them this frame.</summary>
        public PedAppearance? Appearance;
    }

    /// <summary>What the local player fired this frame, if anything.</summary>
    public struct LocalShotSample
    {
        /// <summary>Rounds fired since the previous frame. Zero means nothing to report.</summary>
        public int Rounds;

        public uint WeaponHash;

        /// <summary>Muzzle position.</summary>
        public NetVector3 Origin;

        /// <summary>Impact point, or the aim point when the round hit nothing.</summary>
        public NetVector3 Impact;
    }

    /// <summary>One hit the local player landed on a remote ped, as the game scored it.</summary>
    public struct LocalHitSample
    {
        /// <summary>Game-side handle of the ped that was hit.</summary>
        public int PedHandle;

        public uint WeaponHash;

        /// <summary>
        /// Damage as the *game* computed it — the drop in health plus armour. Using
        /// the engine's number rather than a table of our own means range falloff,
        /// body armour and weapon mods are already accounted for, and the server
        /// still clamps it against its own envelope before applying anything.
        /// </summary>
        public int Damage;

        public NetVector3 HitPosition;

        /// <summary>GTA V bone index, or -1 when the game did not record one.</summary>
        public short HitBone;

        public bool IsMelee;
    }

    /// <summary>Interpolated state applied to another player's ped this frame.</summary>
    public struct RemotePedFrame
    {
        public NetVector3 Position;
        public NetVector3 Velocity;
        public float Heading;
        public int Health;
        public int Armor;
        public PlayerFlags Flags;
        public MovementState Movement;
        public uint CurrentWeaponHash;
        public byte WeaponTint;
        public List<uint>? WeaponComponents;
        public NetVector3 AimPosition;
        public uint AnimationHash;

        /// <summary>Replicated limb positions; <see cref="RagdollPose.None"/> when not ragdolling.</summary>
        public RagdollPose Ragdoll;

        /// <summary>The vehicle this character is riding in, and which seat. -2 means on foot.</summary>
        public EntityId VehicleId;

        public sbyte VehicleSeat;

        /// <summary>Who this character is swinging at, or <see cref="EntityId.None"/>.</summary>
        public EntityId MeleeTargetId;

        /// <summary>Which room of an interior this character is in, 0 outdoors.</summary>
        public uint RoomKey;
    }
}
