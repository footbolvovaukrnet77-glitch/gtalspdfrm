using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.Security
{
    /// <summary>
    /// Validates a state update an owning client reported for an entity it simulates.
    /// <para>
    /// Ownership grants the right to propose, not to decide. The same movement-budget
    /// reasoning as for players applies — a per-update speed check produces false
    /// positives whenever the network bunches updates — with a limit chosen per
    /// entity type, because a jet is not a wheelbarrow.
    /// </para>
    /// </summary>
    public sealed class OwnedEntityValidator
    {
        private readonly Dictionary<EntityId, EntityMotionState> _motion =
            new Dictionary<EntityId, EntityMotionState>();

        public OwnedEntityValidator(AntiCheatSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public AntiCheatSettings Settings { get; }

        /// <summary>Fastest anything in GTA V moves, with headroom for add-on aircraft.</summary>
        public float MaxAircraftSpeed { get; set; } = 400f;

        public ValidationOutcome Validate(NetEntity current, NetEntity proposal, double now)
        {
            var outcome = new ValidationOutcome();

            // --- always-on protocol guards -------------------------------------
            if (!IsFinite(proposal.Position) || !IsFinite(proposal.Velocity))
            {
                return Reject(outcome, ViolationKind.InvalidPosition, "non-finite float in the entity update");
            }

            if (Math.Abs(proposal.Position.X) > Quantize.WorldExtentXY
                || Math.Abs(proposal.Position.Y) > Quantize.WorldExtentXY
                || Math.Abs(proposal.Position.Z) > Quantize.WorldExtentZ)
            {
                return Reject(
                    outcome, ViolationKind.InvalidPosition, $"position {proposal.Position} is outside the world bounds");
            }

            if (proposal is VehicleEntity vehicle && !ValidateVehicle(vehicle, outcome))
            {
                return outcome;
            }

            if (proposal is CharacterEntity character && !ValidateCharacter(character, outcome))
            {
                return outcome;
            }

            if (Settings.Level == AntiCheatLevel.Off)
            {
                return outcome;
            }

            // --- movement budget ------------------------------------------------
            if (!_motion.TryGetValue(current.Id, out EntityMotionState? motion))
            {
                motion = new EntityMotionState();
                _motion[current.Id] = motion;
            }

            double deltaTime = motion.LastUpdateTime > 0 ? now - motion.LastUpdateTime : 0d;
            motion.LastUpdateTime = now;

            double limit = SpeedLimitFor(current) * Settings.SpeedTolerance;
            double capacity = limit * Settings.MovementBurstSeconds;

            if (motion.Budget < 0)
            {
                motion.Budget = capacity;
            }
            else
            {
                motion.Budget = Math.Min(capacity, motion.Budget + (limit * deltaTime));
            }

            float distance = NetVector3.Distance(current.Position, proposal.Position);
            if (distance > motion.Budget)
            {
                return Reject(
                    outcome,
                    ViolationKind.SpeedHack,
                    $"{current.Type} {current.Id} moved {distance:0.#} m with {motion.Budget:0.#} m of budget");
            }

            motion.Budget -= distance;
            return outcome;
        }

        /// <summary>Drops the motion state for an entity that has been destroyed or handed over.</summary>
        public void Forget(EntityId id) => _motion.Remove(id);

        public void Reset(EntityId id)
        {
            if (_motion.TryGetValue(id, out EntityMotionState? motion))
            {
                motion.Budget = -1d;
                motion.LastUpdateTime = 0d;
            }
        }

        private double SpeedLimitFor(NetEntity entity)
        {
            if (entity is VehicleEntity)
            {
                // One limit for every vehicle, set by the fastest thing that flies.
                // Distinguishing a jet from a bicycle would need a model table the
                // server does not have, and getting it wrong grounds honest pilots.
                return MaxAircraftSpeed;
            }

            return entity is CharacterEntity ? Settings.MaxOnFootSpeed : Settings.MaxVehicleSpeed;
        }

        private static bool ValidateVehicle(VehicleEntity vehicle, ValidationOutcome outcome)
        {
            if (vehicle.EngineHealth > 2000f || vehicle.BodyHealth > 2000f || vehicle.PetrolTankHealth > 2000f)
            {
                Reject(outcome, ViolationKind.HealthHack, "vehicle health above the engine's own maximum");
                return false;
            }

            if (float.IsNaN(vehicle.EngineHealth) || float.IsNaN(vehicle.BodyHealth) || float.IsNaN(vehicle.FuelLevel))
            {
                Reject(outcome, ViolationKind.InvalidEvent, "non-finite vehicle health");
                return false;
            }

            if (vehicle.Occupants.Count > 16)
            {
                Reject(outcome, ViolationKind.EntitySpam, $"vehicle reports {vehicle.Occupants.Count} occupants");
                return false;
            }

            return true;
        }

        private static bool ValidateCharacter(CharacterEntity character, ValidationOutcome outcome)
        {
            if (character.Health > character.MaxHealth || character.Health < 0)
            {
                Reject(outcome, ViolationKind.HealthHack, $"health {character.Health} of {character.MaxHealth}");
                return false;
            }

            return true;
        }

        private static ValidationOutcome Reject(ValidationOutcome outcome, ViolationKind kind, string detail)
        {
            outcome.Accepted = false;
            outcome.Violations.Add(new ViolationRecord(kind, detail, ViolationAction.Log));
            return outcome;
        }

        private static bool IsFinite(NetVector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X)
            && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
            && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private sealed class EntityMotionState
        {
            public double LastUpdateTime;
            public double Budget = -1d;
        }
    }
}
