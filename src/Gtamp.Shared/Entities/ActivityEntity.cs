using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    public enum ActivityState : byte
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4,
    }

    public enum ObjectiveState : byte
    {
        Pending = 0,
        Active = 1,
        Completed = 2,
        Failed = 3,
        Skipped = 4,
    }

    public readonly struct ActivityObjective : IEquatable<ActivityObjective>
    {
        public ActivityObjective(byte id, ObjectiveState state, string description)
        {
            Id = id;
            State = state;
            Description = description ?? string.Empty;
        }

        public byte Id { get; }

        public ObjectiveState State { get; }

        public string Description { get; }

        public ActivityObjective WithState(ObjectiveState state) => new ActivityObjective(Id, state, Description);

        public bool Equals(ActivityObjective other) =>
            Id == other.Id && State == other.State
            && string.Equals(Description, other.Description, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ActivityObjective other && Equals(other);

        public override int GetHashCode() => (Id << 8) | (int)State;
    }

    /// <summary>
    /// A mission, callout, job or any other structured activity (master prompt
    /// section 17).
    /// <para>
    /// <b>It is an entity.</b> That is the whole design: an activity replicates,
    /// persists, and appears in the entity inspector through exactly the same
    /// machinery as a vehicle, with no mission-specific networking anywhere. Its
    /// participants and objectives reach every client because entity state already
    /// does.
    /// </para>
    /// <para>
    /// Deliberately not LSPDFR-shaped. A callout is one kind of activity; so is a
    /// race, a heist, a delivery job or a mod's own tutorial.
    /// </para>
    /// </summary>
    public sealed class ActivityEntity : NetEntity
    {
        public const int MaxObjectives = 32;
        public const int MaxParticipants = 64;
        public const int MaxEntities = 128;

        public ActivityEntity(EntityId id)
            : base(id, EntityType.Mission)
        {
        }

        /// <summary>Which kind of activity this is, so a mod can recognise its own.</summary>
        public string DefinitionId { get; set; } = string.Empty;

        /// <summary>Human-readable name, shown in the client's activity list.</summary>
        public string Title { get; set; } = string.Empty;

        public ActivityState State { get; set; } = ActivityState.Pending;

        /// <summary>Player who started it, or 0 when the server did.</summary>
        public uint InitiatorPlayerId { get; set; }

        public List<ActivityObjective> Objectives { get; } = new List<ActivityObjective>();

        /// <summary>Players taking part.</summary>
        public List<uint> Participants { get; } = new List<uint>();

        /// <summary>Entities the activity owns — its suspects, its vehicles, its props.</summary>
        public List<EntityId> Entities { get; } = new List<EntityId>();

        /// <summary>Server time the activity started, and when it must end. 0 means no deadline.</summary>
        public double StartedAt { get; set; }

        public double DeadlineAt { get; set; }

        public bool IsFinished =>
            State == ActivityState.Completed || State == ActivityState.Failed || State == ActivityState.Cancelled;

        public bool HasParticipant(uint playerId) => Participants.Contains(playerId);

        public bool TryGetObjective(byte objectiveId, out ActivityObjective objective)
        {
            foreach (ActivityObjective candidate in Objectives)
            {
                if (candidate.Id == objectiveId)
                {
                    objective = candidate;
                    return true;
                }
            }

            objective = default;
            return false;
        }

        public bool SetObjectiveState(byte objectiveId, ObjectiveState state)
        {
            for (int i = 0; i < Objectives.Count; i++)
            {
                if (Objectives[i].Id != objectiveId)
                {
                    continue;
                }

                Objectives[i] = Objectives[i].WithState(state);
                return true;
            }

            return false;
        }

        public bool AllObjectivesResolved()
        {
            foreach (ActivityObjective objective in Objectives)
            {
                if (objective.State == ObjectiveState.Pending || objective.State == ObjectiveState.Active)
                {
                    return false;
                }
            }

            return true;
        }

        public override NetEntity Clone()
        {
            var clone = new ActivityEntity(Id)
            {
                DefinitionId = DefinitionId,
                Title = Title,
                State = State,
                InitiatorPlayerId = InitiatorPlayerId,
                StartedAt = StartedAt,
                DeadlineAt = DeadlineAt,
            };

            clone.Objectives.AddRange(Objectives);
            clone.Participants.AddRange(Participants);
            clone.Entities.AddRange(Entities);
            CopyBaseTo(clone);
            return clone;
        }
    }

    public sealed class ActivityEntitySerializer : EntitySerializer<ActivityEntity>
    {
        public ActivityEntitySerializer()
            : base((byte)EntityType.Mission, "activity")
        {
        }

        public override NetEntity Create(EntityId id) => new ActivityEntity(id);

        protected override void DeclareFields(EntityFieldSet<ActivityEntity> fields)
        {
            fields
                .Add(
                    "DefinitionId",
                    (a, b) => !string.Equals(a.DefinitionId, b.DefinitionId, StringComparison.Ordinal),
                    (w, e) => w.WriteString(e.DefinitionId),
                    (r, e) => e.DefinitionId = r.ReadString(128))
                .Add(
                    "Title",
                    (a, b) => !string.Equals(a.Title, b.Title, StringComparison.Ordinal),
                    (w, e) => w.WriteString(e.Title),
                    (r, e) => e.Title = r.ReadString(128))
                .Add(
                    "State",
                    (a, b) => a.State != b.State,
                    (w, e) => w.WriteByte((byte)e.State),
                    (r, e) => e.State = (ActivityState)r.ReadByte())
                .Add(
                    "InitiatorPlayerId",
                    (a, b) => a.InitiatorPlayerId != b.InitiatorPlayerId,
                    (w, e) => w.WriteVarUInt(e.InitiatorPlayerId),
                    (r, e) => e.InitiatorPlayerId = r.ReadVarUInt())
                .Add(
                    "Objectives",
                    (a, b) => !ListsEqual(a.Objectives, b.Objectives),
                    WriteObjectives,
                    ReadObjectives)
                .Add(
                    "Participants",
                    (a, b) => !ListsEqual(a.Participants, b.Participants),
                    (w, e) =>
                    {
                        w.WriteVarUInt((uint)e.Participants.Count);
                        foreach (uint participant in e.Participants)
                        {
                            w.WriteVarUInt(participant);
                        }
                    },
                    (r, e) =>
                    {
                        uint count = ReadCount(r, ActivityEntity.MaxParticipants, "participants");
                        e.Participants.Clear();
                        for (uint i = 0; i < count; i++)
                        {
                            e.Participants.Add(r.ReadVarUInt());
                        }
                    })
                .Add(
                    "Entities",
                    (a, b) => !ListsEqual(a.Entities, b.Entities),
                    (w, e) =>
                    {
                        w.WriteVarUInt((uint)e.Entities.Count);
                        foreach (EntityId entityId in e.Entities)
                        {
                            w.WriteVarUInt(entityId.Value);
                        }
                    },
                    (r, e) =>
                    {
                        uint count = ReadCount(r, ActivityEntity.MaxEntities, "entities");
                        e.Entities.Clear();
                        for (uint i = 0; i < count; i++)
                        {
                            e.Entities.Add(new EntityId(r.ReadVarUInt()));
                        }
                    })
                .Add(
                    "Timing",
                    (a, b) => Math.Abs(a.StartedAt - b.StartedAt) > 0.01
                              || Math.Abs(a.DeadlineAt - b.DeadlineAt) > 0.01,
                    (w, e) =>
                    {
                        w.WriteDouble(e.StartedAt);
                        w.WriteDouble(e.DeadlineAt);
                    },
                    (r, e) =>
                    {
                        e.StartedAt = r.ReadDouble();
                        e.DeadlineAt = r.ReadDouble();
                    });
        }

        private static void WriteObjectives(NetWriter writer, ActivityEntity entity)
        {
            writer.WriteVarUInt((uint)entity.Objectives.Count);
            foreach (ActivityObjective objective in entity.Objectives)
            {
                writer.WriteByte(objective.Id);
                writer.WriteByte((byte)objective.State);
                writer.WriteString(objective.Description);
            }
        }

        private static void ReadObjectives(NetReader reader, ActivityEntity entity)
        {
            uint count = ReadCount(reader, ActivityEntity.MaxObjectives, "objectives");
            entity.Objectives.Clear();
            for (uint i = 0; i < count; i++)
            {
                entity.Objectives.Add(new ActivityObjective(
                    reader.ReadByte(), (ObjectiveState)reader.ReadByte(), reader.ReadString(256)));
            }
        }

        private static uint ReadCount(NetReader reader, int limit, string what)
        {
            uint count = reader.ReadVarUInt();
            if (count > limit)
            {
                throw new NetSerializationException($"Activity declares {count} {what}; the limit is {limit}.");
            }

            return count;
        }

        private static bool ListsEqual<T>(List<T> a, List<T> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
