using System;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Behavioural state of a networked NPC (master prompt section 11). Separate
    /// from <see cref="PlayerFlags"/> because none of it applies to a human player.
    /// </summary>
    [Flags]
    public enum PedBehaviourFlags : uint
    {
        None = 0,
        Fleeing = 1 << 0,
        Chasing = 1 << 1,
        Attacking = 1 << 2,
        Surrendered = 1 << 3,
        Arrested = 1 << 4,
        Cuffed = 1 << 5,
        Injured = 1 << 6,
        Alerted = 1 << 7,
        InCombat = 1 << 8,
        Persistent = 1 << 9,
        Mission = 1 << 10,
    }

    /// <summary>How aware a ped is of the player or its target.</summary>
    public enum PedAlertLevel : byte
    {
        Calm = 0,
        Suspicious = 1,
        Alerted = 2,
        Combat = 3,
    }

    /// <summary>
    /// A server-owned NPC with a network identity.
    /// <para>
    /// Only peds the framework or a mod explicitly creates are replicated. GTA V's
    /// own ambient population is spawned locally from a seed the framework does not
    /// control and cannot be made identical across clients — see
    /// docs/ENGINE_ANALYSIS.md §4.6.
    /// </para>
    /// </summary>
    public sealed class PedEntity : CharacterEntity
    {
        public PedEntity(EntityId id)
            : base(id, EntityType.Ped)
        {
        }

        public PedBehaviourFlags Behaviour { get; set; }

        public PedAlertLevel AlertLevel { get; set; }

        /// <summary>Hash of the ped's current task, 0 when idle.</summary>
        public uint TaskHash { get; set; }

        /// <summary>Hash of the scenario the ped is playing, 0 when none.</summary>
        public uint ScenarioHash { get; set; }

        /// <summary>Entity the ped is fighting or chasing.</summary>
        public EntityId CombatTargetId { get; set; }

        /// <summary>GTA V relationship group hash; decides who the ped treats as hostile.</summary>
        public uint RelationshipGroupHash { get; set; }

        /// <summary>
        /// Free-form group identifier, so a mod can keep a set of peds together
        /// (a callout's suspects, a gang) without inventing its own registry.
        /// </summary>
        public string GroupId { get; set; } = string.Empty;

        public bool HasBehaviour(PedBehaviourFlags flag) => (Behaviour & flag) == flag;

        public void SetBehaviour(PedBehaviourFlags flag, bool value)
        {
            if (value)
            {
                Behaviour |= flag;
            }
            else
            {
                Behaviour &= ~flag;
            }
        }

        public override NetEntity Clone()
        {
            var clone = new PedEntity(Id)
            {
                Behaviour = Behaviour,
                AlertLevel = AlertLevel,
                TaskHash = TaskHash,
                ScenarioHash = ScenarioHash,
                CombatTargetId = CombatTargetId,
                RelationshipGroupHash = RelationshipGroupHash,
                GroupId = GroupId,
            };

            CopyCharacterTo(clone);
            return clone;
        }
    }

    public sealed class PedEntitySerializer : EntitySerializer<PedEntity>
    {
        public PedEntitySerializer()
            : base((byte)EntityType.Ped, "ped")
        {
        }

        public override NetEntity Create(EntityId id) => new PedEntity(id);

        protected override void DeclareFields(EntityFieldSet<PedEntity> fields)
        {
            CharacterFields.Declare(fields);

            fields
                .Add(
                    "Behaviour",
                    (a, b) => a.Behaviour != b.Behaviour,
                    (w, e) => w.WriteVarUInt((uint)e.Behaviour),
                    (r, e) => e.Behaviour = (PedBehaviourFlags)r.ReadVarUInt())
                .Add(
                    "AlertLevel",
                    (a, b) => a.AlertLevel != b.AlertLevel,
                    (w, e) => w.WriteByte((byte)e.AlertLevel),
                    (r, e) => e.AlertLevel = (PedAlertLevel)r.ReadByte())
                .Add(
                    "TaskHash",
                    (a, b) => a.TaskHash != b.TaskHash,
                    (w, e) => w.WriteUInt32(e.TaskHash),
                    (r, e) => e.TaskHash = r.ReadUInt32())
                .Add(
                    "ScenarioHash",
                    (a, b) => a.ScenarioHash != b.ScenarioHash,
                    (w, e) => w.WriteUInt32(e.ScenarioHash),
                    (r, e) => e.ScenarioHash = r.ReadUInt32())
                .Add(
                    "CombatTargetId",
                    (a, b) => a.CombatTargetId != b.CombatTargetId,
                    (w, e) => w.WriteVarUInt(e.CombatTargetId.Value),
                    (r, e) => e.CombatTargetId = new EntityId(r.ReadVarUInt()))
                .Add(
                    "RelationshipGroupHash",
                    (a, b) => a.RelationshipGroupHash != b.RelationshipGroupHash,
                    (w, e) => w.WriteUInt32(e.RelationshipGroupHash),
                    (r, e) => e.RelationshipGroupHash = r.ReadUInt32())
                .Add(
                    "GroupId",
                    (a, b) => !string.Equals(a.GroupId, b.GroupId, StringComparison.Ordinal),
                    (w, e) => w.WriteString(e.GroupId),
                    (r, e) => e.GroupId = r.ReadString(64));
        }
    }
}
