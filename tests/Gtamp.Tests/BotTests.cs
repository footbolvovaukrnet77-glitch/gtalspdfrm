using System;
using System.Collections.Generic;
using Gtamp.Bot;
using Gtamp.Bot.Tasks;
using Gtamp.Client.Mods;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The headless bot: a second player without a second person.
    /// <para>
    /// These cover the parts that are decidable here — argument parsing, the
    /// simulated body, and the verdicts. What the bot is actually for is deciding
    /// things against a live server, and that it does by connecting to one.
    /// </para>
    /// </summary>
    public class BotTests
    {
        [Fact]
        public void EveryTaskNameResolvesAndIsUniquelyNamed()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BotTask task in BotTask.All())
            {
                Assert.True(seen.Add(task.Name), $"two tasks are called {task.Name}");
                Assert.True(BotTask.TryResolve(task.Name, out BotTask resolved));
                Assert.Equal(task.Name, resolved.Name);
                Assert.False(string.IsNullOrWhiteSpace(task.Goal), $"{task.Name} does not say what it checks");
            }

            Assert.False(BotTask.TryResolve("nonesuch", out _));
        }

        [Fact]
        public void NoArgumentsRunsEveryTaskAgainstTheDefaultServer()
        {
            BotOptions options = BotOptions.Parse(Array.Empty<string>(), out string? error);

            Assert.Null(error);
            Assert.Equal("127.0.0.1", options.Host);
            Assert.Equal(27015, options.Port);
            Assert.Equal(1, options.Count);
            Assert.Equal(BotTask.All().Count, options.TaskNames.Count);
        }

        [Fact]
        public void AMistypedArgumentIsRefusedWithAReasonRatherThanIgnored()
        {
            BotOptions.Parse(new[] { "--task", "shooot" }, out string? unknownTask);
            Assert.Contains("shooot", unknownTask);

            BotOptions.Parse(new[] { "--server", "127.0.0.1" }, out string? noPort);
            Assert.NotNull(noPort);

            BotOptions.Parse(new[] { "--count", "0" }, out string? badCount);
            Assert.NotNull(badCount);

            BotOptions.Parse(new[] { "--wat" }, out string? unknownKey);
            Assert.Contains("--wat", unknownKey);
        }

        [Fact]
        public void TaskOrderIsTheOrderTheyWereAskedFor()
        {
            BotOptions options = BotOptions.Parse(
                new[] { "--task", "reconnect,stand,drive" }, out string? error);

            Assert.Null(error);
            Assert.Equal(new[] { "reconnect", "stand", "drive" }, options.TaskNames);
        }

        /// <summary>
        /// Velocity has to come from the step actually taken. A body reporting the
        /// speed it *intended* while standing on its target would hand the server a
        /// movement claim it never made, and the anti-cheat would be judging fiction.
        /// </summary>
        [Fact]
        public void AnArrivedBodyReportsNoVelocity()
        {
            var body = new BotBody { Position = new NetVector3(0f, 0f, 0f) };
            var target = new NetVector3(10f, 0f, 0f);

            Assert.False(body.MoveTowards(target, 5f, 1d));
            Assert.Equal(5f, body.Velocity.X, 2);

            for (int i = 0; i < 10; i++)
            {
                body.MoveTowards(target, 5f, 1d);
            }

            Assert.True(body.MoveTowards(target, 5f, 1d));
            Assert.Equal(0f, body.Velocity.X, 3);
            Assert.Equal(0f, body.Velocity.Y, 3);
        }

        [Fact]
        public void TheBridgeReportsShotsOnceAndThenStopsReportingThem()
        {
            var body = new BotBody();
            var bridge = new SimulatedGameBridge(body);

            Assert.Equal(0, bridge.SampleLocalShots().Rounds);

            body.Fire(new NetVector3(5f, 5f, 5f));
            body.Fire(new NetVector3(6f, 5f, 5f));

            Assert.Equal(2, bridge.SampleLocalShots().Rounds);
            Assert.Equal(0, bridge.SampleLocalShots().Rounds);
        }

        /// <summary>
        /// The bot obeys corrections, because a bot that ignored them would report a
        /// healthy session while drifting away from the server — the exact failure it
        /// exists to detect in other people.
        /// </summary>
        [Fact]
        public void ACorrectionMovesTheBotAndIsCounted()
        {
            var body = new BotBody { Position = new NetVector3(0f, 0f, 0f), Health = 200 };
            var bridge = new SimulatedGameBridge(body);

            bridge.ApplyLocalCorrection(new NetVector3(30f, 40f, 0f), 90f, 150, 20);

            Assert.Equal(1, bridge.Seen.CorrectionsApplied);
            Assert.Equal(50d, bridge.Seen.LastCorrectionDistance, 1);
            Assert.Equal(30f, body.Position.X, 2);
            Assert.Equal(150, body.Health);
            Assert.Equal(90f, body.Heading, 2);
        }

        [Fact]
        public void RemotePlayersAndVehiclesAreRecordedAsTheClientDrawsThem()
        {
            var bridge = new SimulatedGameBridge(new BotBody());

            int ped = bridge.CreateRemotePed(0x1234u, new NetVector3(1f, 2f, 3f), 0f);
            int vehicle = bridge.CreateRemoteVehicle(0x5678u, new NetVector3(4f, 5f, 6f), 0f);

            Assert.True(bridge.IsRemotePedValid(ped));
            Assert.True(bridge.IsRemoteVehicleValid(vehicle));
            Assert.Equal(1, bridge.Seen.RemotePedsEverSeen);
            Assert.Equal(1, bridge.Seen.RemoteVehiclesEverSeen);

            bridge.DestroyRemotePed(ped);
            Assert.False(bridge.IsRemotePedValid(ped));

            // "Ever seen" is a count of what the server sent, so removing must not
            // erase the evidence that it was sent.
            Assert.Equal(1, bridge.Seen.RemotePedsEverSeen);
        }

        /// <summary>
        /// A task that needed another player and found none has not failed. Reporting
        /// that as a failure is the same lie as reporting an untested thing as
        /// working, in the opposite direction.
        /// </summary>
        [Fact]
        public void ATaskThatNeededAnotherPlayerAndFoundNoneIsSkippedNotFailed()
        {
            var follow = new FollowTask();
            var shoot = new ShootTask();

            Assert.Equal(TaskResult.Skipped, follow.Finish(NoOneAround(), 10d).Result);
            Assert.Equal(TaskResult.Skipped, shoot.Finish(NoOneAround(), 10d).Result);
        }

        private static BotContext NoOneAround()
        {
            var body = new BotBody();
            var bridge = new SimulatedGameBridge(body);
            return new BotContext("test", body, bridge, null!);
        }
    }

    /// <summary>
    /// Model hash zero is the absence of a value, not a model nobody has. The bot
    /// found this the first time two clients were connected at once: every join
    /// warned that the other player's model was missing, and 0x00000000 went into the
    /// bug report's MISSING CONTENT list, naming nothing anyone could install.
    /// </summary>
    public class MissingContentZeroTests
    {
        [Fact]
        public void AModelHashOfZeroIsNotReportedAsMissingContent()
        {
            var log = new LogBus();
            var tracker = new MissingContentTracker(log);

            Assert.False(tracker.Report(0u, EntityType.Player, new EntityId(7), substituted: true));
            Assert.True(tracker.IsEmpty);
            Assert.Equal(0, tracker.Count);
        }

        [Fact]
        public void ARealHashIsStillReported()
        {
            var log = new LogBus();
            var tracker = new MissingContentTracker(log);

            Assert.True(tracker.Report(0xDEADBEEFu, EntityType.Vehicle, new EntityId(3), substituted: false));
            Assert.Equal(1, tracker.Count);
        }
    }
}
