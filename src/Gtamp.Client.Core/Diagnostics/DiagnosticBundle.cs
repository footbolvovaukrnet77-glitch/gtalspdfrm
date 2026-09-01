using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Gtamp.Client.Core;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Diagnostics
{
    public sealed class BundleResult
    {
        public bool Success { get; set; }

        public string Directory { get; set; } = string.Empty;

        public List<string> Files { get; } = new List<string>();

        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Writes everything needed to diagnose a problem into one folder the player can
    /// attach to a report.
    /// <para>
    /// <b>Nothing is sent anywhere.</b> Master prompt section 47 is explicit that
    /// crash data must not leave the machine without the user's permission, and the
    /// permission model here is the simplest one that cannot be got wrong: the
    /// framework writes files, the player decides what to do with them. There is no
    /// upload path in this code at all, so there is nothing to accidentally enable.
    /// </para>
    /// <para>
    /// <b>The secret is redacted, and that is not optional.</b> Since identity became
    /// a keypair, <c>client.ini</c> holds a private key. A bundle that copied it
    /// verbatim would turn "here is my bug report" into "here is my identity" — the
    /// player would be handing over their character to whoever reads the thread. The
    /// line is replaced with a marker rather than dropped, so the file still shows
    /// that a key exists and is well-formed.
    /// </para>
    /// </summary>
    public static class DiagnosticBundle
    {
        /// <summary>Keys whose values never leave the machine.</summary>
        private static readonly string[] SecretKeys = { "identitysecret", "serverpassword" };

        public const int LogLinesIncluded = 500;

        public static BundleResult Write(MultiplayerClient client, string description, string rootDirectory)
        {
            var result = new BundleResult();

            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string directory = Path.Combine(rootDirectory, "bundle-" + stamp);
                Directory.CreateDirectory(directory);
                result.Directory = directory;

                Write(result, directory, "report.txt", BugReportBuilder.Build(client, description));
                Write(result, directory, "diagnostics.txt", DiagnosticsRunner.Format(DiagnosticsRunner.Run(client)));
                Write(result, directory, "network.txt", NetworkOverlay.Format(NetworkOverlay.Build(client)));
                Write(result, directory, "log.txt", RecentLog(client));
                Write(result, directory, "client.ini.redacted", Redact(client.Config));
                Write(result, directory, "README.txt", Readme(directory));

                result.Success = true;
                return result;
            }
            catch (Exception exception)
            {
                // A bundle that cannot be written must say why. Throwing here would
                // take down the console command a player ran precisely because
                // something was already wrong.
                result.Error = exception.Message;
                return result;
            }
        }

        /// <summary>
        /// Renders the client configuration with every secret replaced. Works from the
        /// live configuration rather than by copying the file, so a key that is only
        /// in memory — or a file the process cannot read — cannot leak by accident and
        /// cannot silently produce an empty bundle either.
        /// </summary>
        public static string Redact(ClientConfig config)
        {
            var builder = new StringBuilder();
            builder.AppendLine("; Redacted copy of client.ini. Secrets are replaced, not removed,");
            builder.AppendLine("; so their presence and shape are still visible.");
            builder.AppendLine("[client]");
            builder.AppendLine($"PlayerName={config.PlayerName}");
            builder.AppendLine($"ServerAddress={config.ServerAddress}");
            builder.AppendLine($"ServerPort={config.ServerPort.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"ServerPassword={Mask(config.ServerPassword)}");
            builder.AppendLine($"IdentityToken={config.IdentityToken}");
            builder.AppendLine($"IdentitySecret={Mask(config.IdentitySecret)}");
            builder.AppendLine($"ConsoleKey={config.ConsoleKey.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"InterpolationDelay={config.InterpolationDelay.ToString("0.###", CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CorrectionThreshold={config.CorrectionThreshold.ToString("0.###", CultureInfo.InvariantCulture)}");
            builder.AppendLine($"HealthCorrectionThreshold={config.HealthCorrectionThreshold.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"ShowNetworkOverlay={config.ShowNetworkOverlay}");
            builder.AppendLine($"ShowPlayerBlips={config.ShowPlayerBlips}");
            builder.AppendLine($"ShowPlayerNames={config.ShowPlayerNames}");
            builder.AppendLine($"VerboseLogging={config.VerboseLogging}");
            builder.Append($"AutoConnectOnStart={config.AutoConnectOnStart}");
            return builder.ToString();
        }

        /// <summary>True when a line from an INI file would expose a secret.</summary>
        public static bool IsSecretLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            string key = line.Substring(0, separator).Trim().ToLowerInvariant();
            foreach (string secret in SecretKeys)
            {
                if (key == secret)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Mask(string value) =>
            string.IsNullOrEmpty(value) ? "(not set)" : $"(redacted, {value.Length} characters)";

        private static string RecentLog(MultiplayerClient client)
        {
            var builder = new StringBuilder();
            List<LogEntry> entries = client.Console.FilteredEntries();
            int from = Math.Max(0, entries.Count - LogLinesIncluded);

            for (int i = from; i < entries.Count; i++)
            {
                builder.AppendLine(entries[i].FormatLine());
            }

            return builder.Length == 0 ? "(no log entries)" : builder.ToString().TrimEnd();
        }

        private static string Readme(string directory) => string.Join(
            Environment.NewLine,
            "GTAMP diagnostic bundle",
            "=======================",
            string.Empty,
            "Written by the in-game console command 'bundle'. Nothing here has been",
            "sent anywhere: this folder exists on your machine and nowhere else.",
            string.Empty,
            "  report.txt           the full bug report",
            "  diagnostics.txt      installation and session checks",
            "  network.txt          the network readout at the moment of writing",
            "  log.txt              the last " + LogLinesIncluded + " client log lines",
            "  client.ini.redacted  your configuration with secrets replaced",
            string.Empty,
            "Before sharing it: client.ini.redacted has already had your identity",
            "secret and server password removed. Do NOT attach the real client.ini —",
            "its IdentitySecret is the private key that proves you are you, and",
            "anyone who has it can play as you and take your character.",
            string.Empty,
            "Folder: " + directory);

        private static void Write(BundleResult result, string directory, string name, string content)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, content);
            result.Files.Add(name);
        }
    }
}
