using System;
using System.Collections.Generic;

namespace Gtamp.Shared.World
{
    /// <summary>
    /// Global, non-entity world state: clock, weather and the blackout flag.
    /// Replicated in every snapshot header because it is tiny and every client
    /// needs it regardless of where they are standing.
    /// </summary>
    public sealed class WorldEnvironment
    {
        /// <summary>In-game time of day in seconds since midnight, 0..86399.</summary>
        public int TimeOfDaySeconds { get; set; } = 12 * 3600;

        /// <summary>How many in-game seconds pass per real second. 1.0 freezes drift to real time.</summary>
        public float ClockScale { get; set; } = 30f;

        /// <summary>GTA V weather type hash (e.g. EXTRASUNNY, RAIN).</summary>
        public uint WeatherHash { get; set; }

        /// <summary>Weather being transitioned to, 0 when stable.</summary>
        public uint NextWeatherHash { get; set; }

        /// <summary>Transition progress 0..1.</summary>
        public float WeatherTransition { get; set; }

        public float WindSpeed { get; set; }

        public float WindDirection { get; set; }

        public bool Blackout { get; set; }

        /// <summary>
        /// Map files the world has switched on: GTA V's own IPLs and anything a mod
        /// asked every client to load.
        /// <para>
        /// Section 16 lists IPL and map add-ons among the things to synchronise, and
        /// this is the part of them that IS state. A map file is content — whether a
        /// player has it is mod negotiation's problem — but whether it is <em>switched
        /// on</em> is a property of the world, and it was the half nobody carried. A
        /// mod that opened the Life Invader lobby on the server opened it on the
        /// server, and every other player walked into a wall where the door was.
        /// </para>
        /// <para>
        /// Names rather than hashes, because REQUEST_IPL takes a name and nothing
        /// turns a hash back into one. They are bounded — see <see cref="MaxIpls"/> —
        /// so a hostile server cannot make a client allocate without limit.
        /// </para>
        /// </summary>
        public List<string> ActiveIpls { get; } = new List<string>();

        /// <summary>
        /// Cap on how many map files may be switched on at once. Far above any real
        /// map and far below what would hurt to receive.
        /// </summary>
        public const int MaxIpls = 64;

        /// <summary>Longest IPL name accepted from the wire.</summary>
        public const int MaxIplNameLength = 64;

        public int Hours => TimeOfDaySeconds / 3600;

        public int Minutes => (TimeOfDaySeconds / 60) % 60;

        public int Seconds => TimeOfDaySeconds % 60;

        public void AdvanceClock(double realSeconds)
        {
            double advanced = TimeOfDaySeconds + (realSeconds * ClockScale);
            TimeOfDaySeconds = (int)(((advanced % 86400d) + 86400d) % 86400d);
        }

        public void SetTime(int hours, int minutes, int seconds)
        {
            int total = (hours * 3600) + (minutes * 60) + seconds;
            TimeOfDaySeconds = ((total % 86400) + 86400) % 86400;
        }

        public WorldEnvironment Clone()
        {
            var clone = new WorldEnvironment
            {
            TimeOfDaySeconds = TimeOfDaySeconds,
            ClockScale = ClockScale,
            WeatherHash = WeatherHash,
            NextWeatherHash = NextWeatherHash,
            WeatherTransition = WeatherTransition,
            WindSpeed = WindSpeed,
            WindDirection = WindDirection,
                Blackout = Blackout,
            };

            clone.ActiveIpls.AddRange(ActiveIpls);
            return clone;
        }

        public bool ValueEquals(WorldEnvironment other) =>
            TimeOfDaySeconds == other.TimeOfDaySeconds
            && Math.Abs(ClockScale - other.ClockScale) < 0.0001f
            && WeatherHash == other.WeatherHash
            && NextWeatherHash == other.NextWeatherHash
            && Math.Abs(WeatherTransition - other.WeatherTransition) < 0.004f
            && Math.Abs(WindSpeed - other.WindSpeed) < 0.01f
            && Math.Abs(WindDirection - other.WindDirection) < 0.01f
            && Blackout == other.Blackout
            && SameIpls(other);

        private bool SameIpls(WorldEnvironment other)
        {
            if (ActiveIpls.Count != other.ActiveIpls.Count)
            {
                return false;
            }

            for (int i = 0; i < ActiveIpls.Count; i++)
            {
                if (!string.Equals(ActiveIpls[i], other.ActiveIpls[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
