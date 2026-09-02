using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using Gtamp.Bot.Tasks;
using Gtamp.Client.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Net;

namespace Gtamp.Bot
{
    /// <summary>
    /// A second player without a second person.
    /// <para>
    /// Everything above <see cref="SimulatedGameBridge"/> is the real client: the
    /// real protocol, the real snapshots, the real anti-cheat on the other end. Only
    /// the game underneath is simulated. That makes this the right instrument for
    /// every question the <em>server</em> answers — is another player replicated, is
    /// a shot arbitrated, is a death, does a reconnect restore state — and the wrong
    /// instrument for every question the <em>engine</em> answers, which still needs a
    /// person looking at a screen.
    /// </para>
    /// </summary>
    public static class Program
    {
        private const double StepSeconds = 1d / 60d;

        public static int Main(string[] args)
        {
            var options = BotOptions.Parse(args, out string? error);
            if (error != null)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(BotOptions.Usage);
                return 2;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(BotOptions.Usage);
                return 0;
            }

            var runners = new List<BotRunner>();
            for (int i = 0; i < options.Count; i++)
            {
                string name = options.Count == 1 ? options.Name : $"{options.Name}{i + 1}";
                runners.Add(new BotRunner(name, options, i));
            }

            Console.WriteLine($"GTAMP bot — {runners.Count} шт., сервер {options.Host}:{options.Port}");
            Console.WriteLine($"Задачи: {string.Join(" -> ", options.TaskNames)}");
            Console.WriteLine();

            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };

            var clock = Stopwatch.StartNew();
            double simulated = 0d;
            double lastReport = 0d;

            while (!cancel.IsCancellationRequested)
            {
                double real = clock.Elapsed.TotalSeconds;
                while (simulated + StepSeconds <= real)
                {
                    simulated += StepSeconds;
                    foreach (BotRunner runner in runners)
                    {
                        runner.Step(simulated, StepSeconds);
                    }
                }

                if (simulated - lastReport >= 5d)
                {
                    lastReport = simulated;
                    foreach (BotRunner runner in runners)
                    {
                        runner.ReportProgress();
                    }
                }

                bool allDone = true;
                foreach (BotRunner runner in runners)
                {
                    allDone &= runner.Finished;
                }

                if (allDone)
                {
                    break;
                }

                Thread.Sleep(5);
            }

            Console.WriteLine();
            int failures = 0;
            foreach (BotRunner runner in runners)
            {
                failures += runner.PrintSummary();
                runner.Dispose();
            }

            Console.WriteLine(failures == 0
                ? "Ни одна задача не провалилась."
                : $"Провалено задач: {failures}. Смотрите строки FAIL выше.");

            return failures == 0 ? 0 : 1;
        }
    }

    public sealed class BotOptions
    {
        public const string Usage = """
Использование: Gtamp.Bot [ключи]

  --server <хост:порт>   куда подключаться (по умолчанию 127.0.0.1:27015)
  --name <имя>           имя бота (по умолчанию Bot). При --count больше 1
                         к имени добавляется номер: Bot1, Bot2, ...
  --count <N>            сколько ботов запустить в одном процессе (по умолчанию 1)
  --task <a,b,c>         какие задачи и в каком порядке (по умолчанию все)
  --at <x,y,z>           где появиться (по умолчанию точка спавна сервера)
  --password <пароль>    пароль сервера, если он задан
  --identities <папка>   где хранить ключи ботов, чтобы сервер узнавал их
                         между запусками (по умолчанию ./bot-identities)
  --verbose              печатать весь клиентский лог, а не только события бота
  --help                 эта справка

Задачи:
  stand      стоять на месте и оставаться видимым
  patrol     ходить квадратом, меняя походку
  drive      взять машину и проехать маршрут
  follow     идти за живым игроком и не терять его
  shoot      стрелять в живого игрока и заявлять попадания
  die        умереть и дождаться респавна
  reconnect  переподключиться и проверить, что сервер помнит

Примеры:
  Gtamp.Bot --task follow --name Напарник
  Gtamp.Bot --count 10 --task patrol      (нагрузка: десять игроков сразу)
""";

        public string Host { get; private set; } = "127.0.0.1";

        public int Port { get; private set; } = 27015;

        public string Name { get; private set; } = "Bot";

        public int Count { get; private set; } = 1;

        public string Password { get; private set; } = string.Empty;

        public string IdentityDirectory { get; private set; } =
            Path.Combine(Directory.GetCurrentDirectory(), "bot-identities");

        public float[] Spawn { get; private set; } = { 215.0f, -810.0f, 30.7f };

        public bool Verbose { get; private set; }

        public bool ShowHelp { get; private set; }

        public List<string> TaskNames { get; } = new List<string>();

        public static BotOptions Parse(string[] args, out string? error)
        {
            var options = new BotOptions();
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                string? Next()
                {
                    return i + 1 < args.Length ? args[++i] : null;
                }

                switch (key)
                {
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        return options;

                    case "--verbose":
                        options.Verbose = true;
                        break;

                    case "--server":
                    {
                        string? value = Next();
                        if (value == null)
                        {
                            error = "--server требует значение вида хост:порт";
                            return options;
                        }

                        int colon = value.LastIndexOf(':');
                        if (colon <= 0 || !int.TryParse(
                                value.Substring(colon + 1), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int port))
                        {
                            error = $"не разобрал адрес сервера: {value}";
                            return options;
                        }

                        options.Host = value.Substring(0, colon);
                        options.Port = port;
                        break;
                    }

                    case "--name":
                    {
                        string? value = Next();
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--name требует значение";
                            return options;
                        }

                        options.Name = value!;
                        break;
                    }

                    case "--password":
                        options.Password = Next() ?? string.Empty;
                        break;

                    case "--identities":
                    {
                        string? value = Next();
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--identities требует путь";
                            return options;
                        }

                        options.IdentityDirectory = value!;
                        break;
                    }

                    case "--count":
                    {
                        if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                            || count < 1 || count > 64)
                        {
                            error = "--count должен быть числом от 1 до 64";
                            return options;
                        }

                        options.Count = count;
                        break;
                    }

                    case "--at":
                    {
                        string[] parts = (Next() ?? string.Empty).Split(',');
                        if (parts.Length != 3
                            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        {
                            error = "--at требует три числа через запятую, например --at 100,-200,30";
                            return options;
                        }

                        options.Spawn = new[] { x, y, z };
                        break;
                    }

                    case "--task":
                    {
                        string? value = Next();
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--task требует список задач через запятую";
                            return options;
                        }

                        foreach (string name in value!.Split(','))
                        {
                            string trimmed = name.Trim();
                            if (trimmed.Length == 0)
                            {
                                continue;
                            }

                            if (!BotTask.TryResolve(trimmed, out _))
                            {
                                error = $"нет такой задачи: {trimmed}";
                                return options;
                            }

                            options.TaskNames.Add(trimmed);
                        }

                        break;
                    }

                    default:
                        error = $"неизвестный ключ: {key}";
                        return options;
                }
            }

            if (options.TaskNames.Count == 0)
            {
                foreach (BotTask task in BotTask.All())
                {
                    options.TaskNames.Add(task.Name);
                }
            }

            return options;
        }
    }
}
