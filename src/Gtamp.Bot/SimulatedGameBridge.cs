using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Bot
{
    /// <summary>What the bot has seen the client try to draw.</summary>
    public sealed class BotObservations
    {
        public Dictionary<int, NetVector3> RemotePeds { get; } = new Dictionary<int, NetVector3>();

        public Dictionary<int, NetVector3> RemoteVehicles { get; } = new Dictionary<int, NetVector3>();

        public int RemotePedsEverSeen { get; set; }

        public int RemoteVehiclesEverSeen { get; set; }

        public int ShotsDrawn { get; set; }

        public int ExplosionsDrawn { get; set; }

        public int CorrectionsApplied { get; set; }

        public double LastCorrectionDistance { get; set; }

        public int ModelChanges { get; set; }

        public List<string> Notifications { get; } = new List<string>();

        public bool AnyRemotePedNow => RemotePeds.Count > 0;
    }

    /// <summary>
    /// The bot's half of <see cref="IGameBridge"/>: an actuator on the way down and
    /// a sensor on the way up.
    /// <para>
    /// Downwards it answers every question the client asks about "the game" from
    /// <see cref="BotBody"/>. Upwards, every call the client makes to draw somebody
    /// else — create a ped, apply a frame, play a shot, explode a car — is recorded
    /// instead of drawn. That recording is the only way a headless bot can report
    /// what the server told it, and it is what makes the bot a test instrument
    /// rather than just a second connection.
    /// </para>
    /// </summary>
    public sealed class SimulatedGameBridge : IGameBridge
    {
        private readonly BotBody _body;
        private int _nextHandle = 1;

        public SimulatedGameBridge(BotBody body)
        {
            _body = body ?? throw new ArgumentNullException(nameof(body));
        }

        public BotObservations Seen { get; } = new BotObservations();

        public string GameVersion => "simulated (no GTA V in this process)";

        public bool IsPlayerReady => true;

        /// <summary>
        /// Every model is available. A real client answers this from the streamer, and
        /// answering "Loading" here would make the bot silently skip drawing peds it
        /// is supposed to be reporting — the bot's job is to see everything the server
        /// sends, not to simulate an install with missing content.
        /// </summary>
        public ModelAvailability GetModelAvailability(uint modelHash) =>
            modelHash == 0 ? ModelAvailability.Unavailable : ModelAvailability.Available;

        public LocalPlayerSample SampleLocalPlayer() => new LocalPlayerSample
        {
            Position = _body.Position,
            Velocity = _body.Velocity,
            Heading = _body.Heading,
            Health = _body.Health,
            MaxHealth = _body.MaxHealth,
            Armor = _body.Armor,
            ModelHash = _body.ModelHash,
            Flags = _body.Flags,
            Movement = _body.Movement,
            CurrentWeaponHash = _body.WeaponHash,
            Ammo = _body.Ammo,
            WeaponTint = 0,
            WeaponComponents = null,
            AimPosition = _body.AimPosition,
            InteriorId = 0,
            WantedLevel = _body.WantedLevel,
            AnimationHash = 0,
            Ragdoll = default,
            Appearance = null,
        };

        public LocalShotSample SampleLocalShots()
        {
            if (_body.PendingRounds == 0)
            {
                return default;
            }

            var sample = new LocalShotSample
            {
                Rounds = _body.PendingRounds,
                WeaponHash = _body.WeaponHash,
                Origin = _body.ShotOrigin,
                Impact = _body.ShotImpact,
            };

            _body.PendingRounds = 0;
            return sample;
        }

        public void SampleLocalHits(List<LocalHitSample> into)
        {
            into.AddRange(_body.PendingHits);
            _body.PendingHits.Clear();
        }

        /// <summary>
        /// The server moving us. A real bridge places the ped; here it is applied to
        /// the body, so the bot obeys the server exactly as a player's game would and
        /// a correction loop shows up as a growing distance rather than as nothing.
        /// </summary>
        public void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor)
        {
            Seen.CorrectionsApplied++;
            Seen.LastCorrectionDistance = Distance(_body.Position, position);
            _body.Position = position;
            _body.Heading = heading;
            _body.Health = health;
            _body.Armor = armor;
        }

        public void SetLocalWantedLevel(int level) => _body.WantedLevel = (byte)Math.Max(0, level);

        public bool TrySetLocalPlayerModel(uint modelHash)
        {
            Seen.ModelChanges++;
            _body.ModelHash = modelHash;
            return true;
        }

        public void SetLocalMaxHealth(int maxHealth) => _body.MaxHealth = maxHealth;

        public int CreateRemotePed(uint modelHash, NetVector3 position, float heading)
        {
            int handle = _nextHandle++;
            Seen.RemotePeds[handle] = position;
            Seen.RemotePedsEverSeen++;
            return handle;
        }

        public void ApplyRemotePedCommand(int handle, in RemotePedCommand command)
        {
            if (Seen.RemotePeds.ContainsKey(handle))
            {
                Seen.RemotePeds[handle] = command.TargetPosition;
            }
        }

        public void ApplyRemotePedAppearance(int handle, PedAppearance appearance)
        {
        }

        public void SetRemotePedRelationshipGroup(int handle, uint relationshipGroupHash)
        {
        }

        public void PlayRemoteShot(int pedHandle, uint weaponHash, NetVector3 origin, NetVector3 impact) =>
            Seen.ShotsDrawn++;

        public bool TryGetRemotePedPosition(int handle, out NetVector3 position) =>
            Seen.RemotePeds.TryGetValue(handle, out position);

        public void ApplyPlayerMarker(int pedHandle, in PlayerMarker marker)
        {
        }

        public void DestroyRemotePed(int handle) => Seen.RemotePeds.Remove(handle);

        public bool IsRemotePedValid(int handle) => Seen.RemotePeds.ContainsKey(handle);

        public int CreateRemoteVehicle(uint modelHash, NetVector3 position, float heading)
        {
            int handle = _nextHandle++;
            Seen.RemoteVehicles[handle] = position;
            Seen.RemoteVehiclesEverSeen++;
            return handle;
        }

        public void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame, int trailerHandle)
        {
            if (Seen.RemoteVehicles.ContainsKey(handle))
            {
                Seen.RemoteVehicles[handle] = frame.Position;
            }
        }

        public void ApplyRemoteVehicleAppearance(int handle, VehicleEntity state)
        {
        }

        public bool TryReadVehicle(int handle, VehicleEntity into)
        {
            if (handle == 0 || handle != _body.VehicleHandle || into == null)
            {
                return false;
            }

            into.ModelHash = _body.VehicleModel;
            into.Position = _body.Position;
            into.Heading = _body.Heading;
            into.Velocity = _body.Velocity;
            into.EngineHealth = 1000f;
            into.BodyHealth = 1000f;
            into.PetrolTankHealth = 1000f;
            into.FuelLevel = 65f;
            return true;
        }

        public void DestroyRemoteVehicle(int handle) => Seen.RemoteVehicles.Remove(handle);

        public bool IsRemoteVehicleValid(int handle) => Seen.RemoteVehicles.ContainsKey(handle);

        public int GetLocalPlayerVehicleHandle() => _body.VehicleHandle;

        public uint GetVehicleModel(int handle) =>
            handle != 0 && handle == _body.VehicleHandle ? _body.VehicleModel : 0u;

        public void PlayVehicleExplosion(int vehicleHandle) => Seen.ExplosionsDrawn++;

        public int CreateRemoteObject(uint modelHash, NetVector3 position, float heading) => _nextHandle++;

        public void ApplyRemoteObject(int handle, ObjectEntity state, int attachParentHandle)
        {
        }

        public void DestroyRemoteObject(int handle)
        {
        }

        public bool IsRemoteObjectValid(int handle) => true;

        public void SetWeather(uint weatherHash, uint nextWeatherHash, float transition)
        {
        }

        public void SetClock(int hours, int minutes, int seconds)
        {
        }

        public void SetWind(float speed, float directionDegrees)
        {
        }

        public void SetBlackout(bool blackout)
        {
        }

        public void ShowNotification(string text) => Seen.Notifications.Add(text);

        public void ShowSubtitle(string text, int durationMilliseconds) => Seen.Notifications.Add(text);

        internal static double Distance(NetVector3 a, NetVector3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
    }
}
