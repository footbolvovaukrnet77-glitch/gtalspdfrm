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
        void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame, int trailerHandle);

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

        /// <summary>Model hash of a local vehicle handle, or 0 when the handle is not valid.</summary>
        uint GetVehicleModel(int handle);

        /// <summary>Puts a ped into a seat of a replicated vehicle.</summary>
        void SeatRemotePedInVehicle(int pedHandle, int vehicleHandle, sbyte seat);

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
        public int InteriorId;

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
    }
}
