using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Turns one noticed problem into a folder somebody else can read.
    /// <para>
    /// The record leads with what happened and what was going on around it,
    /// because the first question on opening one of these is always "what was the
    /// game doing" and the answer is in the lines either side of the trigger, not
    /// in five hundred lines of context.
    /// </para>
    /// </summary>
    public sealed class IncidentWriter
    {
        private const int TailLines = 200;

        private readonly WatcherOptions _options;

        public IncidentWriter(WatcherOptions options)
        {
            _options = options;
        }

        public string Write(Incident incident, IReadOnlyList<string> recentContext, IReadOnlyList<string> watchedFiles)
        {
            string stamp = incident.NoticedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string folder = Path.Combine(_options.IncidentDirectory, $"{stamp}-{incident.Kind}");
            Directory.CreateDirectory(folder);

            var record = new StringBuilder();
            record.AppendLine("=== GTAMP: замеченный сбой ===");
            record.AppendLine();
            record.AppendLine($"КОГДА:      {incident.NoticedAt:yyyy-MM-dd HH:mm:ss} (местное время)");
            record.AppendLine($"ЧТО:        {incident.Kind} ({Severity(incident.Severity)})");
            record.AppendLine($"ЗНАЧИТ:     {incident.Why}");
            record.AppendLine($"ГДЕ НАШЁЛ:  {incident.Source}");
            record.AppendLine($"СБОРКА:     {_options.BuildDescription}");
            record.AppendLine();
            record.AppendLine("СТРОКА, КОТОРАЯ ЭТО ВЫЗВАЛА:");
            record.AppendLine("  " + incident.Line);
            record.AppendLine();

            record.AppendLine("ЧТО ПРОИСХОДИЛО РЯДОМ:");
            if (recentContext.Count == 0)
            {
                record.AppendLine("  (перед этим в логе ничего не было)");
            }
            else
            {
                foreach (string line in recentContext)
                {
                    record.AppendLine("  " + line);
                }
            }

            record.AppendLine();
            record.AppendLine("ФАЙЛЫ В ЭТОЙ ПАПКЕ:");

            var written = new List<string>();
            foreach (string file in watchedFiles)
            {
                if (Redactor.IsForbidden(file))
                {
                    continue;
                }

                IReadOnlyList<string> tail = LogTail.Tail(file, TailLines);
                if (tail.Count == 0)
                {
                    continue;
                }

                string name = Path.GetFileName(file);
                File.WriteAllLines(Path.Combine(folder, name), tail);
                written.Add($"  {name} — последние {tail.Count} строк");
            }

            string? bundle = CopyNewestBundle(folder);
            if (bundle != null)
            {
                written.Add("  bundle/ — диагностический пакет клиента, снятый рядом по времени");
            }

            if (_options.Screenshots)
            {
                string shot = Path.Combine(folder, "screen.png");
                if (ScreenCapture.TryCapture(shot, out string reason) != null)
                {
                    written.Add("  screen.png — снимок экрана в момент сбоя");
                }
                else
                {
                    written.Add("  screen.png — не снялся: " + reason);
                }
            }

            foreach (string line in written)
            {
                record.AppendLine(line);
            }

            record.AppendLine();
            record.AppendLine("ЧТО УЖЕ ВЫЧИЩЕНО:");
            record.AppendLine("  Ключ личности, пароль сервера, имя пользователя Windows в путях,");
            record.AppendLine("  и любые адреса кроме локальных — заменены на (redacted) и (address).");
            record.AppendLine("  Файлы, которые не копируются вообще:");
            foreach (string forbidden in Redactor.ForbiddenExamples())
            {
                record.AppendLine("    " + forbidden);
            }

            if (_options.Screenshots)
            {
                record.AppendLine();
                record.AppendLine("  Снимок экрана вычистить нельзя — на нём может быть видно имя игрока,");
                record.AppendLine("  адрес сервера в оверлее и всё остальное, что было на экране.");
            }

            File.WriteAllText(Path.Combine(folder, "ЧТО-СЛУЧИЛОСЬ.txt"), record.ToString(), Encoding.UTF8);
            return folder;
        }

        /// <summary>
        /// Copies the client's own bundle when it was written near this incident.
        /// An older one describes a different session and would mislead.
        /// </summary>
        private string? CopyNewestBundle(string into)
        {
            try
            {
                string logs = Path.Combine(_options.GameDirectory, "Gtamp", "logs");
                if (!Directory.Exists(logs))
                {
                    return null;
                }

                DirectoryInfo? newest = null;
                foreach (string candidate in Directory.GetDirectories(logs, "bundle-*"))
                {
                    var info = new DirectoryInfo(candidate);
                    if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = info;
                    }
                }

                if (newest == null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromMinutes(10))
                {
                    return null;
                }

                string target = Path.Combine(into, "bundle");
                Directory.CreateDirectory(target);
                foreach (string file in newest.GetFiles().ConvertAllPaths())
                {
                    if (Redactor.IsForbidden(file))
                    {
                        continue;
                    }

                    File.WriteAllText(
                        Path.Combine(target, Path.GetFileName(file)),
                        Redactor.Scrub(File.ReadAllText(file)),
                        Encoding.UTF8);
                }

                return target;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Severity(IncidentSeverity severity) => severity switch
        {
            IncidentSeverity.Fatal => "сессия оборвалась",
            IncidentSeverity.Problem => "игрок бы это заметил",
            _ => "к сведению",
        };
    }

    internal static class FileInfoExtensions
    {
        public static IEnumerable<string> ConvertAllPaths(this FileInfo[] files)
        {
            foreach (FileInfo file in files)
            {
                yield return file.FullName;
            }
        }
    }
}
