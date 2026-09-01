using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Entities
{
    /// <summary>The interpolated vehicle state applied to a local vehicle this frame.</summary>
    public struct RemoteVehicleFrame
    {
        public NetVector3 Position;
        public NetVector3 Velocity;
        public NetVector3 AngularVelocity;
        public float Heading;
        public float Pitch;
        public float Roll;
        public float EngineHealth;
        public float BodyHealth;
        public float PetrolTankHealth;
        public float FuelLevel;
        public float DirtLevel;
        public float Steering;
        public float Throttle;
        public float Brake;
        public float EngineRpm;
        public sbyte Gear;
        public VehicleFlags Flags;
        public VehicleDoorStates Doors;
        public byte Windows;
        public VehicleTireStates Tires;
    }

    /// <summary>One vehicle simulated by somebody else, plus its interpolation buffer.</summary>
    public sealed class RemoteVehicle
    {
        private readonly EntityStateBuffer<VehicleEntity> _buffer = new EntityStateBuffer<VehicleEntity>();

        public RemoteVehicle(EntityId entityId)
        {
            EntityId = entityId;
        }

        public EntityId EntityId { get; }

        /// <summary>Game-side vehicle handle, 0 when none exists yet.</summary>
        public int VehicleHandle { get; set; }

        public uint ModelHash { get; private set; }

        /// <summary>Bumped when cosmetic state changes, so paint and mods are applied on change only.</summary>
        public int AppearanceVersion { get; private set; }

        public VehicleEntity? Latest => _buffer.Newest;

        public int SampleCount => _buffer.Count;

        public void Push(double serverTime, VehicleEntity state)
        {
            VehicleEntity? previous = _buffer.Newest;
            _buffer.Push(serverTime, state);
            ModelHash = state.ModelHash;

            if (previous == null || AppearanceChanged(previous, state))
            {
                AppearanceVersion++;
            }
        }

        public bool TrySample(double renderTime, out RemoteVehicleFrame frame)
        {
            frame = default;
            if (!_buffer.TrySample(
                    renderTime,
                    out VehicleEntity before,
                    out VehicleEntity after,
                    out float blend,
                    out double extrapolation))
            {
                return false;
            }

            frame.Position = NetVector3.Lerp(before.Position, after.Position, blend);
            if (extrapolation > 0d)
            {
                frame.Position += after.Velocity * (float)extrapolation;
            }

            frame.Velocity = NetVector3.Lerp(before.Velocity, after.Velocity, blend);
            frame.AngularVelocity = NetVector3.Lerp(before.AngularVelocity, after.AngularVelocity, blend);
            // All three axes together, not one at a time. Blending pitch, roll and
            // yaw independently walks through orientations on no path between the two
            // ends: a car rolling onto its roof swings its nose through the turn on
            // the way, and an aircraft pitched near vertical loses an axis outright.
            NetQuaternion.LerpEuler(
                before.Pitch, before.Roll, before.Heading,
                after.Pitch, after.Roll, after.Heading,
                blend,
                out float pitch, out float roll, out float yaw);

            frame.Heading = yaw;
            frame.Pitch = pitch;
            frame.Roll = roll;
            frame.Steering = Lerp(before.Steering, after.Steering, blend);
            frame.Throttle = Lerp(before.Throttle, after.Throttle, blend);
            frame.Brake = Lerp(before.Brake, after.Brake, blend);
            frame.EngineRpm = Lerp(before.EngineRpm, after.EngineRpm, blend);

            // Discrete state comes from the newer sample. Interpolating a door halfway
            // open, or health that never took that value, invents state.
            frame.EngineHealth = after.EngineHealth;
            frame.BodyHealth = after.BodyHealth;
            frame.PetrolTankHealth = after.PetrolTankHealth;
            frame.FuelLevel = after.FuelLevel;
            frame.DirtLevel = after.DirtLevel;
            frame.Gear = after.Gear;
            frame.Flags = after.Flags;
            frame.Doors = after.Doors;
            frame.Windows = after.Windows;
            frame.Tires = after.Tires;
            return true;
        }

        public void Clear() => _buffer.Clear();

        private static float Lerp(float from, float to, float t) => from + ((to - from) * t);

        private static bool AppearanceChanged(VehicleEntity previous, VehicleEntity current) =>
            !previous.Colors.Equals(current.Colors)
            || previous.Livery != current.Livery
            || previous.WheelType != current.WheelType
            || previous.Extras != current.Extras
            || previous.NeonColor != current.NeonColor
            || previous.NeonLayout != current.NeonLayout
            || previous.RadioStation != current.RadioStation
            || previous.LicensePlate != current.LicensePlate
            || previous.Mods.Count != current.Mods.Count;
    }
}
