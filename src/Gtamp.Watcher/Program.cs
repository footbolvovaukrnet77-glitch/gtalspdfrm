using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Watches the logs GTA V, ScriptHookV, ScriptHookVDotNet, RAGE Plugin Hook
    /// and GTAMP write, and records what happened whenever one of them says
    /// something went wrong.
    /// <para>
    /// It is a reader, not an overlay. It never touches the game, never injects
    /// anything, and cannot be the reason a session breaks — which is the whole
    /// point of a tool whose job is to be trusted about what broke.
    /// </para>
    /// </summary>
    public static class Program
    {
        /// <summary>Lines kept either side of a trigger, so a record shows what was happening.</summary>
        private const int ContextLines = 40;

        /// <summary>
        /// The same problem, again, within this long, is the same problem.
        /// <para>
        /// Two windows, because the two kinds repeat differently. A resync storms:
        /// one of the user's real thirty-minute logs holds 995 "Requesting a
        /// resync" lines, and one incident per line would bury the session that
        /// caused them. A timeout is rare and each one is its own event.
        /// </para>
        /// </summary>
        private static TimeSpan QuietFor(IncidentSeverity severity) =>
            severity == IncidentSeverity.Fatal ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);

        public static int Main(string[] args)
        {
            WatcherOptions options = WatcherOptions.Parse(args, out string? error);

            if (options.ShowHelp)
            {
                Console.WriteLine(WatcherOptions.Usage);
                return 0;
            }

            if (options.ShowRules)
            {
                Console.WriteLine("Что считается сбоем:");
                Console.WriteLine();
                foreach ((string kind, IncidentSeverity severity, string why) in IncidentRules.Describe())
                {
                    Console.WriteLine($"  {kind,-18} {why}");
                }

                return 0;
            }

            if (error != null)
            {
                Console.Error.WriteLine(error);
                return 2;
            }

            options.BuildDescription = DescribeBuild(options.RepositoryDirectory);
            Directory.CreateDirectory(options.IncidentDirectory);

            var writer = new IncidentWriter(options);
            var publisher = new Publisher(options);
            var tails = new Dictionary<string, LogTail>(StringComparer.OrdinalIgnoreCase);
            var context = new Queue<string>();
            var lastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine("GTAMP watcher");
            Console.WriteLine($"  игра:     {options.GameDirectory}");
            Console.WriteLine($"  записи:   {options.IncidentDirectory}");
            Console.WriteLine($"  сборка:   {options.BuildDescription}");
            Console.WriteLine($"  скриншот: {(options.Screenshots ? "да" : "нет")}");
            Console.WriteLine(options.Publish
                ? $"  отправка: git push {options.Remote} {options.Branch}"
                : "  отправка: нет — записи остаются на диске");
            Console.WriteLine();
            Console.WriteLine("Смотрю. Ctrl+C чтобы остановить.");
            Console.WriteLine();

            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };

            int incidents = 0;

            while (!cancel.IsCancellationRequested)
            {
                foreach (string file in options.WatchedFiles())
                {
                    if (!tails.TryGetValue(file, out LogTail? tail))
                    {
                        tail = new LogTail(file, options.SinceStart);
                        tails[file] = tail;
                        Console.WriteLine($"  + слежу за {Path.GetFileName(file)}");
                    }

                    foreach (string line in tail.ReadNewLines())
                    {
                        context.Enqueue($"[{tail.Name}] {Redactor.Scrub(line)}");
                        while (context.Count > ContextLines)
                        {
                            context.Dequeue();
                        }

                        Incident? incident = IncidentRules.Match(tail.Name, line);
                        if (incident == null)
                        {
                            continue;
                        }

                        if (lastSeen.TryGetValue(incident.Kind, out DateTime previous)
                            && DateTime.Now - previous < QuietFor(incident.Severity))
                        {
                            // A resync storm is one incident, not a thousand.
                            continue;
                        }

                        lastSeen[incident.Kind] = DateTime.Now;
                        incidents++;
                        Record(writer, publisher, options, incident, new List<string>(context));
                    }
                }

                cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(options.IntervalSeconds));
            }

            Console.WriteLine();
            Console.WriteLine(incidents == 0
                ? "Остановился. Ничего не сломалось."
                : $"Остановился. Записей: {incidents}. Они в {options.IncidentDirectory}");

            return 0;
        }

        private static void Record(
            IncidentWriter writer,
            Publisher publisher,
            WatcherOptions options,
            Incident incident,
            IReadOnlyList<string> context)
        {
            string folder;
            try
            {
                folder = writer.Write(incident, context, options.WatchedFiles());
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"  ! не смог записать инцидент: {exception.Message}");
                return;
            }

            Console.WriteLine($"[{incident.NoticedAt:HH:mm:ss}] {incident.Kind}: {incident.Why}");
            Console.WriteLine($"    {folder}");

            if (!options.Publish)
            {
                return;
            }

            if (publisher.Publish(folder, incident, out string detail))
            {
                Console.WriteLine($"    отправлено в {options.Remote}/{options.Branch}");
            }
            else
            {
                Console.Error.WriteLine($"    отправить не вышло: {detail}");
                Console.Error.WriteLine("    запись на диске осталась — отправите вручную.");
            }
        }

        /// <summary>Which build is running, so a record cannot be read against the wrong code.</summary>
        private static string DescribeBuild(string repository)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = repository,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("log");
                start.ArgumentList.Add("-1");
                start.ArgumentList.Add("--format=%h %s");

                using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
                if (process == null)
                {
                    return "неизвестна";
                }

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(20_000);
                return output.Length > 0 ? output : "неизвестна";
            }
            catch (Exception)
            {
                return "неизвестна";
            }
        }
    }
}
