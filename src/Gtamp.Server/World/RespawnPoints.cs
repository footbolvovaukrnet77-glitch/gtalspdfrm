using Gtamp.Shared.Core;

namespace Gtamp.Server.World
{
    /// <summary>
    /// Where a dead player comes back.
    /// <para>
    /// These are GTA V's hospital entrances, and the nearest one to where the player
    /// died is chosen — the same behaviour single player has, so respawning does not
    /// feel like a multiplayer artefact.
    /// </para>
    /// </summary>
    public static class RespawnPoints
    {
        public static readonly RespawnPoint[] Hospitals =
        {
            new RespawnPoint("Central Los Santos Medical Center", new NetVector3(295.83f, -1446.94f, 29.97f), 234f),
            new RespawnPoint("Mount Zonah Medical Center", new NetVector3(-449.34f, -340.83f, 34.50f), 256f),
            new RespawnPoint("Pillbox Hill Medical Center", new NetVector3(298.60f, -584.00f, 43.26f), 76f),
            new RespawnPoint("Sandy Shores Medical Center", new NetVector3(1839.60f, 3672.93f, 34.28f), 210f),
            new RespawnPoint("Paleto Bay Care Center", new NetVector3(-247.76f, 6331.23f, 32.43f), 316f),
        };

        public static RespawnPoint Nearest(NetVector3 position)
        {
            RespawnPoint nearest = Hospitals[0];
            float bestDistance = NetVector3.DistanceSquared(position, nearest.Position);

            for (int i = 1; i < Hospitals.Length; i++)
            {
                float distance = NetVector3.DistanceSquared(position, Hospitals[i].Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = Hospitals[i];
                }
            }

            return nearest;
        }
    }

    public readonly struct RespawnPoint
    {
        public RespawnPoint(string name, NetVector3 position, float heading)
        {
            Name = name;
            Position = position;
            Heading = heading;
        }

        public string Name { get; }

        public NetVector3 Position { get; }

        public float Heading { get; }
    }
}
