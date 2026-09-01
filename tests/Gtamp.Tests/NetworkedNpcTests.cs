using Gtamp.Client.Core;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Networked NPCs reaching the game.
    /// <para>
    /// <c>PedEntity</c> was registered in the entity registry, serialised, delta
    /// encoded, persisted, replicated to every client and accepted by the damage
    /// arbiter — and no client ever created a ped for one. A server or a mod could
    /// spawn a networked NPC, watch it appear in the world state and in every
    /// diagnostic, and see nothing whatsoever in the game. The roadmap called it
    /// complete.
    /// </para>
    /// </summary>
    public class NetworkedNpcTests
    {
        private static PedEntity Npc(TestHarness harness, NetVector3 position, uint model = 0x9B22DBAF)
        {
            var npc = new PedEntity(harness.Server.World.AllocateEntityId())
            {
                Position = position,
                ModelHash = model,
                Health = 200,
                MaxHealth = 200,
            };

            harness.Server.World.Spawn(npc);
            return npc;
        }

        [Fact]
        public void ASpawnedNpcGetsAPed()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            PedEntity npc = Npc(harness, new NetVector3(220f, -800f, 30f));

            Assert.True(harness.AdvanceUntil(() => client.Client.RemoteEntities.NpcCount > 0));
            Assert.True(client.Client.RemoteEntities.TryGetNpc(npc.Id, out RemoteNpc remote));
            Assert.True(harness.AdvanceUntil(() => remote.PedHandle != 0));
            Assert.True(client.Bridge.Peds.ContainsKey(remote.PedHandle));
        }

        [Fact]
        public void AnNpcIsDrivenByTheSameControllerAsAPlayer()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            PedEntity npc = Npc(harness, new NetVector3(220f, -800f, 30f));
            npc.Movement = MovementState.Run;
            npc.Velocity = new NetVector3(4f, 0f, 0f);
            harness.Server.World.Touch(npc);

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetNpc(npc.Id, out RemoteNpc r)
                && r.PedHandle != 0
                && client.Bridge.Peds.ContainsKey(r.PedHandle)));

            Assert.True(client.Client.RemoteEntities.TryGetNpc(npc.Id, out RemoteNpc remote));
            RemotePedCommand command = client.Bridge.Peds[remote.PedHandle];

            // Whatever the controller decided, it decided something — the ped is being
            // driven rather than left where it was created.
            Assert.NotEqual(0u, remote.ModelHash);
            Assert.True(command.Health > 0);
        }

        [Fact]
        public void ADespawnedNpcTakesItsPedWithIt()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            PedEntity npc = Npc(harness, new NetVector3(220f, -800f, 30f));
            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetNpc(npc.Id, out RemoteNpc r) && r.PedHandle != 0));
            Assert.True(client.Client.RemoteEntities.TryGetNpc(npc.Id, out RemoteNpc remote));
            int handle = remote.PedHandle;

            harness.Server.World.Destroy(npc.Id);

            Assert.True(harness.AdvanceUntil(() => client.Client.RemoteEntities.NpcCount == 0));
            Assert.False(client.Bridge.IsRemotePedValid(handle));
        }

        [Fact]
        public void DistanceAloneNeverRemovesAnNpcFromTheServerWorld()
        {
            // The central constraint, applied to the entity type that had no client
            // representation at all until now: replication is filtered, the world is
            // not.
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            PedEntity far = Npc(harness, new NetVector3(6000f, 6000f, 30f));

            harness.Advance(2d);

            Assert.True(harness.Server.World.State.Contains(far.Id));
        }

        [Fact]
        public void AnNpcInterpolatesLikeAPlayerAndDoesNotInventDiscreteState()
        {
            var npc = new RemoteNpc(new EntityId(5));

            var first = new PedEntity(new EntityId(5))
            {
                Position = new NetVector3(0f, 0f, 0f),
                Health = 200,
                Flags = PlayerFlags.None,
            };
            var second = new PedEntity(new EntityId(5))
            {
                Position = new NetVector3(10f, 0f, 0f),
                Health = 60,
                Flags = PlayerFlags.Crouching,
            };

            npc.Push(1d, first);
            npc.Push(2d, second);

            Assert.True(npc.TrySample(1.5d, out RemotePedFrame frame));

            // Position blends; health does not. A halfway health is a value the NPC
            // never had.
            Assert.Equal(5f, frame.Position.X, 3);
            Assert.Equal(60, frame.Health);
            Assert.Equal(PlayerFlags.Crouching, frame.Flags);
        }

        [Fact]
        public void AnNpcsClothingIsAppliedOnChangeOnly()
        {
            var npc = new RemoteNpc(new EntityId(5));
            var state = new PedEntity(new EntityId(5)) { Health = 200 };

            npc.Push(1d, state);
            int afterFirst = npc.AppearanceVersion;

            npc.Push(2d, state);
            Assert.Equal(afterFirst, npc.AppearanceVersion);

            state.Appearance.SetComponent(3, 5, 1, 0);
            npc.Push(3d, state);
            Assert.NotEqual(afterFirst, npc.AppearanceVersion);
        }
    }
}
