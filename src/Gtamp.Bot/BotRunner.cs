using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Gtamp.Bot.Tasks;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Net;

namespace Gtamp.Bot
{
    /// <summary>One bot: a client, a body, and a queue of tasks with verdicts.</summary>
    public sealed class BotRunner : IDisposable
    {
        private const double ConnectTimeoutSeconds = 15d;

        /// <summary>
        /// Seconds to let the session settle before the first task starts.
        /// <para>
        /// The server places a joining player where it decides they belong, which
        /// arrives as one correction. That is correct behaviour, and without this
        /// delay the first task is blamed for it — the bot's very first run reported
        /// "the server moved a bot that was standing still" and the server was right.
        /// </para>
        /// </summary>
        private const double SettleSeconds = 3d;

        private readonly BotOptions _options;
        private readonly BotBody _body;
        private readonly SimulatedGameBridge _bridge;
        private readonly MultiplayerClient _client;
        private readonly BotContext _context;
        private readonly UdpDatagramTransport _transport;
        private readonly Queue<BotTask> _queue = new Queue<BotTask>();
        private readonly List<(string Task, TaskVerdict Verdict)> _results = new List<(string, TaskVerdict)>();

        private BotTask? _current;
        private double _taskStarted;
        private double _startedAt = -1d;
        private bool _announcedConnection;

        public BotRunner(string name, BotOptions options, int index)
        {
            _options = options;
            Name = name;

            var log = new LogBus { MinimumLevel = options.Verbose ? LogLevel.Debug : LogLevel.Warning };
            log.AddSink(new BotLogSink(name, options.Verbose));

            Directory.CreateDirectory(options.IdentityDirectory);
            string configPath = Path.Combine(options.IdentityDirectory, name + ".ini");

            // ClientConfig.Load generates and persists an identity keypair when the
            // file has none, so a bot keeps the same identity between runs and the
            // server's "previous state restored" path is exercised for real.
            ClientConfig config = ClientConfig.Load(configPath);
            config.PlayerName = name;
            config.ServerAddress = options.Host;
            config.ServerPort = options.Port;
            config.ServerPassword = options.Password;
            config.VerboseLogging = options.Verbose;

            _body = new BotBody
            {
                Position = new NetVector3(
                    options.Spawn[0] + (index * 4f),
                    options.Spawn[1],
                    options.Spawn[2]),
            };

            _bridge = new SimulatedGameBridge(_body);
            _transport = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0));
            _client = new MultiplayerClient(config, _bridge, log, _transport)
            {
                ClientVersion = typeof(BotRunner).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                LogDirectory = options.IdentityDirectory,
                ConfigPath = configPath,
            };

            _context = new BotContext(name, _body, _bridge, _client)
            {
                Say = Say,
            };

            foreach (string taskName in options.TaskNames)
            {
                if (BotTask.TryResolve(taskName, out BotTask task))
                {
                    _queue.Enqueue(task);
                }
            }
        }

        public string Name { get; }

        public bool Finished { get; private set; }

        public void Step(double now, double delta)
        {
            if (Finished)
            {
                return;
            }

            if (_startedAt < 0d)
            {
                _startedAt = now;
                _client.Connect(_options.Host, _options.Port);
                Say(Name, $"подключаюсь к {_options.Host}:{_options.Port}");
            }

            _client.Update(now);

            if (!_context.Connected)
            {
                // Not connected yet, or the reconnect task is mid-flight. Only the
                // opening wait is fatal: if the server never answers at all, saying
                // so is more use than running seven tasks against nothing.
                if (_current == null && now - _startedAt > ConnectTimeoutSeconds)
                {
                    Say(Name, "сервер не ответил за 15 с — проверьте, что он запущен и версия протокола совпадает");
                    Finished = true;
                }

                if (_current == null)
                {
                    return;
                }
            }
            else if (!_announcedConnection)
            {
                _announcedConnection = true;
                Say(Name, $"подключён как игрок {_client.LocalPlayerId} (сущность {_client.LocalEntityId})");
            }

            if (_current == null)
            {
                if (_queue.Count == 0)
                {
                    Finished = true;
                    return;
                }

                if (_results.Count == 0 && now - _startedAt < SettleSeconds)
                {
                    return;
                }

                _current = _queue.Dequeue();
                _taskStarted = now;
                Say(Name, $"→ {_current.Name}: {_current.Goal}");
                _current.Start(_context);
                return;
            }

            double elapsed = now - _taskStarted;
            bool done;
            try
            {
                done = _current.Update(_context, elapsed, delta);
            }
            catch (Exception exception)
            {
                _results.Add((_current.Name, TaskVerdict.Fail("задача упала: " + exception.Message)));
                _current = null;
                return;
            }

            if (!done && elapsed < _current.TimeLimitSeconds)
            {
                return;
            }

            TaskVerdict verdict;
            try
            {
                verdict = _current.Finish(_context, elapsed);
            }
            catch (Exception exception)
            {
                verdict = TaskVerdict.Fail("разбор результата упал: " + exception.Message);
            }

            _results.Add((_current.Name, verdict));
            Say(Name, $"   {Label(verdict.Result)} {verdict.Detail}");
            _current = null;
        }

        public void ReportProgress()
        {
            if (Finished || !_context.Connected)
            {
                return;
            }

            BotObservations seen = _bridge.Seen;
            Say(Name,
                $"   рядом: игроков {_client.RemotePlayers.Count}, машин {seen.RemoteVehicles.Count}; " +
                $"снапшотов {_client.SnapshotsApplied}, ресинков {_client.ResyncsRequested}, " +
                $"коррекций {seen.CorrectionsApplied}");
        }

        /// <summary>Prints the verdict table. Returns how many tasks failed.</summary>
        public int PrintSummary()
        {
            Console.WriteLine($"=== {Name} ===");

            int failures = 0;
            foreach ((string task, TaskVerdict verdict) in _results)
            {
                if (verdict.Result == TaskResult.Failed)
                {
                    failures++;
                }

                Console.WriteLine($"  {Label(verdict.Result),-6} {task,-10} {verdict.Detail}");
            }

            if (_results.Count == 0)
            {
                Console.WriteLine("  (ни одна задача не выполнялась)");
            }

            BotObservations seen = _bridge.Seen;
            Console.WriteLine(
                $"  итого: чужих игроков видел {seen.RemotePedsEverSeen}, машин {seen.RemoteVehiclesEverSeen}, " +
                $"чужих выстрелов {seen.ShotsDrawn}, взрывов {seen.ExplosionsDrawn}");
            Console.WriteLine(
                $"  сеть:  снапшотов {_client.SnapshotsApplied} применено / {_client.ReplicatedWorld.SnapshotsDropped} отброшено, " +
                $"ресинков {_client.ResyncsRequested}, коррекций {seen.CorrectionsApplied}");
            Console.WriteLine();

            return failures;
        }

        private static string Label(TaskResult result) => result switch
        {
            TaskResult.Passed => "ok",
            TaskResult.Failed => "FAIL",
            TaskResult.Look => "смотр",
            _ => "--",
        };

        private static void Say(string who, string what) =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {who}: {what}");

        public void Dispose()
        {
            try
            {
                _client.Disconnect("bot finished");
            }
            catch (Exception)
            {
                // Disconnecting is a courtesy; the server times the session out anyway.
            }

            _transport.Dispose();
        }

        /// <summary>Forwards client warnings and errors to the console, tagged by bot.</summary>
        private sealed class BotLogSink : ILogSink
        {
            private readonly string _name;
            private readonly bool _verbose;

            public BotLogSink(string name, bool verbose)
            {
                _name = name;
                _verbose = verbose;
            }

            public void Write(in LogEntry entry)
            {
                if (!_verbose && entry.Level < LogLevel.Warning)
                {
                    return;
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_name} [{entry.Level}] {entry.Message}");
            }
        }
    }
}
