using System.Collections.Generic;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// A time-indexed ring of entity snapshots, used to replay a remote entity a
    /// fixed delay behind the server clock.
    /// <para>
    /// Snapshots arrive at 20 Hz while the game renders at 60+; playing them raw
    /// steps visibly. The buffer keeps the samples either side of the render time so
    /// the caller can interpolate between them, and caps how far past the newest
    /// sample it will extrapolate — guessing far ahead of the last known state
    /// produces entities that slide through scenery and then snap back, which reads
    /// worse than a brief stall.
    /// </para>
    /// </summary>
    public sealed class EntityStateBuffer<T>
        where T : NetEntity
    {
        public const double MaxExtrapolation = 0.25;
        public const int Capacity = 16;

        private readonly List<Sample> _samples = new List<Sample>();

        public int Count => _samples.Count;

        public double NewestTime => _samples.Count > 0 ? _samples[_samples.Count - 1].Time : 0d;

        public T? Newest => _samples.Count > 0 ? _samples[_samples.Count - 1].State : null;

        public void Push(double time, T state)
        {
            // Out-of-order samples would make interpolation walk backwards.
            if (_samples.Count > 0 && time <= _samples[_samples.Count - 1].Time)
            {
                return;
            }

            _samples.Add(new Sample(time, (T)state.Clone()));
            while (_samples.Count > Capacity)
            {
                _samples.RemoveAt(0);
            }
        }

        /// <summary>
        /// Finds the pair of samples straddling <paramref name="renderTime"/>.
        /// </summary>
        /// <param name="blend">
        /// 0 at <paramref name="before"/>, 1 at <paramref name="after"/>.
        /// </param>
        /// <param name="extrapolationSeconds">
        /// How far past the newest sample the render time is, or 0 when interpolating.
        /// The caller decides what to do with it — a vehicle extrapolates along its
        /// velocity, a door does not.
        /// </param>
        public bool TrySample(double renderTime, out T before, out T after, out float blend, out double extrapolationSeconds)
        {
            before = null!;
            after = null!;
            blend = 0f;
            extrapolationSeconds = 0d;

            if (_samples.Count == 0)
            {
                return false;
            }

            Sample newest = _samples[_samples.Count - 1];
            if (_samples.Count == 1 || renderTime >= newest.Time)
            {
                before = newest.State;
                after = newest.State;
                double ahead = renderTime - newest.Time;
                extrapolationSeconds = ahead > 0 && ahead <= MaxExtrapolation ? ahead : 0d;
                return true;
            }

            Sample oldest = _samples[0];
            if (renderTime <= oldest.Time)
            {
                before = oldest.State;
                after = oldest.State;
                return true;
            }

            for (int i = _samples.Count - 1; i > 0; i--)
            {
                Sample later = _samples[i];
                Sample earlier = _samples[i - 1];
                if (renderTime < earlier.Time || renderTime > later.Time)
                {
                    continue;
                }

                double span = later.Time - earlier.Time;
                before = earlier.State;
                after = later.State;
                blend = span <= 0.0001d ? 1f : (float)((renderTime - earlier.Time) / span);
                return true;
            }

            before = newest.State;
            after = newest.State;
            return true;
        }

        public void Clear() => _samples.Clear();

        private readonly struct Sample
        {
            public Sample(double time, T state)
            {
                Time = time;
                State = state;
            }

            public double Time { get; }

            public T State { get; }
        }
    }
}
