using System;
using System.Linq;
using Gtamp.Server.Admin;
using Gtamp.Server.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Phase 10: proving who a player is, keeping the ones you do not want out, and
    /// letting the ones you trust act over the network.
    /// <para>
    /// The identity used to be a GUID in a text file that the client sent to the
    /// server, so anyone who copied that file — or watched one plaintext handshake —
    /// became that player. It is now a keypair whose private half never leaves the
    /// machine.
    /// </para>
    /// </summary>
    public class AuthenticationTests
    {
        [Fact]
        public void AKeypairRoundTripsThroughTheStoredBlob()
        {
            using IdentityKey original = IdentityKey.Create();
            string blob = original.ExportPrivateBlob();

            using IdentityKey? restored = IdentityKey.TryImport(blob);
            Assert.NotNull(restored);
            Assert.Equal(original.PublicKey, restored!.PublicKey);

            // And the restored half really can sign for the original identity.
            byte[] challenge = IdentityKey.BuildChallenge(7, IdentityKey.CreateServerNonce(), "server");
            Assert.True(IdentityKey.Verify(original.PublicKey, challenge, restored.Sign(challenge)));
        }

        [Fact]
        public void AnUnreadableSecretProducesANewIdentityRatherThanACrash()
        {
            Assert.Null(IdentityKey.TryImport("not base64 at all"));
            Assert.Null(IdentityKey.TryImport(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
            Assert.Null(IdentityKey.TryImport(string.Empty));
        }

        [Fact]
        public void VerificationRefusesRubbishWithoutThrowing()
        {
            using IdentityKey key = IdentityKey.Create();
            byte[] challenge = IdentityKey.BuildChallenge(1, IdentityKey.CreateServerNonce(), "server");
            byte[] signature = key.Sign(challenge);

            Assert.True(IdentityKey.Verify(key.PublicKey, challenge, signature));

            // An unauthenticated peer must not be able to raise an exception on the
            // tick thread by sending nonsense.
            Assert.False(IdentityKey.Verify("nonsense", challenge, signature));
            Assert.False(IdentityKey.Verify(key.PublicKey, challenge, new byte[] { 9, 9, 9 }));
            Assert.False(IdentityKey.Verify(key.PublicKey, challenge, Array.Empty<byte>()));
            Assert.False(IdentityKey.Verify(string.Empty, challenge, signature));
        }

        [Fact]
        public void ASignatureForOneChallengeDoesNotVerifyAgainstAnother()
        {
            using IdentityKey key = IdentityKey.Create();
            byte[] nonce = IdentityKey.CreateServerNonce();

            byte[] mine = IdentityKey.BuildChallenge(1, nonce, "my-server");
            byte[] signature = key.Sign(mine);

            // A different attempt, a different server nonce, and a different server
            // are each enough to make a captured proof useless.
            Assert.False(IdentityKey.Verify(key.PublicKey, IdentityKey.BuildChallenge(2, nonce, "my-server"), signature));
            Assert.False(IdentityKey.Verify(
                key.PublicKey, IdentityKey.BuildChallenge(1, IdentityKey.CreateServerNonce(), "my-server"), signature));
            Assert.False(IdentityKey.Verify(key.PublicKey, IdentityKey.BuildChallenge(1, nonce, "other-server"), signature));
        }

        [Fact]
        public void AnHonestClientCompletesTheChallengeAndJoins()
        {
            using var harness = new TestHarness();
            Assert.True(harness.Config.RequireAuthentication);

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected), "alice never authenticated");
            Assert.Equal(1, alice.Client.Connection.ChallengesAnswered);

            // The identity the server knows her by is her public key, not a token she
            // chose for herself.
            Assert.Equal(alice.Config.IdentityToken, harness.Server.Players.Sessions[0].IdentityToken);
            Assert.True(IdentityKey.LooksLikePublicKey(harness.Server.Players.Sessions[0].IdentityToken));
        }

        [Fact]
        public void AClientWithNoSigningKeyIsRefusedWithAReadableReason()
        {
            using var harness = new TestHarness();
            TestClient impostor = harness.CreateClient("impostor");

            // A legacy identity: a token the client made up, with no key behind it.
            impostor.Config.IdentityToken = "legacy-token";
            impostor.Config.IdentitySecret = string.Empty;
            impostor.Client.Connection.Identity = null;

            impostor.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => impostor.Client.Connection.State == Gtamp.Client.Network.ClientConnectionState.Failed),
                "the server accepted a client that cannot prove anything");

            Assert.Contains("signing identity", impostor.Client.Connection.LastError);
            Assert.Equal(0, harness.Server.Players.Count);
        }

        [Fact]
        public void ClaimingSomebodyElsesIdentityWithoutTheirKeyFails()
        {
            // The whole point. The attacker knows the victim's public identity —
            // it is public — and has their own key. That is not enough.
            using var harness = new TestHarness();
            TestClient victim = harness.CreateClient("victim");
            victim.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => victim.Client.IsConnected));

            TestClient attacker = harness.CreateClient("attacker");
            attacker.Config.IdentityToken = victim.Config.IdentityToken;

            attacker.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(
                    () => attacker.Client.Connection.State == Gtamp.Client.Network.ClientConnectionState.Failed,
                    timeoutSeconds: 20),
                "the attacker was admitted while claiming somebody else's identity");

            Assert.Equal(1, harness.Server.Players.Count);
            Assert.True(victim.Client.IsConnected);
        }

        [Fact]
        public void AuthenticationSurvivesALossyLink()
        {
            // The handshake is four legs now instead of two, and every one of them is
            // connectionless. A lost challenge is answered by re-issuing the identical
            // one; a lost proof is resent by the retry timer; a lost accept is resent
            // when the proof arrives again.
            using var harness = new TestHarness(seed: 4242);
            harness.PacketLoss = 0.4;
            harness.Latency = 0.05;

            var clients = new System.Collections.Generic.List<TestClient>();
            for (int i = 0; i < 4; i++)
            {
                TestClient client = harness.CreateClient("player" + i);
                clients.Add(client);
                client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            }

            Assert.True(
                harness.AdvanceUntil(() => clients.TrueForAll(c => c.Client.IsConnected), timeoutSeconds: 30),
                "a client was stranded: " +
                string.Join(", ", clients.ConvertAll(c => $"{c.Config.PlayerName}={c.Client.Connection.State}")));

            Assert.Equal(4, harness.Server.Players.Count);
        }

        [Fact]
        public void TurningAuthenticationOffLetsAnUnkeyedClientIn()
        {
            // An operator's choice, and it has to actually work — a switch that does
            // nothing is worse than no switch.
            var config = new ServerConfig
            {
                ServerName = "open",
                SaveIntervalSeconds = 0,
                RequireAuthentication = false,
            };

            using var harness = new TestHarness(config);
            TestClient guest = harness.CreateClient("guest");
            guest.Config.IdentityToken = "legacy-token";
            guest.Client.Connection.Identity = null;

            guest.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => guest.Client.IsConnected), "the open server refused a guest");
            Assert.Equal(0, guest.Client.Connection.ChallengesAnswered);
        }

        // ------------------------------------------------------------------
        // Bans
        // ------------------------------------------------------------------
        [Fact]
        public void ABannedIdentityIsRefusedAtTheHandshake()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            harness.Server.AddBan(new BanEntry
            {
                PublicKey = alice.Config.IdentityToken,
                PlayerName = "alice",
                Reason = "griefing",
            });

            // Banning somebody who is on removes them.
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Count == 0), "the ban did not remove her");

            TestClient again = harness.CreateClient("alice", alice.Config.IdentitySecret);
            again.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(
                harness.AdvanceUntil(
                    () => again.Client.Connection.State == Gtamp.Client.Network.ClientConnectionState.Failed,
                    timeoutSeconds: 20),
                "a banned identity was let back in");

            Assert.Contains("griefing", again.Client.Connection.LastError);
        }

        [Fact]
        public void ANewKeypairEvadesTheBanAndThatIsSaidPlainly()
        {
            // Nothing available to a server with no account system can stop this. What
            // the design buys is that evading costs the evader everything they had:
            // the new identity is a new player with a new character.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            harness.Server.AddBan(new BanEntry { PublicKey = alice.Config.IdentityToken, Reason = "griefing" });
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Count == 0));

            TestClient fresh = harness.CreateClient("alice");
            fresh.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(harness.AdvanceUntil(() => fresh.Client.IsConnected, timeoutSeconds: 20));
            Assert.NotEqual(alice.Config.IdentityToken, fresh.Config.IdentityToken);
        }

        [Fact]
        public void AnExpiredBanLetsThePlayerBackIn()
        {
            var bans = new BanList();
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            bans.Add(new BanEntry
            {
                PublicKey = "key",
                Reason = "cooling off",
                ExpiresAt = now.AddMinutes(30),
            });

            Assert.True(bans.IsBanned("key", now));
            Assert.True(bans.IsBanned("key", now.AddMinutes(29)));

            // Expiry is applied on lookup rather than by a sweep: a timed ban that
            // outlives its window because nothing swept is the same bug as one that
            // never expires.
            Assert.False(bans.IsBanned("key", now.AddMinutes(31)));
            Assert.Equal(0, bans.Count);
        }

        [Fact]
        public void ABanIsFoundByNameOrFingerprint()
        {
            using IdentityKey key = IdentityKey.Create();
            var bans = new BanList();
            bans.Add(new BanEntry { PublicKey = key.PublicKey, PlayerName = "griefer", Reason = "x" });

            Assert.NotNull(bans.FindByReference("griefer"));
            Assert.NotNull(bans.FindByReference("GRIEFER"));
            Assert.NotNull(bans.FindByReference(key.Fingerprint.Substring(0, 6)));
            Assert.NotNull(bans.FindByReference(key.PublicKey));
            Assert.Null(bans.FindByReference("somebody-else"));
        }

        // ------------------------------------------------------------------
        // Permissions and admin over the network
        // ------------------------------------------------------------------
        [Fact]
        public void AnOrdinaryPlayerMayRunNothing()
        {
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Player, "players"));
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Player, "kick 1"));
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Player, "stop"));
            Assert.Empty(AdminPermissions.CommandsFor(PlayerRole.Player));
        }

        [Fact]
        public void AModeratorPolicesPlayersButCannotChangeTheServer()
        {
            Assert.True(AdminPermissions.IsAllowed(PlayerRole.Moderator, "players"));
            Assert.True(AdminPermissions.IsAllowed(PlayerRole.Moderator, "kick 1"));
            Assert.True(AdminPermissions.IsAllowed(PlayerRole.Moderator, "ban 1 60 griefing"));

            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Moderator, "stop"));
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Moderator, "weather RAIN"));

            // Including their own role: a moderator who can promote themselves is an
            // admin with extra steps.
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Moderator, "role 1 admin"));
        }

        [Fact]
        public void AnUnknownCommandNeedsTheHighestPermission()
        {
            // Default deny. A table where forgetting to add a command makes it public
            // is worse than no table, because it reads as though it protects something.
            Assert.Equal(AdminPermission.Server, AdminPermissions.RequiredFor("something-new"));
            Assert.False(AdminPermissions.IsAllowed(PlayerRole.Moderator, "something-new"));
            Assert.True(AdminPermissions.IsAllowed(PlayerRole.Admin, "something-new"));
        }

        [Fact]
        public void AnAdminCanRunAServerCommandFromInsideTheGame()
        {
            using var harness = new TestHarness();
            _ = new AdminConsole(harness.Server);

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(
                () => alice.Client.IsConnected && bob.Client.IsConnected && harness.Server.Players.Count == 2));

            PlayerSession aliceSession = harness.Server.Players.Sessions.Single(s => s.Name == "alice");
            PlayerSession bobSession = harness.Server.Players.Sessions.Single(s => s.Name == "bob");
            aliceSession.Role = PlayerRole.Admin;

            Assert.True(alice.Client.SendAdminCommand("kick " + bobSession.PlayerId));

            Assert.True(
                harness.AdvanceUntil(() => harness.Server.Players.Count == 1, timeoutSeconds: 5),
                "the admin's kick never took effect");

            // The answer comes back as a security notice on the reliable channel, so
            // it lands a little after the kick itself.
            Assert.True(
                harness.AdvanceUntil(
                    () => alice.Console.FilteredEntries().Any(l => l.Message.Contains("Kicked bob")),
                    timeoutSeconds: 5),
                "the admin never saw the result of their own command");
        }

        [Fact]
        public void AnOrdinaryPlayerIsRefusedAndToldWhatTheyMayRun()
        {
            using var harness = new TestHarness();
            _ = new AdminConsole(harness.Server);

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(
                () => alice.Client.IsConnected && bob.Client.IsConnected && harness.Server.Players.Count == 2));

            PlayerSession bobSession = harness.Server.Players.Sessions.Single(s => s.Name == "bob");
            alice.Client.SendAdminCommand("kick " + bobSession.PlayerId);
            harness.Advance(1.0);

            Assert.Equal(2, harness.Server.Players.Count);
            Assert.True(
                alice.Console.FilteredEntries().Any(l => l.Message.Contains("needs more than the Player role")),
                "the refusal was not reported to the client");
        }
    }
}
