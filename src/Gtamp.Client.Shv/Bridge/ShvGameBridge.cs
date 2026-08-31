using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
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
    /// Remote peds are driven through the game's task system rather than by writing
    /// coordinates, so they animate as they move. Coordinates are still written, but
    /// only as a correction when the ped has drifted past
    /// <see cref="RemotePedController.HardCorrectDistance"/> — tasking alone cannot
    /// guarantee position, and correcting alone cannot produce animation, so both are
    /// needed. The decision between them lives in <see cref="RemotePedController"/>;
    /// this class only executes it.
    /// </para>
    /// </summary>
    public sealed class ShvGameBridge : IGameBridge
    {
        /// <summary>ig_michael, used until a player's own model is known.</summary>
        private const uint DefaultPedModel = 0xD7114C9;

        /// <summary>Re-task a walking ped only when its destination has moved this far.</summary>
        private const float RetaskDistance = 0.75f;

        /// <summary>Task timeout. Long enough to survive several missed snapshots, short enough to expire if we stop.</summary>
        private const int TaskTimeoutMilliseconds = 4000;

        /// <summary>How often the local player's clothing is read back. It changes rarely and each read is ~30 native calls.</summary>
        private const int AppearanceSampleIntervalMilliseconds = 1000;

        private readonly LogBus _log;
        private readonly Dictionary<int, Ped> _remotePeds = new Dictionary<int, Ped>();
        private readonly Dictionary<int, PedDriveState> _driveState = new Dictionary<int, PedDriveState>();
        private readonly PedAppearance _localAppearance = new PedAppearance();

        private int _lastAppearanceSampleTick;

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

        // ------------------------------------------------------------------
        // Local player
        // ------------------------------------------------------------------
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
                InteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, ped.Handle),
                AnimationHash = 0,
                AimPosition = SampleAimPosition(ped),
                Appearance = SampleAppearance(ped),
            };

            Weapon weapon = ped.Weapons.Current;
            if (weapon != null)
            {
                sample.CurrentWeaponHash = unchecked((uint)weapon.Hash);
                sample.Ammo = weapon.Ammo;
            }

            return sample;
        }

        /// <summary>
        /// The point the player is aiming at, taken from the gameplay camera.
        /// <para>
        /// GTA V has no native for "where is this ped aiming"; the aim direction is a
        /// property of the camera, not the ped. Projecting the camera ray 150 m gives
        /// a target the remote side can aim its ped at, which is what the pose needs —
        /// it is not a hit position and is not used as one.
        /// </para>
        /// </summary>
        private static NetVector3 SampleAimPosition(Ped ped)
        {
            if (!Game.Player.IsAiming)
            {
                return ToNet(ped.Position + (ped.ForwardVector * 10f));
            }

            Vector3 origin = GameplayCamera.Position;
            Vector3 direction = GameplayCamera.Direction;
            return ToNet(origin + (direction * 150f));
        }

        private PedAppearance? SampleAppearance(Ped ped)
        {
            int now = Game.GameTime;
            if (_lastAppearanceSampleTick != 0 && now - _lastAppearanceSampleTick < AppearanceSampleIntervalMilliseconds)
            {
                return _localAppearance;
            }

            _lastAppearanceSampleTick = now;

            for (int slot = 0; slot < PedAppearance.ComponentSlots; slot++)
            {
                int drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped.Handle, slot);
                int texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped.Handle, slot);
                int palette = Function.Call<int>(Hash.GET_PED_PALETTE_VARIATION, ped.Handle, slot);

                _localAppearance.SetComponent(
                    slot,
                    (ushort)Clamp(drawable, 0, ushort.MaxValue),
                    (byte)Clamp(texture, 0, byte.MaxValue),
                    (byte)Clamp(palette, 0, byte.MaxValue));
            }

            for (int slot = 0; slot < PedAppearance.PropSlots; slot++)
            {
                int drawable = Function.Call<int>(Hash.GET_PED_PROP_INDEX, ped.Handle, slot);
                if (drawable < 0)
                {
                    _localAppearance.SetProp(slot, PedAppearance.NoProp, 0);
                    continue;
                }

                int texture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, ped.Handle, slot);
                _localAppearance.SetProp(
                    slot, (short)Clamp(drawable, 0, short.MaxValue), (byte)Clamp(texture, 0, byte.MaxValue));
            }

            return _localAppearance;
        }

        public void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor)
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists())
            {
                return;
            }

            // A respawn arrives as a correction: the server has already moved the
            // player and refilled their health, so the client must revive before
            // placing them or the game leaves them dead at the new position.
            if (ped.IsDead && health > 0)
            {
                Function.Call(
                    Hash.NETWORK_RESURRECT_LOCAL_PLAYER,
                    position.X, position.Y, position.Z, heading, false, false);
            }

            ped.PositionNoOffset = ToGame(position);
            ped.Heading = heading;
            ped.Health = health;
            ped.Armor = armor;
        }

        // ------------------------------------------------------------------
        // Remote peds
        // ------------------------------------------------------------------
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
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, ped.Handle, true);

                _remotePeds[ped.Handle] = ped;
                _driveState[ped.Handle] = new PedDriveState();
                return ped.Handle;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not create a remote ped.", exception);
                return 0;
            }
        }

        public bool TryGetRemotePedPosition(int handle, out NetVector3 position)
        {
            if (_remotePeds.TryGetValue(handle, out Ped ped) && ped.Exists())
            {
                position = ToNet(ped.Position);
                return true;
            }

            position = NetVector3.Zero;
            return false;
        }

        public void ApplyRemotePedCommand(int handle, in RemotePedCommand command)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped) || !ped.Exists())
            {
                _remotePeds.Remove(handle);
                _driveState.Remove(handle);
                return;
            }

            if (!_driveState.TryGetValue(handle, out PedDriveState state))
            {
                state = new PedDriveState();
                _driveState[handle] = state;
            }

            ApplyVitals(ped, in command, state);

            switch (command.Action)
            {
                case RemotePedAction.Dead:
                    DriveDead(ped, in command, state);
                    return;

                case RemotePedAction.Ragdoll:
                    DriveRagdoll(ped, state);
                    return;

                case RemotePedAction.InVehicle:
                    // Phase 3 seats the ped in the replicated vehicle. Until then it is
                    // held at the reported position rather than left walking on water.
                    Place(ped, command.TargetPosition, command.Heading);
                    state.Reset();
                    return;

                case RemotePedAction.Idle:
                    DriveIdle(ped, in command, state);
                    return;

                default:
                    DriveLocomotion(ped, in command, state);
                    return;
            }
        }

        private void ApplyVitals(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (command.Action == RemotePedAction.Dead)
            {
                return;
            }

            if (state.WasDead)
            {
                // Coming back from dead: the ped model has to be respawned, because a
                // dead ped in GTA V cannot be revived in place.
                state.WasDead = false;
            }

            if (ped.Health != command.Health)
            {
                ped.Health = command.Health < 1 ? 1 : command.Health;
            }

            if (ped.Armor != command.Armor)
            {
                ped.Armor = command.Armor;
            }
        }

        private void DriveDead(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (!state.WasDead)
            {
                state.WasDead = true;
                state.Reset();
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
                ped.IsInvincible = false;
                ped.Health = 0;
                ped.IsInvincible = true;
                return;
            }

            // Corpses drift. Nudge, do not re-place, or the body twitches.
            if (NetVector3.Distance(ToNet(ped.Position), command.TargetPosition) > RemotePedController.HardCorrectDistance)
            {
                Place(ped, command.TargetPosition, command.Heading);
            }
        }

        private void DriveRagdoll(Ped ped, PedDriveState state)
        {
            if (state.Ragdolling)
            {
                return;
            }

            state.Ragdolling = true;
            state.Reset();
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, true);
            Function.Call(Hash.SET_PED_TO_RAGDOLL, ped.Handle, 2000, 3000, 0, true, true, false);
        }

        private void DriveIdle(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            LeaveRagdoll(ped, state);

            if (state.Tasked)
            {
                Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
                state.Reset();
            }

            if (command.HardCorrect
                || NetVector3.Distance(ToNet(ped.Position), command.TargetPosition) > RemotePedController.ArrivalDistance)
            {
                Place(ped, command.TargetPosition, command.Heading);
            }
            else
            {
                ped.Heading = command.Heading;
            }

            ApplyAim(ped, in command);
        }

        private void DriveLocomotion(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            LeaveRagdoll(ped, state);

            if (command.HardCorrect)
            {
                // Too far behind to walk it off without the ped visibly running through
                // scenery for several seconds.
                Place(ped, command.TargetPosition, command.Heading);
                state.Reset();
            }

            bool destinationMoved =
                NetVector3.Distance(state.TaskTarget, command.TargetPosition) > RetaskDistance;

            // Re-issuing the task every frame restarts the animation and produces a
            // ped that jitters in place, so it is only re-issued when the destination
            // has actually moved or the gait changed.
            if (!state.Tasked || destinationMoved || state.TaskBlend != command.MoveBlendRatio)
            {
                Function.Call(
                    Hash.TASK_GO_STRAIGHT_TO_COORD,
                    ped.Handle,
                    command.TargetPosition.X,
                    command.TargetPosition.Y,
                    command.TargetPosition.Z,
                    command.MoveBlendRatio,
                    TaskTimeoutMilliseconds,
                    command.Heading,
                    0f);

                state.Tasked = true;
                state.TaskTarget = command.TargetPosition;
                state.TaskBlend = command.MoveBlendRatio;
            }

            Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, command.MoveBlendRatio);
            ApplyAim(ped, in command);
        }

        private static void ApplyAim(Ped ped, in RemotePedCommand command)
        {
            if (!command.Aiming)
            {
                return;
            }

            Function.Call(
                Hash.TASK_AIM_GUN_AT_COORD,
                ped.Handle,
                command.AimPosition.X,
                command.AimPosition.Y,
                command.AimPosition.Z,
                200,
                false,
                false);
        }

        private static void LeaveRagdoll(Ped ped, PedDriveState state)
        {
            if (!state.Ragdolling)
            {
                return;
            }

            state.Ragdolling = false;
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, false);
        }

        private static void Place(Ped ped, NetVector3 position, float heading)
        {
            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET, ped.Handle, position.X, position.Y, position.Z, false, false, false);
            ped.Heading = heading;
        }

        public void ApplyRemotePedAppearance(int handle, PedAppearance appearance)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped) || !ped.Exists())
            {
                return;
            }

            for (int slot = 0; slot < PedAppearance.ComponentSlots; slot++)
            {
                PedAppearance.ComponentVariation component = appearance.GetComponent(slot);
                Function.Call(
                    Hash.SET_PED_COMPONENT_VARIATION,
                    ped.Handle,
                    slot,
                    (int)component.Drawable,
                    (int)component.Texture,
                    (int)component.Palette);
            }

            for (int slot = 0; slot < PedAppearance.PropSlots; slot++)
            {
                PedAppearance.PropVariation prop = appearance.GetProp(slot);
                if (prop.IsEmpty)
                {
                    Function.Call(Hash.CLEAR_PED_PROP, ped.Handle, slot);
                    continue;
                }

                Function.Call(Hash.SET_PED_PROP_INDEX, ped.Handle, slot, (int)prop.Drawable, (int)prop.Texture, true);
            }
        }

        public void DestroyRemotePed(int handle)
        {
            _driveState.Remove(handle);
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

        // ------------------------------------------------------------------
        // World
        // ------------------------------------------------------------------
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
            _driveState.Clear();
        }

        // ------------------------------------------------------------------
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

            if (ped.IsShooting)
            {
                flags |= PlayerFlags.Shooting;
            }

            if (ped.IsReloading)
            {
                flags |= PlayerFlags.Reloading;
            }

            if (ped.IsInVehicle())
            {
                flags |= PlayerFlags.InVehicle;
            }

            if (ped.IsGettingIntoVehicle)
            {
                flags |= PlayerFlags.EnteringVehicle;
            }

            if (ped.IsInCover)
            {
                flags |= PlayerFlags.InCover;
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

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        private static NetVector3 ToNet(Vector3 value) => new NetVector3(value.X, value.Y, value.Z);

        private static Vector3 ToGame(NetVector3 value) => new Vector3(value.X, value.Y, value.Z);

        /// <summary>Per-ped bookkeeping so tasks are issued on change rather than every frame.</summary>
        private sealed class PedDriveState
        {
            public bool Tasked;
            public NetVector3 TaskTarget;
            public float TaskBlend;
            public bool Ragdolling;
            public bool WasDead;

            public void Reset()
            {
                Tasked = false;
                TaskTarget = NetVector3.Zero;
                TaskBlend = -1f;
            }
        }
    }
}
