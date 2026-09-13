using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gtamp.Client.Ui;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Shv.Bridge
{
    /// <summary>
    /// Runs <c>Gtamp.Bot</c> as child processes of the game, so bots can be started
    /// and stopped without leaving a full-screen GTA V.
    /// <para>
    /// The bot is a .NET 8 console program and the client is a .NET Framework 4.8
    /// script inside the game's process; they cannot be the same process, and nothing
    /// here pretends otherwise. What this does is remove the alt-tab, which is the
    /// part that was actually stopping two-player situations from being tested.
    /// </para>
    /// <para>
    /// Every bot is stopped when the game's script shuts down. A bot left running
    /// after the player quits would sit in the server's world as a player nobody can
    /// see the console for, and the next session would find strangers standing in it.
    /// </para>
    /// </summary>
    public sealed class BotProcessHost : IBotHost, IServerHost, IDisposable
    {
        /// <summary>How many output lines are kept for the menu to show.</summary>
        private const int LineBufferSize = 12;

        private readonly List<Process> _processes = new List<Process>();
        private readonly Queue<string> _lines = new Queue<string>();
        private readonly object _gate = new object();
        private readonly LogBus _log;
        private readonly string _gameDirectory;
        private readonly string? _configuredPath;
        private readonly string? _configuredServerPath;

        public BotProcessHost(LogBus log, string gameDirectory, string? configuredPath, string? configuredServerPath = null)
        {
            _log = log;
            _gameDirectory = gameDirectory ?? string.Empty;
            _configuredPath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath;
            _configuredServerPath = string.IsNullOrWhiteSpace(configuredServerPath) ? null : configuredServerPath;
        }

        public int RunningCount
        {
            get
            {
                lock (_gate)
                {
                    Reap();
                    return _processes.Count;
                }
            }
        }

        public bool IsAvailable => Locate() != null;

        public string UnavailableReason =>
            "Gtamp.Bot не найден. Положите собранного бота в '"
            + Path.Combine(_gameDirectory, "Gtamp", "bot")
            + "' (tools\\package-client.bat кладёт его туда), "
            + "или укажите BotPath в client.ini.";

        public IReadOnlyList<string> RecentLines
        {
            get
            {
                lock (_gate)
                {
                    return new List<string>(_lines);
                }
            }
        }

        public bool Start(BotLaunch launch, out string error)
        {
            string? bot = Locate();
            if (bot == null)
            {
                error = UnavailableReason;
                return false;
            }

            try
            {
                ProcessStartInfo start = BuildStartInfo(bot, launch);
                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Record(e.Data);
                process.ErrorDataReceived += (_, e) => Record(e.Data);

                if (!process.Start())
                {
                    error = "не удалось запустить процесс бота";
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (_gate)
                {
                    _processes.Add(process);
                }

                _log.Info(LogCategory.Client, $"Started {launch.Count} bot(s) from '{bot}'.");
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not start the bots.", exception);
                error = "запуск не удался: " + exception.Message;
                return false;
            }
        }

        public void StopAll()
        {
            lock (_gate)
            {
                foreach (Process process in _processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                        // Already gone, or gone between the check and the kill. Either
                        // way there is nothing left to stop.
                    }
                }

                _processes.Clear();
            }
        }

        public void Dispose()
        {
            StopAll();
            StopServer();
        }

        // ---- IServerHost ----
        //
        // The same machinery, pointed at a different program. A local server is a child
        // process for exactly the reasons a bot is: it is .NET 8 and the client is
        // .NET Framework 4.8 inside the game's process, so one process was never on
        // offer. What this removes is the second window, which is the whole of what the
        // separate-process design actually costs somebody playing alone.

        private Process? _server;

        public bool IsServerRunning
        {
            get
            {
                lock (_gate)
                {
                    try
                    {
                        if (_server != null && _server.HasExited)
                        {
                            _server = null;
                        }
                    }
                    catch (Exception)
                    {
                        _server = null;
                    }

                    return _server != null;
                }
            }
        }

        public bool IsServerAvailable => LocateServer() != null;

        public string ServerUnavailableReason =>
            "Gtamp.Server не найден. Положите собранный сервер в '"
            + Path.Combine(_gameDirectory, "Gtamp", "server")
            + "' (tools\\package-client.bat кладёт его туда), или укажите ServerPath в client.ini.";

        public bool StartServer(int port, out string error)
        {
            if (IsServerRunning)
            {
                error = "локальный сервер уже запущен";
                return false;
            }

            string? server = LocateServer();
            if (server == null)
            {
                error = ServerUnavailableReason;
                return false;
            }

            try
            {
                bool managedDll = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                string arguments = "--port " + port.ToString(CultureInfo.InvariantCulture);

                var start = new ProcessStartInfo
                {
                    FileName = managedDll ? "dotnet" : server,
                    Arguments = managedDll ? Quote(server) + " " + arguments : arguments,
                    WorkingDirectory = Path.GetDirectoryName(server) ?? _gameDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Record(e.Data);
                process.ErrorDataReceived += (_, e) => Record(e.Data);

                if (!process.Start())
                {
                    error = "не удалось запустить процесс сервера";
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (_gate)
                {
                    _server = process;
                }

                _log.Info(LogCategory.Client, $"Started a local server on port {port} from '{server}'.");
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not start the local server.", exception);
                error = "запуск не удался: " + exception.Message;
                return false;
            }
        }

        public void StopServer()
        {
            lock (_gate)
            {
                if (_server == null)
                {
                    return;
                }

                try
                {
                    if (!_server.HasExited)
                    {
                        // Killed rather than asked to stop. A server told to shut down
                        // saves its world first, which is what you want — but this runs
                        // when the game is closing and there is no frame left to wait
                        // in. Periodic saving is what makes that survivable, and it is
                        // on by default.
                        _server.Kill();
                    }
                }
                catch (Exception)
                {
                    // Already gone.
                }

                _server = null;
            }
        }

        private string? LocateServer()
        {
            if (_configuredServerPath != null && File.Exists(_configuredServerPath))
            {
                return _configuredServerPath;
            }

            string installed = Path.Combine(_gameDirectory, "Gtamp", "server");
            foreach (string name in new[] { "Gtamp.Server.exe", "Gtamp.Server.dll" })
            {
                string candidate = Path.Combine(installed, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// The bot, in the order a player is most likely to have put it:
        /// what they configured, what package-client installs, then the repository's
        /// own build output for somebody running from a checkout.
        /// </summary>
        private string? Locate()
        {
            if (_configuredPath != null && File.Exists(_configuredPath))
            {
                return _configuredPath;
            }

            string installed = Path.Combine(_gameDirectory, "Gtamp", "bot");
            foreach (string name in new[] { "Gtamp.Bot.exe", "Gtamp.Bot.dll" })
            {
                string candidate = Path.Combine(installed, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private ProcessStartInfo BuildStartInfo(string bot, BotLaunch launch)
        {
            string arguments = Arguments(launch);
            bool managedDll = bot.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

            var start = new ProcessStartInfo
            {
                FileName = managedDll ? "dotnet" : bot,
                Arguments = managedDll ? Quote(bot) + " " + arguments : arguments,
                WorkingDirectory = Path.GetDirectoryName(bot) ?? _gameDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // Without this the bot opens a console window over a full-screen game,
                // which is the thing this menu exists to avoid.
                CreateNoWindow = true,
            };

            return start;
        }

        private static string Arguments(BotLaunch launch)
        {
            var parts = new List<string>
            {
                "--count",
                launch.Count.ToString(CultureInfo.InvariantCulture),
                "--server",
                launch.Server,
            };

            if (!string.IsNullOrEmpty(launch.Tasks))
            {
                parts.Add("--task");
                parts.Add(launch.Tasks);
            }

            if (!string.IsNullOrEmpty(launch.Password))
            {
                parts.Add("--password");
                parts.Add(Quote(launch.Password));
            }

            if (launch.At.HasValue)
            {
                NetVector3 at = launch.At.Value;
                parts.Add("--at");
                parts.Add(string.Format(
                    CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", at.X, at.Y, at.Z));
            }

            return string.Join(" ", parts);
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private void Record(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_gate)
            {
                _lines.Enqueue(line!.Trim());
                while (_lines.Count > LineBufferSize)
                {
                    _lines.Dequeue();
                }
            }
        }

        private void Reap()
        {
            for (int i = _processes.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_processes[i].HasExited)
                    {
                        _processes.RemoveAt(i);
                    }
                }
                catch (Exception)
                {
                    _processes.RemoveAt(i);
                }
            }
        }
    }
}
