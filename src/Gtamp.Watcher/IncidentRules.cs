using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gtamp.Watcher
{
    public enum IncidentSeverity
    {
        /// <summary>Worth recording; the session carries on.</summary>
        Notice = 0,

        /// <summary>Something the player would notice going wrong.</summary>
        Problem = 1,

        /// <summary>The session or the game ended.</summary>
        Fatal = 2,
    }

    public sealed class Incident
    {
        public Incident(string kind, IncidentSeverity severity, string source, string line, string why)
        {
            Kind = kind;
            Severity = severity;
            Source = source;
            Line = line;
            Why = why;
            NoticedAt = DateTime.Now;
        }

        /// <summary>Short slug, used in the folder name and the commit subject.</summary>
        public string Kind { get; }

        public IncidentSeverity Severity { get; }

        /// <summary>File the line came from.</summary>
        public string Source { get; }

        /// <summary>The line that tripped the rule, already redacted.</summary>
        public string Line { get; }

        /// <summary>What this means, in the words a person would use.</summary>
        public string Why { get; }

        public DateTime NoticedAt { get; }
    }

    /// <summary>
    /// What counts as something going wrong.
    /// <para>
    /// Every rule here names a defect this project has actually had, or a line the
    /// client prints precisely because somebody has to see it. That is the bar: a
    /// rule that fires on a healthy session trains the reader to ignore the file,
    /// and a file nobody reads is worse than no file.
    /// </para>
    /// </summary>
    public static class IncidentRules
    {
        private sealed class Rule
        {
            public Rule(string kind, IncidentSeverity severity, string pattern, string why)
            {
                Kind = kind;
                Severity = severity;
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Why = why;
            }

            public string Kind { get; }

            public IncidentSeverity Severity { get; }

            public Regex Pattern { get; }

            public string Why { get; }
        }

        private static readonly Rule[] All =
        {
            new Rule("timeout", IncidentSeverity.Fatal, @"Connection timed out",
                "Связь оборвалась. Клиент 15 секунд не получал от сервера ничего."),

            new Rule("cannot-deliver", IncidentSeverity.Fatal, @"The connection can no longer deliver",
                "Надёжный канал встал: сообщение, которое не дошло, блокирует всё за собой."),

            new Rule("resync", IncidentSeverity.Problem, @"Requesting a resync",
                "Клиент потерял baseline и попросил полное состояние мира заново."),

            new Rule("corrections-stuck", IncidentSeverity.Problem, @"correction.*not reaching the game|corrections are stuck",
                "Сервер возвращает игрока на место, а расхождение не уменьшается — коррекция не доезжает."),

            new Rule("script-aborted", IncidentSeverity.Fatal, @"Aborted script|Unhandled exception",
                "ScriptHookVDotNet снял скрипт с выполнения. Дальше клиент не работает."),

            // NOT the bare word "NativeMemory": that appears in every healthy
            // ScriptHookVDotNet log as "Initializing NativeMemory members...".
            // Measured across ten of the user's real logs, bare NativeMemory hit
            // all ten and TypeInitializationException hit only the three that had
            // actually failed. A rule that fires on a healthy session teaches the
            // reader to ignore the file.
            new Rule("shvdn-unusable", IncidentSeverity.Fatal,
                @"managed game API is unusable|TypeInitializationException",
                "SHVDN не понимает эту сборку игры — каждый вызов в мир бросает исключение."),

            new Rule("missing-content", IncidentSeverity.Problem, @"is not installed on this client",
                "Сервер прислал модель, которой на этом клиенте нет."),

            new Rule("kicked", IncidentSeverity.Fatal, @"\[SECURITY\].*(Kick|Ban)|kicked:",
                "Сервер выкинул игрока — сработал античит."),

            new Rule("client-error", IncidentSeverity.Problem, @"^\[[\d:.]+\]\s*\[ERROR\]",
                "Клиент записал ошибку."),

            new Rule("selftest-fail", IncidentSeverity.Problem, @"^\s*FAIL\s+\w",
                "Строка selftest перешла в FAIL — возможность, которая должна работать, не работает."),
        };

        /// <summary>
        /// Matches one log line. Returns null when the line is ordinary.
        /// </summary>
        public static Incident? Match(string source, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            foreach (Rule rule in All)
            {
                if (rule.Pattern.IsMatch(line))
                {
                    return new Incident(rule.Kind, rule.Severity, source, Redactor.Scrub(line.Trim()), rule.Why);
                }
            }

            return null;
        }

        public static IEnumerable<(string Kind, IncidentSeverity Severity, string Why)> Describe()
        {
            foreach (Rule rule in All)
            {
                yield return (rule.Kind, rule.Severity, rule.Why);
            }
        }
    }
}
