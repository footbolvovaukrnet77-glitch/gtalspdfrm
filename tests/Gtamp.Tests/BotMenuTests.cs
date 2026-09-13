using System.Collections.Generic;
using Gtamp.Client.Ui;
using Gtamp.Shared.Core;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The in-game bot menu, tested without a game, a bot or a server — which is the
    /// whole reason the model lives in Gtamp.Client.Core and only the drawing is in
    /// the ScriptHookVDotNet layer.
    /// </summary>
    public class BotMenuTests
    {
        private sealed class FakeBotHost : IBotHost
        {
            public readonly List<BotLaunch> Launches = new List<BotLaunch>();

            public int Stops { get; private set; }

            public int RunningCount { get; set; }

            public bool IsAvailable { get; set; } = true;

            public string UnavailableReason { get; set; } = "нет бота";

            public IReadOnlyList<string> RecentLines { get; set; } = new List<string>();

            public string FailWith { get; set; } = string.Empty;

            public bool Start(BotLaunch launch, out string error)
            {
                if (FailWith.Length > 0)
                {
                    error = FailWith;
                    return false;
                }

                Launches.Add(launch);
                RunningCount++;
                error = string.Empty;
                return true;
            }

            public void StopAll()
            {
                Stops++;
                RunningCount = 0;
            }
        }

        private static (BotMenu Menu, FakeBotHost Host) Open()
        {
            var host = new FakeBotHost();
            var menu = new BotMenu(host);
            menu.Toggle();
            Assert.True(menu.IsOpen);
            return (menu, host);
        }

        /// <summary>Select a row by moving down from the top, which is where it starts.</summary>
        private static void Select(BotMenu menu, int row)
        {
            while (menu.Selected != row)
            {
                menu.MoveDown();
            }
        }

        [Fact]
        public void TheCountRowWrapsRatherThanRunningOffEachEnd()
        {
            var (menu, _) = Open();
            Select(menu, 0);

            // It starts at two, because two is the number that makes a fight.
            menu.Left();
            Assert.Equal("1", menu.Rows[0].Value());

            menu.Left();
            Assert.Equal(BotMenu.MaxCount.ToString(), menu.Rows[0].Value());

            menu.Right();
            Assert.Equal("1", menu.Rows[0].Value());
        }

        [Fact]
        public void LaunchingSendsTheCountTasksAndServerThatAreOnScreen()
        {
            var (menu, host) = Open();
            menu.Server = "10.0.0.5:27020";

            Select(menu, 1);
            menu.Right();
            string tasks = menu.Rows[1].Value();

            Select(menu, 3);
            menu.Activate();

            BotLaunch launch = Assert.Single(host.Launches);
            Assert.Equal(2, launch.Count);
            Assert.Equal(tasks, launch.Tasks);
            Assert.Equal("10.0.0.5:27020", launch.Server);
        }

        /// <summary>
        /// The reason the menu is worth having at all: bots that appear at the server's
        /// spawn point are two kilometres from wherever the player is standing, and a
        /// fight you have to drive to is a fight that does not get tested.
        /// </summary>
        [Fact]
        public void SpawnOnMeHandsTheBotsThePlayersOwnPosition()
        {
            var (menu, host) = Open();
            menu.PlayerPosition = new NetVector3(297.7f, -584.3f, 43.0f);

            Select(menu, 3);
            menu.Activate();

            BotLaunch launch = Assert.Single(host.Launches);
            Assert.True(launch.At.HasValue);
            Assert.Equal(297.7f, launch.At!.Value.X, 1);
            Assert.Equal(-584.3f, launch.At.Value.Y, 1);
        }

        [Fact]
        public void TurningSpawnOnMeOffLetsTheServerChooseThePoint()
        {
            var (menu, host) = Open();
            menu.PlayerPosition = new NetVector3(297.7f, -584.3f, 43.0f);

            Select(menu, 2);
            menu.Right();

            Select(menu, 3);
            menu.Activate();

            Assert.False(Assert.Single(host.Launches).At.HasValue);
        }

        [Fact]
        public void AMissingBotIsReportedOnOpeningRatherThanOnlyWhenLaunchIsPressed()
        {
            var host = new FakeBotHost { IsAvailable = false, UnavailableReason = "положите бота в Gtamp\\bot" };
            var menu = new BotMenu(host);

            menu.Toggle();

            Assert.Equal("положите бота в Gtamp\\bot", menu.Status);

            Select(menu, 3);
            menu.Activate();
            Assert.Empty(host.Launches);
        }

        [Fact]
        public void AFailedLaunchShowsWhyInsteadOfClaimingBotsStarted()
        {
            var (menu, host) = Open();
            host.FailWith = "dotnet не найден";

            Select(menu, 3);
            menu.Activate();

            Assert.Equal("dotnet не найден", menu.Status);
            Assert.Equal(0, menu.RunningCount);
        }

        [Fact]
        public void StoppingWithNothingRunningSaysSoRatherThanLookingLikeItWorked()
        {
            var (menu, host) = Open();

            Select(menu, 4);
            menu.Activate();

            Assert.Equal(0, host.Stops);
            Assert.Equal("ни одного бота не запущено", menu.Status);
        }

        [Fact]
        public void StoppingKillsEveryRunningBatch()
        {
            var (menu, host) = Open();

            Select(menu, 3);
            menu.Activate();
            menu.Activate();
            Assert.Equal(2, menu.RunningCount);

            Select(menu, 4);
            menu.Activate();

            Assert.Equal(1, host.Stops);
            Assert.Equal(0, menu.RunningCount);
        }

        [Fact]
        public void EveryPresetIsARealTaskListOrTheDefaultOfAllOfThem()
        {
            var valid = new HashSet<string>
            {
                "stand", "patrol", "drive", "follow", "shoot", "die", "reconnect",
            };

            foreach (string preset in BotMenu.Presets)
            {
                if (preset.Length == 0)
                {
                    continue;
                }

                foreach (string task in preset.Split(','))
                {
                    Assert.Contains(task, valid);
                }
            }
        }
    }
}
