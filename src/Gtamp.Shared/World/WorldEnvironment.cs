using System;

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

        public WorldEnvironment Clone() => new WorldEnvironment
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

        public bool ValueEquals(WorldEnvironment other) =>
            TimeOfDaySeconds == other.TimeOfDaySeconds
            && Math.Abs(ClockScale - other.ClockScale) < 0.0001f
            && WeatherHash == other.WeatherHash
            && NextWeatherHash == other.NextWeatherHash
            && Math.Abs(WeatherTransition - other.WeatherTransition) < 0.004f
            && Math.Abs(WindSpeed - other.WindSpeed) < 0.01f
            && Math.Abs(WindDirection - other.WindDirection) < 0.01f
            && Blackout == other.Blackout;
    }
}
