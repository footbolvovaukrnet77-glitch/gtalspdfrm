using System;
using System.Linq;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Server.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Phase 9: what happens when two clients do not have the same mods installed
    /// (master prompt section 4, docs/ENGINE_ANALYSIS.md §4.4).
    /// <para>
    /// The rule the engine analysis commits to is "a client that cannot resolve a
    /// hash reports it instead of substituting silently". Before this phase the code
    /// did neither: a missing vehicle model produced an entity that was retried every
    /// frame and never appeared, and a missing player model was quietly replaced with
    /// a default body. Both are the same failure to diagnose — everything else looks
    /// healthy, because it is.
    /// </para>
    /// </summary>
    public class ContentNegotiationTests
    {
        private const uint ModdedCar = 0xDEADBEEF;
        private const uint ModdedBody = 0xFEEDFACE;

        [Fact]
        public void AVehicleModelThisClientDoesNotHaveIsReportedOnce()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            // Bob does not have the mod that adds the car alice is about to drive.
            bob.Bridge.UnavailableModels.Add(ModdedCar);

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => alice.Client.IsConnected && bob.Client.IsConnected),
                "the clients never connected");

            alice.Bridge.PutLocalPlayerInVehicle(ModdedCar, new NetVector3(220f, -810f, 30f));

            Assert.True(
                harness.AdvanceUntil(() => !bob.Client.MissingContent.IsEmpty, timeoutSeconds: 10),
                "bob never reported the model he does not have");

            MissingModel missing = bob.Client.MissingContent.Models.Single();
            Assert.Equal(ModdedCar, missing.ModelHash);
            Assert.Equal(EntityType.Vehicle, missing.WantedBy);
            Assert.False(missing.Substituted);

            // Creation is retried every frame; the record must not grow with attempts.
            harness.Advance(2.0);
            Assert.Single(bob.Client.MissingContent.Models);
            Assert.Equal(1, bob.Client.MissingContent.Models.Single().EntityCount);

            // And alice, who has the mod, sees nothing wrong.
            Assert.True(alice.Client.MissingContent.IsEmpty);
        }

        [Fact]
        public void AMissingPlayerModelIsSubstitutedAndTheSubstitutionIsRecorded()
        {
            // A player is the one case where showing the wrong thing beats showing
            // nothing: an invisible teammate is worse than one in the wrong body.
            // But the substitution is still a defect, so it is recorded as one.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Bridge.Sample.ModelHash = ModdedBody;
            bob.Bridge.UnavailableModels.Add(ModdedBody);

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(
                harness.AdvanceUntil(() => !bob.Client.MissingContent.IsEmpty),
                "bob never reported alice's missing character model");

            MissingModel missing = bob.Client.MissingContent.Models.Single();
            Assert.Equal(ModdedBody, missing.ModelHash);
            Assert.Equal(EntityType.Player, missing.WantedBy);
            Assert.True(missing.Substituted);
            Assert.Contains("substituted", missing.ToString());

            // Alice is still visible to bob, wrong body and all.
            Assert.Equal(2, bob.PlayerCount);
        }

        [Fact]
        public void AModelStillStreamingInIsNotMistakenForAMissingMod()
        {
            // The distinction that matters: "not yet" resolves on a later frame,
            // "never" means an asset the player does not have. Collapsing them would
            // fill the report with false positives on every join.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            bob.Bridge.LoadingModels.Add(ModdedCar);

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => alice.Client.IsConnected && bob.Client.IsConnected),
                "the clients never connected");

            alice.Bridge.PutLocalPlayerInVehicle(ModdedCar, new NetVector3(220f, -810f, 30f));
            Assert.True(
                harness.AdvanceUntil(() => bob.Client.RemoteEntities.VehicleCount == 1, timeoutSeconds: 10),
                "bob never received the vehicle at all");

            harness.Advance(2.0);
            Assert.True(bob.Client.MissingContent.IsEmpty);
        }

        [Fact]
        public void TheTrackerCountsEntitiesRatherThanAttempts()
        {
            var log = new LogBus();
            var tracker = new MissingContentTracker(log);

            Assert.True(tracker.Report(ModdedCar, EntityType.Vehicle, new EntityId(7), substituted: false));
            Assert.False(tracker.Report(ModdedCar, EntityType.Vehicle, new EntityId(7), substituted: false));
            Assert.False(tracker.Report(ModdedCar, EntityType.Vehicle, new EntityId(8), substituted: false));

            MissingModel record = tracker.Models.Single();
            Assert.Equal(2, record.EntityCount);
            Assert.Equal(new EntityId(8), record.LastEntity);

            // One substituting report is enough to mark the whole hash as substituted.
            tracker.Report(ModdedCar, EntityType.Vehicle, new EntityId(9), substituted: true);
            Assert.True(tracker.Models.Single().Substituted);

            tracker.Clear(ModdedCar);
            Assert.True(tracker.IsEmpty);
        }

        [Fact]
        public void MissingContentReachesTheThreePlacesAPlayerWouldLook()
        {
            // The whole point of recording it is that somebody sees it. A tracker
            // nothing reads is the silent failure with extra steps.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Bridge.Sample.ModelHash = ModdedBody;
            bob.Bridge.UnavailableModels.Add(ModdedBody);

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => !bob.Client.MissingContent.IsEmpty),
                "bob never reported the missing model");

            string diagnostics = Gtamp.Client.Diagnostics.DiagnosticsRunner.Format(
                Gtamp.Client.Diagnostics.DiagnosticsRunner.Run(bob.Client));
            Assert.Contains("Mod content", diagnostics);
            Assert.Contains($"0x{ModdedBody:X8}", diagnostics);

            string mods = bob.Console.Submit("mods");
            Assert.Contains($"0x{ModdedBody:X8}", mods);

            string report = Gtamp.Client.Diagnostics.BugReportBuilder.Build(
                bob.Client, "alice looks like the wrong character");
            Assert.Contains("MISSING CONTENT:", report);
            Assert.Contains($"0x{ModdedBody:X8}", report);
        }

        // ------------------------------------------------------------------
        // Custom weapons
        // ------------------------------------------------------------------
        private static PlayerEntity Attacker(uint weapon) => new PlayerEntity(new EntityId(1))
        {
            PlayerId = 1,
            Position = NetVector3.Zero,
            Health = 200,
            CurrentWeaponHash = weapon,
        };

        private static PlayerEntity VictimAt(float metres) => new PlayerEntity(new EntityId(2))
        {
            PlayerId = 2,
            Position = new NetVector3(metres, 0f, 0f),
            Health = 200,
        };

        [Fact]
        public void AnUnknownWeaponFallsBackToThePermissiveDefaultEnvelope()
        {
            // The baseline the two tests below improve on. An unprofiled weapon is not
            // blocked — it is validated loosely, which is wrong in both directions.
            uint railgun = GameHash.Joaat("WEAPON_MYMOD_RAILGUN");
            var settings = CombatSettings.CreateDefault();

            // Well inside the 400 m default: accepted, at the 250-damage default ceiling.
            DamageResolution near = CombatArbiter.Resolve(
                Attacker(railgun), VictimAt(300f), railgun, 400, settings);
            Assert.True(near.Accepted);
            Assert.Equal(settings.DefaultMaxDamagePerHit, near.AppliedDamage);
            Assert.True(near.Clamped);

            // Beyond it: a legitimate long-range shot is rejected.
            DamageResolution far = CombatArbiter.Resolve(
                Attacker(railgun), VictimAt(900f), railgun, 400, settings);
            Assert.Equal(DamageVerdict.RejectedOutOfRange, far.Verdict);
        }

        [Fact]
        public void AServerRegisteredWeaponIsArbitratedAgainstItsOwnEnvelope()
        {
            using var harness = new TestHarness();
            uint railgun = GameHash.Joaat("WEAPON_MYMOD_RAILGUN");

            harness.Server.Mods.RegisterWeapon(new WeaponProfile("WEAPON_MYMOD_RAILGUN", 400, 1000f));

            DamageResolution far = CombatArbiter.Resolve(
                Attacker(railgun), VictimAt(900f), railgun, 400, harness.Server.Combat);

            Assert.True(far.Accepted);
            Assert.Equal(400, far.AppliedDamage);
            Assert.False(far.Clamped);
        }

        [Fact]
        public void AnOperatorCanProfileAWeaponWithoutWritingAServerMod()
        {
            var config = new ServerConfig { ServerName = "weapons", SaveIntervalSeconds = 0 };
            config.CustomWeapons.Add(new CustomWeaponSetting
            {
                Name = "WEAPON_MYMOD_TASER",
                MaxDamagePerHit = 15,
                MaxRange = 10f,
            });

            using var harness = new TestHarness(config);
            uint taser = GameHash.Joaat("WEAPON_MYMOD_TASER");

            // The ceiling now bites: a modded taser cannot claim the 250-damage default.
            DamageResolution hit = CombatArbiter.Resolve(
                Attacker(taser), VictimAt(5f), taser, 200, harness.Server.Combat);

            Assert.True(hit.Accepted);
            Assert.Equal(15, hit.AppliedDamage);
            Assert.True(hit.Clamped);

            DamageResolution reach = CombatArbiter.Resolve(
                Attacker(taser), VictimAt(40f), taser, 10, harness.Server.Combat);
            Assert.Equal(DamageVerdict.RejectedOutOfRange, reach.Verdict);
        }

        [Fact]
        public void AMalformedCustomWeaponEntryIsReportedAndSkipped()
        {
            // A typo in server.json must not take the server down, and must not
            // silently install a nonsense envelope either.
            var config = new ServerConfig { ServerName = "weapons", SaveIntervalSeconds = 0 };
            config.CustomWeapons.Add(new CustomWeaponSetting { Name = "", MaxDamagePerHit = 10, MaxRange = 10f });
            config.CustomWeapons.Add(new CustomWeaponSetting { Name = "WEAPON_BAD", MaxDamagePerHit = 0, MaxRange = 0f });
            config.CustomWeapons.Add(new CustomWeaponSetting
            {
                Name = "WEAPON_GOOD",
                MaxDamagePerHit = 42,
                MaxRange = 50f,
            });

            using var harness = new TestHarness(config);

            Assert.Null(harness.Server.Combat.Find(GameHash.Joaat("WEAPON_BAD")));

            WeaponProfile? good = harness.Server.Combat.Find(GameHash.Joaat("WEAPON_GOOD"));
            Assert.NotNull(good);
            Assert.Equal(42, good!.MaxDamagePerHit);
        }

        [Fact]
        public void AClientCannotWidenTheEnvelopeItsOwnHitsAreCheckedAgainst()
        {
            // The reason RegisterCustomWeapon is only half of the feature: combat is
            // arbitrated on the server, so a client-side declaration describes the
            // weapon locally and grants it nothing.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            uint railgun = GameHash.Joaat("WEAPON_MYMOD_RAILGUN");

            alice.Client.Sdk.RegisterCustomWeapon(
                "WEAPON_MYMOD_RAILGUN", new WeaponProfile("WEAPON_MYMOD_RAILGUN", 9999, 5000f));

            // The client now names it, which is the whole of what it gained.
            Assert.Contains("WEAPON_MYMOD_RAILGUN", alice.Client.Sdk.DescribeWeapon(railgun));

            // The server has never heard of it, so its hits are still validated
            // against the default envelope.
            Assert.Null(harness.Server.Combat.Find(railgun));

            DamageResolution far = CombatArbiter.Resolve(
                Attacker(railgun), VictimAt(900f), railgun, 9999, harness.Server.Combat);
            Assert.Equal(DamageVerdict.RejectedOutOfRange, far.Verdict);
        }
    }
}
