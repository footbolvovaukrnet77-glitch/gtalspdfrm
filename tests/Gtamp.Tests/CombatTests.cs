using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    public class CombatArbiterTests
    {
        private static readonly uint Pistol = GameHash.Joaat("WEAPON_PISTOL");
        private static readonly uint Sniper = GameHash.Joaat("WEAPON_HEAVYSNIPER");
        private static readonly uint Knife = GameHash.Joaat("WEAPON_KNIFE");

        private static PlayerEntity Attacker(NetVector3 position, uint weapon) => new PlayerEntity(new EntityId(1))
        {
            PlayerId = 1,
            Position = position,
            Health = 200,
            CurrentWeaponHash = weapon,
        };

        private static PlayerEntity Victim(NetVector3 position, int health = 200) => new PlayerEntity(new EntityId(2))
        {
            PlayerId = 2,
            Position = position,
            Health = health,
        };

        [Fact]
        public void TheJoaatHashesMatchTheGamesOwn()
        {
            // These are documented GTA V weapon hashes. If the hash function ever
            // drifts, every weapon profile and every model lookup silently stops
            // matching, so it is pinned here rather than trusted.
            Assert.Equal(0x1B06D571u, GameHash.Joaat("WEAPON_PISTOL"));
            Assert.Equal(0xA2719263u, GameHash.Joaat("WEAPON_UNARMED"));
            Assert.Equal(0x83BF0278u, GameHash.Joaat("WEAPON_CARBINERIFLE"));
            Assert.Equal(0x97AA0A79u, GameHash.Joaat("EXTRASUNNY"));

            // The game lower-cases before hashing, so case must not matter.
            Assert.Equal(GameHash.Joaat("WEAPON_PISTOL"), GameHash.Joaat("weapon_pistol"));
        }

        [Fact]
        public void APlausibleHitIsAccepted()
        {
            DamageResolution resolution = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, Pistol),
                Victim(new NetVector3(10f, 0f, 0f)),
                Pistol,
                45,
                CombatSettings.CreateDefault());

            Assert.True(resolution.Accepted);
            Assert.Equal(45, resolution.AppliedDamage);
            Assert.False(resolution.Clamped);
            Assert.False(resolution.Fatal);
        }

        [Fact]
        public void DamageBeyondTheWeaponsCeilingIsClampedNotDropped()
        {
            // Dropping it would let a cheat deny damage entirely by over-claiming.
            DamageResolution resolution = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, Pistol),
                Victim(new NetVector3(10f, 0f, 0f)),
                Pistol,
                100000,
                CombatSettings.CreateDefault());

            Assert.True(resolution.Accepted);
            Assert.True(resolution.Clamped);
            Assert.Equal(90, resolution.AppliedDamage);
        }

        [Fact]
        public void AShotBeyondTheWeaponsRangeIsRejected()
        {
            DamageResolution resolution = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, Pistol),
                Victim(new NetVector3(500f, 0f, 0f)),
                Pistol,
                40,
                CombatSettings.CreateDefault());

            Assert.False(resolution.Accepted);
            Assert.Equal(DamageVerdict.RejectedOutOfRange, resolution.Verdict);
        }

        [Fact]
        public void ASniperReachesFurtherThanAPistol()
        {
            CombatSettings settings = CombatSettings.CreateDefault();
            var target = new NetVector3(600f, 0f, 0f);

            Assert.Equal(
                DamageVerdict.RejectedOutOfRange,
                CombatArbiter.Resolve(Attacker(NetVector3.Zero, Pistol), Victim(target), Pistol, 40, settings).Verdict);

            Assert.True(
                CombatArbiter.Resolve(Attacker(NetVector3.Zero, Sniper), Victim(target), Sniper, 150, settings).Accepted);
        }

        [Fact]
        public void MeleeIsHeldToArmsLength()
        {
            CombatSettings settings = CombatSettings.CreateDefault();

            Assert.True(
                CombatArbiter.Resolve(
                    Attacker(NetVector3.Zero, Knife), Victim(new NetVector3(2f, 0f, 0f)), Knife, 50, settings).Accepted);

            Assert.Equal(
                DamageVerdict.RejectedOutOfRange,
                CombatArbiter.Resolve(
                    Attacker(NetVector3.Zero, Knife), Victim(new NetVector3(30f, 0f, 0f)), Knife, 50, settings).Verdict);
        }

        [Fact]
        public void SelfHarmAndNonPositiveDamageAreRejected()
        {
            CombatSettings settings = CombatSettings.CreateDefault();
            PlayerEntity attacker = Attacker(NetVector3.Zero, Pistol);

            Assert.Equal(
                DamageVerdict.RejectedSelfHarm,
                CombatArbiter.Resolve(attacker, attacker, Pistol, 10, settings).Verdict);

            Assert.Equal(
                DamageVerdict.RejectedInvalidDamage,
                CombatArbiter.Resolve(attacker, Victim(NetVector3.Zero), Pistol, 0, settings).Verdict);

            Assert.Equal(
                DamageVerdict.RejectedInvalidDamage,
                CombatArbiter.Resolve(attacker, Victim(NetVector3.Zero), Pistol, -50, settings).Verdict);
        }

        [Fact]
        public void ADeadAttackerLandsNothingAndADeadTargetTakesNothing()
        {
            CombatSettings settings = CombatSettings.CreateDefault();

            PlayerEntity deadAttacker = Attacker(NetVector3.Zero, Pistol);
            deadAttacker.Health = 0;
            Assert.False(CombatArbiter.Resolve(deadAttacker, Victim(NetVector3.Zero), Pistol, 10, settings).Accepted);

            Assert.Equal(
                DamageVerdict.RejectedTargetAlreadyDead,
                CombatArbiter.Resolve(
                    Attacker(NetVector3.Zero, Pistol), Victim(NetVector3.Zero, health: 0), Pistol, 10, settings).Verdict);
        }

        [Fact]
        public void AMissingTargetIsRejected()
        {
            Assert.Equal(
                DamageVerdict.RejectedNoTarget,
                CombatArbiter.Resolve(
                    Attacker(NetVector3.Zero, Pistol), null, Pistol, 10, CombatSettings.CreateDefault()).Verdict);
        }

        [Fact]
        public void ServerRulesCanSwitchOffEachKindOfDamage()
        {
            var settings = CombatSettings.CreateDefault();
            settings.PlayerVersusPlayer = false;
            settings.NpcDamage = false;
            settings.VehicleDamage = false;

            PlayerEntity attacker = Attacker(NetVector3.Zero, Pistol);

            Assert.Equal(
                DamageVerdict.RejectedPvpDisabled,
                CombatArbiter.Resolve(attacker, Victim(NetVector3.Zero), Pistol, 10, settings).Verdict);

            Assert.Equal(
                DamageVerdict.RejectedNpcDamageDisabled,
                CombatArbiter.Resolve(attacker, new PedEntity(new EntityId(3)), Pistol, 10, settings).Verdict);

            Assert.Equal(
                DamageVerdict.RejectedVehicleDamageDisabled,
                CombatArbiter.Resolve(attacker, new VehicleEntity(new EntityId(4)), Pistol, 10, settings).Verdict);
        }

        [Fact]
        public void WeaponMatchingIsOnlyEnforcedWhenAskedFor()
        {
            var settings = CombatSettings.CreateDefault();
            PlayerEntity attacker = Attacker(NetVector3.Zero, Pistol);

            // By default a mismatch is tolerated: the shot and the state update are
            // two different packets, and a player who switches weapons right after
            // firing produces one legitimately.
            Assert.True(CombatArbiter.Resolve(attacker, Victim(NetVector3.Zero), Sniper, 50, settings).Accepted);

            settings.EnforceWeaponMatch = true;
            Assert.Equal(
                DamageVerdict.RejectedWeaponNotHeld,
                CombatArbiter.Resolve(attacker, Victim(NetVector3.Zero), Sniper, 50, settings).Verdict);
        }

        [Fact]
        public void ArmourAbsorbsBeforeHealth()
        {
            var victim = Victim(NetVector3.Zero);
            victim.Armor = 30;

            CombatArbiter.Apply(victim, new DamageResolution { AppliedDamage = 50 });

            Assert.Equal(0, victim.Armor);
            Assert.Equal(180, victim.Health);
        }

        [Fact]
        public void AFatalHitSetsTheDeathFlag()
        {
            var victim = Victim(NetVector3.Zero, health: 20);
            DamageResolution resolution = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, Pistol), victim, Pistol, 50, CombatSettings.CreateDefault());

            Assert.True(resolution.Fatal);

            CombatArbiter.Apply(victim, resolution);
            Assert.Equal(0, victim.Health);
            Assert.True(victim.HasFlag(PlayerFlags.Dead));
        }

        [Fact]
        public void VehicleDamageWearsDownTheBodyAndEventuallyDisablesIt()
        {
            var vehicle = new VehicleEntity(new EntityId(5)) { BodyHealth = 100f };
            CombatArbiter.Apply(vehicle, new DamageResolution { AppliedDamage = 150 });

            Assert.Equal(0f, vehicle.BodyHealth, 1);
            Assert.True(vehicle.HasFlag(VehicleFlags.Undriveable));
        }

        [Fact]
        public void AnUnknownWeaponFallsBackToTheDefaultEnvelope()
        {
            var settings = CombatSettings.CreateDefault();
            uint unknown = GameHash.Joaat("WEAPON_SOME_MOD_ADDED_GUN");

            DamageResolution accepted = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, unknown), Victim(new NetVector3(100f, 0f, 0f)), unknown, 200, settings);
            Assert.True(accepted.Accepted);

            DamageResolution clamped = CombatArbiter.Resolve(
                Attacker(NetVector3.Zero, unknown), Victim(new NetVector3(100f, 0f, 0f)), unknown, 5000, settings);
            Assert.True(clamped.Clamped);
            Assert.Equal(settings.DefaultMaxDamagePerHit, clamped.AppliedDamage);
        }
    }
}
