using System;
using System.Collections.Generic;
using Gtamp.Server.World;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;

namespace Gtamp.Server.Missions
{
    /// <summary>Everything needed to start one activity.</summary>
    public sealed class ActivityDefinition
    {
        public ActivityDefinition(string definitionId, string title)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Title = title ?? string.Empty;
        }

        public string DefinitionId { get; }

        public string Title { get; }

        /// <summary>Seconds before the activity fails on its own. 0 means no deadline.</summary>
        public double TimeLimitSeconds { get; set; }

        /// <summary>
        /// Whether the activity ends by itself once every objective is resolved. Off for
        /// activities whose ending is decided by the mod rather than by its objectives.
        /// </summary>
        public bool CompleteWhenObjectivesResolved { get; set; } = true;

        public List<ActivityObjective> Objectives { get; } = new List<ActivityObjective>();

        public ActivityDefinition WithObjective(byte id, string description)
        {
            Objectives.Add(new ActivityObjective(id, ObjectiveState.Pending, description));
            return this;
        }
    }

    /// <summary>
    /// Runs activities server-side (master prompt section 17).
    /// <para>
    /// Activities are entities, so replication, persistence and the entity inspector
    /// come for free. This class holds only the rules an entity cannot express for
    /// itself: when an activity ends, what happens to its objectives, and what
    /// becomes of its entities when it does.
    /// </para>
    /// <para>
    /// Nothing here knows about police work. A callout is one kind of activity; so
    /// is a race, a heist or a delivery.
    /// </para>
    /// </summary>
    public sealed class ActivityManager
    {
        private readonly ServerWorld _world;
        private readonly LogBus _log;
        private readonly Dictionary<string, ActivityDefinition> _definitions =
            new Dictionary<string, ActivityDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly List<ActivityEntity> _scratch = new List<ActivityEntity>();

        public ActivityManager(ServerWorld world, LogBus log)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public int Started { get; private set; }

        public int Completed { get; private set; }

        public int Failed { get; private set; }

        public IEnumerable<ActivityDefinition> Definitions => _definitions.Values;

        public void Register(ActivityDefinition definition)
        {
            if (_definitions.ContainsKey(definition.DefinitionId))
            {
                throw new InvalidOperationException($"Activity '{definition.DefinitionId}' is already registered.");
            }

            _definitions[definition.DefinitionId] = definition;
            _log.Info(LogCategory.Mod, $"Registered activity '{definition.DefinitionId}'.");
        }

        public bool IsRegistered(string definitionId) => _definitions.ContainsKey(definitionId);

        /// <summary>Starts an instance of a registered activity.</summary>
        public ActivityEntity? Start(string definitionId, uint initiatorPlayerId, double now)
        {
            if (!_definitions.TryGetValue(definitionId, out ActivityDefinition? definition))
            {
                _log.Warning(LogCategory.Mod, $"Cannot start unknown activity '{definitionId}'.");
                return null;
            }

            var activity = new ActivityEntity(_world.AllocateEntityId())
            {
                DefinitionId = definition.DefinitionId,
                Title = definition.Title,
                State = ActivityState.Running,
                InitiatorPlayerId = initiatorPlayerId,
                StartedAt = now,
                DeadlineAt = definition.TimeLimitSeconds > 0 ? now + definition.TimeLimitSeconds : 0d,
            };

            activity.Objectives.AddRange(definition.Objectives);

            if (activity.Objectives.Count > 0)
            {
                activity.Objectives[0] = activity.Objectives[0].WithState(ObjectiveState.Active);
            }

            if (initiatorPlayerId != 0)
            {
                activity.Participants.Add(initiatorPlayerId);
            }

            _world.Spawn(activity);
            Started++;

            _log.Info(
                LogCategory.Mod,
                $"Activity '{definition.DefinitionId}' started as {activity.Id}.",
                $"entity:{activity.Id.Value}");

            return activity;
        }

        public bool AddParticipant(EntityId activityId, uint playerId)
        {
            ActivityEntity? activity = Get(activityId);
            if (activity == null || activity.IsFinished || activity.HasParticipant(playerId))
            {
                return false;
            }

            if (activity.Participants.Count >= ActivityEntity.MaxParticipants)
            {
                return false;
            }

            activity.Participants.Add(playerId);
            _world.Touch(activity);
            return true;
        }

        public bool RemoveParticipant(EntityId activityId, uint playerId)
        {
            ActivityEntity? activity = Get(activityId);
            if (activity == null || !activity.Participants.Remove(playerId))
            {
                return false;
            }

            _world.Touch(activity);
            return true;
        }

        /// <summary>Removes a departing player from every activity they were part of.</summary>
        public void RemoveParticipantEverywhere(uint playerId)
        {
            foreach (ActivityEntity activity in All())
            {
                if (activity.Participants.Remove(playerId))
                {
                    _world.Touch(activity);
                }
            }
        }

        /// <summary>Attaches an entity to an activity so it can be cleaned up with it.</summary>
        public bool AddEntity(EntityId activityId, EntityId entityId)
        {
            ActivityEntity? activity = Get(activityId);
            if (activity == null || activity.Entities.Count >= ActivityEntity.MaxEntities)
            {
                return false;
            }

            if (!activity.Entities.Contains(entityId))
            {
                activity.Entities.Add(entityId);
                _world.Touch(activity);
            }

            return true;
        }

        /// <summary>
        /// Moves an objective on, advancing to the next one and finishing the activity
        /// when the definition says its objectives decide the ending.
        /// </summary>
        public bool SetObjectiveState(EntityId activityId, byte objectiveId, ObjectiveState state, double now)
        {
            ActivityEntity? activity = Get(activityId);
            if (activity == null || activity.IsFinished || !activity.SetObjectiveState(objectiveId, state))
            {
                return false;
            }

            _world.Touch(activity);

            if (state == ObjectiveState.Failed && ShouldFailOnObjectiveFailure(activity))
            {
                Finish(activity, ActivityState.Failed, "an objective failed", now);
                return true;
            }

            ActivateNextObjective(activity);

            if (activity.AllObjectivesResolved() && ShouldCompleteOnObjectives(activity))
            {
                Finish(activity, ActivityState.Completed, "all objectives resolved", now);
            }

            return true;
        }

        public bool Finish(EntityId activityId, ActivityState state, string reason, double now)
        {
            ActivityEntity? activity = Get(activityId);
            return activity != null && Finish(activity, state, reason, now);
        }

        /// <summary>Fails activities whose deadline has passed. Called from the tick loop.</summary>
        public void Update(double now)
        {
            _scratch.Clear();
            foreach (ActivityEntity activity in All())
            {
                if (!activity.IsFinished && activity.DeadlineAt > 0 && now >= activity.DeadlineAt)
                {
                    _scratch.Add(activity);
                }
            }

            foreach (ActivityEntity activity in _scratch)
            {
                Finish(activity, ActivityState.Failed, "the time limit expired", now);
            }
        }

        /// <summary>
        /// Removes finished activities and the entities they own, once every client has
        /// had time to see the ending.
        /// </summary>
        public int CleanUpFinished(double now, double lingerSeconds)
        {
            _scratch.Clear();
            foreach (ActivityEntity activity in All())
            {
                if (activity.IsFinished && now - activity.StartedAt > lingerSeconds)
                {
                    _scratch.Add(activity);
                }
            }

            int removed = 0;
            foreach (ActivityEntity activity in _scratch)
            {
                foreach (EntityId owned in activity.Entities)
                {
                    _world.Destroy(owned);
                }

                _world.Destroy(activity.Id);
                removed++;
            }

            return removed;
        }

        public ActivityEntity? Get(EntityId activityId) => _world.State.Get<ActivityEntity>(activityId);

        public IEnumerable<ActivityEntity> All() => _world.State.OfType<ActivityEntity>();

        private bool Finish(ActivityEntity activity, ActivityState state, string reason, double now)
        {
            if (activity.IsFinished)
            {
                return false;
            }

            activity.State = state;

            // Anything still outstanding is skipped rather than left dangling, so a
            // client rendering the objective list does not show a live objective on a
            // finished activity.
            for (int i = 0; i < activity.Objectives.Count; i++)
            {
                if (activity.Objectives[i].State is ObjectiveState.Pending or ObjectiveState.Active)
                {
                    activity.Objectives[i] = activity.Objectives[i].WithState(ObjectiveState.Skipped);
                }
            }

            _world.Touch(activity);

            if (state == ActivityState.Completed)
            {
                Completed++;
            }
            else if (state == ActivityState.Failed)
            {
                Failed++;
            }

            _log.Info(
                LogCategory.Mod,
                $"Activity '{activity.DefinitionId}' {activity.Id} {state}: {reason}.",
                $"entity:{activity.Id.Value}");

            return true;
        }

        private static void ActivateNextObjective(ActivityEntity activity)
        {
            foreach (ActivityObjective objective in activity.Objectives)
            {
                if (objective.State == ObjectiveState.Active)
                {
                    return;
                }
            }

            for (int i = 0; i < activity.Objectives.Count; i++)
            {
                if (activity.Objectives[i].State == ObjectiveState.Pending)
                {
                    activity.Objectives[i] = activity.Objectives[i].WithState(ObjectiveState.Active);
                    return;
                }
            }
        }

        private bool ShouldCompleteOnObjectives(ActivityEntity activity) =>
            !_definitions.TryGetValue(activity.DefinitionId, out ActivityDefinition? definition)
            || definition.CompleteWhenObjectivesResolved;

        private bool ShouldFailOnObjectiveFailure(ActivityEntity activity) =>
            _definitions.TryGetValue(activity.DefinitionId, out ActivityDefinition? definition)
            && definition.CompleteWhenObjectivesResolved;
    }
}
