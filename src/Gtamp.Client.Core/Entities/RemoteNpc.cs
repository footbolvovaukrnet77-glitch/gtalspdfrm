using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// One networked NPC, plus the sample buffer used to render it smoothly.
    /// <para>
    /// <b>Why this exists at all.</b> <see cref="PedEntity"/> was registered,
    /// serialised, replicated, persisted and accepted by the damage arbiter from the
    /// day the entity system was written — and no client ever created a ped for one.
    /// A server or a mod could spawn a networked NPC, watch it appear in the world
    /// state and in every diagnostic, and see nothing at all in the game. The
    /// roadmap called it complete.
    /// </para>
    /// <para>
    /// An NPC is a character, so it is driven by exactly the same
    /// <see cref="RemotePedController"/> as a player: the gait selection, the
    /// correction thresholds, the ragdoll pose and the death handling are the same
    /// problem, and a second copy of that logic would be a second set of bugs.
    /// </para>
    /// </summary>
    public sealed class RemoteNpc
    {
        private readonly EntityStateBuffer<PedEntity> _buffer = new EntityStateBuffer<PedEntity>();

        public RemoteNpc(EntityId entityId)
        {
            EntityId = entityId;
        }

        public EntityId EntityId { get; }

        /// <summary>Game-side ped handle, 0 when none exists yet.</summary>
        public int PedHandle { get; set; }

        public uint ModelHash { get; private set; }

        /// <summary>Bumped when clothing changes, so it is applied on change rather than every frame.</summary>
        public int AppearanceVersion { get; private set; }

        public PedEntity? Latest => _buffer.Newest;

        public int SampleCount => _buffer.Count;

        public void Push(double serverTime, PedEntity state)
        {
            PedEntity? previous = _buffer.Newest;
            _buffer.Push(serverTime, state);
            ModelHash = state.ModelHash;

            if (previous == null || !previous.Appearance.ValueEquals(state.Appearance))
            {
                AppearanceVersion++;
            }
        }

        public bool TrySample(double renderTime, out RemotePedFrame frame)
        {
            frame = default;
            if (!_buffer.TrySample(
                    renderTime,
                    out PedEntity before,
                    out PedEntity after,
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
            frame.Heading = RemotePlayer.LerpAngle(before.Heading, after.Heading, blend);
            frame.AimPosition = NetVector3.Lerp(before.AimPosition, after.AimPosition, blend);
            frame.Ragdoll = RagdollPose.Lerp(before.Ragdoll, after.Ragdoll, blend);

            // Discrete state comes from the newer sample. A half-interpolated health or
            // flag set is a value the NPC never actually had.
            frame.Health = after.Health;
            frame.Armor = after.Armor;
            frame.Flags = after.Flags;
            frame.Movement = after.Movement;
            frame.CurrentWeaponHash = after.CurrentWeaponHash;
            frame.AnimationHash = after.AnimationHash;
            return true;
        }
    }
}
