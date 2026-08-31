using System;
using System.Collections.Generic;
using Gtamp.Client.Entities;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using GTA;
using GTA.Math;
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
        private const int DoorCount = 8;
        private const int TireCount = 8;

        private readonly LogBus _log;
        private readonly Dictionary<int, Vehicle> _vehicles = new Dictionary<int, Vehicle>();
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

        public void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame)
        {
            if (!_vehicles.TryGetValue(handle, out Vehicle vehicle) || !vehicle.Exists())
            {
                _vehicles.Remove(handle);
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

            ApplyLights(vehicle, frame.Flags);
            ApplyDoors(vehicle, frame.Doors);
            ApplyWindows(vehicle, frame.Windows);
            ApplyTires(vehicle, frame.Tires);
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
            Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle.Handle, (flags & VehicleFlags.Handbrake) != 0);
            Function.Call(
                Hash.SET_VEHICLE_DOORS_LOCKED, vehicle.Handle, (flags & VehicleFlags.Locked) != 0 ? 2 : 1);
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
                if (intact)
                {
                    continue;
                }

                if (Function.Call<bool>(Hash.IS_VEHICLE_WINDOW_INTACT, vehicle.Handle, window))
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
                Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, vehicle.Handle, state.LicensePlate);
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

            if (Function.Call<int>(Hash.GET_VEHICLE_DOOR_LOCK_STATUS, vehicle.Handle) == 2)
            {
                flags |= VehicleFlags.Locked;
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

        public void ApplyRemoteObject(int handle, ObjectEntity state)
        {
            if (!_objects.TryGetValue(handle, out Prop prop) || !prop.Exists())
            {
                _objects.Remove(handle);
                return;
            }

            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET,
                prop.Handle, state.Position.X, state.Position.Y, state.Position.Z, false, false, false);

            Function.Call(Hash.SET_ENTITY_ROTATION, prop.Handle, state.Pitch, state.Roll, state.Heading, 2, true);

            prop.IsVisible = state.HasFlag(ObjectFlags.Visible);
            prop.IsCollisionEnabled = state.HasFlag(ObjectFlags.HasCollision);
            prop.IsPositionFrozen = state.HasFlag(ObjectFlags.Frozen);
        }

        public void DestroyRemoteObject(int handle)
        {
            if (!_objects.TryGetValue(handle, out Prop prop))
            {
                return;
            }

            _objects.Remove(handle);
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
