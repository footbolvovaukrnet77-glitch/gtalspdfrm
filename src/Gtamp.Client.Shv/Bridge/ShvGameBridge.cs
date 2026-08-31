using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;
using GTA;
using GTA.Math;
using GTA.Native;
using GtaWorld = GTA.World;

namespace Gtamp.Client.Shv.Bridge
{
    /// <summary>
    /// <see cref="IGameBridge"/> over ScriptHookVDotNet 3.
    /// <para>
    /// <b>Known limitation — remote player animation.</b> Remote peds are moved by
    /// writing their coordinates every frame from the interpolation buffer. That is
    /// positionally correct but the ped plays an idle animation while sliding,
    /// because GTA V's locomotion is driven by the ped's own task system rather than
    /// by its coordinates. Driving the task system from replicated movement state is
    /// Phase 2 work (see docs/ROADMAP.md); doing it here would mean guessing at a
    /// task API before the movement state that feeds it is finished. Position,
    /// heading, health and armour are correct today; gait is not.
    /// </para>
    /// </summary>
    public sealed class ShvGameBridge : IGameBridge
    {
        private readonly LogBus _log;
        private readonly Dictionary<int, Ped> _remotePeds = new Dictionary<int, Ped>();

        public ShvGameBridge(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public string GameVersion => Game.Version.ToString();

        public bool IsPlayerReady
        {
            get
            {
                if (Game.IsLoading || Game.IsPaused)
                {
                    return false;
                }

                Ped character = Game.Player.Character;
                return character != null && character.Exists();
            }
        }

        public LocalPlayerSample SampleLocalPlayer()
        {
            Ped ped = Game.Player.Character;
            var sample = new LocalPlayerSample
            {
                Position = ToNet(ped.Position),
                Velocity = ToNet(ped.Velocity),
                Heading = ped.Heading,
                Health = ped.Health,
                MaxHealth = ped.MaxHealth,
                Armor = ped.Armor,
                ModelHash = unchecked((uint)ped.Model.Hash),
                Movement = SampleMovement(ped),
                Flags = SampleFlags(ped),
                InteriorId = 0,
                AnimationHash = 0,
            };

            Weapon weapon = ped.Weapons.Current;
            if (weapon != null)
            {
                sample.CurrentWeaponHash = unchecked((uint)weapon.Hash);
                sample.Ammo = weapon.Ammo;
            }

            sample.AimPosition = sample.Position;
            return sample;
        }

        public void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor)
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists())
            {
                return;
            }

            ped.PositionNoOffset = ToGame(position);
            ped.Heading = heading;
            ped.Health = health;
            ped.Armor = armor;
        }

        public int CreateRemotePed(uint modelHash, NetVector3 position, float heading)
        {
            try
            {
                var model = new Model(unchecked((int)(modelHash == 0 ? DefaultPedModel : modelHash)));
                if (!model.IsValid)
                {
                    model = new Model(unchecked((int)DefaultPedModel));
                }

                // Request is asynchronous. Returning 0 makes the caller retry on a
                // later frame, which is cheaper than blocking the game thread.
                if (!model.IsLoaded)
                {
                    model.Request();
                    return 0;
                }

                Ped? ped = GtaWorld.CreatePed(model, ToGame(position), heading);
                model.MarkAsNoLongerNeeded();
                if (ped == null || !ped.Exists())
                {
                    return 0;
                }

                // A replicated ped must not be simulated by the local game: no AI
                // reactions, no ragdoll from local physics, no damage from local
                // events. Its state comes from the server and nowhere else.
                ped.IsInvincible = true;
                ped.BlockPermanentEvents = true;
                ped.CanRagdoll = false;
                ped.RelationshipGroup = Game.Player.Character.RelationshipGroup;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, ped.Handle, true);

                _remotePeds[ped.Handle] = ped;
                return ped.Handle;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not create a remote ped.", exception);
                return 0;
            }
        }

        public void UpdateRemotePed(int handle, in RemotePedFrame frame)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped) || !ped.Exists())
            {
                _remotePeds.Remove(handle);
                return;
            }

            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET,
                ped.Handle,
                frame.Position.X,
                frame.Position.Y,
                frame.Position.Z,
                false,
                false,
                false);

            ped.Heading = frame.Heading;
            ped.Velocity = ToGame(frame.Velocity);

            if (ped.Health != frame.Health)
            {
                ped.Health = frame.Health < 0 ? 0 : frame.Health;
            }

            if (ped.Armor != frame.Armor)
            {
                ped.Armor = frame.Armor;
            }
        }

        public void DestroyRemotePed(int handle)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped))
            {
                return;
            }

            _remotePeds.Remove(handle);
            try
            {
                if (ped.Exists())
                {
                    ped.MarkAsNoLongerNeeded();
                    ped.Delete();
                }
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Client, $"Could not delete remote ped {handle}: {exception.Message}");
            }
        }

        public bool IsRemotePedValid(int handle) =>
            handle != 0 && _remotePeds.TryGetValue(handle, out Ped ped) && ped.Exists();

        public void SetWeather(uint weatherHash, uint nextWeatherHash, float transition)
        {
            if (!WeatherCatalog.TryGetName(weatherHash, out string name))
            {
                // A weather type from a mod this client does not have. Leaving the
                // local weather alone is better than snapping it to a wrong value.
                return;
            }

            if (!TryParseWeather(name, out Weather weather))
            {
                return;
            }

            if (nextWeatherHash != 0
                && WeatherCatalog.TryGetName(nextWeatherHash, out string nextName)
                && TryParseWeather(nextName, out Weather next)
                && transition > 0f)
            {
                if (GtaWorld.Weather != weather)
                {
                    GtaWorld.Weather = weather;
                }

                GtaWorld.TransitionToWeather(next, transition);
                return;
            }

            if (GtaWorld.Weather != weather)
            {
                GtaWorld.Weather = weather;
            }
        }

        public void SetClock(int hours, int minutes, int seconds)
        {
            TimeSpan target = new TimeSpan(hours, minutes, seconds);
            TimeSpan current = GtaWorld.CurrentTimeOfDay;

            // Writing the clock every frame makes the sky flicker; only correct when
            // the local clock has drifted more than a few in-game seconds.
            if (Math.Abs((target - current).TotalSeconds) > 20d)
            {
                GtaWorld.CurrentTimeOfDay = target;
            }
        }

        public void ShowNotification(string text) => GTA.UI.Notification.Show(text, false);

        public void ShowSubtitle(string text, int durationMilliseconds) =>
            GTA.UI.Screen.ShowSubtitle(text, durationMilliseconds);

        /// <summary>Removes every replicated ped. Called when the session ends or the script aborts.</summary>
        public void CleanUp()
        {
            foreach (Ped ped in _remotePeds.Values)
            {
                try
                {
                    if (ped.Exists())
                    {
                        ped.MarkAsNoLongerNeeded();
                        ped.Delete();
                    }
                }
                catch (Exception)
                {
                    // Best effort during teardown.
                }
            }

            _remotePeds.Clear();
        }

        /// <summary>ig_michael as a stand-in until clothing and model replication lands in Phase 2.</summary>
        private const uint DefaultPedModel = 0xD7114C9;

        private static MovementState SampleMovement(Ped ped)
        {
            if (ped.IsSprinting)
            {
                return MovementState.Sprint;
            }

            if (ped.IsRunning)
            {
                return MovementState.Run;
            }

            return ped.IsWalking ? MovementState.Walk : MovementState.Idle;
        }

        private static PlayerFlags SampleFlags(Ped ped)
        {
            PlayerFlags flags = PlayerFlags.None;

            if (ped.IsDucking)
            {
                flags |= PlayerFlags.Crouching;
            }

            if (ped.IsSprinting)
            {
                flags |= PlayerFlags.Sprinting;
            }

            if (ped.IsJumping)
            {
                flags |= PlayerFlags.Jumping;
            }

            if (ped.IsFalling)
            {
                flags |= PlayerFlags.Falling;
            }

            if (ped.IsSwimming)
            {
                flags |= PlayerFlags.Swimming;
            }

            if (ped.IsSwimmingUnderWater)
            {
                flags |= PlayerFlags.Diving;
            }

            if (ped.IsClimbing || ped.IsVaulting)
            {
                flags |= PlayerFlags.Climbing;
            }

            if (ped.IsRagdoll)
            {
                flags |= PlayerFlags.Ragdoll;
            }

            if (ped.IsDead)
            {
                flags |= PlayerFlags.Dead;
            }

            if (Game.Player.IsAiming)
            {
                flags |= PlayerFlags.Aiming;
            }

            if (ped.IsInVehicle())
            {
                flags |= PlayerFlags.InVehicle;
            }

            if (ped.IsGettingIntoVehicle)
            {
                flags |= PlayerFlags.EnteringVehicle;
            }

            if (ped.IsInvincible)
            {
                flags |= PlayerFlags.Invincible;
            }

            return flags;
        }

        private static bool TryParseWeather(string name, out Weather weather)
        {
            switch (name)
            {
                case "EXTRASUNNY": weather = Weather.ExtraSunny; return true;
                case "CLEAR": weather = Weather.Clear; return true;
                case "CLOUDS": weather = Weather.Clouds; return true;
                case "SMOG": weather = Weather.Smog; return true;
                case "FOGGY": weather = Weather.Foggy; return true;
                case "OVERCAST": weather = Weather.Overcast; return true;
                case "RAIN": weather = Weather.Raining; return true;
                case "THUNDER": weather = Weather.ThunderStorm; return true;
                case "CLEARING": weather = Weather.Clearing; return true;
                case "NEUTRAL": weather = Weather.Neutral; return true;
                case "SNOW": weather = Weather.Snowing; return true;
                case "BLIZZARD": weather = Weather.Blizzard; return true;
                case "SNOWLIGHT": weather = Weather.Snowlight; return true;
                case "XMAS": weather = Weather.Christmas; return true;
                case "HALLOWEEN": weather = Weather.Halloween; return true;
                default: weather = Weather.Clear; return false;
            }
        }

        private static NetVector3 ToNet(Vector3 value) => new NetVector3(value.X, value.Y, value.Z);

        private static Vector3 ToGame(NetVector3 value) => new Vector3(value.X, value.Y, value.Z);
    }
}
