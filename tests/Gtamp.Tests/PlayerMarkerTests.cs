using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Blips and floating names — being able to find and identify another player.
    /// <para>
    /// Everything needed to draw both had been replicated since Phase 2: name,
    /// position, health, wanted level, whether they are driving. Nothing read any of
    /// it for the screen. A session gave you no blip on the map and no name over a
    /// ped, so two people who wanted to meet had to read coordinates out loud, and a
    /// ped in the street was indistinguishable from ambient traffic.
    /// </para>
    /// </summary>
    public class PlayerMarkerTests
    {
        private static PlayerEntity Player(string name = "alice") => new PlayerEntity(new EntityId(2))
        {
            Name = name,
            Health = 200,
            MaxHealth = 200,
            Position = new NetVector3(10f, 0f, 30f),
        };

        [Fact]
        public void AHealthyPlayerOnFootIsGreen()
        {
            PlayerMarker marker = PlayerMarkers.Decide(Player(), NetVector3.Zero, true, true);

            Assert.True(marker.ShowBlip);
            Assert.Equal(BlipColour.Green, marker.BlipColour);
            Assert.Equal("alice", marker.Name);
        }

        [Fact]
        public void ADrivingPlayerIsBlue()
        {
            PlayerEntity driving = Player();
            driving.Flags = PlayerFlags.InVehicle;

            Assert.Equal(BlipColour.Blue, PlayerMarkers.Decide(driving, NetVector3.Zero, true, true).BlipColour);
        }

        [Fact]
        public void ABadlyHurtPlayerIsYellow()
        {
            PlayerEntity hurt = Player();
            hurt.Health = 40;

            Assert.Equal(BlipColour.Yellow, PlayerMarkers.Decide(hurt, NetVector3.Zero, true, true).BlipColour);
        }

        [Fact]
        public void AWantedPlayerOutranksEverythingButDeath()
        {
            // The one thing another player most needs to see at a glance, so it beats
            // both "hurt" and "in a car".
            PlayerEntity wanted = Player();
            wanted.WantedLevel = 2;
            wanted.Health = 40;
            wanted.Flags = PlayerFlags.InVehicle;

            Assert.Equal(BlipColour.Red, PlayerMarkers.Decide(wanted, NetVector3.Zero, true, true).BlipColour);
        }

        [Fact]
        public void ADeadPlayerIsGreyAndStillNamed()
        {
            // Knowing who is down, and where, is the whole reason to look.
            PlayerEntity dead = Player();
            dead.Health = 0;
            dead.Flags = PlayerFlags.Dead | PlayerFlags.InVehicle;
            dead.WantedLevel = 3;

            PlayerMarker marker = PlayerMarkers.Decide(dead, NetVector3.Zero, true, true);

            Assert.Equal(BlipColour.Grey, marker.BlipColour);
            Assert.True(marker.ShowName);
            Assert.Equal("alice", marker.Name);
        }

        [Fact]
        public void ANameStopsBeingDrawnWhenItWouldBeUnreadable()
        {
            PlayerEntity far = Player();
            far.Position = new NetVector3(PlayerMarkers.NameDistance + 10f, 0f, 30f);

            PlayerMarker marker = PlayerMarkers.Decide(far, NetVector3.Zero, true, true);

            Assert.False(marker.ShowName);

            // The blip does not go with it: the map is not the world, and a player
            // across the city is exactly who you are looking for.
            Assert.True(marker.ShowBlip);
        }

        [Fact]
        public void TheNameFadesRatherThanPopping()
        {
            Assert.Equal(1f, PlayerMarkers.NameOpacity(0f));
            Assert.Equal(1f, PlayerMarkers.NameOpacity(PlayerMarkers.NameFullDistance));
            Assert.Equal(0f, PlayerMarkers.NameOpacity(PlayerMarkers.NameDistance));
            Assert.Equal(0f, PlayerMarkers.NameOpacity(PlayerMarkers.NameDistance + 50f));

            float middle = PlayerMarkers.NameOpacity(
                (PlayerMarkers.NameFullDistance + PlayerMarkers.NameDistance) / 2f);
            Assert.True(middle > 0.4f && middle < 0.6f, $"midpoint opacity was {middle}");
        }

        [Fact]
        public void BothCanBeTurnedOffIndependently()
        {
            PlayerEntity player = Player();

            PlayerMarker noBlip = PlayerMarkers.Decide(player, NetVector3.Zero, false, true);
            Assert.False(noBlip.ShowBlip);
            Assert.True(noBlip.ShowName);

            PlayerMarker noName = PlayerMarkers.Decide(player, NetVector3.Zero, true, false);
            Assert.True(noName.ShowBlip);
            Assert.False(noName.ShowName);
        }

        [Fact]
        public void TheSettingsAreReadRatherThanMerelyStored()
        {
            // The defect this whole class of check exists for: ShowNetworkOverlay was
            // a setting in client.ini that nothing read. These two are asserted to
            // reach the bridge, not merely to parse.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Config.ShowPlayerBlips = false;
            alice.Config.ShowPlayerNames = false;
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(harness.AdvanceUntil(() => alice.Bridge.Markers.Count > 0));

            foreach (PlayerMarker marker in alice.Bridge.Markers.Values)
            {
                Assert.False(marker.ShowBlip);
                Assert.False(marker.ShowName);
            }

            alice.Config.ShowPlayerBlips = true;
            Assert.True(harness.AdvanceUntil(() =>
            {
                foreach (PlayerMarker marker in alice.Bridge.Markers.Values)
                {
                    if (marker.ShowBlip)
                    {
                        return true;
                    }
                }

                return false;
            }));
        }

        [Fact]
        public void AMarkerCarriesTheNameTheServerHasForThePlayer()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(harness.AdvanceUntil(() =>
            {
                foreach (PlayerMarker marker in alice.Bridge.Markers.Values)
                {
                    if (marker.Name == "bob")
                    {
                        return true;
                    }
                }

                return false;
            }));
        }
    }
}
