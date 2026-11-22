using System;
using System.Collections.Generic;
using System.Globalization;
using Gtamp.Shared.Core;

namespace Gtamp.Client.Ui
{
    /// <summary>What a bot host can be asked to do, and what it can report back.</summary>
    /// <remarks>
    /// The menu talks to this and never to a process, which is what lets the whole
    /// menu be tested without .NET 8, without a server and without a game.
    /// </remarks>
    public interface IBotHost
    {
        /// <summary>How many bot processes are running right now.</summary>
        int RunningCount { get; }

        /// <summary>True when this installation has a bot to launch at all.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Why <see cref="IsAvailable"/> is false, phrased for a player rather than a
        /// developer, or empty when it is true.
        /// </summary>
        string UnavailableReason { get; }

        /// <summary>The most recent output lines from the bots, oldest first.</summary>
        IReadOnlyList<string> RecentLines { get; }

        /// <summary>Starts one batch of bots. Returns false and fills <paramref name="error"/> if it could not.</summary>
        bool Start(BotLaunch launch, out string error);

        /// <summary>Stops every running bot.</summary>
        void StopAll();
    }

    /// <summary>One request to run bots.</summary>
    public readonly struct BotLaunch
    {
        public BotLaunch(int count, string tasks, string server, string password, NetVector3? at)
        {
            Count = count;
            Tasks = tasks;
            Server = server;
            Password = password;
            At = at;
        }

        public int Count { get; }

        /// <summary>Comma-separated task names, or empty for every task in order.</summary>
        public string Tasks { get; }

        public string Server { get; }

        public string Password { get; }

        /// <summary>Where the bots should appear, or null to use the server's spawn point.</summary>
        public NetVector3? At { get; }
    }

    /// <summary>What a local server can be asked to do, and what it can report back.</summary>
    /// <remarks>
    /// Behind an interface for the same reason the bot host is: the menu talks to this
    /// and never to a process, so the whole menu is testable without .NET 8, without a
    /// game and without anything listening on a port.
    /// </remarks>
    public interface IServerHost
    {
        bool IsServerRunning { get; }

        bool IsServerAvailable { get; }

        string ServerUnavailableReason { get; }

        bool StartServer(int port, out string error);

        void StopServer();
    }

    /// <summary>What the menu is allowed to do to the connection and the world.</summary>
    /// <remarks>
    /// The menu does not hold a MultiplayerClient. It holds this, which is the four
    /// things it actually needs — and which a test can supply without a network.
    /// </remarks>
    public interface IMenuSession
    {
        bool IsConnected { get; }

        int PlayerCount { get; }

        string ServerAddress { get; }

        int ServerPort { get; }

        void Connect(string host, int port);

        void Disconnect();

        /// <summary>Sends an admin command. False when the server refused or there is no server.</summary>
        bool SendAdminCommand(string commandLine);
    }

    /// <summary>Which half of the menu is on screen.</summary>
    public enum MenuPage : byte
    {
        Bots = 0,
        Server = 1,
    }

    /// <summary>One selectable row.</summary>
    public sealed class BotMenuRow
    {
        public BotMenuRow(string label, Func<string> value, bool isAction = false)
        {
            Label = label;
            Value = value;
            IsAction = isAction;
        }

        public string Label { get; }

        public Func<string> Value { get; }

        /// <summary>True for a row that does something when Enter is pressed rather than holding a setting.</summary>
        public bool IsAction { get; }
    }

    /// <summary>
    /// The in-game menu: everything about it except the drawing.
    /// <para>
    /// It exists because both halves of setting up a session were a second window. Bots
    /// meant alt-tabbing out of a full-screen game, typing a command line, alt-tabbing
    /// back, and doing it again for every change of task; a server meant leaving one
    /// running in a console beside the game. That is enough friction that the
    /// two-player cases went untested, which is how a framework ends up with a combat
    /// system that has never worked.
    /// </para>
    /// <para>
    /// Two pages rather than two menus, switched with Tab. A second key is a second
    /// thing to discover and a second thing to collide with another mod, and the two
    /// pages are the same job seen from either end: what is running, and who is in it.
    /// </para>
    /// </summary>
    public sealed class BotMenu
    {
        /// <summary>Task presets, in the order the menu cycles them.</summary>
        public static readonly string[] Presets =
        {
            string.Empty,
            "stand",
            "patrol",
            "drive",
            "follow",
            "shoot",
            "melee",
            "jump",
            "traffic",
            "die",
            "reconnect",
            "shoot,die",
            "melee,jump,traffic",
            "stand,follow,shoot,die",
        };

        /// <summary>Weather the server page can set, by GTA V's own names.</summary>
        public static readonly string[] Weathers =
        {
            "EXTRASUNNY", "CLEAR", "CLOUDS", "OVERCAST", "RAIN",
            "THUNDER", "FOGGY", "SMOG", "SNOWLIGHT", "XMAS",
        };

        /// <summary>Times of day the server page can set.</summary>
        public static readonly string[] Times =
        {
            "06:00", "09:00", "12:00", "15:00", "18:00", "21:00", "00:00", "03:00",
        };

        public const int MaxCount = 8;

        /// <summary>Port a locally started server listens on. The protocol's own default.</summary>
        public const int LocalServerPort = 27015;

        private readonly IBotHost _host;
        private readonly IServerHost? _serverHost;
        private readonly IMenuSession? _session;
        private readonly List<BotMenuRow> _botRows = new List<BotMenuRow>();
        private readonly List<BotMenuRow> _serverRows = new List<BotMenuRow>();
        private int _count = 2;
        private int _preset;
        private bool _spawnOnMe = true;
        private int _weather;
        private int _time = 2;

        public BotMenu(IBotHost host, IServerHost? serverHost = null, IMenuSession? session = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _serverHost = serverHost;
            _session = session;

            _botRows.Add(new BotMenuRow("Ботов", () => _count.ToString(CultureInfo.InvariantCulture)));
            _botRows.Add(new BotMenuRow("Задачи", () => Presets[_preset].Length == 0 ? "все по порядку" : Presets[_preset]));
            _botRows.Add(new BotMenuRow("Появиться", () => _spawnOnMe ? "рядом со мной" : "точка сервера"));
            _botRows.Add(new BotMenuRow("Запустить", () => _host.RunningCount > 0 ? "ещё" : string.Empty, isAction: true));
            _botRows.Add(new BotMenuRow("Остановить всех", () => _host.RunningCount.ToString(CultureInfo.InvariantCulture), isAction: true));

            _serverRows.Add(new BotMenuRow("Локальный сервер", DescribeServer, isAction: true));
            _serverRows.Add(new BotMenuRow("Подключение", DescribeConnection, isAction: true));
            _serverRows.Add(new BotMenuRow("Погода", () => Weathers[_weather], isAction: true));
            _serverRows.Add(new BotMenuRow("Время", () => Times[_time], isAction: true));
        }

        public bool IsOpen { get; private set; }

        public MenuPage Page { get; private set; } = MenuPage.Bots;

        public int Selected { get; private set; }

        public IReadOnlyList<BotMenuRow> Rows => Page == MenuPage.Bots ? _botRows : _serverRows;

        /// <summary>The last thing the menu did or refused to do, shown under the rows.</summary>
        public string Status { get; private set; } = string.Empty;

        /// <summary>Where the player is standing, set by the host each frame; used by "next to me".</summary>
        public NetVector3 PlayerPosition { get; set; }

        /// <summary>Server address and password to hand the bots, set by the host from the live connection.</summary>
        public string Server { get; set; } = "127.0.0.1:27015";

        public string Password { get; set; } = string.Empty;

        public int RunningCount => _host.RunningCount;

        public IReadOnlyList<string> RecentLines => _host.RecentLines;

        public void Toggle()
        {
            IsOpen = !IsOpen;
            if (IsOpen)
            {
                Status = _host.IsAvailable ? string.Empty : _host.UnavailableReason;
            }
        }

        public void Close() => IsOpen = false;

        /// <summary>
        /// Switches page, and puts the cursor back at the top.
        /// <para>
        /// The selection is reset rather than carried across, because the pages are
        /// different lengths and a cursor remembered on row four of a five-row page
        /// lands on nothing on a four-row one. Carrying it would also mean arriving on
        /// whatever happened to be at that index, which on the server page is a button
        /// that starts things.
        /// </para>
        /// </summary>
        public void NextPage()
        {
            Page = Page == MenuPage.Bots ? MenuPage.Server : MenuPage.Bots;
            Selected = 0;
            Status = string.Empty;
        }

        public void MoveUp() => Selected = Selected == 0 ? Rows.Count - 1 : Selected - 1;

        public void MoveDown() => Selected = (Selected + 1) % Rows.Count;

        public void Left() => Adjust(-1);

        public void Right() => Adjust(1);

        /// <summary>Enter on the selected row.</summary>
        public void Activate()
        {
            if (Page == MenuPage.Server)
            {
                ActivateServerRow();
                return;
            }

            switch (Selected)
            {
                case 3:
                    Launch();
                    return;

                case 4:
                    if (_host.RunningCount == 0)
                    {
                        Status = "ни одного бота не запущено";
                        return;
                    }

                    _host.StopAll();
                    Status = "останавливаю ботов";
                    return;

                default:
                    // A setting row: Enter behaves like Right, so the menu can be used
                    // without learning which rows are which.
                    Adjust(1);
                    return;
            }
        }

        private string DescribeServer()
        {
            if (_serverHost == null)
            {
                return "недоступен";
            }

            return _serverHost.IsServerRunning
                ? "запущен на " + LocalServerPort.ToString(CultureInfo.InvariantCulture)
                : "выключен";
        }

        private string DescribeConnection()
        {
            if (_session == null)
            {
                return "недоступно";
            }

            return _session.IsConnected
                ? $"подключён, игроков {_session.PlayerCount}"
                : "не подключён";
        }

        private void ActivateServerRow()
        {
            switch (Selected)
            {
                case 0:
                    ToggleLocalServer();
                    return;

                case 1:
                    ToggleConnection();
                    return;

                case 2:
                    Status = SendWorldCommand("weather " + Weathers[_weather], "погода: " + Weathers[_weather]);
                    return;

                case 3:
                    Status = SendWorldCommand("time " + Times[_time], "время: " + Times[_time]);
                    return;
            }
        }

        private void ToggleLocalServer()
        {
            if (_serverHost == null)
            {
                Status = "локальный сервер недоступен в этой сборке";
                return;
            }

            if (_serverHost.IsServerRunning)
            {
                _serverHost.StopServer();
                Status = "локальный сервер остановлен";
                return;
            }

            if (!_serverHost.IsServerAvailable)
            {
                Status = _serverHost.ServerUnavailableReason;
                return;
            }

            Status = _serverHost.StartServer(LocalServerPort, out string error)
                ? $"локальный сервер запущен на {LocalServerPort}"
                : error;
        }

        private void ToggleConnection()
        {
            if (_session == null)
            {
                Status = "подключение недоступно в этой сборке";
                return;
            }

            if (_session.IsConnected)
            {
                _session.Disconnect();
                Status = "отключаюсь";
                return;
            }

            _session.Connect(_session.ServerAddress, _session.ServerPort);
            Status = $"подключаюсь к {_session.ServerAddress}:{_session.ServerPort}";
        }

        /// <summary>
        /// Sends one world command and says what happened.
        /// <para>
        /// A refusal is reported rather than swallowed. These go through the same admin
        /// path the console uses and the server checks permission for them, so a player
        /// without it must be told that and not left looking at a menu that appears to
        /// have worked.
        /// </para>
        /// </summary>
        private string SendWorldCommand(string commandLine, string success)
        {
            if (_session == null)
            {
                return "недоступно в этой сборке";
            }

            if (!_session.IsConnected)
            {
                return "нужно быть подключённым к серверу";
            }

            return _session.SendAdminCommand(commandLine)
                ? success
                : "сервер отказал — нужны права администратора";
        }

        private void Launch()
        {
            if (!_host.IsAvailable)
            {
                Status = _host.UnavailableReason;
                return;
            }

            var launch = new BotLaunch(
                _count,
                Presets[_preset],
                Server,
                Password,
                _spawnOnMe ? PlayerPosition : (NetVector3?)null);

            Status = _host.Start(launch, out string error)
                ? $"запущено ботов: {_count}"
                : error;
        }

        private void Adjust(int direction)
        {
            if (Page == MenuPage.Server)
            {
                switch (Selected)
                {
                    case 2:
                        _weather = (_weather + direction + Weathers.Length) % Weathers.Length;
                        return;

                    case 3:
                        _time = (_time + direction + Times.Length) % Times.Length;
                        return;
                }

                return;
            }

            switch (Selected)
            {
                case 0:
                    _count = Wrap(_count + direction, 1, MaxCount);
                    return;

                case 1:
                    _preset = (_preset + direction + Presets.Length) % Presets.Length;
                    return;

                case 2:
                    _spawnOnMe = !_spawnOnMe;
                    return;
            }
        }

        private static int Wrap(int value, int min, int max)
        {
            if (value < min)
            {
                return max;
            }

            return value > max ? min : value;
        }
    }
}
