using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.Security
{
    /// <summary>One weapon's validation envelope.</summary>
    public sealed class WeaponProfile
    {
        public WeaponProfile(string name, int maxDamagePerHit, float maxRange, bool melee = false)
        {
            Name = name;
            Hash = GameHash.Joaat(name);
            MaxDamagePerHit = maxDamagePerHit;
            MaxRange = maxRange;
            IsMelee = melee;
        }

        public string Name { get; }

        public uint Hash { get; }

        /// <summary>Ceiling for a single hit, generous enough to cover headshot multipliers.</summary>
        public int MaxDamagePerHit { get; }

        public float MaxRange { get; }

        public bool IsMelee { get; }
    }

    public enum DamageVerdict : byte
    {
        Accepted = 0,
        RejectedNoTarget = 1,
        RejectedSelfHarm = 2,
        RejectedTargetAlreadyDead = 3,
        RejectedPvpDisabled = 4,
        RejectedNpcDamageDisabled = 5,
        RejectedVehicleDamageDisabled = 6,
        RejectedOutOfRange = 7,
        RejectedInvalidDamage = 8,
        RejectedWeaponNotHeld = 9,
        RejectedTargetNotDamageable = 10,
    }

    public sealed class DamageResolution
    {
        public DamageVerdict Verdict { get; set; } = DamageVerdict.Accepted;

        public int AppliedDamage { get; set; }

        /// <summary>True when the claim exceeded the weapon's ceiling and was reduced to it.</summary>
        public bool Clamped { get; set; }

        public bool Fatal { get; set; }

        public string Detail { get; set; } = string.Empty;

        public bool Accepted => Verdict == DamageVerdict.Accepted;
    }

    public sealed class CombatSettings
    {
        public bool PlayerVersusPlayer { get; set; } = true;

        public bool FriendlyFire { get; set; } = true;

        public bool NpcDamage { get; set; } = true;

        public bool VehicleDamage { get; set; } = true;

        /// <summary>Ceiling for a weapon with no profile.</summary>
        public int DefaultMaxDamagePerHit { get; set; } = 250;

        public float DefaultMaxRange { get; set; } = 400f;

        public float MeleeMaxRange { get; set; } = 5f;

        /// <summary>
        /// Reject a report whose weapon is not the attacker's current one.
        /// <para>
        /// Off by default, and that is deliberate. The weapon a shot was fired with
        /// and the weapon in the attacker's last state update are two different
        /// packets; a player who switches weapons right after firing legitimately
        /// produces a mismatch. Enforcing it costs honest players hits, so it belongs
        /// at Strict where the operator has accepted that trade.
        /// </para>
        /// </summary>
        public bool EnforceWeaponMatch { get; set; }

        public Dictionary<uint, WeaponProfile> Weapons { get; } = new Dictionary<uint, WeaponProfile>();

        public WeaponProfile? Find(uint weaponHash) =>
            Weapons.TryGetValue(weaponHash, out WeaponProfile? profile) ? profile : null;

        public void Add(WeaponProfile profile) => Weapons[profile.Hash] = profile;

        /// <summary>
        /// A starting table covering the weapon classes, keyed by joaat of the game's
        /// own weapon names. The numbers are validation ceilings, not damage values —
        /// the game decides how much a hit actually does; these only bound what a
        /// client is allowed to claim.
        /// </summary>
        public static CombatSettings CreateDefault()
        {
            var settings = new CombatSettings();

            settings.Add(new WeaponProfile("WEAPON_UNARMED", 30, 3f, melee: true));
            settings.Add(new WeaponProfile("WEAPON_KNIFE", 80, 4f, melee: true));
            settings.Add(new WeaponProfile("WEAPON_BAT", 70, 4f, melee: true));
            settings.Add(new WeaponProfile("WEAPON_HAMMER", 70, 4f, melee: true));
            settings.Add(new WeaponProfile("WEAPON_NIGHTSTICK", 60, 4f, melee: true));

            settings.Add(new WeaponProfile("WEAPON_PISTOL", 90, 120f));
            settings.Add(new WeaponProfile("WEAPON_COMBATPISTOL", 90, 120f));
            settings.Add(new WeaponProfile("WEAPON_HEAVYPISTOL", 110, 120f));
            settings.Add(new WeaponProfile("WEAPON_PISTOL50", 130, 120f));
            settings.Add(new WeaponProfile("WEAPON_STUNGUN", 20, 12f));

            settings.Add(new WeaponProfile("WEAPON_MICROSMG", 80, 150f));
            settings.Add(new WeaponProfile("WEAPON_SMG", 85, 180f));
            settings.Add(new WeaponProfile("WEAPON_ASSAULTSMG", 85, 180f));

            settings.Add(new WeaponProfile("WEAPON_ASSAULTRIFLE", 110, 300f));
            settings.Add(new WeaponProfile("WEAPON_CARBINERIFLE", 110, 300f));
            settings.Add(new WeaponProfile("WEAPON_ADVANCEDRIFLE", 110, 300f));
            settings.Add(new WeaponProfile("WEAPON_SPECIALCARBINE", 110, 300f));

            settings.Add(new WeaponProfile("WEAPON_PUMPSHOTGUN", 160, 60f));
            settings.Add(new WeaponProfile("WEAPON_SAWNOFFSHOTGUN", 170, 40f));
            settings.Add(new WeaponProfile("WEAPON_ASSAULTSHOTGUN", 150, 60f));

            settings.Add(new WeaponProfile("WEAPON_SNIPERRIFLE", 220, 800f));
            settings.Add(new WeaponProfile("WEAPON_HEAVYSNIPER", 300, 1200f));
            settings.Add(new WeaponProfile("WEAPON_MARKSMANRIFLE", 200, 600f));

            settings.Add(new WeaponProfile("WEAPON_MG", 120, 300f));
            settings.Add(new WeaponProfile("WEAPON_COMBATMG", 130, 300f));

            settings.Add(new WeaponProfile("WEAPON_GRENADE", 400, 120f));
            settings.Add(new WeaponProfile("WEAPON_STICKYBOMB", 400, 120f));
            settings.Add(new WeaponProfile("WEAPON_RPG", 600, 400f));
            settings.Add(new WeaponProfile("WEAPON_MOLOTOV", 200, 60f));

            return settings;
        }
    }

    /// <summary>
    /// Decides whether a reported hit happened.
    /// <para>
    /// The attacking client is the only party that knows it fired — the server
    /// cannot raycast, because it has no map (docs/ENGINE_ANALYSIS.md §2). So a hit
    /// arrives as a claim and is checked against what the server does know: where
    /// both parties are, what the attacker was holding, what that weapon could
    /// plausibly do, and what the server's own rules allow.
    /// </para>
    /// <para>
    /// This is not line-of-sight validation and does not pretend to be. A client
    /// claiming a hit through a wall, within range and with the right weapon, is
    /// accepted. Detecting that would need the map geometry the server does not have.
    /// </para>
    /// </summary>
    public static class CombatArbiter
    {
        public static DamageResolution Resolve(
            CharacterEntity attacker,
            NetEntity? target,
            uint weaponHash,
            int claimedDamage,
            CombatSettings settings)
        {
            var resolution = new DamageResolution();

            if (target == null)
            {
                return Reject(resolution, DamageVerdict.RejectedNoTarget, "the target does not exist");
            }

            if (target.Id == attacker.Id)
            {
                return Reject(resolution, DamageVerdict.RejectedSelfHarm, "a client cannot report damage to itself");
            }

            if (claimedDamage <= 0)
            {
                return Reject(resolution, DamageVerdict.RejectedInvalidDamage, $"claimed damage was {claimedDamage}");
            }

            if (!attacker.IsAlive)
            {
                return Reject(resolution, DamageVerdict.RejectedSelfHarm, "a dead attacker cannot land hits");
            }

            switch (target)
            {
                case PlayerEntity player:
                    if (!settings.PlayerVersusPlayer)
                    {
                        return Reject(resolution, DamageVerdict.RejectedPvpDisabled, "player versus player is disabled");
                    }

                    if (!player.IsAlive)
                    {
                        return Reject(resolution, DamageVerdict.RejectedTargetAlreadyDead, "the target is already dead");
                    }

                    break;

                case PedEntity ped:
                    if (!settings.NpcDamage)
                    {
                        return Reject(resolution, DamageVerdict.RejectedNpcDamageDisabled, "NPC damage is disabled");
                    }

                    if (!ped.IsAlive)
                    {
                        return Reject(resolution, DamageVerdict.RejectedTargetAlreadyDead, "the target is already dead");
                    }

                    break;

                case VehicleEntity:
                    if (!settings.VehicleDamage)
                    {
                        return Reject(resolution, DamageVerdict.RejectedVehicleDamageDisabled, "vehicle damage is disabled");
                    }

                    break;

                default:
                    return Reject(
                        resolution, DamageVerdict.RejectedTargetNotDamageable, $"{target.Type} cannot take damage");
            }

            WeaponProfile? profile = settings.Find(weaponHash);

            if (settings.EnforceWeaponMatch && attacker.CurrentWeaponHash != weaponHash)
            {
                return Reject(
                    resolution,
                    DamageVerdict.RejectedWeaponNotHeld,
                    $"reported weapon 0x{weaponHash:X8} but the attacker is holding 0x{attacker.CurrentWeaponHash:X8}");
            }

            float range = profile != null
                ? (profile.IsMelee ? Math.Min(profile.MaxRange, settings.MeleeMaxRange) : profile.MaxRange)
                : settings.DefaultMaxRange;

            float distance = NetVector3.Distance(attacker.Position, target.Position);
            if (distance > range)
            {
                return Reject(
                    resolution,
                    DamageVerdict.RejectedOutOfRange,
                    $"target was {distance:0.#} m away, beyond the {range:0} m limit for this weapon");
            }

            int ceiling = profile?.MaxDamagePerHit ?? settings.DefaultMaxDamagePerHit;
            int applied = claimedDamage;
            if (applied > ceiling)
            {
                // Clamped rather than rejected: a legitimate headshot or explosive can
                // exceed the base figure, and dropping the hit entirely would let a
                // cheat deny damage simply by over-claiming it.
                applied = ceiling;
                resolution.Clamped = true;
                resolution.Detail = $"claimed {claimedDamage}, clamped to the {ceiling} ceiling for this weapon";
            }

            resolution.Verdict = DamageVerdict.Accepted;
            resolution.AppliedDamage = applied;

            if (target is CharacterEntity character)
            {
                resolution.Fatal = character.Health - applied <= 0;
            }

            return resolution;
        }

        /// <summary>Applies an accepted resolution to the target. Armour absorbs first, as the game does.</summary>
        public static void Apply(NetEntity target, DamageResolution resolution)
        {
            if (!resolution.Accepted)
            {
                return;
            }

            switch (target)
            {
                case CharacterEntity character:
                {
                    int remaining = resolution.AppliedDamage;
                    if (character.Armor > 0)
                    {
                        int absorbed = Math.Min(character.Armor, remaining);
                        character.Armor -= absorbed;
                        remaining -= absorbed;
                    }

                    character.Health = Math.Max(0, character.Health - remaining);
                    if (character.Health == 0)
                    {
                        character.SetFlag(PlayerFlags.Dead, true);
                    }

                    break;
                }

                case VehicleEntity vehicle:
                {
                    vehicle.BodyHealth = Math.Max(0f, vehicle.BodyHealth - resolution.AppliedDamage);
                    if (vehicle.BodyHealth <= 0f)
                    {
                        vehicle.SetFlag(VehicleFlags.Undriveable, true);
                    }

                    break;
                }
            }
        }

        private static DamageResolution Reject(DamageResolution resolution, DamageVerdict verdict, string detail)
        {
            resolution.Verdict = verdict;
            resolution.Detail = detail;
            resolution.AppliedDamage = 0;
            return resolution;
        }
    }
}
