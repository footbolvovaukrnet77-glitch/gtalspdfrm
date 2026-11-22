using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Gtamp.Watcher
{
    public sealed class WatcherOptions
    {
        public const string Usage = """
Использование: Gtamp.Watcher [ключи]

Смотрит за логами GTA V и GTAMP, и когда что-то ломается — записывает,
что именно, что происходило рядом и в каких файлах это видно.

  --game <путь>        папка GTA V (по умолчанию ищется сама)
  --repo <путь>        папка репозитория (по умолчанию текущая)
  --out <путь>         куда складывать записи (по умолчанию <repo>\diagnostics)
  --screenshot         снимать экран при сбое. Игра должна быть в оконном
                       режиме без рамки: в полноэкранном снимок выходит чёрным
  --publish            отправлять записи в репозиторий через git push
  --branch <имя>       ветка для записей (по умолчанию diagnostics)
  --remote <имя>       remote для push (по умолчанию origin)
  --public-ok          подтвердить, что репозиторий публичный и записи в нём
                       увидит кто угодно. Без этого --publish откажет
  --interval <сек>     как часто проверять логи (по умолчанию 2)
  --since-start        прочитать логи с начала, а не только то, что появится
  --rules              напечатать, что именно считается сбоем, и выйти
  --help               эта справка

Ничего никуда не уходит, пока не указан --publish. Без него записи просто
лежат на диске, и вы решаете, что с ними делать.
""";

        public string GameDirectory { get; private set; } = string.Empty;

        public string RepositoryDirectory { get; private set; } = Directory.GetCurrentDirectory();

        public string IncidentDirectory { get; private set; } = string.Empty;

        public string Branch { get; private set; } = "diagnostics";

        public string Remote { get; private set; } = "origin";

        public bool Screenshots { get; private set; }

        public bool Publish { get; private set; }

        public bool PublicAcknowledged { get; private set; }

        public bool SinceStart { get; private set; }

        public double IntervalSeconds { get; private set; } = 2d;

        public bool ShowHelp { get; private set; }

        public bool ShowRules { get; private set; }

        public string BuildDescription { get; set; } = "неизвестна";

        /// <summary>Every file the watcher follows, in the order it reports them.</summary>
        public IReadOnlyList<string> WatchedFiles()
        {
            var files = new List<string>();
            string gtamp = Path.Combine(GameDirectory, "Gtamp", "logs");

            if (Directory.Exists(gtamp))
            {
                // The client writes one file per day; the newest is this session's.
                string? newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (string candidate in Directory.GetFiles(gtamp, "client-*.log"))
                {
                    DateTime written = File.GetLastWriteTimeUtc(candidate);
                    if (written > newestTime)
                    {
                        newestTime = written;
                        newest = candidate;
                    }
                }

                if (newest != null)
                {
                    files.Add(newest);
                }

                string failure = Path.Combine(gtamp, "startup-failure.log");
                if (File.Exists(failure))
                {
                    files.Add(failure);
                }
            }

            foreach (string name in new[]
                     {
                         "ScriptHookV.log",
                         "ScriptHookVDotNet.log",
                         "RagePluginHook.log",
                         "asiloader.log",
                     })
            {
                string path = Path.Combine(GameDirectory, name);
                if (File.Exists(path))
                {
                    files.Add(path);
                }
            }

            return files;
        }

        public static WatcherOptions Parse(string[] args, out string? error)
        {
            var options = new WatcherOptions();
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (key)
                {
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        return options;

                    case "--rules":
                        options.ShowRules = true;
                        return options;

                    case "--screenshot":
                        options.Screenshots = true;
                        break;

                    case "--publish":
                        options.Publish = true;
                        break;

                    case "--public-ok":
                        options.PublicAcknowledged = true;
                        break;

                    case "--since-start":
                        options.SinceStart = true;
                        break;

                    case "--game":
                        options.GameDirectory = Next() ?? string.Empty;
                        break;

                    case "--repo":
                        options.RepositoryDirectory = Next() ?? options.RepositoryDirectory;
                        break;

                    case "--out":
                        options.IncidentDirectory = Next() ?? string.Empty;
                        break;

                    case "--branch":
                        options.Branch = Next() ?? options.Branch;
                        break;

                    case "--remote":
                        options.Remote = Next() ?? options.Remote;
                        break;

                    case "--interval":
                    {
                        if (!double.TryParse(
                                Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                            || seconds < 0.5d || seconds > 600d)
                        {
                            error = "--interval должен быть числом секунд от 0,5 до 600";
                            return options;
                        }

                        options.IntervalSeconds = seconds;
                        break;
                    }

                    default:
                        error = $"неизвестный ключ: {key}";
                        return options;
                }
            }

            if (options.GameDirectory.Length == 0)
            {
                options.GameDirectory = FindGame() ?? string.Empty;
                if (options.GameDirectory.Length == 0)
                {
                    error = "не нашёл папку GTA V — укажите её ключом --game";
                    return options;
                }
            }

            if (!Directory.Exists(options.GameDirectory))
            {
                error = $"папки нет: {options.GameDirectory}";
                return options;
            }

            if (options.IncidentDirectory.Length == 0)
            {
                options.IncidentDirectory = Path.Combine(options.RepositoryDirectory, "diagnostics");
            }

            if (options.Publish && !options.PublicAcknowledged
                && Publisher.RemoteLooksPublic(options.RepositoryDirectory, out string remote))
            {
                error =
                    $"--publish откажет: origin ({remote}) — публичный репозиторий на GitHub.\n" +
                    "Всё, что туда уйдёт, увидит кто угодно и навсегда: пути на вашей машине,\n" +
                    "список модов, имя игрока, адреса серверов. Секреты вычищаются, но остальное — нет.\n\n" +
                    "Если это устраивает — добавьте --public-ok.\n" +
                    "Если нет — не указывайте --publish: записи останутся лежать на диске,\n" +
                    "и вы отправите ровно то, что решите сами.";
                return options;
            }

            return options;
        }

        /// <summary>The usual install locations, checked in the order they are usual.</summary>
        private static string? FindGame()
        {
            foreach (string candidate in new[]
                     {
                         @"C:\Program Files\Rockstar Games\Grand Theft Auto V",
                         @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V",
                         @"C:\Program Files\Epic Games\GTAV",
                         @"D:\Games\Grand Theft Auto V",
                         @"E:\SteamLibrary\steamapps\common\Grand Theft Auto V",
                     })
            {
                if (File.Exists(Path.Combine(candidate, "GTA5.exe"))
                    || File.Exists(Path.Combine(candidate, "GTA5_Enhanced.exe")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
