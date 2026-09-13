using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.World;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// Makes the traffic on the street the same traffic for everybody.
    /// <para>
    /// Ambient cars are spawned by GTA V itself, on every machine, independently. Two
    /// players standing on the same corner saw entirely different streets, which is
    /// the plainest possible statement that they are not in one world — and section 15
    /// of the specification lists traffic among the things to synchronise.
    /// </para>
    /// <para>
    /// <b>The server does not spawn it, and cannot.</b> Traffic follows GTA V's road
    /// network — its nodes, lanes and junction rules — and that is game data the server
    /// does not have. A server that spawned cars would put them through walls. So the
    /// game goes on spawning traffic on exactly one machine per group of players, that
    /// machine hands what appeared to the server, and everybody including the spawner
    /// sees the replicated copies. Every other client stops spawning its own.
    /// </para>
    /// <para>
    /// <b>What this costs, stated rather than discovered.</b> A client that is not the
    /// source sees no car until the source has offered it and a snapshot has carried
    /// it, so traffic appears a fraction of a second late on arrival in a new street.
    /// A car is replicated as an entity rather than simulated, so its driving is the
    /// source's driving interpolated, not local AI. And the cars and people near a
    /// source are replicated to everybody, which is eighty-odd extra entities sharing a
    /// snapshot budget that cannot be raised — it is an MTU limit, not a policy, and
    /// the attempt to raise it is recorded against `SnapshotByteBudget`. They update
    /// every second or third snapshot where a player updates every one, and that
    /// ordering is correct: a player must never lose priority to a parked car.
    /// </para>
    /// <para>
    /// Pedestrians travel the same way and under their own ceiling, and the two halves
    /// of that are one switch on purpose. Suppressing them without replicating them
    /// would empty the pavements, which is further from one world rather than closer —
    /// so shared cars with local people is a coherent city and a permitted setting,
    /// while shared cars with nobody walking is neither.
    /// </para>
    /// </summary>
    public sealed class AmbientTrafficController
    {
        /// <summary>
        /// How far from the player ambient cars are collected, in metres.
        /// <para>
        /// A little under FiveM's own focus radius of 424 units: far enough that a car
        /// is handed over before a player can see it, close enough that a source is not
        /// offering the server a whole district it is about to drive away from.
        /// </para>
        /// </summary>
        public const float CollectionRadius = 400f;

        /// <summary>
        /// Most ambient cars this client will hold on the server's behalf at once.
        /// <para>
        /// A ceiling rather than a target. GTA V will happily keep spawning as a player
        /// drives, and without a limit a long drive turns into a thousand-entity world
        /// that every client pays for. Sixty is about what is visible from one place.
        /// </para>
        /// </summary>
        public const int MaxAdopted = 60;

        /// <summary>
        /// Most ambient pedestrians held at once, on top of the cars.
        /// <para>
        /// Smaller than the car ceiling on purpose. A pedestrian is replicated at the
        /// same cost as a player and is worth a great deal less: nobody has ever
        /// noticed the twelfth person on a pavement, and everybody notices a car that
        /// updates twice a second.
        /// </para>
        /// </summary>
        public const int MaxAdoptedPeds = 24;

        /// <summary>
        /// How often ambient cars are offered to the server, in seconds.
        /// <para>
        /// Not every frame: collecting them walks the game's vehicle list, and a car
        /// that appeared a quarter of a second ago is not yet anywhere anyone is
        /// looking.
        /// </para>
        /// </summary>
        public const double OfferIntervalSeconds = 0.5;

        private readonly IGameBridge _bridge;
        private readonly OwnedEntityStreamer _streamer;
        private readonly List<int> _ambient = new List<int>();
        private readonly List<int> _ambientPeds = new List<int>();
        private double _nextOffer;

        public AmbientTrafficController(IGameBridge bridge, OwnedEntityStreamer streamer)
        {
            _bridge = bridge;
            _streamer = streamer;
        }

        /// <summary>Whether shared traffic is wanted at all; false restores per-client traffic.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether the server has made this client the source for its area. Set from
        /// the snapshot header, which carries it in every snapshot so it can never go
        /// stale.
        /// </summary>
        public bool IsSource { get; set; }

        /// <summary>Ambient cars this client has offered to the server since connecting.</summary>
        public int Offered { get; private set; }

        /// <summary>Ambient pedestrians this client has offered since connecting.</summary>
        public int PedsOffered { get; private set; }

        /// <summary>
        /// Whether pedestrians are shared as well as traffic.
        /// <para>
        /// Separate from <see cref="Enabled"/> because the two failure modes are not
        /// the same. Shared cars with local people is a coherent city. Shared cars with
        /// NO people is not, and that is what this being half-on would produce, so it
        /// is one switch that turns both halves of the pedestrian work on together.
        /// </para>
        /// </summary>
        public bool SharePedestrians { get; set; } = true;

        /// <summary>Frames this client has told the game not to spawn traffic.</summary>
        public int SuppressedFrames { get; private set; }

        /// <summary>
        /// Called once per frame. Suppression has to be per-frame because the natives
        /// behind it are; the offering is rate-limited inside.
        /// </summary>
        public void Update(EntitySnapshotView view, double now)
        {
            if (!Enabled)
            {
                return;
            }

            if (!IsSource)
            {
                _bridge.SuppressAmbientTrafficThisFrame();
                if (SharePedestrians)
                {
                    _bridge.SuppressAmbientPedsThisFrame();
                }

                SuppressedFrames++;
                return;
            }

            if (now < _nextOffer)
            {
                return;
            }

            _nextOffer = now + OfferIntervalSeconds;

            OfferVehicles(view, now);

            // Outside the vehicle ceiling, not inside it: a street full of traffic would
            // otherwise leave no room for anybody walking on it, because the cars are
            // collected first and there are more of them.
            OfferPedestrians(view, now);
        }

        private void OfferVehicles(EntitySnapshotView view, double now)
        {
            // The cap counts everything this client holds for the server that is not a
            // pedestrian: the player's own car and anything a mod spawned are the same
            // weight in the world and on the wire as an ambient one.
            int room = MaxAdopted - (_streamer.OwnedCount - PedsHeld);
            if (room <= 0)
            {
                return;
            }

            _bridge.SampleAmbientVehicles(_ambient, CollectionRadius);

            for (int i = 0; i < _ambient.Count && room > 0; i++)
            {
                if (_streamer.OwnsHandle(_ambient[i]))
                {
                    continue;
                }

                _streamer.RegisterVehicle(_ambient[i], view, now, ambient: true);
                Offered++;
                room--;
            }
        }

        /// <summary>
        /// Hands the people on the pavement over, under their own ceiling.
        /// <para>
        /// Counted separately from the cars rather than sharing one budget, because a
        /// street full of traffic would otherwise leave no room for anybody walking on
        /// it — the cars are collected first and there are more of them.
        /// </para>
        /// </summary>
        private void OfferPedestrians(EntitySnapshotView view, double now)
        {
            if (!SharePedestrians)
            {
                return;
            }

            int room = MaxAdoptedPeds - PedsHeld;
            if (room <= 0)
            {
                return;
            }

            _bridge.SampleAmbientPeds(_ambientPeds, CollectionRadius);

            for (int i = 0; i < _ambientPeds.Count && room > 0; i++)
            {
                if (_streamer.OwnsHandle(_ambientPeds[i]))
                {
                    continue;
                }

                _streamer.RegisterPed(_ambientPeds[i], view, now, ambient: true);
                PedsOffered++;
                PedsHeld++;
                room--;
            }
        }

        /// <summary>
        /// Pedestrians this client currently holds for the server. Counted here rather
        /// than asked of the streamer, which knows how many entities it owns and not
        /// which of them are people.
        /// </summary>
        public int PedsHeld { get; private set; }

        /// <summary>Forgotten on disconnect, like everything else that was true of a session.</summary>
        public void Reset()
        {
            IsSource = false;
            _nextOffer = 0;
            PedsHeld = 0;
            _ambient.Clear();
            _ambientPeds.Clear();
        }
    }
}
