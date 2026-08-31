using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Client.Network;

namespace Gtamp.Client.Diagnostics
{
    public enum CheckStatus : byte
    {
        Pass,
        Warn,
        Fail,
    }

    public readonly struct DiagnosticCheck
    {
        public DiagnosticCheck(string name, CheckStatus status, string detail)
        {
            Name = name;
            Status = status;
            Detail = detail;
        }

        public string Name { get; }

        public CheckStatus Status { get; }

        public string Detail { get; }

        public string Mark => Status switch
        {
            CheckStatus.Pass => "✓",
            CheckStatus.Warn => "⚠",
            _ => "✗",
        };
    }

    /// <summary>
    /// The /diagnostics command from master prompt section 48. It reports what is
    /// installed and what state the session is in; an optional component that is
    /// absent is a warning, never a failure, because every optional component in
    /// this framework is genuinely optional.
    /// </summary>
    public static class DiagnosticsRunner
    {
        public static List<DiagnosticCheck> Run(MultiplayerClient client)
        {
            var checks = new List<DiagnosticCheck>();
            ModEnvironment environment = client.Environment;

            checks.Add(new DiagnosticCheck("GTA V", CheckStatus.Pass, client.Bridge.GameVersion));
            checks.Add(new DiagnosticCheck("Multiplayer", CheckStatus.Pass, $"client {client.ClientVersion}"));

            checks.Add(environment.ScriptHookV
                ? new DiagnosticCheck("ScriptHookV", CheckStatus.Pass, "installed")
                : new DiagnosticCheck("ScriptHookV", CheckStatus.Fail, "not found — the client cannot run without it"));

            checks.Add(environment.ScriptHookVDotNet
                ? new DiagnosticCheck("ScriptHookVDotNet", CheckStatus.Pass, environment.ScriptHookVDotNetVersion)
                : new DiagnosticCheck("ScriptHookVDotNet", CheckStatus.Fail, "not found — the client cannot run without it"));

            checks.Add(environment.RagePluginHook
                ? new DiagnosticCheck("RAGE Plugin Hook", CheckStatus.Pass, environment.RagePluginHookVersion)
                : new DiagnosticCheck("RAGE Plugin Hook", CheckStatus.Warn, "not installed (optional)"));

            checks.Add(environment.Lspdfr
                ? new DiagnosticCheck("LSPDFR", CheckStatus.Pass, environment.LspdfrVersion)
                : new DiagnosticCheck("LSPDFR", CheckStatus.Warn, "not installed (optional)"));

            checks.Add(new DiagnosticCheck(
                "Mods",
                CheckStatus.Pass,
                $"{environment.Mods.Count} detected " +
                $"({environment.AsiPlugins.Count} ASI, {environment.Scripts.Count} scripts, {environment.LspdfrPlugins.Count} LSPDFR plugins)"));

            // Reported as a warning, never a pass with a count: a player whose friend's
            // car is invisible needs this line to be the one that stands out.
            checks.Add(client.MissingContent.IsEmpty
                ? new DiagnosticCheck("Mod content", CheckStatus.Pass, "every replicated model resolved")
                : new DiagnosticCheck(
                    "Mod content",
                    CheckStatus.Warn,
                    $"{client.MissingContent.Count} model(s) not installed here: " +
                    string.Join(", ", new List<string>(client.MissingContent.Describe()).ToArray())));

            checks.Add(client.Adapters.Failed.Count == 0
                ? new DiagnosticCheck("Adapters", CheckStatus.Pass, $"{client.Adapters.Active.Count} active, {client.Adapters.Skipped.Count} inactive")
                : new DiagnosticCheck("Adapters", CheckStatus.Warn, $"{client.Adapters.Failed.Count} failed to load: {string.Join(", ", new List<string>(client.Adapters.Failed).ToArray())}"));

            checks.Add(client.Connection.State switch
            {
                ClientConnectionState.Connected => new DiagnosticCheck(
                    "Server",
                    CheckStatus.Pass,
                    $"{client.Connection.Accept?.ServerName} at {client.Connection.ServerEndPoint}"),
                ClientConnectionState.Connecting => new DiagnosticCheck("Server", CheckStatus.Warn, "connecting..."),
                ClientConnectionState.Failed => new DiagnosticCheck("Server", CheckStatus.Fail, client.Connection.LastError),
                _ => new DiagnosticCheck("Server", CheckStatus.Warn, "not connected"),
            });

            if (client.Connection.Peer != null)
            {
                int ping = client.Connection.Peer.Stats.PingMilliseconds;
                double loss = client.Connection.Peer.Stats.PacketLoss;
                CheckStatus status = ping > 250 || loss > 0.1 ? CheckStatus.Warn : CheckStatus.Pass;
                checks.Add(new DiagnosticCheck("Network", status, $"{ping} ms, {loss * 100:0.0}% loss"));
            }
            else
            {
                checks.Add(new DiagnosticCheck("Network", CheckStatus.Warn, "no active session"));
            }

            checks.Add(new DiagnosticCheck(
                "Entity schema",
                CheckStatus.Pass,
                $"0x{client.Registry.ComputeSchemaHash():X8}"));

            checks.Add(Directory.Exists(environment.GameDirectory)
                ? new DiagnosticCheck("Game directory", CheckStatus.Pass, environment.GameDirectory)
                : new DiagnosticCheck("Game directory", CheckStatus.Fail, $"'{environment.GameDirectory}' does not exist"));

            return checks;
        }

        public static string Format(List<DiagnosticCheck> checks)
        {
            var builder = new StringBuilder();
            builder.AppendLine("=== DIAGNOSTICS ===");
            foreach (DiagnosticCheck check in checks)
            {
                builder.AppendLine($"{check.Mark} {check.Name,-20} {check.Detail}");
            }

            builder.Append("=== END DIAGNOSTICS ===");
            return builder.ToString();
        }
    }
}
