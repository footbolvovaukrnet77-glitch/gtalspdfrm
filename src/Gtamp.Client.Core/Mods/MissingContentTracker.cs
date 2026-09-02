using System;
using System.Collections.Generic;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Mods
{
    /// <summary>One model this client cannot resolve, and what wanted it.</summary>
    public sealed class MissingModel
    {
        public MissingModel(uint modelHash, EntityType wantedBy)
        {
            ModelHash = modelHash;
            WantedBy = wantedBy;
        }

        public uint ModelHash { get; }

        /// <summary>The kind of entity that first asked for it.</summary>
        public EntityType WantedBy { get; }

        /// <summary>How many replicated entities have wanted this model.</summary>
        public int EntityCount { get; internal set; }

        /// <summary>The most recent entity that wanted it, for `/entity`.</summary>
        public EntityId LastEntity { get; internal set; }

        /// <summary>Whether something was shown in its place, or nothing at all.</summary>
        public bool Substituted { get; internal set; }

        public override string ToString()
        {
            string fate = Substituted ? "substituted" : "not shown";
            return $"0x{ModelHash:X8} ({WantedBy}, {EntityCount} entity(s), {fate})";
        }
    }

    /// <summary>
    /// Records the model hashes this client cannot resolve (master prompt section 4,
    /// and docs/ENGINE_ANALYSIS.md §4.4).
    /// <para>
    /// <b>Why this exists.</b> Models travel as hashes and are resolved against
    /// locally installed assets, so a player without the vehicle mod their friend is
    /// driving simply has nothing to create. Without this the symptom is an entity
    /// that never appears and no explanation anywhere — the worst possible failure
    /// to diagnose, because everything else looks healthy: the entity is in the
    /// snapshot, the server knows about it, the network is clean.
    /// </para>
    /// <para>
    /// So each unresolvable hash is recorded once, counted, and surfaced through
    /// <c>/diagnostics</c>, <c>/mods</c> and the bug report. The design rule from the
    /// engine analysis is "report it instead of substituting silently"; where a
    /// substitution does happen — a ped falls back to a default body so the player is
    /// visible at all — the record says so rather than letting it pass as correct.
    /// </para>
    /// <para>
    /// Deduplication is the point. Creation is retried every frame, so a logged line
    /// per attempt would be sixty per second per missing car.
    /// </para>
    /// </summary>
    public sealed class MissingContentTracker
    {
        private readonly Dictionary<uint, MissingModel> _models = new Dictionary<uint, MissingModel>();
        private readonly HashSet<EntityId> _countedEntities = new HashSet<EntityId>();
        private readonly LogBus _log;

        public MissingContentTracker(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public IReadOnlyCollection<MissingModel> Models => _models.Values;

        public int Count => _models.Count;

        public bool IsEmpty => _models.Count == 0;

        /// <summary>
        /// Records that <paramref name="entityId"/> wants a model this client cannot
        /// resolve. Returns true the first time a given hash is seen, which is when
        /// the caller should say something.
        /// </summary>
        public bool Report(uint modelHash, EntityType wantedBy, EntityId entityId, bool substituted)
        {
            if (modelHash == 0)
            {
                // Zero is the absence of a value, not a model nobody has. It is what
                // an entity carries between the moment the server creates it and the
                // moment its owner's first state update lands — so every join
                // reported "a mod is missing" at the other players, and 0x00000000
                // went into the bug report's MISSING CONTENT list, where it names
                // nothing anyone could install. Found by the bot the first time two
                // clients were ever connected at once.
                return false;
            }

            if (!_models.TryGetValue(modelHash, out MissingModel? record))
            {
                record = new MissingModel(modelHash, wantedBy);
                _models[modelHash] = record;

                record.EntityCount = 1;
                record.LastEntity = entityId;
                record.Substituted = substituted;
                _countedEntities.Add(entityId);

                _log.Warning(
                    LogCategory.Mod,
                    $"Model 0x{modelHash:X8} is not installed on this client. " +
                    $"{wantedBy} #{entityId.Value} " +
                    (substituted
                        ? "is being shown with a default model."
                        : "will not appear until the mod that provides it is installed."),
                    $"entity:{entityId.Value}");

                return true;
            }

            record.LastEntity = entityId;
            record.Substituted |= substituted;

            // Counted per entity, not per attempt: creation is retried every frame.
            if (_countedEntities.Add(entityId))
            {
                record.EntityCount++;
            }

            return false;
        }

        /// <summary>
        /// Forgets a hash, for the case where the asset became resolvable — a mod
        /// finished streaming, or the player installed it and rejoined.
        /// </summary>
        public void Clear(uint modelHash) => _models.Remove(modelHash);

        public void Clear()
        {
            _models.Clear();
            _countedEntities.Clear();
        }

        /// <summary>One line per missing model, for /diagnostics and the bug report.</summary>
        public IEnumerable<string> Describe()
        {
            foreach (MissingModel model in _models.Values)
            {
                yield return model.ToString();
            }
        }
    }
}
