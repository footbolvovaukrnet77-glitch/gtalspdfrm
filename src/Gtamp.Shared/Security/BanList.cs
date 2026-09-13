using System;
using System.Collections.Generic;

namespace Gtamp.Shared.Security
{
    /// <summary>One ban. Keyed by identity public key, which is the only thing a player cannot change.</summary>
    public sealed class BanEntry
    {
        /// <summary>The banned identity's public key, base64. Empty is never valid.</summary>
        public string PublicKey { get; set; } = string.Empty;

        /// <summary>The name they were using, for the operator's benefit only. Not matched on.</summary>
        public string PlayerName { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        /// <summary>Who issued it: an admin's name, or "server" for an automatic ban.</summary>
        public string IssuedBy { get; set; } = "server";

        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Null means permanent.</summary>
        public DateTime? ExpiresAt { get; set; }

        public bool IsExpired(DateTime now) => ExpiresAt.HasValue && ExpiresAt.Value <= now;

        public string Describe(DateTime now)
        {
            string window = ExpiresAt.HasValue
                ? $"until {ExpiresAt.Value:yyyy-MM-dd HH:mm}Z ({(ExpiresAt.Value - now).TotalMinutes:0} min left)"
                : "permanent";

            string who = string.IsNullOrEmpty(PlayerName) ? IdentityKey.FingerprintOf(PublicKey) : PlayerName;
            return $"{who} [{IdentityKey.FingerprintOf(PublicKey)}] — {window} — {Reason} (by {IssuedBy})";
        }
    }

    /// <summary>
    /// Who is not allowed in.
    /// <para>
    /// <b>Keyed by public key, not by name or address.</b> A name is chosen by the
    /// player and changed in a text file; an address is shared by everyone behind one
    /// router and changed by reconnecting a home line. Banning either one is a
    /// combination of trivially evaded and hitting people who did nothing. The
    /// identity key is the one thing a returning player has to keep in order to be
    /// the same player at all — the same property that makes their character come
    /// back makes the ban stick.
    /// </para>
    /// <para>
    /// <b>What it does not stop:</b> somebody generating a fresh keypair and coming
    /// back as a new player with a new character. Nothing available to a server with
    /// no account system can stop that, and this does not pretend otherwise. What it
    /// buys is that evading a ban costs the evader everything they had.
    /// </para>
    /// </summary>
    public sealed class BanList
    {
        private readonly Dictionary<string, BanEntry> _bans = new Dictionary<string, BanEntry>(StringComparer.Ordinal);

        public int Count => _bans.Count;

        public IEnumerable<BanEntry> Entries => _bans.Values;

        /// <summary>Adds or replaces a ban. Returns false for an entry with no identity to ban.</summary>
        public bool Add(BanEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PublicKey))
            {
                return false;
            }

            _bans[entry.PublicKey.Trim()] = entry;
            return true;
        }

        public bool Remove(string publicKey) =>
            !string.IsNullOrWhiteSpace(publicKey) && _bans.Remove(publicKey.Trim());

        /// <summary>
        /// Finds an active ban, dropping it if it has expired. Expiry is checked on
        /// lookup rather than by a timer: a timed ban that outlives its window
        /// because nothing swept the list is the same bug as never expiring it.
        /// </summary>
        public BanEntry? Find(string publicKey, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(publicKey)
                || !_bans.TryGetValue(publicKey.Trim(), out BanEntry? entry))
            {
                return null;
            }

            if (!entry.IsExpired(now))
            {
                return entry;
            }

            _bans.Remove(publicKey.Trim());
            return null;
        }

        public bool IsBanned(string publicKey, DateTime now) => Find(publicKey, now) != null;

        /// <summary>Matches by name or by fingerprint prefix, for an admin typing a command.</summary>
        public BanEntry? FindByReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            string needle = reference.Trim();
            foreach (BanEntry entry in _bans.Values)
            {
                if (string.Equals(entry.PlayerName, needle, StringComparison.OrdinalIgnoreCase)
                    || IdentityKey.FingerprintOf(entry.PublicKey).StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.PublicKey == needle)
                {
                    return entry;
                }
            }

            return null;
        }

        public void Clear() => _bans.Clear();

        public void Replace(IEnumerable<BanEntry> entries)
        {
            _bans.Clear();
            foreach (BanEntry entry in entries)
            {
                Add(entry);
            }
        }
    }
}
