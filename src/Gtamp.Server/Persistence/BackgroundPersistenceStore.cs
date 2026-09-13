using System;
using System.Collections.Generic;
using Gtamp.Shared.Security;
using System.Threading;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Server.Persistence
{
    /// <summary>
    /// Moves persistence writes off the tick thread.
    /// <para>
    /// A save is a disk write, and a disk write on the simulation thread is a stall
    /// every player feels. At Phase 1 scale that stall was microseconds; with
    /// entities persisted as well it is a transaction over every object in the world,
    /// which is not something a 60 Hz loop should be doing inline.
    /// </para>
    /// <para>
    /// Writes are queued and <b>coalesced</b>: only the newest state for each player,
    /// for the world, and for the entity set is kept. A slow disk therefore costs
    /// freshness, never an unbounded queue — which is the failure mode that turns a
    /// slow disk into an out-of-memory crash.
    /// </para>
    /// <para>
    /// Reads stay synchronous. They happen at startup and when a player joins, both
    /// rare enough that a few milliseconds does not matter, and making them
    /// asynchronous would mean a join could not be answered in the tick that
    /// received it.
    /// </para>
    /// </summary>
    public sealed class BackgroundPersistenceStore : IPersistenceStore
    {
        private readonly IPersistenceStore _inner;
        private readonly LogBus _log;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _work = new AutoResetEvent(false);

        private readonly Dictionary<string, PersistedPlayer> _pendingPlayers =
            new Dictionary<string, PersistedPlayer>(StringComparer.Ordinal);

        private PersistedWorld? _pendingWorld;
        private IReadOnlyList<PersistedEntity>? _pendingEntities;

        private Thread? _worker;
        private volatile bool _stopping;

        public BackgroundPersistenceStore(IPersistenceStore inner, LogBus log)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool Enabled => _inner.Enabled;

        /// <summary>Writes actually committed to the underlying store.</summary>
        public int WritesCommitted { get; private set; }

        /// <summary>Writes replaced by a newer one before they reached the disk.</summary>
        public int WritesCoalesced { get; private set; }

        public void Initialize()
        {
            lock (_gate)
            {
                _inner.Initialize();
            }

            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "gtamp-persistence",
            };

            _worker.Start();
        }

        public void SavePlayer(PersistedPlayer player)
        {
            lock (_gate)
            {
                if (_pendingPlayers.ContainsKey(player.IdentityToken))
                {
                    WritesCoalesced++;
                }

                _pendingPlayers[player.IdentityToken] = player;
            }

            _work.Set();
        }

        public void SaveWorld(PersistedWorld world)
        {
            lock (_gate)
            {
                if (_pendingWorld != null)
                {
                    WritesCoalesced++;
                }

                _pendingWorld = world;
            }

            _work.Set();
        }

        public void SaveEntities(IReadOnlyList<PersistedEntity> entities)
        {
            lock (_gate)
            {
                if (_pendingEntities != null)
                {
                    WritesCoalesced++;
                }

                _pendingEntities = entities;
            }

            _work.Set();
        }

        public PersistedPlayer? LoadPlayer(string identityToken)
        {
            lock (_gate)
            {
                // A queued write for this player has not reached the disk yet, and it is
                // newer than anything the disk holds.
                if (_pendingPlayers.TryGetValue(identityToken, out PersistedPlayer? pending))
                {
                    return pending;
                }

                return _inner.LoadPlayer(identityToken);
            }
        }

        public PersistedWorld? LoadWorld()
        {
            lock (_gate)
            {
                return _pendingWorld ?? _inner.LoadWorld();
            }
        }

        /// <summary>
        /// Bans are written straight through, not queued.
        /// <para>
        /// Every other write here is coalesced because it happens on the tick thread
        /// many times a second. A ban happens when an admin types one word, and the
        /// cost of doing it synchronously is a single small transaction. The cost of
        /// queueing it is that a ban issued moments before a crash is not on disk when
        /// the server comes back — precisely when nobody is watching.
        /// </para>
        /// </summary>
        public void SaveBans(IReadOnlyList<BanEntry> bans)
        {
            lock (_gate)
            {
                _inner.SaveBans(bans);
            }
        }

        public IReadOnlyList<BanEntry> LoadBans()
        {
            lock (_gate)
            {
                return _inner.LoadBans();
            }
        }

        public IReadOnlyList<PersistedEntity> LoadEntities()
        {
            lock (_gate)
            {
                return _pendingEntities ?? _inner.LoadEntities();
            }
        }

        public string Describe() => _inner.Describe() + " (buffered)";

        /// <summary>Blocks until every queued write has been committed.</summary>
        public void Flush()
        {
            while (true)
            {
                if (!DrainOnce())
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _work.Set();

            try
            {
                // Give the worker a moment to finish what it is doing, then make sure
                // nothing queued is lost: a shutdown that drops the last save is how a
                // clean restart still loses a session's worth of progress.
                _worker?.Join(TimeSpan.FromSeconds(5));
                Flush();
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Persistence, "The persistence writer did not shut down cleanly.", exception);
            }

            _work.Dispose();
            _inner.Dispose();
        }

        private void Run()
        {
            while (!_stopping)
            {
                _work.WaitOne(250);

                try
                {
                    while (DrainOnce())
                    {
                    }
                }
                catch (Exception exception)
                {
                    // A failed write must not take the writer thread down with it, or
                    // every later save is silently lost.
                    _log.Error(LogCategory.Persistence, "A persistence write failed.", exception);
                }
            }
        }

        /// <summary>Commits one queued item. Returns false when there was nothing to do.</summary>
        private bool DrainOnce()
        {
            PersistedPlayer? player = null;
            PersistedWorld? world = null;
            IReadOnlyList<PersistedEntity>? entities = null;

            lock (_gate)
            {
                foreach (KeyValuePair<string, PersistedPlayer> pair in _pendingPlayers)
                {
                    player = pair.Value;
                    _pendingPlayers.Remove(pair.Key);
                    break;
                }

                if (player == null)
                {
                    entities = _pendingEntities;
                    _pendingEntities = null;

                    if (entities == null)
                    {
                        world = _pendingWorld;
                        _pendingWorld = null;
                    }
                }

                if (player == null && world == null && entities == null)
                {
                    return false;
                }

                // The underlying store is not thread-safe, and reads take the same lock,
                // so the write happens inside it rather than after.
                if (player != null)
                {
                    _inner.SavePlayer(player);
                }
                else if (entities != null)
                {
                    _inner.SaveEntities(entities);
                }
                else if (world != null)
                {
                    _inner.SaveWorld(world);
                }

                WritesCommitted++;
                return true;
            }
        }
    }
}
