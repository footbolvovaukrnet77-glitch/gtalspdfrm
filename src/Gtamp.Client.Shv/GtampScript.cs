using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Client.Network;
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
        private readonly OverlayRenderer _overlay = new OverlayRenderer();
        private ClientConfig? _config;
        private string _configPath = string.Empty;
        private bool _failed;

        /// <summary>
        /// The compatibility probe runs once, on the first frame rather than in the
        /// constructor, because it calls into the game and the first frame is the
        /// earliest point where that is unambiguously safe.
        /// </summary>
        private bool _compatibilityChecked;

        /// <summary>
        /// Resolved before anything that can throw, so the last-resort crash log lands in
        /// the same place as every other file this client owns. It used to resolve its own
        /// directory from the app domain base — which is the scripts folder — so the one
        /// log a player is told to look for when nothing else worked was written somewhere
        /// they were never told to look.
        /// </summary>
        private readonly string _gameDirectory;

        private readonly string _baseDirectory;

        public GtampScript()
        {
            _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _gameDirectory = GameDirectory.Resolve(_baseDirectory, TryGetExecutablePath());

            try
            {
                Initialize();
            }
            catch (Exception exception)
            {
                _failed = true;
                Ui.NativeDraw.Notify("~r~GTAMP failed to start~s~. See Gtamp/logs.");
                WriteFallbackCrashLog(_gameDirectory, exception);
            }

            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }

        private void Initialize()
        {
            string baseDirectory = _baseDirectory;
            string gameDirectory = _gameDirectory;
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
                LogDirectory = Path.Combine(root, "logs"),
                ConfigPath = _configPath,
            };

            _renderer = new ConsoleRenderer(console);

            _log.Success(LogCategory.Client, $"GTAMP client {_client.ClientVersion} loaded. Press F8 for the console.");

            // On screen as well as in the log, and not only as a courtesy. It is drawn by
            // the same native text machinery the console uses, so it is the one signal a
            // player gets *before* opening anything: if this notification appears, drawing
            // works; if the game is running and it never appears, the client either did
            // not load or cannot draw, and those are the two questions worth separating
            // first. Until this existed, a client that loaded and a client that did not
            // looked identical from inside the game.
            Ui.NativeDraw.Notify(
                $"~g~GTAMP {_client.ClientVersion}~s~ loaded. Press "
                + (_config.ConsoleKey == ClientConfig.DefaultConsoleKey ? "F8" : $"key {_config.ConsoleKey}")
                + " for the console.");
            _log.Info(LogCategory.Client, $"Config: {_configPath}");

            // Both, always. The app domain base is not the game directory under
            // ScriptHookVDotNet — it is the scripts folder — and believing otherwise put
            // every file this client owns one directory below where the install guide
            // says it is, and made the mod scan look for ScriptHookV.dll inside
            // scripts\. Printing the two side by side is how the next surprise of this
            // kind gets noticed in one line rather than in a bug report.
            _log.Info(LogCategory.Client, $"Game directory: {gameDirectory} (app domain base: {baseDirectory})");

            string? legacyRoot = GameDirectory.LegacyRoot(baseDirectory, gameDirectory);
            if (legacyRoot != null && Directory.Exists(Path.Combine(legacyRoot, "Gtamp")))
            {
                _log.Warning(
                    LogCategory.Client,
                    $"An older build kept its files in '{Path.Combine(legacyRoot, "Gtamp")}'. This build uses " +
                    $"'{root}'. Move client.ini across if you want to keep the identity key in it, or a new one " +
                    "is generated and other servers will see you as a different player.");
            }

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

            if (!_compatibilityChecked)
            {
                _compatibilityChecked = true;
                CheckGameApi();
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

            // Drawn after the console so it is never on top of it, and only when the
            // player asked for it — an always-on readout is clutter for everybody who
            // is not debugging.
            if (_client.Config.ShowNetworkOverlay && !_client.Console.IsOpen)
            {
                _overlay.Draw(NetworkOverlay.Build(_client));
            }
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

        /// <summary>
        /// Full path of the running executable, or null when the host will not say.
        /// <c>MainModule</c> throws often enough — a partially initialised process, a
        /// permission refusal — that this is worth a catch rather than a crash before
        /// the logger exists.
        /// </summary>
        /// <summary>
        /// Asks ScriptHookVDotNet one question that goes through the memory map it builds
        /// by scanning the game's code, and refuses to connect when the answer is an
        /// exception.
        /// <para>
        /// Without this the failure arrives later and somewhere else: the client starts,
        /// says it loaded, connects, and dies on whichever frame first tries to spawn a
        /// remote player — with a .NET stack trace naming SHVDN internals, in whatever
        /// language the player's Windows is set to. The information needed to diagnose it
        /// is available on the first frame, so it is read on the first frame.
        /// </para>
        /// </summary>
        private void CheckGameApi()
        {
            bool works;
            try
            {
                // Game.Version is (GameVersion)NativeMemory.GetGameVersion(): the shortest
                // route into the class that fails, needing no ped, vehicle or world state.
                _ = Game.Version;
                works = true;
            }
            catch (Exception exception)
            {
                works = false;
                _log?.Debug(LogCategory.Client, "The managed game API is unusable: " + exception);
            }

            string? reason = ScriptHostCompatibility.Describe(works, ReadGameBuild(), ReadScriptHostVersion());
            if (reason == null || _client == null)
            {
                return;
            }

            _client.BlockReason = reason;
            _log?.Error(LogCategory.Client, reason);
            Ui.NativeDraw.Notify(ScriptHostCompatibility.ShortNotification);

            // AutoConnectOnStart runs before the first frame, so a session may already be
            // opening by the time this is known. Refusing new connections without closing
            // that one would leave exactly the half-working session this check exists to
            // prevent.
            if (_client.Connection.State != ClientConnectionState.Disconnected)
            {
                _client.Disconnect("the game API is unusable on this game build");
            }
        }

        /// <summary>
        /// The game build, read from GTA5.exe rather than from the game, because the
        /// call that asks the game is the one that just failed.
        /// </summary>
        private string? ReadGameBuild()
        {
            try
            {
                string? executable = TryGetExecutablePath();
                if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                {
                    return null;
                }

                return FileVersionInfo.GetVersionInfo(executable!).FileVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The API assembly actually loaded, which is the one that has to change.</summary>
        private static string? ReadScriptHostVersion()
        {
            try
            {
                return typeof(Game).Assembly.GetName().Version?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? TryGetExecutablePath()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_failed || _client == null || _config == null)
            {
                // A key that does nothing at all is the worst answer available: the
                // player cannot tell a broken install from a wrong key. The startup
                // notification is shown once and is easy to miss behind a loading
                // screen, so the console key says it again, every time it is pressed.
                int wanted = _config?.ConsoleKey ?? ClientConfig.DefaultConsoleKey;
                if ((int)e.KeyCode == wanted)
                {
                    Ui.NativeDraw.Notify(
                        "~r~GTAMP did not start~s~, so the console is not available. "
                        + "See Gtamp/logs/startup-failure.log.");
                }

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
        /// <summary>
        /// Hands the key to <see cref="ConsoleKeyMap"/>, which is where the decision
        /// lives so that it can be tested. Nothing about this mapping needs a game.
        /// </summary>
        private static char TranslateKey(KeyEventArgs e) =>
            ConsoleKeyMap.Translate((int)e.KeyCode, e.Shift);

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
        private static void WriteFallbackCrashLog(string gameDirectory, Exception exception)
        {
            try
            {
                string directory = Path.Combine(gameDirectory, "Gtamp", "logs");
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
