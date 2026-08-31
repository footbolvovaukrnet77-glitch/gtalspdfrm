using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;
using Gtamp.Client.Core;
using Gtamp.Client.Diagnostics;
using Gtamp.Client.Shv.Bridge;
using Gtamp.Client.Shv.Ui;
using Gtamp.Client.Ui;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Net;
using GTA;
using GTA.Native;

namespace Gtamp.Client.Shv
{
    /// <summary>
    /// The ScriptHookVDotNet entry point. Everything it does is bookkeeping around
    /// <see cref="MultiplayerClient"/>: build the paths, wire up the bridge and the
    /// console, then pump the client once per game frame.
    /// <para>
    /// It is deliberately thin. Anything with logic worth testing lives in
    /// Gtamp.Client.Core, which builds and runs without GTA V.
    /// </para>
    /// </summary>
    public sealed class GtampScript : Script
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private LogBus? _log;
        private ClientFileLogSink? _fileSink;
        private ShvGameBridge? _bridge;
        private MultiplayerClient? _client;
        private ConsoleRenderer? _renderer;
        private ClientConfig? _config;
        private string _configPath = string.Empty;
        private bool _failed;

        public GtampScript()
        {
            try
            {
                Initialize();
            }
            catch (Exception exception)
            {
                _failed = true;
                GTA.UI.Notification.Show("~r~GTAMP failed to start~s~. See Gtamp/logs.", false);
                WriteFallbackCrashLog(exception);
            }

            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }

        private void Initialize()
        {
            string gameDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string root = Path.Combine(gameDirectory, "Gtamp");
            _configPath = Path.Combine(root, "client.ini");

            _config = ClientConfig.Load(_configPath);

            _log = new LogBus { MinimumLevel = _config.VerboseLogging ? LogLevel.Debug : LogLevel.Info };
            _fileSink = new ClientFileLogSink(Path.Combine(root, "logs"));
            _log.AddSink(_fileSink);

            var console = new DeveloperConsole(new WindowsClipboard(_log));
            _log.AddSink(console);

            _bridge = new ShvGameBridge(_log);

            // Port 0 lets the OS pick a free source port, so two GTA V instances on
            // one machine can both connect to the same server.
            var transport = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0));

            _client = new MultiplayerClient(_config, _bridge, _log, transport, console)
            {
                ClientVersion = typeof(GtampScript).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            };

            _renderer = new ConsoleRenderer(console);

            _log.Success(LogCategory.Client, $"GTAMP client {_client.ClientVersion} loaded. Press F8 for the console.");
            _log.Info(LogCategory.Client, $"Config: {_configPath}");

            _client.InitializeMods(gameDirectory, Path.Combine(root, "Adapters"));

            if (_config.AutoConnectOnStart)
            {
                _client.Connect(_config.ServerAddress, _config.ServerPort);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_failed || _client == null || _renderer == null)
            {
                return;
            }

            double now = _clock.Elapsed.TotalSeconds;

            try
            {
                _client.Update(now);
            }
            catch (Exception exception)
            {
                // A single bad frame must not kill the script; log it and carry on.
                _log?.Error(LogCategory.Client, "Unhandled exception in the client update.", exception);
            }

            if (_client.Console.IsOpen)
            {
                // Stop the game consuming keystrokes meant for the console.
                Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
                _renderer.StatusLine = BuildStatusLine();
            }

            _renderer.Draw();
        }

        private string BuildStatusLine()
        {
            if (_client == null)
            {
                return string.Empty;
            }

            NetStats? stats = _client.Connection.Peer?.Stats;
            string network = stats == null
                ? "offline"
                : $"{stats.PingMilliseconds} ms, {stats.PacketLoss * 100:0.0}% loss";

            return $"[{_client.Connection.State}] entities: {_client.ReplicatedWorld.EntityCount}  " +
                   $"players: {_client.RemotePlayers.Count + 1}  snapshot: {_client.ReplicatedWorld.LastAppliedSnapshotId}  {network}";
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_failed || _client == null || _config == null)
            {
                return;
            }

            DeveloperConsole console = _client.Console;

            if ((int)e.KeyCode == _config.ConsoleKey)
            {
                console.Toggle();
                e.SuppressKeyPress = true;
                return;
            }

            if (!console.IsOpen)
            {
                return;
            }

            e.SuppressKeyPress = true;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    console.Submit(console.InputLine);
                    return;

                case Keys.Escape:
                    console.Close();
                    return;

                case Keys.Back:
                    if (console.InputLine.Length > 0)
                    {
                        console.InputLine = console.InputLine.Substring(0, console.InputLine.Length - 1);
                    }

                    return;

                case Keys.Up:
                    console.HistoryPrevious();
                    return;

                case Keys.Down:
                    console.HistoryNext();
                    return;

                case Keys.PageUp:
                    console.Scroll(console.VisibleLineCount);
                    return;

                case Keys.PageDown:
                    console.Scroll(-console.VisibleLineCount);
                    return;

                case Keys.End:
                    console.ScrollToBottom();
                    return;
            }

            char character = TranslateKey(e);
            if (character != '\0')
            {
                console.InputLine += character;
            }
        }

        /// <summary>
        /// Maps a key event to a character. Deliberately US-layout only: SHVDN
        /// delivers virtual key codes, not text, so a full layout-aware translation
        /// would mean calling into the Win32 keyboard layout API. Console input is
        /// ASCII commands, so this is good enough and has no dependencies.
        /// </summary>
        private static char TranslateKey(KeyEventArgs e)
        {
            bool shift = e.Shift;

            if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
            {
                char letter = (char)('a' + (e.KeyCode - Keys.A));
                return shift ? char.ToUpperInvariant(letter) : letter;
            }

            if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
            {
                if (!shift)
                {
                    return (char)('0' + (e.KeyCode - Keys.D0));
                }

                return e.KeyCode switch
                {
                    Keys.D1 => '!',
                    Keys.D2 => '@',
                    Keys.D3 => '#',
                    Keys.D4 => '$',
                    Keys.D5 => '%',
                    Keys.D6 => '^',
                    Keys.D7 => '&',
                    Keys.D8 => '*',
                    Keys.D9 => '(',
                    Keys.D0 => ')',
                    _ => '\0',
                };
            }

            if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
            {
                return (char)('0' + (e.KeyCode - Keys.NumPad0));
            }

            return e.KeyCode switch
            {
                Keys.Space => ' ',
                Keys.OemPeriod or Keys.Decimal => '.',
                Keys.Oemcomma => ',',
                Keys.OemMinus or Keys.Subtract => shift ? '_' : '-',
                Keys.Oemplus or Keys.Add => shift ? '+' : '=',
                Keys.OemQuestion or Keys.Divide => shift ? '?' : '/',
                Keys.Oem1 => shift ? ':' : ';',
                Keys.Oem7 => shift ? '"' : '\'',
                Keys.Oem5 => shift ? '|' : '\\',
                _ => '\0',
            };
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                _client?.Dispose();
                _bridge?.CleanUp();
                _fileSink?.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful left to do while the script is being torn down.
            }
        }

        /// <summary>Last-resort log when the failure happened before the log bus existed.</summary>
        private static void WriteFallbackCrashLog(Exception exception)
        {
            try
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Gtamp", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "startup-failure.log"),
                    $"[{DateTime.UtcNow:u}] {exception}{System.Environment.NewLine}");
            }
            catch (Exception)
            {
                // If even this fails there is nowhere left to report to.
            }
        }
    }
}
