using System;
using System.Security.Cryptography;

namespace Gtamp.Shared.Security
{
    /// <summary>
    /// The ephemeral half of the handshake: a fresh ECDH keypair per connection,
    /// whose public half is signed by the long-lived identity key.
    /// <para>
    /// <b>Why ephemeral.</b> Deriving the session key from the identity keys
    /// themselves would mean that anyone who ever obtains a player's private key can
    /// decrypt every session they have recorded, past and future. A fresh keypair per
    /// connection, thrown away afterwards, means a stolen identity key lets an
    /// attacker impersonate the player from then on but not read what they said
    /// before — forward secrecy, and it costs one key generation per join.
    /// </para>
    /// <para>
    /// <b>Why signed.</b> Unauthenticated ECDH agrees a key with whoever is on the
    /// other end, which on a public network is whoever got there first. Both ephemeral
    /// public keys go into the bytes the identity key already signs during the
    /// challenge, so the same signature that proves who the client is also binds the
    /// key exchange to them. One signature, both jobs.
    /// </para>
    /// <para>
    /// P-256 again, and for the same reason as <see cref="IdentityKey"/>: it is what
    /// .NET Framework 4.8 has.
    /// </para>
    /// </summary>
    public sealed class EphemeralKeyExchange : IDisposable
    {
        /// <summary>Bytes in an uncompressed P-256 point: X followed by Y.</summary>
        public const int PublicKeyLength = 64;

        private const int CoordinateLength = 32;

#if NETSTANDARD2_0
        private EphemeralKeyExchange()
        {
        }

        /// <summary>
        /// Not available on netstandard2.0, which has no <c>ECDiffieHellman</c>. Both
        /// real hosts build against net48 or net8.0; this exists so the shared
        /// assembly still compiles for a future host, and throws rather than silently
        /// producing a session that is not encrypted.
        /// </summary>
        public static EphemeralKeyExchange Create() => throw new PlatformNotSupportedException(
            "Session encryption needs ECDiffieHellman, which the netstandard2.0 build does not have. " +
            "Use the net48 or net8.0 build of Gtamp.Shared.");

        public byte[] PublicKey => throw new PlatformNotSupportedException();

        public byte[] Agree(byte[] peerPublicKey) => throw new PlatformNotSupportedException();

        public void Dispose()
        {
        }
#else
        private readonly ECDiffieHellman _key;

        private EphemeralKeyExchange(ECDiffieHellman key, byte[] publicKey)
        {
            _key = key;
            PublicKey = publicKey;
        }

        /// <summary>The uncompressed point to send to the other side. Not a secret.</summary>
        public byte[] PublicKey { get; }

        public static EphemeralKeyExchange Create()
        {
            ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);

            var encoded = new byte[PublicKeyLength];
            CopyFixed(parameters.Q.X!, encoded, 0);
            CopyFixed(parameters.Q.Y!, encoded, CoordinateLength);

            return new EphemeralKeyExchange(key, encoded);
        }

        /// <summary>
        /// Derives the shared secret from the other side's public point.
        /// <para>
        /// Hashed rather than used raw: the raw agreement is a curve point coordinate,
        /// which is uniform enough for a hash input and not uniform enough to be a key.
        /// </para>
        /// </summary>
        public byte[] Agree(byte[] peerPublicKey)
        {
            if (peerPublicKey == null || peerPublicKey.Length != PublicKeyLength)
            {
                throw new ArgumentException("An ephemeral public key must be 64 bytes.", nameof(peerPublicKey));
            }

            var x = new byte[CoordinateLength];
            var y = new byte[CoordinateLength];
            Buffer.BlockCopy(peerPublicKey, 0, x, 0, CoordinateLength);
            Buffer.BlockCopy(peerPublicKey, CoordinateLength, y, 0, CoordinateLength);

            using ECDiffieHellman peer = ECDiffieHellman.Create();
            peer.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            });

            return _key.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        }

        /// <summary>
        /// Right-aligns a coordinate into a fixed field. Providers may strip leading
        /// zero bytes, and copying such a coordinate left-aligned silently produces a
        /// different point.
        /// </summary>
        private static void CopyFixed(byte[] value, byte[] destination, int offset)
        {
            if (value.Length > CoordinateLength)
            {
                Buffer.BlockCopy(value, value.Length - CoordinateLength, destination, offset, CoordinateLength);
                return;
            }

            Buffer.BlockCopy(value, 0, destination, offset + (CoordinateLength - value.Length), value.Length);
        }

        public void Dispose() => _key.Dispose();
#endif

        /// <summary>True when a byte array is shaped like an ephemeral public key.</summary>
        public static bool IsWellFormed(byte[] publicKey) =>
            publicKey != null && publicKey.Length == PublicKeyLength;
    }
}
