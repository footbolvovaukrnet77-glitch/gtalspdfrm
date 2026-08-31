using System;
using System.Collections.Generic;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Client.Missions
{
    /// <summary>
    /// What a mod implements to react to its own activities.
    /// <para>
    /// The server decides what happens; this is where a mod does the local half —
    /// blips, markers, sounds, UI. It is never asked whether something may happen.
    /// </para>
    /// </summary>
    public interface IActivityHandler
    {
        void OnStarted(ActivityEntity activity);

        void OnObjectiveChanged(ActivityEntity activity, ActivityObjective objective);

        void OnFinished(ActivityEntity activity);
    }

    /// <summary>
    /// Turns replicated activity state into events for the mod that owns it.
    /// <para>
    /// There is no activity protocol: activities are entities, so this watches the
    /// ordinary replicated world and diffs what it sees. That means a client which
    /// missed a snapshot still ends up in the right place — it reacts to the state,
    /// not to a stream of events it might have gaps in.
    /// </para>
    /// </summary>
    public sealed class ActivityWatcher
    {
        private readonly LogBus _log;

        private readonly Dictionary<string, IActivityHandler> _handlers =
            new Dictionary<string, IActivityHandler>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<EntityId, TrackedActivity> _tracked = new Dictionary<EntityId, TrackedActivity>();
        private readonly List<EntityId> _removalBuffer = new List<EntityId>();

        public ActivityWatcher(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public int TrackedCount => _tracked.Count;

        public int HandlerCount => _handlers.Count;

        public void RegisterHandler(string definitionId, IActivityHandler handler)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
            {
                throw new ArgumentException("An activity definition id must not be empty.", nameof(definitionId));
            }

            _handlers[definitionId] = handler ?? throw new ArgumentNullException(nameof(handler));
            _log.Info(LogCategory.Mod, $"Registered an activity handler for '{definitionId}'.");
        }

        public bool TryGet(EntityId id, out ActivityEntity activity)
        {
            if (_tracked.TryGetValue(id, out TrackedActivity? tracked))
            {
                activity = tracked.Snapshot;
                return true;
            }

            activity = null!;
            return false;
        }

        public IEnumerable<ActivityEntity> Activities
        {
            get
            {
                foreach (TrackedActivity tracked in _tracked.Values)
                {
                    yield return tracked.Snapshot;
                }
            }
        }

        /// <summary>Diffs a freshly applied view and raises the events a mod cares about.</summary>
        public void Sync(EntitySnapshotView view)
        {
            foreach (NetEntity entity in view.Entities)
            {
                if (entity is not ActivityEntity activity)
                {
                    continue;
                }

                if (!_tracked.TryGetValue(activity.Id, out TrackedActivity? tracked))
                {
                    tracked = new TrackedActivity((ActivityEntity)activity.Clone());
                    _tracked[activity.Id] = tracked;
                    Raise(activity, handler => handler.OnStarted(activity));
                    continue;
                }

                DiffObjectives(tracked, activity);

                if (tracked.Snapshot.State != activity.State && activity.IsFinished)
                {
                    Raise(activity, handler => handler.OnFinished(activity));
                }

                tracked.Snapshot = (ActivityEntity)activity.Clone();
            }

            _removalBuffer.Clear();
            foreach (EntityId id in _tracked.Keys)
            {
                if (!view.Contains(id))
                {
                    _removalBuffer.Add(id);
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                TrackedActivity tracked = _tracked[id];
                _tracked.Remove(id);

                // An activity removed before its ending was seen — a client that joined
                // late, or one that missed the finishing snapshot. The mod is still told
                // it is over, because the alternative is a mod that never cleans up.
                if (!tracked.Snapshot.IsFinished)
                {
                    tracked.Snapshot.State = ActivityState.Cancelled;
                    Raise(tracked.Snapshot, handler => handler.OnFinished(tracked.Snapshot));
                }
            }
        }

        private void DiffObjectives(TrackedActivity tracked, ActivityEntity current)
        {
            foreach (ActivityObjective objective in current.Objectives)
            {
                if (tracked.Snapshot.TryGetObjective(objective.Id, out ActivityObjective previous)
                    && previous.State == objective.State)
                {
                    continue;
                }

                ActivityObjective changed = objective;
                Raise(current, handler => handler.OnObjectiveChanged(current, changed));
            }
        }

        public void Clear() => _tracked.Clear();

        private void Raise(ActivityEntity activity, Action<IActivityHandler> action)
        {
            if (!_handlers.TryGetValue(activity.DefinitionId, out IActivityHandler? handler))
            {
                return;
            }

            try
            {
                action(handler);
            }
            catch (Exception exception)
            {
                // One mod's handler must not take the session down.
                _log.Error(LogCategory.Mod, $"An activity handler for '{activity.DefinitionId}' threw.", exception);
            }
        }

        private sealed class TrackedActivity
        {
            public TrackedActivity(ActivityEntity snapshot)
            {
                Snapshot = snapshot;
            }

            public ActivityEntity Snapshot { get; set; }
        }
    }
}
