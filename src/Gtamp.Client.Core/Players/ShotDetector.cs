using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>
    /// Counts the rounds the local player actually fired between two frames.
    /// <para>
    /// <b>Why counting is the hard part.</b> The obvious signal is the shooting flag,
    /// and the obvious mistake is to send one shot per frame it is set: a rifle
    /// firing at 600 rounds a minute holds that flag for six frames per round at 60
    /// fps, so the flag alone reports six times as many shots as were fired, and a
    /// player holding the trigger fills the uplink with them. The flag says a weapon
    /// is being fired. It does not say how often.
    /// </para>
    /// <para>
    /// Ammunition in the clip does. It falls by exactly one per round, so the
    /// difference between two frames is the number of rounds fired in between —
    /// which is also correct for a burst that started and finished inside one frame,
    /// something the flag cannot express at all.
    /// </para>
    /// <para>
    /// <b>What it cannot see.</b> A weapon with no clip to count from — the reload
    /// that refills it, a switch to another weapon, infinite ammo — has no
    /// difference to read, so those frames report nothing rather than guessing.
    /// A player firing with a cheat that never decrements the clip is invisible to
    /// this, which costs a muzzle flash and nothing else: damage is arbitrated from
    /// a separate report against the server's own world.
    /// </para>
    /// </summary>
    public sealed class ShotDetector
    {
        /// <summary>
        /// Rounds reported from a single frame's difference. A clip that jumps by
        /// more than this did not empty into a target — it was reloaded, swapped or
        /// refilled by a script, and echoing that as gunfire would fire a burst at
        /// whatever the player happened to be pointing at.
        /// </summary>
        public const int MaxRoundsPerFrame = 4;

        private uint _weaponHash;
        private int _clipAmmo = -1;
        private bool _armed;

        /// <summary>The weapon the last observation was made with. Diagnostic only.</summary>
        public uint CurrentWeapon => _weaponHash;

        /// <summary>Rounds in the clip as of the last observation, or -1 before the first.</summary>
        public int ClipAmmo => _clipAmmo;

        /// <summary>
        /// How many rounds to report this frame. <paramref name="shooting"/> is the
        /// game's own shooting flag; it gates the count so a clip emptied by a script
        /// is not read as gunfire.
        /// </summary>
        public int Observe(uint weaponHash, int clipAmmo, bool shooting)
        {
            // First sight of a weapon establishes the baseline. There is nothing to
            // subtract from yet, and treating the whole clip as fired would open a
            // session with a burst.
            if (!_armed || weaponHash != _weaponHash)
            {
                _weaponHash = weaponHash;
                _clipAmmo = clipAmmo;
                _armed = true;
                return 0;
            }

            int fired = _clipAmmo - clipAmmo;
            _clipAmmo = clipAmmo;

            if (fired <= 0 || !shooting)
            {
                // A rising clip is a reload. A falling one with the flag clear is
                // something other than a trigger pull.
                return 0;
            }

            return fired > MaxRoundsPerFrame ? MaxRoundsPerFrame : fired;
        }

        /// <summary>
        /// Forgets the baseline. Called when the player is no longer holding a weapon
        /// this can count — the next observation starts a fresh baseline rather than
        /// subtracting from a stale one.
        /// </summary>
        public void Reset()
        {
            _armed = false;
            _clipAmmo = -1;
            _weaponHash = 0;
        }

        /// <summary>
        /// True when a weapon fires something this should echo. Projectiles are not
        /// hitscan: a rocket or a grenade is an entity that flies, and drawing it as
        /// an instant line from muzzle to impact would show every player an explosion
        /// travelling at the speed of light. Those are
        /// <see cref="EntityType.Projectile"/>'s job, which is not built.
        /// </summary>
        public static bool IsHitscan(WeaponClass weaponClass) => weaponClass == WeaponClass.Hitscan;
    }

    /// <summary>What kind of thing a weapon sends downrange. Decided by the bridge, which can ask the game.</summary>
    public enum WeaponClass : byte
    {
        /// <summary>Nothing that produces a visible shot — fists, a melee weapon, no weapon.</summary>
        None = 0,

        /// <summary>An instant line from muzzle to impact: every gun that fires bullets.</summary>
        Hitscan = 1,

        /// <summary>A flying entity — rockets, grenades, flares. Not echoed; see the projectile roadmap item.</summary>
        Projectile = 2,
    }
}
