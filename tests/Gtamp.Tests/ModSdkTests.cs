using System;
using System.Collections.Generic;
using Gtamp.Client.Mods;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Security;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// A mod-defined entity, written the way a third-party mod would write one.
    /// If this replicates end to end without touching the networking layer, the
    /// entity system is genuinely extensible rather than nominally so.
    /// </summary>
    public sealed class TurretEntity : NetEntity
    {
        public const byte WireTypeId = (byte)EntityType.ModDefinedFirst;

        public TurretEntity(EntityId id)
            : base(id, (EntityType)WireTypeId)
        {
        }

        public int Ammo { get; set; }

        public float BarrelPitch { get; set; }

        public bool Deployed { get; set; }

        public override NetEntity Clone()
        {
            var clone = new TurretEntity(Id) { Ammo = Ammo, BarrelPitch = BarrelPitch, Deployed = Deployed };
            CopyBaseTo(clone);
            return clone;
        }
    }

    public sealed class TurretSerializer : EntitySerializer<TurretEntity>
    {
        public TurretSerializer()
            : base(TurretEntity.WireTypeId, "mod.turret")
        {
        }

        public override NetEntity Create(EntityId id) => new TurretEntity(id);

        protected override void DeclareFields(EntityFieldSet<TurretEntity> fields)
        {
            fields
                .Add("Ammo", (a, b) => a.Ammo != b.Ammo, (w, e) => w.WriteVarInt(e.Ammo), (r, e) => e.Ammo = r.ReadVarInt())
                .Add(
                    "BarrelPitch",
                    (a, b) => Math.Abs(a.BarrelPitch - b.BarrelPitch) > 0.01f,
                    (w, e) => w.WriteAngleDegrees(e.BarrelPitch),
                    (r, e) => e.BarrelPitch = r.ReadAngleDegrees())
                .Add("Deployed", (a, b) => a.Deployed != b.Deployed, (w, e) => w.WriteBool(e.Deployed), (r, e) => e.Deployed = r.ReadBool());
        }
    }

    public class ModSdkTests
    {
        private static ModSdk CreateSdk(EntityRegistry registry, out List<string> sent)
        {
            var sentEvents = new List<string>();
            sent = sentEvents;
            return new ModSdk(registry, new LogBus(), (name, payload, reliable) =>
            {
                sentEvents.Add($"{name}:{payload.Length}:{reliable}");
                return true;
            });
        }

        [Fact]
        public void AModDefinedEntityReplicatesThroughTheOrdinarySnapshotPath()
        {
            var registry = EntityRegistry.CreateDefault();
            ModSdk sdk = CreateSdk(registry, out _);

            byte typeId = sdk.RegisterEntity(new TurretSerializer());
            Assert.Equal(TurretEntity.WireTypeId, typeId);

            var world = new WorldState { Tick = 5 };
            world.Add(new TurretEntity(new EntityId(1))
            {
                Position = new NetVector3(10f, 20f, 30f),
                Ammo = 250,
                BarrelPitch = 33.5f,
                Deployed = true,
            });

            var order = new List<NetEntity>(world.Entities);
            SnapshotWriteResult full = SnapshotCodec.Write(world, EntitySnapshotView.Empty, registry, order, 1, 4096);
            EntitySnapshotView view = SnapshotCodec.Apply(full.Payload, EntitySnapshotView.Empty, registry).View;

            var turret = (TurretEntity)view.GetOrNull(new EntityId(1))!;
            Assert.Equal(250, turret.Ammo);
            Assert.True(turret.Deployed);
            Assert.Equal(33.5f, turret.BarrelPitch, 1);
            Assert.Equal(10f, turret.Position.X, 2);

            // And a delta on the mod entity works exactly like a built-in one.
            world.Get<TurretEntity>(new EntityId(1))!.Ammo = 249;
            SnapshotWriteResult delta = SnapshotCodec.Write(world, full.ResultingView, registry, order, 2, 4096);
            Assert.Equal(1, delta.DeltaEntityCount);

            view = SnapshotCodec.Apply(delta.Payload, full.ResultingView, registry).View;
            Assert.Equal(249, ((TurretEntity)view.GetOrNull(new EntityId(1))!).Ammo);
        }

        [Fact]
        public void APeerWithoutTheModReportsAClearErrorInsteadOfCorruptingItsWorld()
        {
            var serverRegistry = EntityRegistry.CreateDefault();
            ModSdk sdk = CreateSdk(serverRegistry, out _);
            sdk.RegisterEntity(new TurretSerializer());

            var clientRegistry = EntityRegistry.CreateDefault();

            var world = new WorldState();
            world.Add(new TurretEntity(new EntityId(1)) { Ammo = 10 });

            SnapshotWriteResult result = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, serverRegistry, new List<NetEntity>(world.Entities), 1, 4096);

            NetSerializationException exception = Assert.Throws<NetSerializationException>(() =>
                SnapshotCodec.Apply(result.Payload, EntitySnapshotView.Empty, clientRegistry));

            Assert.Contains("type id", exception.Message);
            Assert.Contains("/diagnostics", exception.Message);
        }

        [Fact]
        public void RegisteringAModEntityChangesTheSchemaHash()
        {
            var plain = EntityRegistry.CreateDefault();
            var extended = EntityRegistry.CreateDefault();
            CreateSdk(extended, out _).RegisterEntity(new TurretSerializer());

            Assert.NotEqual(plain.ComputeSchemaHash(), extended.ComputeSchemaHash());

            // And the hash is stable for the same type table.
            var extendedAgain = EntityRegistry.CreateDefault();
            CreateSdk(extendedAgain, out _).RegisterEntity(new TurretSerializer());
            Assert.Equal(extended.ComputeSchemaHash(), extendedAgain.ComputeSchemaHash());
        }

        [Fact]
        public void ModEntityIdsMustStayInTheReservedRange()
        {
            var registry = EntityRegistry.CreateDefault();
            ModSdk sdk = CreateSdk(registry, out _);

            Assert.Throws<ArgumentException>(() => sdk.RegisterEntity(new CollidingSerializer()));
        }

        private sealed class CollidingSerializer : EntitySerializer<TurretEntity>
        {
            public CollidingSerializer()
                : base((byte)EntityType.Vehicle, "mod.collides-with-vehicle")
            {
            }

            public override NetEntity Create(EntityId id) => new TurretEntity(id);

            protected override void DeclareFields(EntityFieldSet<TurretEntity> fields)
            {
            }
        }

        [Fact]
        public void RegisteringAfterTheNetworkLayerStartsIsRefused()
        {
            var registry = EntityRegistry.CreateDefault();
            ModSdk sdk = CreateSdk(registry, out _);
            registry.Lock();

            Assert.Throws<InvalidOperationException>(() => sdk.RegisterEntity(new TurretSerializer()));
        }

        [Fact]
        public void NetworkEventsAreRoutedByNameAndRoundTripThroughTheDispatcher()
        {
            var registry = EntityRegistry.CreateDefault();
            ModSdk sdk = CreateSdk(registry, out List<string> sent);

            byte received = 0;
            uint sender = 0;
            sdk.RegisterNetworkEvent("turret.fire", (playerId, payload) =>
            {
                sender = playerId;
                received = payload[0];
            });

            Assert.True(sdk.IsEventRegistered("turret.fire"));

            sdk.SendNetworkEvent("turret.fire", new byte[] { 42 });
            Assert.Single(sent);
            Assert.Equal("turret.fire:1:True", sent[0]);

            Assert.True(sdk.Dispatch("turret.fire", 7, new byte[] { 42 }));
            Assert.Equal(42, received);
            Assert.Equal(7u, sender);

            Assert.False(sdk.Dispatch("never.registered", 7, new byte[] { 1 }));
        }

        [Fact]
        public void EventNamesAreCaseInsensitiveSoTwoModsCannotDisagreeOverCapitals()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);

            bool called = false;
            sdk.RegisterNetworkEvent("MyMod.Ping", (_, _) => called = true);

            Assert.True(sdk.Dispatch("mymod.ping", 0, System.Array.Empty<byte>()));
            Assert.True(called);
        }

        [Fact]
        public void SendingAnUnregisteredEventFailsLoudly()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            Assert.Throws<InvalidOperationException>(() => sdk.SendNetworkEvent("never.registered", new byte[0]));
        }

        [Fact]
        public void CustomStateMustBeDeclaredBeforeItIsWritten()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            var entity = new PlayerEntity(new EntityId(1));

            Assert.Throws<InvalidOperationException>(() => sdk.SetState(entity, "lspdfr.callout", "traffic-stop"));

            sdk.RegisterState("lspdfr.callout", "the callout this entity belongs to");
            sdk.SetState(entity, "lspdfr.callout", "traffic-stop");

            Assert.Equal("traffic-stop", sdk.GetState(entity, "lspdfr.callout"));
            Assert.Null(sdk.GetState(entity, "lspdfr.pursuit"));
        }

        [Fact]
        public void CustomStateSurvivesAReplicationRoundTrip()
        {
            var registry = EntityRegistry.CreateDefault();
            var world = new WorldState();
            var player = new PlayerEntity(new EntityId(1)) { Name = "officer" };
            player.CustomData["lspdfr.role"] = "officer";
            world.Add(player);

            SnapshotWriteResult result = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, registry, new List<NetEntity>(world.Entities), 1, 4096);

            EntitySnapshotView view = SnapshotCodec.Apply(result.Payload, EntitySnapshotView.Empty, registry).View;
            Assert.Equal("officer", view.GetOrNull(new EntityId(1))!.CustomData["lspdfr.role"]);
        }

        [Fact]
        public void EverySdkRegistrationNameFromTheSpecIsNowImplemented()
        {
            // This test used to assert that RegisterCustomWeapon threw with a phase
            // number. It was the last of the fifteen names in master prompt section 21
            // that did not exist, so its replacement asserts the opposite: none of them
            // throws NotSupportedException any more.
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);

            sdk.RegisterCustomWeapon("WEAPON_MYMOD_RAILGUN", null!);

            Assert.Contains("WEAPON_MYMOD_RAILGUN", sdk.DescribeWeapon(GameHash.Joaat("WEAPON_MYMOD_RAILGUN")));
        }

        [Fact]
        public void ANamedWeaponIsReadableInsteadOfABareHash()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            uint hash = GameHash.Joaat("WEAPON_MYMOD_RAILGUN");

            // Before registration the console has nothing but the hash to show.
            Assert.Equal($"0x{hash:X8}", sdk.DescribeWeapon(hash));

            sdk.RegisterCustomWeapon("WEAPON_MYMOD_RAILGUN", new WeaponProfile("WEAPON_MYMOD_RAILGUN", 400, 900f));

            Assert.Equal($"WEAPON_MYMOD_RAILGUN (0x{hash:X8})", sdk.DescribeWeapon(hash));
            Assert.Equal("none", sdk.DescribeWeapon(0));
        }

        [Fact]
        public void RegisteringAWeaponWithNoNameIsRefused()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            Assert.Throws<ArgumentException>(() => sdk.RegisterCustomWeapon("  ", null!));
        }

        [Fact]
        public void RpcAndMissionRegistrationFailClearlyWhenTheSdkIsNotWiredUp()
        {
            // A bare SDK — one built without a client behind it — says so rather than
            // registering something that could never fire.
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);

            Assert.Throws<InvalidOperationException>(() => sdk.RegisterRPC("x", _ => new byte[0]));
            Assert.Throws<InvalidOperationException>(() => sdk.RegisterMission("m", new StubActivityHandler()));
        }

        private sealed class StubActivityHandler : Gtamp.Client.Missions.IActivityHandler
        {
            public void OnStarted(ActivityEntity activity)
            {
            }

            public void OnObjectiveChanged(ActivityEntity activity, ActivityObjective objective)
            {
            }

            public void OnFinished(ActivityEntity activity)
            {
            }
        }

        [Fact]
        public void DimensionsAndInteriorsAreAllocatedOnce()
        {
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);

            uint first = sdk.RegisterDimension("callout-instance");
            uint second = sdk.RegisterDimension("other");
            Assert.NotEqual(first, second);
            Assert.Equal(first, sdk.RegisterDimension("callout-instance"));
        }

        // --- adapter host -------------------------------------------------
        private sealed class FakeAdapter : IModAdapter
        {
            private readonly bool _available;
            private readonly bool _throwOnInit;
            private readonly bool _throwOnUpdate;

            public FakeAdapter(string id, bool available, bool throwOnInit = false, bool throwOnUpdate = false)
            {
                Id = id;
                _available = available;
                _throwOnInit = throwOnInit;
                _throwOnUpdate = throwOnUpdate;
            }

            public string Id { get; }

            public string DisplayName => Id;

            public int Updates { get; private set; }

            public bool ShutdownCalled { get; private set; }

            public bool IsAvailable(ModEnvironment environment) => _available;

            public void Initialize(IModSdk sdk, ModEnvironment environment)
            {
                if (_throwOnInit)
                {
                    throw new InvalidOperationException("adapter blew up during init");
                }
            }

            public void Update(double now)
            {
                if (_throwOnUpdate)
                {
                    throw new InvalidOperationException("adapter blew up during update");
                }

                Updates++;
            }

            public void Shutdown() => ShutdownCalled = true;

            public string DescribeStatus() => "fake";
        }

        [Fact]
        public void UnavailableAdaptersAreSkippedNotFailed()
        {
            var host = new AdapterHost(new LogBus());
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            var environment = new ModEnvironment();

            host.Add(new FakeAdapter("absent", available: false), sdk, environment);

            Assert.Empty(host.Active);
            Assert.Single(host.Skipped);
            Assert.Empty(host.Failed);
        }

        [Fact]
        public void AnAdapterThatThrowsDuringInitDoesNotStopTheOthers()
        {
            var host = new AdapterHost(new LogBus());
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            var environment = new ModEnvironment();

            var good = new FakeAdapter("good", available: true);
            host.Add(new FakeAdapter("bad", available: true, throwOnInit: true), sdk, environment);
            host.Add(good, sdk, environment);

            Assert.Single(host.Active);
            Assert.Single(host.Failed);

            host.Update(1.0);
            Assert.Equal(1, good.Updates);
        }

        [Fact]
        public void AnAdapterThatThrowsDuringUpdateIsDisabledRatherThanCrashingTheClient()
        {
            var host = new AdapterHost(new LogBus());
            ModSdk sdk = CreateSdk(EntityRegistry.CreateDefault(), out _);
            var environment = new ModEnvironment();

            var bad = new FakeAdapter("bad", available: true, throwOnUpdate: true);
            var good = new FakeAdapter("good", available: true);
            host.Add(bad, sdk, environment);
            host.Add(good, sdk, environment);

            host.Update(1.0);
            host.Update(2.0);

            Assert.Single(host.Active);
            Assert.Contains("bad", host.Failed);
            Assert.True(bad.ShutdownCalled);
            Assert.Equal(2, good.Updates);
        }
    }

    public class ModCompatibilityTests
    {
        private static ModManifest Manifest(params ModDescriptor[] mods)
        {
            var manifest = new ModManifest();
            manifest.Mods.AddRange(mods);
            return manifest;
        }

        private static ModDescriptor Mod(
            string id,
            string version = "1.0.0",
            string hash = "",
            ModNetworkRequirement requirement = ModNetworkRequirement.Optional) =>
            new ModDescriptor { Id = id, Name = id, Version = version, Hash = hash, Requirement = requirement };

        [Fact]
        public void MatchingModsAreCompatible()
        {
            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(Mod("lspdfr")), Manifest(Mod("lspdfr")));

            Assert.Single(report);
            Assert.Equal(ModCompatibility.Compatible, report[0].Status);
            Assert.False(ModCompatibilityChecker.HasBlockingIssue(report));
        }

        [Fact]
        public void AMissingOptionalModIsReportedButDoesNotBlock()
        {
            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(Mod("lspdfr")), Manifest());

            Assert.Equal(ModCompatibility.Missing, report[0].Status);
            Assert.False(report[0].BlocksConnection);
        }

        [Fact]
        public void AMissingRequiredModBlocks()
        {
            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(Mod("core-gamemode", requirement: ModNetworkRequirement.Required)), Manifest());

            Assert.Equal(ModCompatibility.Missing, report[0].Status);
            Assert.True(report[0].BlocksConnection);
            Assert.True(ModCompatibilityChecker.HasBlockingIssue(report));
        }

        [Fact]
        public void VersionAndHashMismatchesAreDistinguished()
        {
            List<ModCompatibilityEntry> version = ModCompatibilityChecker.Compare(
                Manifest(Mod("lspdfr", "0.4.9")), Manifest(Mod("lspdfr", "0.4.8")));
            Assert.Equal(ModCompatibility.WrongVersion, version[0].Status);

            List<ModCompatibilityEntry> hash = ModCompatibilityChecker.Compare(
                Manifest(Mod("lspdfr", "0.4.9", "aaaa")), Manifest(Mod("lspdfr", "0.4.9", "bbbb")));
            Assert.Equal(ModCompatibility.HashMismatch, hash[0].Status);
        }

        [Fact]
        public void MissingDependenciesDowngradeToPartiallyCompatible()
        {
            ModDescriptor callout = Mod("callout-pack");
            callout.Dependencies.Add("common-data-framework");

            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(callout), Manifest(Mod("callout-pack")));

            Assert.Equal(ModCompatibility.PartiallyCompatible, report[0].Status);
            Assert.Contains("common-data-framework", report[0].Detail);
            Assert.False(report[0].BlocksConnection);
        }

        [Fact]
        public void ClientOnlyModsAreNeverCompared()
        {
            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(Mod("graphics-pack", requirement: ModNetworkRequirement.ClientOnly)), Manifest());

            Assert.Empty(report);
        }

        [Fact]
        public void ModsTheServerDoesNotKnowAboutAreReportedAsUnsupportedButNeverBlock()
        {
            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(
                Manifest(), Manifest(Mod("some-local-script")));

            Assert.Single(report);
            Assert.Equal(ModCompatibility.Unsupported, report[0].Status);
            Assert.False(report[0].BlocksConnection);
        }

        [Fact]
        public void TheManifestRoundTripsOnTheWire()
        {
            ModDescriptor mod = Mod("lspdfr", "0.4.9", "abcd1234");
            mod.Dependencies.Add("rageluginhook");

            var manifest = Manifest(mod);
            manifest.SchemaHash = 0xDEADBEEF;
            manifest.LspdfrPresent = true;
            manifest.LspdfrVersion = "0.4.9";
            manifest.RagePluginHookPresent = true;

            var writer = new NetWriter();
            manifest.Write(writer);
            ModManifest restored = ModManifest.Read(new NetReader(writer.ToArray()));

            Assert.Equal(0xDEADBEEFu, restored.SchemaHash);
            Assert.True(restored.LspdfrPresent);
            Assert.Equal("0.4.9", restored.LspdfrVersion);
            Assert.Single(restored.Mods);
            Assert.Equal("abcd1234", restored.Mods[0].Hash);
            Assert.Equal("rageluginhook", restored.Mods[0].Dependencies[0]);
        }
    }
}
