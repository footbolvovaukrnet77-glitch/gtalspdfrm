using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Gtamp.Server.Admin;
using Gtamp.Server.Core;
using Gtamp.Server.Diagnostics;
using Gtamp.Server.Persistence;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Net;

namespace Gtamp.Server
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string configPath = "server.json";
            int? portOverride = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config" when i + 1 < args.Length:
                        configPath = args[++i];
                        break;

                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port):
                        portOverride = port;
                        i++;
                        break;

                    case "--help":
                    case "-h":
                        Console.WriteLine("Usage: Gtamp.Server [--config <path>] [--port <port>]");
                        return 0;
                }
            }

            var log = new LogBus();
            var consoleSink = new ConsoleLogSink();
            log.AddSink(consoleSink);

            ServerConfig config;
            try
            {
                config = ServerConfig.LoadOrCreate(configPath);
                if (portOverride.HasValue)
                {
                    config.Port = portOverride.Value;
                }

                config.Validate();
            }
            catch (Exception exception)
            {
                log.Critical(LogCategory.Server, $"Could not load '{configPath}': {exception.Message}");
                return 1;
            }

            using var fileSink = new FileLogSink(config.LogDirectory);
            log.AddSink(fileSink);

            log.Info(LogCategory.Server, BuildInfo.Describe());

            IDatagramTransport transport;
            try
            {
                var bind = new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port);
                transport = new UdpDatagramTransport(bind);
            }
            catch (Exception exception)
            {
                log.Critical(
                    LogCategory.Network,
                    $"Could not bind UDP {config.BindAddress}:{config.Port} — {exception.Message}",
                    "Another process may already be using the port. See TROUBLESHOOTING.md, 'Server won't start'.");
                return 2;
            }

            IPersistenceStore persistence = config.PersistenceEnabled
                ? new SqlitePersistenceStore(config.DatabasePath)
                : new NullPersistenceStore();

            using var server = new GameServer(config, log, transport, persistence);
            var admin = new AdminConsole(server);

            var stopwatch = Stopwatch.StartNew();
            server.Start(stopwatch.Elapsed.TotalSeconds);

            using var shutdown = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                log.Info(LogCategory.Server, "Shutdown requested (Ctrl+C).");
                shutdown.Set();
            };

            var commands = new ConcurrentQueue<string>();
            StartConsoleReader(commands, shutdown);

            double tickInterval = config.TickIntervalSeconds;
            double nextTick = stopwatch.Elapsed.TotalSeconds;

            while (!shutdown.IsSet && !admin.StopRequested)
            {
                double now = stopwatch.Elapsed.TotalSeconds;

                while (commands.TryDequeue(out string? line))
                {
                    try
                    {
                        string output = admin.Execute(line!);
                        if (output.Length > 0)
                        {
                            Console.WriteLine(output);
                        }
                    }
                    catch (Exception exception)
                    {
                        log.Error(LogCategory.Console, $"Command '{line}' failed.", exception);
                    }
                }

                try
                {
                    server.Tick(now);
                }
                catch (Exception exception)
                {
                    // A tick must never take the process down: log it, keep serving.
                    log.Critical(LogCategory.Server, "Unhandled exception in the tick loop.", exception.ToString());
                }

                nextTick += tickInterval;
                double sleep = nextTick - stopwatch.Elapsed.TotalSeconds;
                if (sleep > 0)
                {
                    Thread.Sleep(sleep > 0.001 ? (int)(sleep * 1000) : 0);
                }
                else
                {
                    // Fell behind: give up the lost time rather than spiralling.
                    nextTick = stopwatch.Elapsed.TotalSeconds;
                }
            }

            log.Info(LogCategory.Server, "Shutting down...");
            server.Stop();
            return 0;
        }

        private static void StartConsoleReader(ConcurrentQueue<string> commands, ManualResetEventSlim shutdown)
        {
            // Redirected stdin is read too, not skipped: that is how a scripted
            // start ("echo stop | Gtamp.Server") and a container's console both
            // reach the admin commands. End of stream simply ends the reader.
            var thread = new Thread(() =>
            {
                while (!shutdown.IsSet)
                {
                    string? line = Console.ReadLine();
                    if (line == null)
                    {
                        return;
                    }

                    commands.Enqueue(line);
                }
            })
            {
                IsBackground = true,
                Name = "gtamp-console",
            };

            thread.Start();
        }
    }
}
