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
            uint weaponHash)
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

        public static RemotePedCommand Decide(in RemotePedFrame frame, NetVector3 pedPosition)
        {
            bool dead = frame.Health <= 0 || (frame.Flags & PlayerFlags.Dead) != 0;
            if (dead)
            {
                // A dead ped is placed, not tasked: the game's own death animation
                // owns it from there, and walking a corpse looks like a bug.
                return new RemotePedCommand(
                    RemotePedAction.Dead, frame.Position, frame.Heading, 0f, true, false, frame.AimPosition, 0, 0,
                    frame.CurrentWeaponHash);
            }

            if ((frame.Flags & PlayerFlags.Ragdoll) != 0)
            {
                // Physics owns the ped while it is ragdolling. Correcting its position
                // would fight the solver and produce the twitching it is meant to avoid.
                return new RemotePedCommand(
                    RemotePedAction.Ragdoll,
                    frame.Position,
                    frame.Heading,
                    0f,
                    hardCorrect: false,
                    aiming: false,
                    frame.AimPosition,
                    frame.Health,
                    frame.Armor,
                    frame.CurrentWeaponHash);
            }

            if ((frame.Flags & PlayerFlags.InVehicle) != 0)
            {
                // The vehicle carries the ped. Until vehicles replicate (Phase 3) the
                // ped is held at the replicated position without tasking.
                return new RemotePedCommand(
                    RemotePedAction.InVehicle,
                    frame.Position,
                    frame.Heading,
                    0f,
                    hardCorrect: true,
                    aiming: false,
                    frame.AimPosition,
                    frame.Health,
                    frame.Armor,
                    frame.CurrentWeaponHash);
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
                frame.CurrentWeaponHash);
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
