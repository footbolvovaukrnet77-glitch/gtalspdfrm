using System;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>What a remote ped should be doing this frame.</summary>
    public enum RemotePedAction : byte
    {
        /// <summary>Standing still.</summary>
        Idle = 0,

        Walk = 1,
        Run = 2,
        Sprint = 3,

        /// <summary>Physics-driven; the game moves it, we only trigger the ragdoll.</summary>
        Ragdoll = 4,

        /// <summary>Dead. No tasks, no correction beyond the death position.</summary>
        Dead = 5,

        /// <summary>Seated in a vehicle; the vehicle owns the position (Phase 3).</summary>
        InVehicle = 6,
    }

    /// <summary>The instruction the game bridge executes for one remote ped this frame.</summary>
    public readonly struct RemotePedCommand
    {
        public RemotePedCommand(
            RemotePedAction action,
            NetVector3 targetPosition,
            float heading,
            float moveBlendRatio,
            bool hardCorrect,
            bool aiming,
            NetVector3 aimPosition,
            int health,
            int armor,
            uint weaponHash,
            RagdollPose ragdoll = default,
            PlayerFlags flags = PlayerFlags.None,
            int vehicleHandle = 0,
            sbyte vehicleSeat = -2,
            byte weaponTint = 0,
            System.Collections.Generic.List<uint>? weaponComponents = null)
        {
            Action = action;
            TargetPosition = targetPosition;
            Heading = heading;
            MoveBlendRatio = moveBlendRatio;
            HardCorrect = hardCorrect;
            Aiming = aiming;
            AimPosition = aimPosition;
            Health = health;
            Armor = armor;
            WeaponHash = weaponHash;
            Ragdoll = ragdoll;
            Flags = flags;
            VehicleHandle = vehicleHandle;
            VehicleSeat = vehicleSeat;
            WeaponTint = weaponTint;
            WeaponComponents = weaponComponents;
        }

        public RemotePedAction Action { get; }

        /// <summary>
        /// The weapon this player is holding, or 0 for unarmed.
        /// <para>
        /// It reached the wire, the server and the console long before it reached
        /// anyone's hands: nothing in the ScriptHookVDotNet layer ever armed a remote
        /// ped, so every other player appeared empty-handed whatever they were
        /// carrying. Zero has to travel as deliberately as any other value — a player
        /// who holsters is a state change, and treating 0 as "nothing to do" is how
        /// a remote player ends up permanently holding the last weapon they drew.
        /// </para>
        /// </summary>
        public uint WeaponHash { get; }

        /// <summary>
        /// Where this player's limbs are on their own machine, while they are
        /// ragdolling. <see cref="RagdollPose.None"/> at every other time — the pose
        /// is only read by <see cref="RemotePedAction.Ragdoll"/>.
        /// </summary>
        public RagdollPose Ragdoll { get; }

        /// <summary>
        /// The player's replicated posture flags, passed through whole.
        /// <para>
        /// The action above says what the ped is <em>doing</em>; these say how it
        /// looks while doing it, and the bridge reads the handful it can act on. Most
        /// of them it cannot: see the flag table in docs/ENTITY_SYSTEM.md, which lists
        /// every flag and whether anything applies it, rather than leaving a dozen
        /// values travelling to no effect.
        /// </para>
        /// </summary>
        public PlayerFlags Flags { get; }

        /// <summary>
        /// Game-side handle of the vehicle this ped belongs in, or 0 when there is
        /// none to put it in — the vehicle has not been created on this client yet, or
        /// the ped is on foot.
        /// <para>
        /// Resolved by the manager rather than the controller: the controller works in
        /// replicated ids and knows nothing about what the local game has built.
        /// </para>
        /// </summary>
        public int VehicleHandle { get; }

        /// <summary>GTA V seat index; -1 is the driver seat, -2 means not in a vehicle.</summary>
        public sbyte VehicleSeat { get; }

        /// <summary>
        /// Tint and components fitted to <see cref="WeaponHash"/>. Null when none were
        /// reported — which is not the same as an empty list, and the bridge treats it
        /// differently: null leaves the weapon as it is, empty strips it bare.
        /// </summary>
        public byte WeaponTint { get; }

        public System.Collections.Generic.List<uint>? WeaponComponents { get; }

        public NetVector3 TargetPosition { get; }

        public float Heading { get; }

        /// <summary>GTA V's move-blend convention: 0 still, 1 walk, 2 run, 3 sprint.</summary>
        public float MoveBlendRatio { get; }

        /// <summary>
        /// True when the ped must be placed at <see cref="TargetPosition"/> outright
        /// rather than walked there.
        /// </summary>
        public bool HardCorrect { get; }

        public bool Aiming { get; }

        public NetVector3 AimPosition { get; }

        public int Health { get; }

        public int Armor { get; }

        public bool IsMoving => MoveBlendRatio > 0f;
    }

    /// <summary>
    /// Decides how a remote ped should be driven, given its replicated state and
    /// where its ped currently is.
    /// <para>
    /// <b>Why this exists as its own class.</b> Phase 1 wrote coordinates every
    /// frame, which is positionally correct and visually wrong: GTA V drives
    /// locomotion animation from the ped's task system, so a ped moved by
    /// coordinates slides in an idle pose. The fix is to task the ped to walk and
    /// only correct its position when it has drifted — and that decision has real
    /// logic in it (when to correct, what gait, what to do while ragdolled or dead).
    /// Keeping the decision here rather than in the bridge means it is unit-tested;
    /// the bridge is left with nothing but native calls.
    /// </para>
    /// </summary>
    public static class RemotePedController
    {
        /// <summary>Beyond this error the ped is placed rather than walked. Roughly a second of sprinting.</summary>
        public const float HardCorrectDistance = 8f;

        /// <summary>Inside this distance the ped is treated as arrived and stops, so it does not shuffle in place.</summary>
        public const float ArrivalDistance = 0.4f;

        /// <summary>Replicated speed below which the player is considered stationary regardless of their gait flag.</summary>
        public const float StationarySpeed = 0.35f;

        /// <summary>Distance at which a ped is walked even though its replicated gait says idle.</summary>
        public const float StaleIdleDistance = 1.5f;

        /// <param name="vehicleHandle">
        /// Game handle of the vehicle this character is riding in, resolved by the
        /// caller, or 0 when the local game has no such vehicle.
        /// </param>
        public static RemotePedCommand Decide(in RemotePedFrame frame, NetVector3 pedPosition, int vehicleHandle = 0)
        {
            bool dead = frame.Health <= 0 || (frame.Flags & PlayerFlags.Dead) != 0;
            if (dead)
            {
                // A dead ped is placed, not tasked: the game's own death animation
                // owns it from there, and walking a corpse looks like a bug.
                return new RemotePedCommand(
                    RemotePedAction.Dead, frame.Position, frame.Heading, 0f, true, false, frame.AimPosition, 0, 0,
                    frame.CurrentWeaponHash, RagdollPose.None, frame.Flags, 0, -2,
                    frame.WeaponTint, frame.WeaponComponents);
            }

            if ((frame.Flags & PlayerFlags.Ragdoll) != 0)
            {
                // Physics owns the ped while it is ragdolling, so it is corrected with
                // impulses rather than coordinates — writing positions into a running
                // solver produces the twitching this is meant to avoid.
                //
                // Except when the two copies are no longer describing the same fall.
                // Past RagdollDriver.PlaceDistance a clamped impulse will not close the
                // gap before the ragdoll ends, and a body several seconds of travel
                // away from where it should be is worse than one visible teleport.
                bool lost = NetVector3.Distance(pedPosition, frame.Position) > RagdollDriver.PlaceDistance;

                return new RemotePedCommand(
                    RemotePedAction.Ragdoll,
                    frame.Position,
                    frame.Heading,
                    0f,
                    hardCorrect: lost,
                    aiming: false,
                    frame.AimPosition,
                    frame.Health,
                    frame.Armor,
                    frame.CurrentWeaponHash,
                    frame.Ragdoll,
                    frame.Flags,
                    0,
                    -2,
                    frame.WeaponTint,
                    frame.WeaponComponents);
            }

            if ((frame.Flags & PlayerFlags.InVehicle) != 0)
            {
                // The vehicle carries the ped, so the ped is put in a seat and left
                // alone — a seated ped that is also being placed every frame is a ped
                // fighting the car it is sitting in.
                //
                // Placement is the fallback for when there is no seat to put it in:
                // the vehicle has not been created on this client yet, or it is one
                // this client cannot build. A player standing at their car's
                // coordinates looks wrong; a player at the origin looks broken.
                bool seatable = vehicleHandle != 0 && frame.VehicleSeat > -2;

                return new RemotePedCommand(
                    RemotePedAction.InVehicle,
                    frame.Position,
                    frame.Heading,
                    0f,
                    hardCorrect: !seatable,
                    aiming: false,
                    frame.AimPosition,
                    frame.Health,
                    frame.Armor,
                    frame.CurrentWeaponHash,
                    RagdollPose.None,
                    frame.Flags,
                    seatable ? vehicleHandle : 0,
                    frame.VehicleSeat,
                    frame.WeaponTint,
                    frame.WeaponComponents);
            }

            float distance = NetVector3.Distance(pedPosition, frame.Position);
            bool hardCorrect = distance > HardCorrectDistance;

            RemotePedAction action = ChooseGait(in frame, distance);
            float blend = action switch
            {
                RemotePedAction.Walk => 1f,
                RemotePedAction.Run => 2f,
                RemotePedAction.Sprint => 3f,
                _ => 0f,
            };

            bool aiming = (frame.Flags & (PlayerFlags.Aiming | PlayerFlags.Shooting)) != 0;

            return new RemotePedCommand(
                action,
                frame.Position,
                frame.Heading,
                blend,
                hardCorrect,
                aiming,
                frame.AimPosition,
                frame.Health,
                frame.Armor,
                frame.CurrentWeaponHash,
                RagdollPose.None,
                frame.Flags,
                0,
                -2,
                frame.WeaponTint,
                frame.WeaponComponents);
        }

        private static RemotePedAction ChooseGait(in RemotePedFrame frame, float distance)
        {
            // Close enough that walking the last few centimetres would just make the
            // ped shuffle on the spot.
            if (distance <= ArrivalDistance)
            {
                return RemotePedAction.Idle;
            }

            float speed = frame.Velocity.Length;

            // The replicated gait can be stale — a snapshot taken during a pause, or
            // one lost while the player started moving. If the ped is clearly behind
            // its target, walk regardless of what the gait flag says.
            if (frame.Movement == MovementState.Idle)
            {
                return speed < StationarySpeed && distance < StaleIdleDistance
                    ? RemotePedAction.Idle
                    : RemotePedAction.Walk;
            }

            // Conversely, a gait flag that says sprint while the player is barely
            // moving means they have stopped and the flag has not caught up.
            if (speed < StationarySpeed && distance < StaleIdleDistance)
            {
                return RemotePedAction.Idle;
            }

            return frame.Movement switch
            {
                MovementState.Walk => RemotePedAction.Walk,
                MovementState.Run => RemotePedAction.Run,
                MovementState.Sprint => RemotePedAction.Sprint,
                _ => RemotePedAction.Walk,
            };
        }
    }
}
