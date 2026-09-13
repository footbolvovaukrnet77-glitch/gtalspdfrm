using System;
using System.Security.Cryptography;
using System.Text;

namespace Gtamp.Shared.Security
{
    /// <summary>
    /// Authenticated encryption for a session, and the key schedule that seeds it.
    /// <para>
    /// <b>What it protects.</b> Until this existed the protocol was plaintext UDP:
    /// anyone on the path could read every position, every chat line and every mod
    /// payload, and could forge packets into a session they had never authenticated.
    /// Phase 10 proved <em>who opened</em> a session; this protects what travels
    /// inside it.
    /// </para>
    /// <para>
    /// <b>AES-CBC with HMAC-SHA256, not AES-GCM.</b> GCM would be the obvious choice
    /// and is not available: the client runs on .NET Framework 4.8 inside GTA V's CLR,
    /// where <c>AesGcm</c> does not exist. Encrypt-then-MAC with independent keys is
    /// the well-understood construction that is available on every target here, and
    /// the MAC covers the packet header as well as the ciphertext, so a rewritten
    /// sequence number is rejected along with forged content.
    /// </para>
    /// <para>
    /// <b>The IV is derived, not transmitted.</b> Each packet already carries a
    /// sequence number and each direction has its own keys, so the IV comes from
    /// AES over the direction and sequence rather than costing sixteen bytes on the
    /// wire. At a 1200-byte MTU, twenty snapshots a second and thirty-two players,
    /// sixteen bytes per packet is real bandwidth.
    /// </para>
    /// <para>
    /// <b>The tag is truncated to 16 bytes.</b> 128 bits of authentication is what GCM
    /// offers by default; the other sixteen buy nothing and cost the same bandwidth
    /// again.
    /// </para>
    /// </summary>
    public sealed class SessionCrypto : IDisposable
    {
        public const int KeyLength = 32;
        public const int IvLength = 16;

        /// <summary>Bytes added to every encrypted packet: the truncated authentication tag.</summary>
        public const int Overhead = 16;

        private const string ClientToServerLabel = "gtamp-c2s-v1";
        private const string ServerToClientLabel = "gtamp-s2c-v1";
        private const string IvLabel = "gtamp-iv-v1";

        private readonly byte[] _sendKey;
        private readonly byte[] _receiveKey;
        private readonly byte[] _sendMacKey;
        private readonly byte[] _receiveMacKey;
        private readonly byte[] _ivKey;
        private readonly byte _sendDirection;
        private readonly byte _receiveDirection;

        private SessionCrypto(byte[] sharedSecret, bool isServer)
        {
            // Five independent keys from one secret. Reusing one key for encryption and
            // authentication, or across directions, is the classic way to turn a sound
            // construction into an unsound one.
            byte[] clientToServerCipher = Derive(sharedSecret, ClientToServerLabel, 1);
            byte[] clientToServerMac = Derive(sharedSecret, ClientToServerLabel, 2);
            byte[] serverToClientCipher = Derive(sharedSecret, ServerToClientLabel, 1);
            byte[] serverToClientMac = Derive(sharedSecret, ServerToClientLabel, 2);
            _ivKey = Derive(sharedSecret, IvLabel, 1);

            if (isServer)
            {
                _sendKey = serverToClientCipher;
                _sendMacKey = serverToClientMac;
                _receiveKey = clientToServerCipher;
                _receiveMacKey = clientToServerMac;
                _sendDirection = 1;
                _receiveDirection = 0;
            }
            else
            {
                _sendKey = clientToServerCipher;
                _sendMacKey = clientToServerMac;
                _receiveKey = serverToClientCipher;
                _receiveMacKey = serverToClientMac;
                _sendDirection = 0;
                _receiveDirection = 1;
            }
        }

        /// <summary>Packets encrypted with this session, for the network debugger.</summary>
        public int Encrypted { get; private set; }

        /// <summary>Packets that failed authentication and were discarded.</summary>
        public int Rejected { get; private set; }

        /// <summary>Builds one side's crypto from an agreed shared secret.</summary>
        public static SessionCrypto FromSharedSecret(byte[] sharedSecret, bool isServer)
        {
            if (sharedSecret == null || sharedSecret.Length < KeyLength)
            {
                throw new ArgumentException(
                    "A session secret must be at least " + KeyLength + " bytes.", nameof(sharedSecret));
            }

            return new SessionCrypto(sharedSecret, isServer);
        }

        /// <summary>
        /// Encrypts one packet's payload, authenticating the header alongside it.
        /// <para>
        /// The header stays in the clear because the receiver needs the sequence number
        /// to derive the IV before it can decrypt anything. It is still authenticated,
        /// so an attacker cannot rewrite a sequence number to make a captured packet
        /// decrypt somewhere else in the stream.
        /// </para>
        /// </summary>
        public byte[] Encrypt(byte[] header, byte[] plaintext, ushort sequence)
        {
            byte[] iv = DeriveIv(_sendDirection, sequence);

            using Aes aes = CreateAes(_sendKey, iv);
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

            byte[] tag = ComputeTag(_sendMacKey, header, ciphertext);

            var packet = new byte[ciphertext.Length + Overhead];
            Buffer.BlockCopy(ciphertext, 0, packet, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, packet, ciphertext.Length, Overhead);

            Encrypted++;
            return packet;
        }

        /// <summary>
        /// Verifies then decrypts. Returns false rather than throwing: a forged or
        /// corrupt packet is an ordinary event on a public network, and an
        /// unauthenticated peer must not be able to raise an exception on the tick
        /// thread by sending rubbish.
        /// </summary>
        public bool TryDecrypt(byte[] header, byte[] packet, ushort sequence, out byte[] plaintext)
        {
            plaintext = Array.Empty<byte>();

            if (packet == null || packet.Length < Overhead + IvLength
                || (packet.Length - Overhead) % IvLength != 0)
            {
                Rejected++;
                return false;
            }

            int ciphertextLength = packet.Length - Overhead;
            var ciphertext = new byte[ciphertextLength];
            Buffer.BlockCopy(packet, 0, ciphertext, 0, ciphertextLength);

            // Authenticate before decrypting. Decrypting first and checking afterwards
            // is what padding-oracle attacks are built on.
            byte[] expected = ComputeTag(_receiveMacKey, header, ciphertext);
            if (!ConstantTimeEquals(expected, packet, ciphertextLength))
            {
                Rejected++;
                return false;
            }

            try
            {
                byte[] iv = DeriveIv(_receiveDirection, sequence);
                using Aes aes = CreateAes(_receiveKey, iv);
                using ICryptoTransform decryptor = aes.CreateDecryptor();
                plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertextLength);
                return true;
            }
            catch (CryptographicException)
            {
                // The tag already passed, so this is padding that survived
                // authentication, which should be impossible. Counted rather than
                // trusted.
                Rejected++;
                return false;
            }
        }

        /// <summary>
        /// The IV is one AES block over the direction and sequence number.
        /// <para>
        /// A single ECB block over a counter is exactly how CTR mode makes its
        /// keystream blocks, and it is used here for the same reason: it turns a small,
        /// predictable counter into an unpredictable, non-repeating block. The
        /// direction byte keeps the two streams from colliding even though both start
        /// at sequence 1.
        /// </para>
        /// </summary>
        private byte[] DeriveIv(byte direction, ushort sequence)
        {
            var block = new byte[IvLength];
            block[0] = direction;
            block[1] = (byte)sequence;
            block[2] = (byte)(sequence >> 8);

            using Aes aes = Aes.Create();
            aes.Key = _ivKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using ICryptoTransform transform = aes.CreateEncryptor();
            return transform.TransformFinalBlock(block, 0, IvLength);
        }

        private static Aes CreateAes(byte[] key, byte[] iv)
        {
            Aes aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        private static byte[] ComputeTag(byte[] macKey, byte[] header, byte[] ciphertext)
        {
            int headerLength = header?.Length ?? 0;

            using var hmac = new HMACSHA256(macKey);
            var buffer = new byte[headerLength + ciphertext.Length];

            if (headerLength > 0)
            {
                Buffer.BlockCopy(header!, 0, buffer, 0, headerLength);
            }

            Buffer.BlockCopy(ciphertext, 0, buffer, headerLength, ciphertext.Length);
            return hmac.ComputeHash(buffer);
        }

        /// <summary>
        /// Compares in time independent of where the first difference is. A
        /// short-circuit comparison leaks the position of the mismatch, which is enough
        /// to forge a tag one byte at a time.
        /// </summary>
        private static bool ConstantTimeEquals(byte[] expected, byte[] packet, int offset)
        {
            int difference = 0;
            for (int i = 0; i < Overhead; i++)
            {
                difference |= expected[i] ^ packet[offset + i];
            }

            return difference == 0;
        }

        /// <summary>
        /// One HMAC over the label and a counter. A single extraction step is enough
        /// here because the input is already a uniformly random ECDH output; the
        /// counter is what makes the cipher key and the MAC key of one direction
        /// different, which is the whole point of deriving them separately.
        /// </summary>
        private static byte[] Derive(byte[] secret, string label, byte counter)
        {
            using var hmac = new HMACSHA256(secret);

            byte[] labelBytes = Encoding.UTF8.GetBytes(label);
            var message = new byte[labelBytes.Length + 1];
            Buffer.BlockCopy(labelBytes, 0, message, 0, labelBytes.Length);
            message[labelBytes.Length] = counter;

            return hmac.ComputeHash(message);
        }

        public void Dispose()
        {
            Array.Clear(_sendKey, 0, _sendKey.Length);
            Array.Clear(_receiveKey, 0, _receiveKey.Length);
            Array.Clear(_sendMacKey, 0, _sendMacKey.Length);
            Array.Clear(_receiveMacKey, 0, _receiveMacKey.Length);
            Array.Clear(_ivKey, 0, _ivKey.Length);
        }
    }
}
