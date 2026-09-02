using System.Collections.Generic;
using Gtamp.Shared.Protocol;

namespace Gtamp.Shared.World
{
    /// <summary>
    /// Bounded ring of snapshot views keyed by snapshot id. The server keeps one per
    /// client (so it can encode against whatever the client last acknowledged) and
    /// the client keeps one (so it can decode a delta written against a baseline it
    /// has since moved past).
    /// </summary>
    public sealed class SnapshotHistory
    {
        private readonly Dictionary<uint, EntitySnapshotView> _views = new Dictionary<uint, EntitySnapshotView>();
        private readonly Queue<uint> _order = new Queue<uint>();

        public SnapshotHistory(int capacity = ProtocolConstants.SnapshotHistory)
        {
            Capacity = capacity < 2 ? 2 : capacity;
        }

        public int Capacity { get; }

        /// <summary>
        /// A snapshot id that eviction must not take, or 0 for none.
        /// <para>
        /// The ring is otherwise oldest-first, which is wrong for the one view that
        /// matters most: the baseline the other side is currently encoding against.
        /// A client that falls behind comes back to a backlog of snapshots all
        /// written against the last baseline it was heard to acknowledge, and applying
        /// them stores a view each — so after <see cref="Capacity"/> of them the
        /// baseline every remaining snapshot names has been evicted by the snapshots
        /// that name it, and the rest of the backlog cannot be decoded at all. That is
        /// a resync and a run of dropped snapshots as long as the overrun.
        /// </para>
        /// <para>
        /// Pinning it costs one slot and removes the whole failure. It does not make
        /// the ring unbounded: exactly one id is ever pinned, and it is released as
        /// soon as the far side names a newer one.
        /// </para>
        /// </summary>
        public uint PinnedId { get; set; }

        public int Count => _views.Count;

        public EntitySnapshotView Latest { get; private set; } = EntitySnapshotView.Empty;

        public void Store(EntitySnapshotView view)
        {
            if (_views.ContainsKey(view.SnapshotId))
            {
                _views[view.SnapshotId] = view;
            }
            else
            {
                _views[view.SnapshotId] = view;
                _order.Enqueue(view.SnapshotId);

                // Bounded by the queue length so a pinned id can be passed over
                // without the loop turning on it for ever.
                int rotations = _order.Count;
                while (_order.Count > Capacity && rotations-- > 0)
                {
                    uint oldest = _order.Dequeue();
                    if (oldest == PinnedId && oldest != view.SnapshotId)
                    {
                        _order.Enqueue(oldest);
                        continue;
                    }

                    _views.Remove(oldest);
                }
            }

            if (view.SnapshotId >= Latest.SnapshotId)
            {
                Latest = view;
            }
        }

        public bool TryGet(uint snapshotId, out EntitySnapshotView view)
        {
            if (snapshotId == 0)
            {
                view = EntitySnapshotView.Empty;
                return true;
            }

            return _views.TryGetValue(snapshotId, out view!);
        }

        public void Clear()
        {
            _views.Clear();
            _order.Clear();
            PinnedId = 0;
            Latest = EntitySnapshotView.Empty;
        }
    }
}
