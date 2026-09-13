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
    /// The in-game bot menu: everything about it except the drawing.
    /// <para>
    /// It exists because the bots were a second console window. Testing a two-player
    /// situation meant alt-tabbing out of a full-screen game, typing a command line,
    /// alt-tabbing back, and doing it again for every change of task — which is enough
    /// friction that the two-player cases went untested, which is how a framework ends
    /// up with a combat system that has never worked.
    /// </para>
    /// <para>
    /// The spawn point is the part that pays for itself: "next to me" launches the
    /// bots at the player's own coordinates. Bots at the server's spawn are two
    /// kilometres away from wherever the player is standing, and a fight that cannot
    /// be reached is not a test.
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
            "die",
            "reconnect",
            "shoot,die",
            "stand,follow,shoot,die",
        };

        public const int MaxCount = 8;

        private readonly IBotHost _host;
        private readonly List<BotMenuRow> _rows = new List<BotMenuRow>();
        private int _count = 2;
        private int _preset;
        private bool _spawnOnMe = true;

        public BotMenu(IBotHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _rows.Add(new BotMenuRow("Ботов", () => _count.ToString(CultureInfo.InvariantCulture)));
            _rows.Add(new BotMenuRow("Задачи", () => Presets[_preset].Length == 0 ? "все по порядку" : Presets[_preset]));
            _rows.Add(new BotMenuRow("Появиться", () => _spawnOnMe ? "рядом со мной" : "точка сервера"));
            _rows.Add(new BotMenuRow("Запустить", () => _host.RunningCount > 0 ? "ещё" : string.Empty, isAction: true));
            _rows.Add(new BotMenuRow("Остановить всех", () => _host.RunningCount.ToString(CultureInfo.InvariantCulture), isAction: true));
        }

        public bool IsOpen { get; private set; }

        public int Selected { get; private set; }

        public IReadOnlyList<BotMenuRow> Rows => _rows;

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

        public void MoveUp() => Selected = Selected == 0 ? _rows.Count - 1 : Selected - 1;

        public void MoveDown() => Selected = (Selected + 1) % _rows.Count;

        public void Left() => Adjust(-1);

        public void Right() => Adjust(1);

        /// <summary>Enter on the selected row.</summary>
        public void Activate()
        {
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
