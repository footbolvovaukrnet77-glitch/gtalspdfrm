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
                while (_order.Count > Capacity)
                {
                    _views.Remove(_order.Dequeue());
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
            Latest = EntitySnapshotView.Empty;
        }
    }
}
