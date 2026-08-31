using System;
using System.Collections.Generic;
using System.Text;
using Gtamp.Server.Core;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Phase 12: session encryption, and the stress the framework was only ever
    /// claimed to survive.
    /// <para>
    /// Every document up to here pointed at this phase for the same sentence: "the
    /// protocol is plaintext UDP, so anyone on the path can read and forge packets".
    /// These tests are what let that sentence be deleted rather than softened.
    /// </para>
    /// </summary>
    public class EncryptionTests
    {
        private static byte[] Secret(byte seed)
        {
            var secret = new byte[SessionCrypto.KeyLength];
            for (int i = 0; i < secret.Length; i++)
            {
                secret[i] = (byte)(seed + i);
            }

            return secret;
        }

        [Fact]
        public void APacketRoundTripsBetweenTheTwoSides()
        {
            byte[] secret = Secret(1);
            using SessionCrypto server = SessionCrypto.FromSharedSecret(secret, isServer: true);
            using SessionCrypto client = SessionCrypto.FromSharedSecret(secret, isServer: false);

            byte[] header = { 1, 2, 3, 4 };
            byte[] plaintext = Encoding.UTF8.GetBytes("the quick brown fox");

            byte[] sealedPacket = server.Encrypt(header, plaintext, sequence: 7);
            Assert.True(client.TryDecrypt(header, sealedPacket, 7, out byte[] decrypted));
            Assert.Equal(plaintext, decrypted);

            // And back the other way, on keys derived for the other direction.
            byte[] reply = client.Encrypt(header, plaintext, sequence: 8);
            Assert.True(server.TryDecrypt(header, reply, 8, out byte[] decryptedReply));
            Assert.Equal(plaintext, decryptedReply);
        }

        [Fact]
        public void TheTwoDirectionsDoNotShareAKey()
        {
            // If they did, an attacker could reflect a captured packet back at its
            // sender and have it accepted as though the other side had said it.
            byte[] secret = Secret(2);
            using SessionCrypto server = SessionCrypto.FromSharedSecret(secret, isServer: true);
            using SessionCrypto alsoServer = SessionCrypto.FromSharedSecret(secret, isServer: true);

            byte[] header = { 9 };
            byte[] fromServer = server.Encrypt(header, Encoding.UTF8.GetBytes("hello"), 1);

            // Another server-side instance shares the send key but not the receive key,
            // so it cannot read its own direction's traffic.
            Assert.False(alsoServer.TryDecrypt(header, fromServer, 1, out _));
        }

        [Fact]
        public void TamperedCiphertextIsRejected()
        {
            byte[] secret = Secret(3);
            using SessionCrypto server = SessionCrypto.FromSharedSecret(secret, isServer: true);
            using SessionCrypto client = SessionCrypto.FromSharedSecret(secret, isServer: false);

            byte[] header = { 1, 2 };
            byte[] sealedPacket = server.Encrypt(header, Encoding.UTF8.GetBytes("position 100 200"), 4);

            sealedPacket[2] ^= 0xFF;

            Assert.False(client.TryDecrypt(header, sealedPacket, 4, out _));
            Assert.Equal(1, client.Rejected);
        }

        [Fact]
        public void ARewrittenHeaderIsRejected()
        {
            // The header travels in the clear because the receiver needs it to find the
            // peer and derive the IV. It is authenticated so that being readable does
            // not make it editable.
            byte[] secret = Secret(4);
            using SessionCrypto server = SessionCrypto.FromSharedSecret(secret, isServer: true);
            using SessionCrypto client = SessionCrypto.FromSharedSecret(secret, isServer: false);

            byte[] header = { 1, 2, 3, 4 };
            byte[] sealedPacket = server.Encrypt(header, Encoding.UTF8.GetBytes("payload"), 5);

            byte[] rewritten = { 1, 2, 3, 99 };
            Assert.False(client.TryDecrypt(rewritten, sealedPacket, 5, out _));
        }

        [Fact]
        public void APacketReplayedAtAnotherSequenceIsRejected()
        {
            // The IV is derived from the sequence number, so a captured packet only
            // decrypts at the position it was sent from.
            byte[] secret = Secret(5);
            using SessionCrypto server = SessionCrypto.FromSharedSecret(secret, isServer: true);
            using SessionCrypto client = SessionCrypto.FromSharedSecret(secret, isServer: false);

            byte[] header = { 7 };
            byte[] sealedPacket = server.Encrypt(header, Encoding.UTF8.GetBytes("payload"), 11);

            Assert.False(client.TryDecrypt(header, sealedPacket, 12, out _));
            Assert.True(client.TryDecrypt(header, sealedPacket, 11, out _));
        }

        [Fact]
        public void AShortOrMisshapenPacketIsRejectedWithoutThrowing()
        {
            byte[] secret = Secret(6);
            using SessionCrypto client = SessionCrypto.FromSharedSecret(secret, isServer: false);

            Assert.False(client.TryDecrypt(new byte[] { 1 }, Array.Empty<byte>(), 1, out _));
            Assert.False(client.TryDecrypt(new byte[] { 1 }, new byte[15], 1, out _));
            Assert.False(client.TryDecrypt(new byte[] { 1 }, new byte[100], 1, out _));
            Assert.False(client.TryDecrypt(new byte[] { 1 }, null!, 1, out _));
        }

        [Fact]
        public void ASecretThatIsTooShortIsRefused()
        {
            Assert.Throws<ArgumentException>(() => SessionCrypto.FromSharedSecret(new byte[8], isServer: true));
            Assert.Throws<ArgumentException>(() => SessionCrypto.FromSharedSecret(null!, isServer: false));
        }

        // ------------------------------------------------------------------
        // Key agreement
        // ------------------------------------------------------------------
        [Fact]
        public void BothSidesAgreeTheSameSecretAndNobodyElseCan()
        {
            using EphemeralKeyExchange server = EphemeralKeyExchange.Create();
            using EphemeralKeyExchange client = EphemeralKeyExchange.Create();
            using EphemeralKeyExchange eavesdropper = EphemeralKeyExchange.Create();

            byte[] onServer = server.Agree(client.PublicKey);
            byte[] onClient = client.Agree(server.PublicKey);

            Assert.Equal(onServer, onClient);
            Assert.Equal(SessionCrypto.KeyLength, onServer.Length);

            // Watching both public keys go past is not enough to derive the secret.
            Assert.NotEqual(onServer, eavesdropper.Agree(server.PublicKey));
        }

        [Fact]
        public void EachConnectionGetsAFreshKeypair()
        {
            // Forward secrecy: a stolen identity key must not decrypt sessions that
            // were recorded before it was stolen.
            using EphemeralKeyExchange first = EphemeralKeyExchange.Create();
            using EphemeralKeyExchange second = EphemeralKeyExchange.Create();

            Assert.NotEqual(first.PublicKey, second.PublicKey);
        }

        [Fact]
        public void AMalformedEphemeralKeyIsRefused()
        {
            using EphemeralKeyExchange exchange = EphemeralKeyExchange.Create();

            Assert.False(EphemeralKeyExchange.IsWellFormed(new byte[10]));
            Assert.False(EphemeralKeyExchange.IsWellFormed(null!));
            Assert.True(EphemeralKeyExchange.IsWellFormed(exchange.PublicKey));

            Assert.Throws<ArgumentException>(() => exchange.Agree(new byte[10]));
        }

        [Fact]
        public void TheSignedChallengeBindsBothEphemeralKeys()
        {
            // Unauthenticated ECDH agrees a key with whoever is on the other end. The
            // ephemeral keys are inside the bytes the identity key signs, so swapping
            // either one invalidates the proof.
            using IdentityKey identity = IdentityKey.Create();
            byte[] nonce = IdentityKey.CreateServerNonce();
            using EphemeralKeyExchange serverSide = EphemeralKeyExchange.Create();
            using EphemeralKeyExchange clientSide = EphemeralKeyExchange.Create();
            using EphemeralKeyExchange attacker = EphemeralKeyExchange.Create();

            byte[] honest = IdentityKey.BuildChallenge(
                1, nonce, "server", serverSide.PublicKey, clientSide.PublicKey);
            byte[] signature = identity.Sign(honest);

            Assert.True(IdentityKey.Verify(identity.PublicKey, honest, signature));

            byte[] substitutedServerKey = IdentityKey.BuildChallenge(
                1, nonce, "server", attacker.PublicKey, clientSide.PublicKey);
            Assert.False(IdentityKey.Verify(identity.PublicKey, substitutedServerKey, signature));

            byte[] substitutedClientKey = IdentityKey.BuildChallenge(
                1, nonce, "server", serverSide.PublicKey, attacker.PublicKey);
            Assert.False(IdentityKey.Verify(identity.PublicKey, substitutedClientKey, signature));
        }

        // ------------------------------------------------------------------
        // End to end
        // ------------------------------------------------------------------
        [Fact]
        public void ASessionIsActuallyEncryptedOnTheWire()
        {
            // Not "reports itself as encrypted" — actually unreadable. The player's
            // name is chosen as the canary because it is sent in the clear during the
            // handshake and then appears in every snapshot afterwards, so finding it
            // after the handshake means the session traffic is readable.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("CanaryPlayerName");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected), "alice never connected");
            Assert.True(alice.Client.Connection.IsEncrypted, "the session was not encrypted");

            var seen = new List<byte[]>();
            harness.Network.Observer = payload => seen.Add(payload);
            harness.Advance(2.0);
            harness.Network.Observer = null;

            Assert.NotEmpty(seen);
            byte[] canary = Encoding.UTF8.GetBytes("CanaryPlayerName");
            Assert.DoesNotContain(seen, datagram => Contains(datagram, canary));
        }

        [Fact]
        public void WithEncryptionOffTheSameTrafficIsReadable()
        {
            // The control for the test above. Without it, a canary that never appears
            // proves nothing — it might simply never be sent.
            var config = new ServerConfig
            {
                ServerName = "plain",
                SaveIntervalSeconds = 0,
                EncryptSessions = false,
            };

            using var harness = new TestHarness(config);
            TestClient alice = harness.CreateClient("CanaryPlayerName");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            Assert.False(alice.Client.Connection.IsEncrypted);

            var seen = new List<byte[]>();
            harness.Network.Observer = payload => seen.Add(payload);
            harness.Advance(2.0);
            harness.Network.Observer = null;

            byte[] canary = Encoding.UTF8.GetBytes("CanaryPlayerName");
            Assert.Contains(seen, datagram => Contains(datagram, canary));
        }

        [Fact]
        public void EncryptedSessionsStillSurviveALossyLink()
        {
            // Encryption adds a tag and padding to every packet, which changes how much
            // fits in one. A packet that only exceeded the MTU once encrypted would be
            // fragmented by the network invisibly.
            using var harness = new TestHarness(seed: 9001);
            harness.PacketLoss = 0.25;
            harness.Latency = 0.05;

            var clients = new List<TestClient>();
            for (int i = 0; i < 4; i++)
            {
                TestClient client = harness.CreateClient("player" + i);
                clients.Add(client);
                client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            }

            Assert.True(
                harness.AdvanceUntil(() => clients.TrueForAll(c => c.Client.IsConnected), timeoutSeconds: 30),
                "a client never connected over an encrypted lossy link");

            Assert.True(
                harness.AdvanceUntil(() => clients.TrueForAll(c => c.PlayerCount == 4), timeoutSeconds: 30),
                "the world never converged over an encrypted lossy link");
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
