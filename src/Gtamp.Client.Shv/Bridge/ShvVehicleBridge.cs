using System;
using System.Collections.Generic;
using Gtamp.Client.Entities;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using GTA;
using GTA.Math;
using Gtamp.Client.Shv.Interop;
using GTA.Native;
using GtaWorld = GTA.World;
using NetVehicleMod = Gtamp.Shared.Entities.VehicleMod;

namespace Gtamp.Client.Shv.Bridge
{
    /// <summary>
    /// The vehicle and object half of the game bridge, split out so
    /// <see cref="ShvGameBridge"/> stays readable.
    /// <para>
    /// <b>What is not read.</b> Deformation samples and per-wheel suspension are not
    /// replicated. GTA V exposes deformation only through natives that write into a
    /// vehicle rather than read from one, so a faithful copy of body damage cannot be
    /// sampled at all; body health and the door, window and tyre states carry as much
    /// of it as the engine will give up. Documented rather than approximated with
    /// something that would look right and be wrong.
    /// </para>
    /// </summary>
    public sealed class ShvVehicleBridge
    {
        /// <summary>Neon strip bits, matching <see cref="VehicleEntity.NeonLayout"/>'s documented order.</summary>
        private const byte NeonLeft = 1 << 0;
        private const byte NeonRight = 1 << 1;
        private const byte NeonFront = 1 << 2;
        private const byte NeonBack = 1 << 3;

        /// <summary>
        /// Radio index meaning "off". 255 rather than 0, because 0 is a real station
        /// and treating it as silence would mute one station for everybody.
        /// </summary>
        private const byte RadioOff = 255;

        /// <summary>
        /// Hitch search radius the trailer native takes. Generous: the two vehicles
        /// are each a round trip out of position when the attach is issued, and a
        /// radius that just fails leaves the trailer loose with no second attempt
        /// until the towing state changes again.
        /// </summary>
        /// <summary>
        /// How long a sounded horn is asked to hold. Longer than a snapshot interval
        /// so it does not lapse between updates, short enough that a lost "stopped"
        /// message cannot leave a car blaring indefinitely.
        /// </summary>
        private const int HornHoldMilliseconds = 3000;

        private const float TrailerHitchRadius = 4f;

        private const int DoorCount = 8;
        private const int TireCount = 8;

        private readonly LogBus _log;
        private readonly Dictionary<int, Vehicle> _vehicles = new Dictionary<int, Vehicle>();

        /// <summary>
        /// Decides which damage transitions are worth a native call. Without it, a
        /// state the engine answers differently than it was written -- a tyre burst on
        /// the rim is the usual one -- is re-applied every frame, which is an argument
        /// with the engine at sixty hertz and looks like flickering.
        /// </summary>
        /// <summary>Whether each replicated vehicle's horn is currently sounding, so it is started and stopped on the edge.</summary>
        private readonly Dictionary<int, bool> _horning = new Dictionary<int, bool>();

        /// <summary>Which trailer each replicated vehicle is currently hitched to, so it is attached on change only.</summary>
        private readonly Dictionary<int, int> _towing = new Dictionary<int, int>();

        /// <summary>Which entity each replicated object is currently attached to.</summary>
        private readonly Dictionary<int, int> _attachedTo = new Dictionary<int, int>();

        private readonly VehicleDamageTracker _damage = new VehicleDamageTracker();
        private readonly Dictionary<int, Prop> _objects = new Dictionary<int, Prop>();

        public ShvVehicleBridge(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        // ------------------------------------------------------------------
        // Vehicles
        // ------------------------------------------------------------------
        public int CreateRemoteVehicle(uint modelHash, NetVector3 position, float heading)
        {
            try
            {
                var model = new Model(unchecked((int)modelHash));
                if (!model.IsValid)
                {
                    return 0;
                }

                if (!model.IsLoaded)
                {
                    model.Request();
                    return 0;
                }

                Vehicle? vehicle = GtaWorld.CreateVehicle(model, ToGame(position), heading);
                model.MarkAsNoLongerNeeded();
                if (vehicle == null || !vehicle.Exists())
                {
                    return 0;
                }

                // A replicated vehicle is driven by the network, not by local physics
                // or local AI. Leaving it damageable would let one client's collision
                // desynchronise everyone's view of it.
                vehicle.IsInvincible = true;
                vehicle.IsPersistent = true;
                Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle.Handle, 0);

                _vehicles[vehicle.Handle] = vehicle;
                return vehicle.Handle;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Entity, "Could not create a replicated vehicle.", exception);
                return 0;
            }
        }

        public void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame, int trailerHandle)
        {
            if (!_vehicles.TryGetValue(handle, out Vehicle vehicle) || !vehicle.Exists())
            {
                _vehicles.Remove(handle);

                // The per-handle history goes with it. Handles are reused, and a new
                // vehicle inheriting the previous one's would have its whole state
                // judged as "unchanged" and never applied — a car that silently
                // renders undamaged, untowed and mute forever.
                ForgetVehicle(handle);
                return;
            }

            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET,
                vehicle.Handle, frame.Position.X, frame.Position.Y, frame.Position.Z, false, false, false);

            Function.Call(
                Hash.SET_ENTITY_ROTATION, vehicle.Handle, frame.Pitch, frame.Roll, frame.Heading, 2, true);

            vehicle.Velocity = ToGame(frame.Velocity);
            vehicle.RotationVelocity = ToGame(frame.AngularVelocity);

            ApplyIfChanged(vehicle.EngineHealth, frame.EngineHealth, v => vehicle.EngineHealth = v);
            ApplyIfChanged(vehicle.BodyHealth, frame.BodyHealth, v => vehicle.BodyHealth = v);
            ApplyIfChanged(vehicle.PetrolTankHealth, frame.PetrolTankHealth, v => vehicle.PetrolTankHealth = v);
            ApplyIfChanged(vehicle.FuelLevel, frame.FuelLevel, v => vehicle.FuelLevel = v);
            ApplyIfChanged(vehicle.DirtLevel, frame.DirtLevel, v => vehicle.DirtLevel = v);

            vehicle.SteeringAngle = frame.Steering;
            vehicle.CurrentRPM = frame.EngineRpm;
            vehicle.ThrottlePower = frame.Throttle;
            vehicle.BrakePower = frame.Brake;

            bool engineRunning = (frame.Flags & VehicleFlags.EngineRunning) != 0;
            if (vehicle.IsEngineRunning != engineRunning)
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehicle.Handle, engineRunning, true, true);
            }

            ApplyTrailer(vehicle, trailerHandle);
            ApplyLights(vehicle, frame.Flags);
            ApplyHorn(vehicle, frame.Flags);
            VehicleDamageTracker.Change damage = _damage.Observe(handle, frame.Doors, frame.Windows, frame.Tires);
            if (damage.Doors)
            {
                ApplyDoors(vehicle, frame.Doors);
            }

            if (damage.Windows)
            {
                ApplyWindows(vehicle, frame.Windows);
            }

            if (damage.Tires)
            {
                ApplyTires(vehicle, frame.Tires);
            }
        }

        private static void ApplyLights(Vehicle vehicle, VehicleFlags flags)
        {
            bool lights = (flags & VehicleFlags.Lights) != 0;
            bool highBeams = (flags & VehicleFlags.HighBeams) != 0;

            if (vehicle.AreLightsOn != lights || vehicle.AreHighBeamsOn != highBeams)
            {
                Function.Call(Hash.SET_VEHICLE_LIGHTS, vehicle.Handle, lights ? 3 : 4);
                vehicle.AreHighBeamsOn = highBeams;
            }

            bool siren = (flags & VehicleFlags.SirenActive) != 0;
            if (vehicle.IsSirenActive != siren)
            {
                vehicle.IsSirenActive = siren;
            }

            vehicle.IsSirenSilent = (flags & VehicleFlags.SirenMuted) != 0;
            vehicle.IsInteriorLightOn = (flags & VehicleFlags.InteriorLight) != 0;

            Function.Call(
                Hash.SET_VEHICLE_INDICATOR_LIGHTS, vehicle.Handle, 1, (flags & VehicleFlags.LeftIndicator) != 0);
            Function.Call(
                Hash.SET_VEHICLE_INDICATOR_LIGHTS, vehicle.Handle, 0, (flags & VehicleFlags.RightIndicator) != 0);
            Function.Call(
                Hash.SET_VEHICLE_DOORS_LOCKED, vehicle.Handle, (flags & VehicleFlags.Locked) != 0 ? 2 : 1);

            vehicle.IsTaxiLightOn = (flags & VehicleFlags.TaxiLight) != 0;
            vehicle.IsSearchLightOn = (flags & VehicleFlags.SearchLight) != 0;
            Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, vehicle.Handle, (flags & VehicleFlags.Undriveable) != 0);

            if (vehicle.HasRoof)
            {
                bool wantOpen = (flags & VehicleFlags.RoofOpen) != 0;
                bool isOpen = vehicle.RoofState == VehicleRoofState.Opened;

                // On change only: the roof natives start an animation, and re-issuing
                // one every frame keeps a convertible permanently mid-fold.
                if (wantOpen != isOpen)
                {
                    Function.Call(
                        wantOpen ? Hash.LOWER_CONVERTIBLE_ROOF : Hash.RAISE_CONVERTIBLE_ROOF,
                        vehicle.Handle,
                        false);
                }
            }

            // Handbrake is deliberately not applied. SET_VEHICLE_HANDBRAKE has no
            // paired getter, so the flag could never be sampled — it was written as
            // false on every frame, which released the handbrake of every replicated
            // car sixty times a second. A write-only flag is worse than an unapplied
            // one, and the position is being corrected every frame anyway.
        }

        /// <summary>Drops every per-handle record for a vehicle that is gone. Handles are reused.</summary>
        private void ForgetVehicle(int handle)
        {
            _damage.Forget(handle);
            _towing.Remove(handle);
            _horning.Remove(handle);
        }

        /// <summary>
        /// Sounds or silences a replicated horn.
        /// <para>
        /// Edge-triggered, not level-applied: <c>START_VEHICLE_HORN</c> begins a sound
        /// with a duration, so calling it every frame restarts it and produces a
        /// stutter rather than a note. The horn is held with a long duration and
        /// stopped by re-sounding it for zero milliseconds.
        /// </para>
        /// </summary>
        private void ApplyHorn(Vehicle vehicle, VehicleFlags flags)
        {
            bool sounding = (flags & VehicleFlags.HornActive) != 0;
            _horning.TryGetValue(vehicle.Handle, out bool wasSounding);
            if (sounding == wasSounding)
            {
                return;
            }

            _horning[vehicle.Handle] = sounding;
            Function.Call(
                Hash.START_VEHICLE_HORN, vehicle.Handle, sounding ? HornHoldMilliseconds : 0, 0, false);
        }

        private static void ApplyDoors(Vehicle vehicle, VehicleDoorStates doors)
        {
            for (int door = 0; door < DoorCount; door++)
            {
                if (doors.IsBroken(door))
                {
                    Function.Call(Hash.SET_VEHICLE_DOOR_BROKEN, vehicle.Handle, door, true);
                    continue;
                }

                bool open = doors.IsOpen(door);
                bool currentlyOpen = Function.Call<float>(Hash.GET_VEHICLE_DOOR_ANGLE_RATIO, vehicle.Handle, door) > 0.1f;
                if (open == currentlyOpen)
                {
                    continue;
                }

                if (open)
                {
                    Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, vehicle.Handle, door, false, false);
                }
                else
                {
                    Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, vehicle.Handle, door, false);
                }
            }
        }

        private static void ApplyWindows(Vehicle vehicle, byte windows)
        {
            for (int window = 0; window < 8; window++)
            {
                bool intact = (windows & (1 << window)) != 0;
                bool currentlyIntact = Function.Call<bool>(Hash.IS_VEHICLE_WINDOW_INTACT, vehicle.Handle, window);

                if (intact == currentlyIntact)
                {
                    continue;
                }

                if (intact)
                {
                    // The repair direction, which is only safe now that this runs on a
                    // reported transition rather than on every frame the engine and the
                    // report disagree. Without the tracker this is the smash/repair loop.
                    Function.Call(Hash.FIX_VEHICLE_WINDOW, vehicle.Handle, window);
                }
                else
                {
                    Function.Call(Hash.SMASH_VEHICLE_WINDOW, vehicle.Handle, window);
                }
            }
        }

        private static void ApplyTires(Vehicle vehicle, VehicleTireStates tires)
        {
            for (int tire = 0; tire < TireCount; tire++)
            {
                bool burst = tires.IsBurst(tire);
                bool currentlyBurst = Function.Call<bool>(Hash.IS_VEHICLE_TYRE_BURST, vehicle.Handle, tire, false);

                if (burst == currentlyBurst)
                {
                    continue;
                }

                if (burst)
                {
                    Function.Call(Hash.SET_VEHICLE_TYRE_BURST, vehicle.Handle, tire, true, 1000f);
                }
                else
                {
                    Function.Call(Hash.SET_VEHICLE_TYRE_FIXED, vehicle.Handle, tire);
                }
            }
        }

        public void ApplyRemoteVehicleAppearance(int handle, VehicleEntity state)
        {
            if (!_vehicles.TryGetValue(handle, out Vehicle vehicle) || !vehicle.Exists())
            {
                return;
            }

            Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle.Handle, 0);
            ApplyNeonAndRadio(vehicle, state);

            VehicleModCollection mods = vehicle.Mods;
            mods.PrimaryColor = (VehicleColor)state.Colors.Primary;
            mods.SecondaryColor = (VehicleColor)state.Colors.Secondary;
            mods.PearlescentColor = (VehicleColor)state.Colors.Pearlescent;
            mods.RimColor = (VehicleColor)state.Colors.Wheel;
            mods.TrimColor = (VehicleColor)state.Colors.Interior;
            mods.DashboardColor = (VehicleColor)state.Colors.Dashboard;

            if (state.Livery >= 0)
            {
                mods.Livery = state.Livery;
            }

            mods.WheelType = (VehicleWheelType)state.WheelType;

            if (!string.IsNullOrEmpty(state.LicensePlate))
            {
                try
                {
                    Function.Call(
                        Hash.SET_VEHICLE_NUMBER_PLATE_TEXT,
                        vehicle.Handle,
                        NativeString.Arg(state.LicensePlate));
                }
                finally
                {
                    NativeString.Release();
                }
            }

            mods.LicensePlateStyle = (LicensePlateStyle)state.PlateType;

            foreach (NetVehicleMod mod in state.Mods)
            {
                Function.Call(Hash.SET_VEHICLE_MOD, vehicle.Handle, (int)mod.Type, (int)mod.Index, false);
            }

            for (int extra = 0; extra < 16; extra++)
            {
                if (!Function.Call<bool>(Hash.DOES_EXTRA_EXIST, vehicle.Handle, extra))
                {
                    continue;
                }

                bool enabled = (state.Extras & (1 << extra)) != 0;

                // The native's flag is inverted: 0 turns the extra on.
                Function.Call(Hash.SET_VEHICLE_EXTRA, vehicle.Handle, extra, !enabled);
            }
        }

        /// <summary>
        /// Neon strips: which are lit, and what colour.
        /// <para>
        /// <c>HasNeonLight</c> is asked first because a model without the strip
        /// answers <c>IsNeonLightsOn</c> with something meaningless, and reporting
        /// that would light up strips on every other player's copy of a car that has
        /// none.
        /// </para>
        /// </summary>
        private static void ReadNeon(Vehicle vehicle, VehicleEntity into)
        {
            VehicleModCollection neon = vehicle.Mods;
            byte layout = 0;
            if (neon.HasNeonLight(VehicleNeonLight.Left) && neon.IsNeonLightsOn(VehicleNeonLight.Left))
            {
                layout |= NeonLeft;
            }

            if (neon.HasNeonLight(VehicleNeonLight.Right) && neon.IsNeonLightsOn(VehicleNeonLight.Right))
            {
                layout |= NeonRight;
            }

            if (neon.HasNeonLight(VehicleNeonLight.Front) && neon.IsNeonLightsOn(VehicleNeonLight.Front))
            {
                layout |= NeonFront;
            }

            if (neon.HasNeonLight(VehicleNeonLight.Back) && neon.IsNeonLightsOn(VehicleNeonLight.Back))
            {
                layout |= NeonBack;
            }

            into.NeonLayout = layout;

            System.Drawing.Color colour = neon.NeonLightsColor;
            into.NeonColor = unchecked((uint)((colour.R << 16) | (colour.G << 8) | colour.B));
        }

        /// <summary>
        /// The station the local player is listening to, or <see cref="RadioOff"/>.
        /// <para>
        /// There is no native for "what station is <em>this vehicle</em> playing"; the
        /// radio is a property of the listening player. That is exactly right here
        /// anyway — the only vehicle this client reports is the one it is driving.
        /// </para>
        /// </summary>
        private static byte ReadRadioStation(Vehicle vehicle)
        {
            if (!Function.Call<bool>(Hash.IS_VEHICLE_RADIO_ON, vehicle.Handle))
            {
                return RadioOff;
            }

            int index = Function.Call<int>(Hash.GET_PLAYER_RADIO_STATION_INDEX);
            return index < 0 || index >= RadioOff ? RadioOff : (byte)index;
        }

        /// <summary>
        /// Applies the neon strips and the radio station.
        /// <para>
        /// Both were replicated from the day <c>VehicleEntity</c> was written and
        /// neither was ever read from the game or written to it: the fields travelled
        /// as their defaults, cost a delta bit and described nothing. Every replicated
        /// car was silent, with its neon off, whatever its owner had set.
        /// </para>
        /// </summary>
        private static void ApplyNeonAndRadio(Vehicle vehicle, VehicleEntity state)
        {
            System.Drawing.Color colour = System.Drawing.Color.FromArgb(
                (int)((state.NeonColor >> 16) & 0xFF),
                (int)((state.NeonColor >> 8) & 0xFF),
                (int)(state.NeonColor & 0xFF));

            VehicleModCollection neon = vehicle.Mods;
            if (neon.HasNeonLights)
            {
                neon.NeonLightsColor = colour;
                SetNeon(neon, VehicleNeonLight.Left, (state.NeonLayout & NeonLeft) != 0);
                SetNeon(neon, VehicleNeonLight.Right, (state.NeonLayout & NeonRight) != 0);
                SetNeon(neon, VehicleNeonLight.Front, (state.NeonLayout & NeonFront) != 0);
                SetNeon(neon, VehicleNeonLight.Back, (state.NeonLayout & NeonBack) != 0);
            }

            ApplyRadioStation(vehicle, state.RadioStation);
        }

        private static void SetNeon(VehicleModCollection neon, VehicleNeonLight light, bool on)
        {
            if (neon.HasNeonLight(light))
            {
                neon.SetNeonLightsOn(light, on);
            }
        }

        /// <summary>
        /// Tunes a replicated vehicle's radio by station <em>name</em>, resolved from
        /// the index on this machine.
        /// <para>
        /// The index is not a stable identity: a client with a radio mod installed
        /// numbers its stations differently from one without, so an index applied
        /// directly plays the wrong station or none. Resolving it to a name here and
        /// dropping it when the name comes back empty means an unknown station leaves
        /// the radio alone instead of throwing or retuning to something arbitrary —
        /// the failure GTACoOp records as "Fix Sync error if radiostation doesn't
        /// exist".
        /// </para>
        /// </summary>
        private static void ApplyRadioStation(Vehicle vehicle, byte station)
        {
            if (station == RadioOff)
            {
                Function.Call(Hash.SET_VEHICLE_RADIO_ENABLED, vehicle.Handle, false);
                return;
            }

            // Read as a pointer and decoded here rather than through
            // Function.Call<string>, which marshals via SHVDN's NativeMemory — the layer
            // that fails outright on a game build the installed ScriptHookVDotNet does
            // not know. See Interop.NativeString.
            string name = NativeString.Read(
                Function.Call<IntPtr>(Hash.GET_RADIO_STATION_NAME, (int)station));
            if (string.IsNullOrEmpty(name))
            {
                // A station this client does not have. Silence is wrong; the wrong
                // station is worse, and an exception is worst of all.
                return;
            }

            Function.Call(Hash.SET_VEHICLE_RADIO_ENABLED, vehicle.Handle, true);
            try
            {
                Function.Call(Hash.SET_VEH_RADIO_STATION, vehicle.Handle, NativeString.Arg(name));
            }
            finally
            {
                NativeString.Release();
            }
        }

        /// <summary>
        /// Hitches or unhitches a replicated trailer.
        /// <para>
        /// <c>TrailerId</c> travelled from the first version of the vehicle entity and
        /// nothing ever attached anything: a lorry and its trailer replicated as two
        /// unrelated vehicles, each corrected to its own reported position, which is
        /// the same trailer jack-knifing through the cab on every screen but the
        /// driver's.
        /// </para>
        /// <para>
        /// Attach only on a change. <c>ATTACH_VEHICLE_TO_TRAILER</c> snaps the trailer
        /// to the hitch, so calling it every frame pins it there rigidly and it stops
        /// swinging behind the cab at all.
        /// </para>
        /// </summary>
        private void ApplyTrailer(Vehicle vehicle, int trailerHandle)
        {
            _towing.TryGetValue(vehicle.Handle, out int towed);
            if (towed == trailerHandle)
            {
                return;
            }

            if (trailerHandle == 0)
            {
                Function.Call(Hash.DETACH_VEHICLE_FROM_TRAILER, vehicle.Handle);
                _towing.Remove(vehicle.Handle);
                return;
            }

            if (!_vehicles.TryGetValue(trailerHandle, out Vehicle trailer) || !trailer.Exists())
            {
                return;
            }

            Function.Call(Hash.ATTACH_VEHICLE_TO_TRAILER, vehicle.Handle, trailer.Handle, TrailerHitchRadius);
            _towing[vehicle.Handle] = trailerHandle;
        }

        /// <summary>Reads a vehicle this client owns so its state can be reported to the server.</summary>
        public bool TryReadVehicle(int handle, VehicleEntity into)
        {
            Vehicle? vehicle = FindVehicle(handle);
            if (vehicle == null)
            {
                return false;
            }

            into.ModelHash = unchecked((uint)vehicle.Model.Hash);
            into.Position = ToNet(vehicle.Position);
            into.Velocity = ToNet(vehicle.Velocity);
            into.AngularVelocity = ToNet(vehicle.RotationVelocity);

            Vector3 rotation = vehicle.Rotation;
            into.Pitch = rotation.X;
            into.Roll = rotation.Y;
            into.Heading = vehicle.Heading;

            into.EngineHealth = vehicle.EngineHealth;
            into.BodyHealth = vehicle.BodyHealth;
            into.PetrolTankHealth = vehicle.PetrolTankHealth;
            into.FuelLevel = vehicle.FuelLevel;
            into.DirtLevel = vehicle.DirtLevel;
            into.Throttle = vehicle.ThrottlePower;
            into.Brake = vehicle.BrakePower;
            into.Steering = vehicle.SteeringAngle;
            into.EngineRpm = vehicle.CurrentRPM;
            into.Gear = (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, vehicle.CurrentGear));
            VehicleModCollection mods = vehicle.Mods;
            into.LicensePlate = mods.LicensePlate ?? string.Empty;
            into.PlateType = (byte)mods.LicensePlateStyle;

            ReadNeon(vehicle, into);
            into.RadioStation = ReadRadioStation(vehicle);

            into.Colors = new VehicleColors(
                (byte)mods.PrimaryColor,
                (byte)mods.SecondaryColor,
                (byte)mods.PearlescentColor,
                (byte)mods.RimColor,
                (byte)mods.TrimColor,
                (byte)mods.DashboardColor);

            into.Livery = (sbyte)Math.Max(-1, Math.Min(sbyte.MaxValue, mods.Livery));
            into.WheelType = (byte)mods.WheelType;

            into.Flags = ReadFlags(vehicle);
            into.Doors = ReadDoors(vehicle);
            into.Windows = ReadWindows(vehicle);
            into.Tires = ReadTires(vehicle);
            into.Extras = ReadExtras(vehicle);
            return true;
        }

        private static VehicleFlags ReadFlags(Vehicle vehicle)
        {
            VehicleFlags flags = VehicleFlags.None;

            if (vehicle.IsEngineRunning)
            {
                flags |= VehicleFlags.EngineRunning;
            }

            if (vehicle.AreLightsOn)
            {
                flags |= VehicleFlags.Lights;
            }

            if (vehicle.AreHighBeamsOn)
            {
                flags |= VehicleFlags.HighBeams;
            }

            if (vehicle.IsSirenActive)
            {
                flags |= VehicleFlags.SirenActive;
            }

            if (vehicle.IsInteriorLightOn)
            {
                flags |= VehicleFlags.InteriorLight;
            }

            if (vehicle.EngineHealth <= 0f)
            {
                flags |= VehicleFlags.Undriveable;
            }

            // The game's own answer to "is this wreck a wreck", rather than a threshold
            // guessed from engine health. It is what tells every other client to draw
            // the explosion, so getting it from anywhere else would be guessing on
            // everyone's behalf.
            if (vehicle.IsDead)
            {
                flags |= VehicleFlags.Burnt;
            }

            if (Function.Call<int>(Hash.GET_VEHICLE_DOOR_LOCK_STATUS, vehicle.Handle) == 2)
            {
                flags |= VehicleFlags.Locked;
            }

            // Everything below was declared from the first version of VehicleFlags and
            // never read. Four of them were nonetheless *applied*, which is worse than
            // not applying: a flag that is written but never sampled is always false,
            // so every replicated car had its indicators forced off sixty times a
            // second whatever its driver was signalling.
            if (vehicle.IsLeftIndicatorLightOn)
            {
                flags |= VehicleFlags.LeftIndicator;
            }

            if (vehicle.IsRightIndicatorLightOn)
            {
                flags |= VehicleFlags.RightIndicator;
            }

            if (vehicle.IsTaxiLightOn)
            {
                flags |= VehicleFlags.TaxiLight;
            }

            if (vehicle.IsSearchLightOn)
            {
                flags |= VehicleFlags.SearchLight;
            }

            if (vehicle.HasRoof && vehicle.RoofState == VehicleRoofState.Opened)
            {
                flags |= VehicleFlags.RoofOpen;
            }

            if (Function.Call<bool>(Hash.IS_HORN_ACTIVE, vehicle.Handle))
            {
                flags |= VehicleFlags.HornActive;
            }

            // Muted is the absence of siren audio while the siren itself is on, which
            // is how a police car runs lights without the wail.
            if (vehicle.IsSirenActive && !Function.Call<bool>(Hash.IS_VEHICLE_SIREN_AUDIO_ON, vehicle.Handle))
            {
                flags |= VehicleFlags.SirenMuted;
            }

            return flags;
        }

        private static VehicleDoorStates ReadDoors(Vehicle vehicle)
        {
            var doors = new VehicleDoorStates(0);
            for (int door = 0; door < DoorCount; door++)
            {
                if (Function.Call<bool>(Hash.IS_VEHICLE_DOOR_DAMAGED, vehicle.Handle, door))
                {
                    doors = doors.WithBroken(door, true);
                    continue;
                }

                if (Function.Call<float>(Hash.GET_VEHICLE_DOOR_ANGLE_RATIO, vehicle.Handle, door) > 0.1f)
                {
                    doors = doors.WithOpen(door, true);
                }
            }

            return doors;
        }

        private static byte ReadWindows(Vehicle vehicle)
        {
            byte windows = 0;
            for (int window = 0; window < 8; window++)
            {
                if (Function.Call<bool>(Hash.IS_VEHICLE_WINDOW_INTACT, vehicle.Handle, window))
                {
                    windows |= (byte)(1 << window);
                }
            }

            return windows;
        }

        private static VehicleTireStates ReadTires(Vehicle vehicle)
        {
            var tires = new VehicleTireStates(0);
            for (int tire = 0; tire < TireCount; tire++)
            {
                if (Function.Call<bool>(Hash.IS_VEHICLE_TYRE_BURST, vehicle.Handle, tire, true))
                {
                    tires = tires.WithBurst(tire, true);
                }
                else if (Function.Call<bool>(Hash.IS_VEHICLE_TYRE_BURST, vehicle.Handle, tire, false))
                {
                    tires = tires.WithPunctured(tire, true);
                }
            }

            return tires;
        }

        private static ushort ReadExtras(Vehicle vehicle)
        {
            ushort extras = 0;
            for (int extra = 0; extra < 16; extra++)
            {
                if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, vehicle.Handle, extra)
                    && Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, vehicle.Handle, extra))
                {
                    extras |= (ushort)(1 << extra);
                }
            }

            return extras;
        }

        public void DestroyRemoteVehicle(int handle)
        {
            if (!_vehicles.TryGetValue(handle, out Vehicle vehicle))
            {
                return;
            }

            _vehicles.Remove(handle);
            ForgetVehicle(handle);
            try
            {
                if (vehicle.Exists())
                {
                    vehicle.MarkAsNoLongerNeeded();
                    vehicle.Delete();
                }
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Entity, $"Could not delete replicated vehicle {handle}: {exception.Message}");
            }
        }

        public bool IsRemoteVehicleValid(int handle) =>
            handle != 0 && _vehicles.TryGetValue(handle, out Vehicle vehicle) && vehicle.Exists();

        public int GetLocalPlayerVehicleHandle()
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || !ped.IsInVehicle())
            {
                return 0;
            }

            Vehicle? vehicle = ped.CurrentVehicle;

            // Only the driver reports a vehicle. A passenger reporting it would mean
            // two clients streaming the same entity, each overwriting the other.
            if (vehicle == null || !vehicle.Exists() || vehicle.Driver != ped)
            {
                return 0;
            }

            return vehicle.Handle;
        }

        public uint GetVehicleModel(int handle)
        {
            Vehicle? vehicle = FindVehicle(handle);
            return vehicle == null ? 0u : unchecked((uint)vehicle.Model.Hash);
        }

        /// <summary>
        /// Draws the explosion of a vehicle somebody else's game destroyed.
        /// <para>
        /// <b>Damage scale zero, and that is not a detail.</b> The server arbitrates
        /// damage from the victim's own report, exactly as it does for gunfire; an
        /// explosion that also wounded would wound once per client that happened to
        /// draw it. The visual and the audio are the whole point of the call.
        /// </para>
        /// <para>
        /// What zero damage does <i>not</i> remove is force: an explosion is a physics
        /// event, and there is no native flag that suppresses the shove while keeping
        /// the fireball. So a car blowing up next to you can still move you. That is
        /// the same trade every collision in this framework already makes — each client
        /// simulates its own physics and the server arbitrates the health that results
        /// — and it is stated rather than hidden.
        /// </para>
        /// </summary>
        public void PlayVehicleExplosion(int vehicleHandle)
        {
            Vehicle? vehicle = FindVehicle(vehicleHandle);
            if (vehicle == null || !vehicle.Exists())
            {
                return;
            }

            Vector3 position = vehicle.Position;
            Function.Call(
                Hash.ADD_EXPLOSION,
                position.X, position.Y, position.Z,
                (int)ExplosionType.Car,
                0f,     // damageScale — see above
                true,   // isAudible
                false,  // isInvisible
                1f);    // cameraShake
        }

        public void SeatRemotePedInVehicle(int pedHandle, int vehicleHandle, sbyte seat)
        {
            if (pedHandle == 0 || vehicleHandle == 0)
            {
                return;
            }

            Function.Call(Hash.SET_PED_INTO_VEHICLE, pedHandle, vehicleHandle, (int)seat);
        }

        /// <summary>
        /// Resolves a handle to a vehicle, whether we created it or the game did.
        /// A vehicle the local player got into is the game's, not ours, so the lookup
        /// falls through to the entity pool.
        /// </summary>
        private Vehicle? FindVehicle(int handle)
        {
            if (_vehicles.TryGetValue(handle, out Vehicle known))
            {
                return known.Exists() ? known : null;
            }

            if (handle == 0)
            {
                return null;
            }

            var vehicle = (Vehicle?)Entity.FromHandle(handle);
            return vehicle != null && vehicle.Exists() ? vehicle : null;
        }

        // ------------------------------------------------------------------
        // Objects
        // ------------------------------------------------------------------
        public int CreateRemoteObject(uint modelHash, NetVector3 position, float heading)
        {
            try
            {
                var model = new Model(unchecked((int)modelHash));
                if (!model.IsValid)
                {
                    return 0;
                }

                if (!model.IsLoaded)
                {
                    model.Request();
                    return 0;
                }

                Prop? prop = GtaWorld.CreatePropNoOffset(model, ToGame(position), false);
                model.MarkAsNoLongerNeeded();
                if (prop == null || !prop.Exists())
                {
                    return 0;
                }

                prop.Heading = heading;
                prop.IsPersistent = true;
                _objects[prop.Handle] = prop;
                return prop.Handle;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Entity, "Could not create a replicated object.", exception);
                return 0;
            }
        }

        public void ApplyRemoteObject(int handle, ObjectEntity state, int attachParentHandle)
        {
            if (!_objects.TryGetValue(handle, out Prop prop) || !prop.Exists())
            {
                _objects.Remove(handle);
                _attachedTo.Remove(handle);
                return;
            }

            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET,
                prop.Handle, state.Position.X, state.Position.Y, state.Position.Z, false, false, false);

            Function.Call(Hash.SET_ENTITY_ROTATION, prop.Handle, state.Pitch, state.Roll, state.Heading, 2, true);

            prop.IsVisible = state.HasFlag(ObjectFlags.Visible);
            prop.IsCollisionEnabled = state.HasFlag(ObjectFlags.HasCollision);
            prop.IsPositionFrozen = state.HasFlag(ObjectFlags.Frozen);

            ApplyAttachment(prop, state, attachParentHandle);
        }

        /// <summary>
        /// Attaches a replicated object to whatever it says it is attached to.
        /// <para>
        /// <c>AttachedToId</c>, <c>AttachOffset</c> and <c>AttachBone</c> were
        /// declared, serialised, delta encoded and persisted, and nothing ever called
        /// an attach native: a briefcase carried by a player, a light bar on a car, a
        /// crate on a forklift all sat at their last replicated world position while
        /// the thing carrying them drove off.
        /// </para>
        /// <para>
        /// The position write above and an attachment are mutually exclusive — the
        /// game ignores coordinates on an attached entity — so the order matters only
        /// for the frame an object detaches on, where the coordinate wins and the
        /// object lands where it was left.
        /// </para>
        /// </summary>
        private void ApplyAttachment(Prop prop, ObjectEntity state, int attachParentHandle)
        {
            _attachedTo.TryGetValue(prop.Handle, out int attached);

            if (!state.IsAttached || attachParentHandle == 0)
            {
                // Attached to something this client does not have is the same as not
                // attached, as far as this machine can act on it.
                if (attached != 0)
                {
                    Function.Call(Hash.DETACH_ENTITY, prop.Handle, true, true);
                    _attachedTo.Remove(prop.Handle);
                }

                return;
            }

            if (attached == attachParentHandle)
            {
                return;
            }

            Function.Call(
                Hash.ATTACH_ENTITY_TO_ENTITY,
                prop.Handle,
                attachParentHandle,
                (int)state.AttachBone,
                state.AttachOffset.X, state.AttachOffset.Y, state.AttachOffset.Z,
                0f, 0f, 0f,
                false,  // p9
                false,  // useSoftPinning
                state.HasFlag(ObjectFlags.HasCollision),
                false,  // isPed
                2,      // vertexIndex: rotation order, matching SET_ENTITY_ROTATION above
                true);  // fixedRot

            _attachedTo[prop.Handle] = attachParentHandle;
        }

        public void DestroyRemoteObject(int handle)
        {
            if (!_objects.TryGetValue(handle, out Prop prop))
            {
                return;
            }

            _objects.Remove(handle);
            _attachedTo.Remove(handle);
            try
            {
                if (prop.Exists())
                {
                    prop.MarkAsNoLongerNeeded();
                    prop.Delete();
                }
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Entity, $"Could not delete replicated object {handle}: {exception.Message}");
            }
        }

        public bool IsRemoteObjectValid(int handle) =>
            handle != 0 && _objects.TryGetValue(handle, out Prop prop) && prop.Exists();

        public void CleanUp()
        {
            foreach (Vehicle vehicle in _vehicles.Values)
            {
                TryDelete(vehicle);
            }

            foreach (Prop prop in _objects.Values)
            {
                TryDelete(prop);
            }

            _vehicles.Clear();
            _objects.Clear();
        }

        private static void TryDelete(Entity entity)
        {
            try
            {
                if (entity.Exists())
                {
                    entity.MarkAsNoLongerNeeded();
                    entity.Delete();
                }
            }
            catch (Exception)
            {
                // Best effort during teardown.
            }
        }

        private static void ApplyIfChanged(float current, float target, Action<float> apply)
        {
            if (Math.Abs(current - target) > 0.5f)
            {
                apply(target);
            }
        }

        private static NetVector3 ToNet(Vector3 value) => new NetVector3(value.X, value.Y, value.Z);

        private static Vector3 ToGame(NetVector3 value) => new Vector3(value.X, value.Y, value.Z);
    }
}
