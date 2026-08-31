using System;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.World;

namespace Gtamp.Client.World
{
    /// <summary>
    /// The client's mirror of the server world, reconstructed from snapshots.
    /// <para>
    /// Snapshots are not applied to a single mutable world: each one is decoded
    /// against the baseline view it names, producing a new immutable view. That is
    /// what makes the client correct when snapshots arrive out of order or a
    /// baseline acknowledgement is still in flight — the older view is still there
    /// to decode against.
    /// </para>
    /// </summary>
    public sealed class ReplicatedWorld
    {
        private readonly SnapshotHistory _history = new SnapshotHistory();

        public ReplicatedWorld(EntityRegistry registry)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public EntityRegistry Registry { get; }

        public EntitySnapshotView Current { get; private set; } = EntitySnapshotView.Empty;

        /// <summary>Highest snapshot id applied so far. This is what the client acknowledges.</summary>
        public uint LastAppliedSnapshotId => Current.SnapshotId;

        public int SnapshotsApplied { get; private set; }

        public int SnapshotsDropped { get; private set; }

        public int EntityCount => Current.Count;

        public double ServerTime => Current.ServerTime;

        public WorldEnvironment Environment => Current.Environment;

        /// <summary>
        /// Applies one snapshot payload.
        /// </summary>
        /// <returns>
        /// True when the world advanced. False means the snapshot could not be
        /// decoded and the caller must ask the server for a resync; <paramref name="error"/>
        /// says why, and the existing world is left untouched.
        /// </returns>
        public bool TryApply(byte[] payload, out SnapshotHeader? header, out string error)
        {
            header = null;
            error = string.Empty;

            uint snapshotId;
            uint baselineId;
            try
            {
                SnapshotCodec.ReadIds(payload, out snapshotId, out baselineId);
            }
            catch (NetSerializationException exception)
            {
                error = "malformed snapshot header: " + exception.Message;
                SnapshotsDropped++;
                return false;
            }

            if (snapshotId <= Current.SnapshotId)
            {
                // An older snapshot overtook a newer one. Dropping it is correct:
                // the newer view already contains everything this one would say.
                SnapshotsDropped++;
                return false;
            }

            if (!_history.TryGet(baselineId, out EntitySnapshotView baseline))
            {
                error = $"baseline snapshot {baselineId} is no longer in history";
                SnapshotsDropped++;
                return false;
            }

            try
            {
                SnapshotApplyResult result = SnapshotCodec.Apply(payload, baseline, Registry);
                Current = result.View;
                _history.Store(result.View);
                SnapshotsApplied++;
                header = result.Header;
                return true;
            }
            catch (NetSerializationException exception)
            {
                error = exception.Message;
                SnapshotsDropped++;
                return false;
            }
        }

        public bool TryGet(EntityId id, out NetEntity entity) => Current.TryGet(id, out entity);

        public PlayerEntity? GetPlayer(EntityId id) => Current.GetOrNull(id) as PlayerEntity;

        public void Reset()
        {
            _history.Clear();
            Current = EntitySnapshotView.Empty;
        }
    }
}
