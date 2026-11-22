using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Section 16: interiors, rooms, and the map files a world has switched on.
    /// <para>
    /// <c>InteriorId</c> was sampled from Phase 1 and applied to nothing, so every
    /// replicated ped was outdoors as far as the engine was concerned. GTA V culls by
    /// room: a ped it believes is outdoors while it stands in a building is drawn
    /// through the wall, and one it believes is in the wrong room vanishes where it
    /// should be visible.
    /// </para>
    /// </summary>
    public class InteriorTests
    {
        /// <summary>
        /// The reason the room travels and the interior does not, stated where it can be
        /// checked: an interior handle is whatever number a particular machine's loaded
        /// map gave that building, and a room key is a hash of a name.
        /// </summary>
        [Fact]
        public void TheRoomKeySurvivesTheWireOnACharacter()
        {
            var before = new PlayerEntity(new EntityId(3));
            var after = (PlayerEntity)before.Clone();
            after.RoomKey = GameHash.Joaat("V_Michael_Lounge");

            INetEntitySerializer serializer = EntityRegistry.CreateDefault().Get((byte)EntityType.Player);
            var writer = new NetWriter(512);
            serializer.WriteDelta(writer, before, after);

            var applied = (PlayerEntity)before.Clone();
            serializer.ReadDelta(new NetReader(writer.ToArray()), applied);

            Assert.Equal(GameHash.Joaat("V_Michael_Lounge"), applied.RoomKey);
        }

        [Fact]
        public void TheRoomKeyReachesTheServerFromAClient()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            alice.Bridge.Sample.RoomKey = GameHash.Joaat("V_Michael_Lounge");

            Assert.True(harness.AdvanceUntil(
                () => harness.Server.World.GetPlayer(alice.Client.LocalEntityId)?.RoomKey
                    == GameHash.Joaat("V_Michael_Lounge")));
        }

        /// <summary>
        /// And out the other side: the watching client's game must be told to put that
        /// ped in that room, which is the half that was missing entirely.
        /// </summary>
        [Fact]
        public void TheRoomReachesTheOtherClientsGame()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 2 && bob.PlayerCount >= 2));
            harness.Advance(1.0);

            uint room = GameHash.Joaat("V_Michael_Lounge");
            alice.Bridge.Sample.RoomKey = room;

            Assert.True(
                harness.AdvanceUntil(() => bob.Bridge.Rooms.ContainsValue(room)),
                "bob's game was never told which room alice is in");
        }

        /// <summary>
        /// Walking out of a building has to travel as well as walking in. A ped left
        /// assigned to a room it has left is culled with that room: it vanishes in the
        /// street outside.
        /// </summary>
        [Fact]
        public void LeavingARoomTravelsAsWellAsEnteringOne()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 2 && bob.PlayerCount >= 2));
            harness.Advance(1.0);

            uint room = GameHash.Joaat("V_Michael_Lounge");
            alice.Bridge.Sample.RoomKey = room;
            Assert.True(harness.AdvanceUntil(() => bob.Bridge.Rooms.ContainsValue(room)));

            alice.Bridge.Sample.RoomKey = 0;
            Assert.True(
                harness.AdvanceUntil(() => !bob.Bridge.Rooms.ContainsValue(room)),
                "bob's game was never told alice had left");
        }

        [Fact]
        public void TheWorldsMapFilesTravelInTheEnvironment()
        {
            var environment = new WorldEnvironment();
            environment.ActiveIpls.Add("v_michael");
            environment.ActiveIpls.Add("hei_dlc_bank_finale");

            var writer = new NetWriter(256);
            var world = new WorldState();
            world.Environment.ActiveIpls.AddRange(environment.ActiveIpls);

            // Round-tripped through the codec's own environment section by writing a
            // full snapshot, because that is the only way it ever travels.
            SnapshotWriteResult written = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, EntityRegistry.CreateDefault(),
                new List<NetEntity>(), 1, 1024);

            SnapshotApplyResult applied = SnapshotCodec.Apply(
                written.Payload, EntitySnapshotView.Empty, EntityRegistry.CreateDefault());

            Assert.Equal(2, applied.View.Environment.ActiveIpls.Count);
            Assert.Contains("v_michael", applied.View.Environment.ActiveIpls);
            Assert.Contains("hei_dlc_bank_finale", applied.View.Environment.ActiveIpls);
        }

        /// <summary>
        /// A map-file list is a thing a server sends and a client allocates for, so it
        /// is bounded. A server claiming ten thousand of them is refused rather than
        /// believed.
        /// </summary>
        [Fact]
        public void AnAbsurdNumberOfMapFilesIsRefused()
        {
            var world = new WorldState();
            for (int i = 0; i <= WorldEnvironment.MaxIpls; i++)
            {
                world.Environment.ActiveIpls.Add($"ipl_{i}");
            }

            SnapshotWriteResult written = SnapshotCodec.Write(
                world, EntitySnapshotView.Empty, EntityRegistry.CreateDefault(),
                new List<NetEntity>(), 1, 4096);

            Assert.Throws<NetSerializationException>(
                () => SnapshotCodec.Apply(
                    written.Payload, EntitySnapshotView.Empty, EntityRegistry.CreateDefault()));
        }

        /// <summary>
        /// The environment is only written when it changes, and a new map file is a
        /// change. Without this the list would be sent once and never updated.
        /// </summary>
        [Fact]
        public void SwitchingAMapFileOnCountsAsAnEnvironmentChange()
        {
            var before = new WorldEnvironment();
            var after = before.Clone();
            after.ActiveIpls.Add("v_michael");

            Assert.False(before.ValueEquals(after));
            Assert.True(before.ValueEquals(before.Clone()));
        }
    }
}
