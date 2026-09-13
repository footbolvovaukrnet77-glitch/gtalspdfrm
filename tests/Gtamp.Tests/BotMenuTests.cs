using System;
using System.Collections.Generic;
using Gtamp.Bot.Tasks;
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


        private sealed class FakeServerHost : IServerHost
        {
            public int Started { get; private set; }

            public int Stopped { get; private set; }

            public int LastPort { get; private set; }

            public bool IsServerRunning { get; set; }

            public bool IsServerAvailable { get; set; } = true;

            public string ServerUnavailableReason { get; set; } = "сервер не найден";

            public string FailWith { get; set; } = string.Empty;

            public bool StartServer(int port, out string error)
            {
                if (FailWith.Length > 0)
                {
                    error = FailWith;
                    return false;
                }

                Started++;
                LastPort = port;
                IsServerRunning = true;
                error = string.Empty;
                return true;
            }

            public void StopServer()
            {
                Stopped++;
                IsServerRunning = false;
            }
        }

        private sealed class FakeSession : IMenuSession
        {
            public readonly List<string> Commands = new List<string>();

            public int Connects { get; private set; }

            public int Disconnects { get; private set; }

            public bool IsConnected { get; set; }

            public int PlayerCount { get; set; }

            public string ServerAddress { get; set; } = "127.0.0.1";

            public int ServerPort { get; set; } = 27015;

            public bool RefuseCommands { get; set; }

            public void Connect(string host, int port)
            {
                Connects++;
                IsConnected = true;
            }

            public void Disconnect()
            {
                Disconnects++;
                IsConnected = false;
            }

            public bool SendAdminCommand(string commandLine)
            {
                if (RefuseCommands)
                {
                    return false;
                }

                Commands.Add(commandLine);
                return true;
            }
        }

        private static (BotMenu Menu, FakeServerHost Server, FakeSession Session) OpenServerPage()
        {
            var server = new FakeServerHost();
            var session = new FakeSession();
            var menu = new BotMenu(new FakeBotHost(), server, session);
            menu.Toggle();
            menu.NextPage();
            Assert.Equal(MenuPage.Server, menu.Page);
            return (menu, server, session);
        }

        [Fact]
        public void TabMovesBetweenThePagesAndBack()
        {
            (BotMenu menu, FakeBotHost _) = Open();

            Assert.Equal(MenuPage.Bots, menu.Page);
            menu.NextPage();
            Assert.Equal(MenuPage.Server, menu.Page);
            menu.NextPage();
            Assert.Equal(MenuPage.Bots, menu.Page);
        }

        /// <summary>
        /// The pages are different lengths, so a cursor carried across can land past the
        /// end of the shorter one -- or, worse, on whatever happens to sit at that index,
        /// which on the server page is a button that starts a process.
        /// </summary>
        [Fact]
        public void SwitchingPageDoesNotCarryTheCursorOntoARowItNeverMeantToSelect()
        {
            (BotMenu menu, FakeBotHost _) = Open();

            menu.MoveDown();
            menu.MoveDown();
            menu.MoveDown();
            menu.MoveDown();
            Assert.Equal(4, menu.Selected);

            menu.NextPage();

            Assert.Equal(0, menu.Selected);
            Assert.True(menu.Selected < menu.Rows.Count);
        }

        [Fact]
        public void TheLocalServerRowStartsAndThenStopsTheServer()
        {
            (BotMenu menu, FakeServerHost server, FakeSession _) = OpenServerPage();

            menu.Activate();
            Assert.Equal(1, server.Started);
            Assert.Equal(BotMenu.LocalServerPort, server.LastPort);
            Assert.True(server.IsServerRunning);

            menu.Activate();
            Assert.Equal(1, server.Stopped);
            Assert.False(server.IsServerRunning);
        }

        [Fact]
        public void AnInstallationWithNoServerSaysSoRatherThanLookingLikeItWorked()
        {
            (BotMenu menu, FakeServerHost server, FakeSession _) = OpenServerPage();
            server.IsServerAvailable = false;
            server.ServerUnavailableReason = "Gtamp.Server не найден";

            menu.Activate();

            Assert.Equal(0, server.Started);
            Assert.Contains("не найден", menu.Status);
        }

        [Fact]
        public void TheConnectionRowConnectsAndThenDisconnects()
        {
            (BotMenu menu, FakeServerHost _, FakeSession session) = OpenServerPage();
            menu.MoveDown();

            menu.Activate();
            Assert.Equal(1, session.Connects);

            menu.Activate();
            Assert.Equal(1, session.Disconnects);
        }

        [Fact]
        public void WeatherAndTimeGoOutAsAdminCommands()
        {
            (BotMenu menu, FakeServerHost _, FakeSession session) = OpenServerPage();
            session.IsConnected = true;

            menu.MoveDown();
            menu.MoveDown();
            menu.Right();
            menu.Activate();

            menu.MoveDown();
            menu.Activate();

            Assert.Equal(2, session.Commands.Count);
            Assert.StartsWith("weather ", session.Commands[0]);
            Assert.StartsWith("time ", session.Commands[1]);
        }

        /// <summary>
        /// Section 46: these go through the same admin path the console uses and the
        /// server decides whether the player may. A refusal has to reach the player,
        /// because a menu that silently does nothing reads as a broken menu.
        /// </summary>
        [Fact]
        public void AServerThatRefusesAWorldCommandIsReportedAndNotSwallowed()
        {
            (BotMenu menu, FakeServerHost _, FakeSession session) = OpenServerPage();
            session.IsConnected = true;
            session.RefuseCommands = true;

            menu.MoveDown();
            menu.MoveDown();
            menu.Activate();

            Assert.Empty(session.Commands);
            Assert.Contains("администратор", menu.Status);
        }

        [Fact]
        public void SettingTheWeatherWithNoServerToSetItOnSaysThatInstead()
        {
            (BotMenu menu, FakeServerHost _, FakeSession session) = OpenServerPage();
            session.IsConnected = false;

            menu.MoveDown();
            menu.MoveDown();
            menu.Activate();

            Assert.Empty(session.Commands);
            Assert.Contains("подключённым", menu.Status);
        }

        /// <summary>
        /// The bot page must keep working in a build where the menu was handed no
        /// server and no session at all, because that is how every existing caller
        /// constructs it.
        /// </summary>
        [Fact]
        public void TheServerPageDegradesInsteadOfThrowingWhenThereIsNoServerHost()
        {
            (BotMenu menu, FakeBotHost _) = Open();
            menu.NextPage();

            menu.Activate();

            Assert.Contains("недоступен", menu.Status);
        }

        [Fact]
        public void EveryPresetIsARealTaskListOrTheDefaultOfAllOfThem()
        {
            // Taken from the bot rather than written down here. A list copied into a
            // test goes stale exactly as quietly as one copied into a --help string,
            // and then the test's job -- catching a preset that names a task nobody
            // implemented -- is being done against the wrong list.
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BotTask task in BotTask.All())
            {
                valid.Add(task.Name);
            }

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
