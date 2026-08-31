using System;
using System.Net;
using Gtamp.Server.Replication;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Security;

namespace Gtamp.Server.Players
{
    public enum PlayerRole : byte
    {
        Player = 0,
        Moderator = 1,
        Admin = 2,
    }

    /// <summary>One connected player: identity, transport peer, replication state and validation counters.</summary>
    public sealed class PlayerSession
    {
        public PlayerSession(uint playerId, NetPeer peer, string name, string identityToken)
        {
            PlayerId = playerId;
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            Name = name;
            IdentityToken = identityToken;
        }

        public uint PlayerId { get; }

        public NetPeer Peer { get; }

        public IPEndPoint EndPoint => Peer.Remote;

        public string Name { get; set; }

        /// <summary>Stable per-installation secret used to recognise a reconnecting player.</summary>
        public string IdentityToken { get; }

        public EntityId EntityId { get; set; } = EntityId.None;

        public PlayerRole Role { get; set; } = PlayerRole.Player;

        public ClientReplicationState Replication { get; } = new ClientReplicationState();

        /// <summary>Adapts this client's snapshot budget to what its link is carrying.</summary>
        public BandwidthShaper? Bandwidth { get; set; }

        public PlayerValidationState Validation { get; } = new PlayerValidationState();

        public ModManifest Manifest { get; set; } = new ModManifest();

        /// <summary>
        /// The sequence of the newest client state update this session has processed.
        /// Echoed in that client's next snapshot so it can tell a rejection from a
        /// snapshot that simply predates its report.
        /// </summary>
        public uint LastProcessedUpdateSequence { get; set; }

        public double ConnectedAt { get; set; }

        /// <summary>
        /// The nonce from the connect request that produced this session, plus the
        /// accept packet that answered it. Kept so a retried request — which means
        /// the first accept was lost — can be answered with the identical accept
        /// instead of being dropped.
        /// </summary>
        public uint HandshakeNonce { get; set; }

        public byte[] AcceptPayload { get; set; } = System.Array.Empty<byte>();

        public double LastSnapshotSentAt { get; set; }

        public double LastStateUpdateAt { get; set; }

        /// <summary>Server time the player died, or 0 while alive.</summary>
        public double DiedAt { get; set; }

        public bool IsDead => DiedAt > 0;

        /// <summary>
        /// Set when the server has moved the player itself and the snapshot carrying
        /// that move has not been sent yet.
        /// </summary>
        public bool PendingAuthorityHold { get; set; }

        /// <summary>
        /// Snapshot id the client must acknowledge before its own state updates are
        /// accepted again, or 0 when there is no hold.
        /// <para>
        /// After the server moves a player — a join placing them at their persisted
        /// position, or a respawn — the client is still reporting where it thinks it
        /// is. Those reports are in flight and describe a world that no longer exists;
        /// accepting them would drag the player straight back out of the position the
        /// server just put them in. The hold ends the moment the client confirms it has
        /// seen the move.
        /// </para>
        /// </summary>
        public uint AuthorityHoldSnapshot { get; set; }

        /// <summary>Server time the hold gives up waiting, so lost packets cannot freeze a player forever.</summary>
        public double AuthorityHoldExpiry { get; set; }

        /// <summary>
        /// A narrower hold covering health and armour only.
        /// <para>
        /// When the server arbitrates a hit, the victim's client is still reporting the
        /// health it had before. Accepting that undoes the damage the server just
        /// applied — the shot lands and then un-lands, once per snapshot, for as long
        /// as the firefight lasts. A full authority hold would fix it but would also
        /// freeze the victim's movement for a round trip every time they were hit,
        /// which is worse than the bug. So only health and armour are held.
        /// </para>
        /// </summary>
        public bool PendingHealthHold { get; set; }

        public uint HealthHoldSnapshot { get; set; }

        public double HealthHoldExpiry { get; set; }

        /// <summary>Set once a disconnect has been decided, so the tick loop can reap the session.</summary>
        public bool PendingRemoval { get; set; }

        public bool IsAdmin => Role == PlayerRole.Admin;

        public override string ToString() => $"{Name}#{PlayerId} ({EndPoint})";
    }
}
