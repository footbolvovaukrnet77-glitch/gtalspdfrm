using System;
using System.Collections.Generic;
using System.Net;

namespace Gtamp.Server.Players
{
    /// <summary>Lookup tables for connected players. Endpoint is the routing key for inbound datagrams.</summary>
    public sealed class PlayerRegistry
    {
        private readonly Dictionary<IPEndPoint, PlayerSession> _byEndPoint =
            new Dictionary<IPEndPoint, PlayerSession>();

        private readonly Dictionary<uint, PlayerSession> _byPlayerId = new Dictionary<uint, PlayerSession>();
        private readonly List<PlayerSession> _sessions = new List<PlayerSession>();

        private uint _nextPlayerId = 1;

        public int Count => _sessions.Count;

        public IReadOnlyList<PlayerSession> Sessions => _sessions;

        public uint AllocatePlayerId() => _nextPlayerId++;

        public void Add(PlayerSession session)
        {
            _byEndPoint[session.EndPoint] = session;
            _byPlayerId[session.PlayerId] = session;
            _sessions.Add(session);
        }

        public void Remove(PlayerSession session)
        {
            _byEndPoint.Remove(session.EndPoint);
            _byPlayerId.Remove(session.PlayerId);
            _sessions.Remove(session);
        }

        public bool TryGetByEndPoint(IPEndPoint endPoint, out PlayerSession session) =>
            _byEndPoint.TryGetValue(endPoint, out session!);

        public bool TryGetByPlayerId(uint playerId, out PlayerSession session) =>
            _byPlayerId.TryGetValue(playerId, out session!);

        public PlayerSession? FindByIdentity(string identityToken)
        {
            foreach (PlayerSession session in _sessions)
            {
                if (string.Equals(session.IdentityToken, identityToken, StringComparison.Ordinal))
                {
                    return session;
                }
            }

            return null;
        }

        public PlayerSession? FindByName(string name)
        {
            foreach (PlayerSession session in _sessions)
            {
                if (string.Equals(session.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return session;
                }
            }

            return null;
        }
    }
}
