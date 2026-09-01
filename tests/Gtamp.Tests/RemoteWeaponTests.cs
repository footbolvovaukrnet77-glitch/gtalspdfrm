using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The weapon a remote player is holding has to reach the instruction the game
    /// bridge executes.
    /// <para>
    /// It did not. The hash was read from the local player, serialised, sent, stored
    /// on the server, replicated to every other client and printed by both
    /// <c>players</c> and <c>diff</c> — and the only weapon-related call anywhere in
    /// the ScriptHookVDotNet layer was the read. Nothing armed a remote ped, so every
    /// other player stood empty-handed whatever they were carrying, while the server's
    /// damage arbiter scored their rifle hits.
    /// </para>
    /// <para>
    /// Found by reading RAGECOOP-V, which is MIT licensed and has been played for
    /// years. Its own fix — "network players never switching back to unarmed" — is the
    /// narrow version of this: they applied weapons and forgot the holster. Nothing was
    /// copied; the bug was the clue.
    /// </para>
    /// </summary>
    public class RemoteWeaponTests
    {
        private static RemotePedFrame Frame(uint weapon, PlayerFlags flags = PlayerFlags.None) =>
            new RemotePedFrame
            {
                Position = new NetVector3(10f, 20f, 30f),
                Heading = 90f,
                Health = 200,
                Armor = 0,
                Flags = flags,
                CurrentWeaponHash = weapon,
            };

        [Theory]
        [InlineData(0u)]
        [InlineData(0x1B06D571u)]
        [InlineData(uint.MaxValue)]
        public void TheWeaponReachesTheCommand(uint weapon)
        {
            RemotePedCommand command = RemotePedController.Decide(
                Frame(weapon), new NetVector3(10f, 20f, 30f));

            Assert.Equal(weapon, command.WeaponHash);
        }

        [Fact]
        public void HolsteringTravelsAsDeliberatelyAsDrawing()
        {
            // Unarmed is 0, and 0 is the value a "skip if empty" shortcut throws away.
            // A player who holsters must stop holding the rifle, so the command has to
            // carry the change rather than let the previous value stand.
            RemotePedCommand drawn = RemotePedController.Decide(
                Frame(0x1B06D571u), new NetVector3(10f, 20f, 30f));
            RemotePedCommand holstered = RemotePedController.Decide(
                Frame(0u), new NetVector3(10f, 20f, 30f));

            Assert.Equal(0x1B06D571u, drawn.WeaponHash);
            Assert.Equal(0u, holstered.WeaponHash);
            Assert.NotEqual(drawn.WeaponHash, holstered.WeaponHash);
        }

        [Theory]
        [InlineData(PlayerFlags.Ragdoll)]
        [InlineData(PlayerFlags.InVehicle)]
        [InlineData(PlayerFlags.Aiming)]
        public void EveryStateCarriesTheWeaponNotJustWalking(PlayerFlags flags)
        {
            // Decide() returns early for ragdoll and for a player in a vehicle. Each of
            // those early returns builds its own command, and each one had to be given
            // the weapon separately -- exactly the kind of place a field gets added to
            // the happy path and forgotten in the branches.
            RemotePedCommand command = RemotePedController.Decide(
                Frame(0x1B06D571u, flags), new NetVector3(10f, 20f, 30f));

            Assert.Equal(0x1B06D571u, command.WeaponHash);
        }

        [Fact]
        public void ADeadPlayerReportsTheirWeaponToo()
        {
            // The dead branch zeroes health and armour deliberately. It must not zero
            // the weapon by the same reflex: a corpse still holds what it was holding,
            // and the ped is re-driven from this command when it respawns.
            var frame = Frame(0x1B06D571u);
            frame.Health = 0;

            RemotePedCommand command = RemotePedController.Decide(frame, new NetVector3(10f, 20f, 30f));

            Assert.Equal(RemotePedAction.Dead, command.Action);
            Assert.Equal(0x1B06D571u, command.WeaponHash);
        }
    }
}
