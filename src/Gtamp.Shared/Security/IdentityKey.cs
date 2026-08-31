using System;
using System.Security.Cryptography;
using System.Text;

namespace Gtamp.Shared.Security
{
    /// <summary>
    /// A player's cryptographic identity: an ECDSA P-256 keypair whose public half
    /// names them and whose private half never leaves their machine.
    /// <para>
    /// <b>What this replaces.</b> Before this, identity was a GUID in
    /// <c>client.ini</c> that the client sent to the server. Anyone who copied that
    /// file — or who watched one plaintext handshake go past — became that player.
    /// It solved continuity ("give me my character back") and was documented as not
    /// solving identity.
    /// </para>
    /// <para>
    /// <b>Why a signature rather than a shared secret.</b> A shared secret has to
    /// reach the server at least once, and the protocol is plaintext UDP until
    /// session encryption lands. A keypair has no such moment: the public key is
    /// public by definition, so enrolment leaks nothing, and every later join proves
    /// possession of a private key that has never been transmitted.
    /// </para>
    /// <para>
    /// <b>P-256 rather than Ed25519,</b> which would be the better curve, because the
    /// client runs on .NET Framework 4.8 inside GTA V's CLR and Ed25519 is not
    /// available there. P-256 with SHA-256 is, on every target this project builds
    /// for, without a third-party package the client would then have to ship.
    /// </para>
    /// <para>
    /// <b>Keys are stored as raw curve parameters,</b> not PKCS#8, because
    /// <c>ExportPkcs8PrivateKey</c> does not exist on .NET Framework 4.8. Raw
    /// <see cref="ECParameters"/> round-trip on every target.
    /// </para>
    /// </summary>
    public sealed class IdentityKey : IDisposable
    {
        /// <summary>Bytes in one P-256 coordinate.</summary>
        private const int CoordinateLength = 32;

        /// <summary>A public key is the uncompressed point: X followed by Y.</summary>
        public const int PublicKeyLength = CoordinateLength * 2;

        private readonly ECDsa _key;

        private IdentityKey(ECDsa key, string publicKey)
        {
            _key = key;
            PublicKey = publicKey;
        }

        /// <summary>Base64 of X‖Y. This is the player's identity, and it is not a secret.</summary>
        public string PublicKey { get; }

        /// <summary>First 16 hex characters of SHA-256 over the public key. For logs and ban lists.</summary>
        public string Fingerprint => FingerprintOf(PublicKey);

        public static IdentityKey Create()
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new IdentityKey(key, EncodePublic(key.ExportParameters(false)));
        }

        /// <summary>
        /// Restores a key from the client's stored blob. Returns null rather than
        /// throwing when the blob is unreadable — a corrupt line in an INI file must
        /// produce a new identity with a clear log line, not a client that cannot
        /// start.
        /// </summary>
        public static IdentityKey? TryImport(string privateBlob)
        {
            if (string.IsNullOrWhiteSpace(privateBlob))
            {
                return null;
            }

            try
            {
                byte[] raw = Convert.FromBase64String(privateBlob.Trim());
                if (raw.Length != CoordinateLength * 3)
                {
                    return null;
                }

                var parameters = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = Slice(raw, 0),
                        Y = Slice(raw, CoordinateLength),
                    },
                    D = Slice(raw, CoordinateLength * 2),
                };

                ECDsa key = ECDsa.Create();
                key.ImportParameters(parameters);
                return new IdentityKey(key, EncodePublic(parameters));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>X‖Y‖D as base64. Written to client.ini and never sent anywhere.</summary>
        public string ExportPrivateBlob()
        {
            ECParameters parameters = _key.ExportParameters(includePrivateParameters: true);
            var raw = new byte[CoordinateLength * 3];
            CopyFixed(parameters.Q.X!, raw, 0);
            CopyFixed(parameters.Q.Y!, raw, CoordinateLength);
            CopyFixed(parameters.D!, raw, CoordinateLength * 2);
            return Convert.ToBase64String(raw);
        }

        public byte[] Sign(byte[] challenge) =>
            _key.SignData(challenge ?? Array.Empty<byte>(), HashAlgorithmName.SHA256);

        /// <summary>
        /// Verifies a signature against a base64 public key. Never throws: a
        /// malformed key or signature is a failed authentication, not a server fault,
        /// and an unauthenticated peer must not be able to raise an exception on the
        /// tick thread by sending rubbish.
        /// </summary>
        public static bool Verify(string publicKey, byte[] challenge, byte[] signature)
        {
            if (string.IsNullOrWhiteSpace(publicKey) || challenge == null || signature == null)
            {
                return false;
            }

            try
            {
                byte[] raw = Convert.FromBase64String(publicKey.Trim());
                if (raw.Length != PublicKeyLength)
                {
                    return false;
                }

                using ECDsa key = ECDsa.Create();
                key.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Slice(raw, 0), Y = Slice(raw, CoordinateLength) },
                });

                return key.VerifyData(challenge, signature, HashAlgorithmName.SHA256);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>True when a string is shaped like a public key. Cheap, before any crypto.</summary>
        public static bool LooksLikePublicKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                return Convert.FromBase64String(value.Trim()).Length == PublicKeyLength;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static string FingerprintOf(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
            {
                return "unknown";
            }

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(publicKey));
            var builder = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// The bytes both sides sign over: both nonces and the server's identity.
        /// <para>
        /// The client nonce binds the proof to this specific connect attempt, the
        /// server nonce makes it unreplayable against a later one, and the server
        /// name stops a proof captured on one server being replayed to another that
        /// happens to reuse a nonce.
        /// </para>
        /// </summary>
        public static byte[] BuildChallenge(uint clientNonce, byte[] serverNonce, string serverName)
        {
            byte[] name = Encoding.UTF8.GetBytes(serverName ?? string.Empty);
            byte[] nonce = serverNonce ?? Array.Empty<byte>();

            var buffer = new byte[4 + nonce.Length + name.Length];
            buffer[0] = (byte)clientNonce;
            buffer[1] = (byte)(clientNonce >> 8);
            buffer[2] = (byte)(clientNonce >> 16);
            buffer[3] = (byte)(clientNonce >> 24);
            Array.Copy(nonce, 0, buffer, 4, nonce.Length);
            Array.Copy(name, 0, buffer, 4 + nonce.Length, name.Length);
            return buffer;
        }

        public static byte[] CreateServerNonce()
        {
            var nonce = new byte[16];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(nonce);
            return nonce;
        }

        public void Dispose() => _key.Dispose();

        private static string EncodePublic(ECParameters parameters)
        {
            var raw = new byte[PublicKeyLength];
            CopyFixed(parameters.Q.X!, raw, 0);
            CopyFixed(parameters.Q.Y!, raw, CoordinateLength);
            return Convert.ToBase64String(raw);
        }

        private static byte[] Slice(byte[] source, int offset)
        {
            var slice = new byte[CoordinateLength];
            Array.Copy(source, offset, slice, 0, CoordinateLength);
            return slice;
        }

        /// <summary>
        /// Right-aligns a coordinate into a fixed 32-byte field. Some providers strip
        /// leading zero bytes, so a coordinate can legitimately be shorter; copying it
        /// left-aligned would silently produce a different key.
        /// </summary>
        private static void CopyFixed(byte[] value, byte[] destination, int offset)
        {
            if (value.Length > CoordinateLength)
            {
                Array.Copy(value, value.Length - CoordinateLength, destination, offset, CoordinateLength);
                return;
            }

            Array.Copy(value, 0, destination, offset + (CoordinateLength - value.Length), value.Length);
        }
    }
}
