using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.World
{
    /// <summary>
    /// The stock GTA V weather types.
    /// <para>
    /// Weather travels on the wire as a joaat hash of the name rather than an index,
    /// so a server can name a weather type a client has never heard of (a weather mod)
    /// without the protocol changing. Clients resolve the hash back to a name here;
    /// an unknown hash is left alone rather than being forced to a wrong value.
    /// </para>
    /// </summary>
    public static class WeatherCatalog
    {
        public static readonly string[] Names =
        {
            "EXTRASUNNY",
            "CLEAR",
            "CLOUDS",
            "SMOG",
            "FOGGY",
            "OVERCAST",
            "RAIN",
            "THUNDER",
            "CLEARING",
            "NEUTRAL",
            "SNOW",
            "BLIZZARD",
            "SNOWLIGHT",
            "XMAS",
            "HALLOWEEN",
        };

        private static readonly Dictionary<uint, string> ByHash = BuildIndex();

        public static uint HashOf(string name) => GameHash.Joaat(name);

        public static bool TryGetName(uint hash, out string name) => ByHash.TryGetValue(hash, out name!);

        public static bool IsKnown(uint hash) => ByHash.ContainsKey(hash);

        private static Dictionary<uint, string> BuildIndex()
        {
            var index = new Dictionary<uint, string>();
            foreach (string name in Names)
            {
                index[GameHash.Joaat(name)] = name;
            }

            return index;
        }
    }
}
