using System;
using System.Globalization;
using System.Text;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;

namespace Gtamp.Client.Diagnostics
{
    /// <summary>
    /// Produces the plain-text bug report from master prompt section 43. The exact
    /// layout matters: it is meant to be copied out of the F8 console and pasted
    /// straight into a bug tracker or into Claude Code, so it must be readable with
    /// no post-processing and must carry enough context to diagnose without a
    /// follow-up question.
    /// </summary>
    public static class BugReportBuilder
    {
        public const int RecentLogLines = 40;
        public const int RecentErrorLines = 10;

        public static string Build(MultiplayerClient client, string description, string severity = "MEDIUM")
        {
            var builder = new StringBuilder(4096);
            builder.AppendLine("=== MULTIPLAYER BUG REPORT ===");
            builder.AppendLine();
            builder.AppendLine($"BUG_ID: {Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant()}");
            builder.AppendLine($"DATE: {DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)}");
            builder.AppendLine($"SEVERITY: {severity}");
            builder.AppendLine();
            builder.AppendLine($"SUBSYSTEM: {GuessSubsystem(client)}");
            builder.AppendLine($"GTA V VERSION: {client.Bridge.GameVersion}");
            builder.AppendLine($"MULTIPLAYER VERSION: {client.ClientVersion}");
            builder.AppendLine($"RPH VERSION: {Describe(client.Environment.RagePluginHook, client.Environment.RagePluginHookVersion)}");
            builder.AppendLine($"LSPDFR VERSION: {Describe(client.Environment.Lspdfr, client.Environment.LspdfrVersion)}");
            builder.AppendLine($"SCRIPTHOOKV: {(client.Environment.ScriptHookV ? "present" : "not installed")}");
            builder.AppendLine();

            builder.AppendLine("MODS:");
            if (client.Environment.Mods.Count == 0)
            {
                builder.AppendLine("  (none detected)");
            }
            else
            {
                foreach (ModDescriptor mod in client.Environment.Mods)
                {
                    builder.AppendLine($"  {mod.Id} {mod.Version}" + (mod.Hash.Length > 0 ? $" [{mod.Hash}]" : string.Empty));
                }
            }

            builder.AppendLine();
            builder.AppendLine("MISSING CONTENT:");
            if (client.MissingContent.IsEmpty)
            {
                builder.AppendLine("  (none — every replicated model resolved)");
            }
            else
            {
                // Near the top of the report on purpose. "The car is invisible" and
                // "the model is not installed" look like two different bugs until
                // these lines are in front of whoever is reading it.
                foreach (string line in client.MissingContent.Describe())
                {
                    builder.AppendLine("  " + line);
                }
            }

            builder.AppendLine();
            builder.AppendLine("DESCRIPTION:");
            builder.AppendLine("  " + (string.IsNullOrWhiteSpace(description) ? "(not provided)" : description.Trim()));
            builder.AppendLine();
            builder.AppendLine("STEPS TO REPRODUCE:");
            builder.AppendLine("  1. ");
            builder.AppendLine("  2. ");
            builder.AppendLine("  3. ");
            builder.AppendLine();
            builder.AppendLine("EXPECTED:");
            builder.AppendLine("  ");
            builder.AppendLine("ACTUAL:");
            builder.AppendLine("  " + (string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim()));
            builder.AppendLine();

            AppendPlayer(builder, client);
            AppendEntities(builder, client);
            AppendNetwork(builder, client);
            AppendSelfTest(builder, client);
            AppendStackTrace(builder, client);
            AppendRecentEvents(builder, client);
            AppendLogs(builder, client);

            builder.AppendLine("=== END REPORT ===");
            return builder.ToString();
        }

        private static void AppendPlayer(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("PLAYER:");
            builder.AppendLine($"  name        {client.Config.PlayerName}");
            builder.AppendLine($"  playerId    {client.LocalPlayerId}");
            builder.AppendLine($"  entityId    {client.LocalEntityId}");

            PlayerEntity? local = client.ReplicatedWorld.GetPlayer(client.LocalEntityId);
            builder.AppendLine($"  position    {local?.Position.ToString() ?? "(unknown)"}");
            builder.AppendLine($"  health      {local?.Health.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            builder.AppendLine($"  interior    {local?.InteriorId.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            builder.AppendLine();
        }

        private static void AppendEntities(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("ENTITY:");
            builder.AppendLine($"  replicated  {client.ReplicatedWorld.EntityCount}");
            builder.AppendLine($"  remotePeds  {client.RemotePlayers.Count}");

            int listed = 0;
            foreach (NetEntity entity in client.ReplicatedWorld.Current.Entities)
            {
                builder.AppendLine($"  {entity.Id,-8} {entity.Type,-8} owner={entity.OwnerId,-4} v{entity.NetworkVersion,-5} {entity.Position}");
                if (++listed >= 25)
                {
                    builder.AppendLine($"  ... and {client.ReplicatedWorld.EntityCount - listed} more");
                    break;
                }
            }

            builder.AppendLine();
        }

        private static void AppendNetwork(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("NETWORK:");
            builder.AppendLine($"  state           {client.Connection.State}");
            builder.AppendLine($"  server          {client.Connection.ServerEndPoint?.ToString() ?? "(none)"}");

            NetStats? stats = client.Connection.Peer?.Stats;
            if (stats == null)
            {
                builder.AppendLine("  (no active session)");
            }
            else
            {
                builder.AppendLine($"  ping            {stats.PingMilliseconds} ms");
                builder.AppendLine($"  packetLoss      {stats.PacketLoss * 100:0.00}%");
                builder.AppendLine($"  packets         {stats.PacketsSent} sent / {stats.PacketsReceived} received / {stats.PacketsLost} lost");
                builder.AppendLine($"  bytes           {stats.BytesSent} sent / {stats.BytesReceived} received");
                builder.AppendLine($"  retransmits     {stats.ReliableRetransmits}");
            }

            builder.AppendLine($"  snapshots       {client.ReplicatedWorld.SnapshotsApplied} applied / {client.ReplicatedWorld.SnapshotsDropped} dropped");
            builder.AppendLine($"  resyncs         {client.ResyncsRequested}");
            builder.AppendLine($"  shots           {client.ShotsFired} fired / {client.ShotsSeen} seen");
            builder.AppendLine($"  hits            {client.HitsReported} reported");
            builder.AppendLine();
        }

        /// <summary>
        /// What the running game answered about each replicated capability.
        /// <para>
        /// The most valuable thing a bug report from a real session can carry: the
        /// test suite proves the decisions and can prove nothing about the engine, so
        /// this is the only place the two meet.
        /// </para>
        /// </summary>
        private static void AppendSelfTest(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("SELF TEST");
            builder.AppendLine(BridgeSelfTest.Format(BridgeSelfTest.Run(client)));
            builder.AppendLine();
        }

        private static void AppendStackTrace(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("STACK TRACE:");
            LogEntry? problem = client.Console.LastProblem();
            if (problem?.Detail is { Length: > 0 })
            {
                foreach (string line in problem.Value.Detail!.Split('\n'))
                {
                    builder.AppendLine("  " + line.TrimEnd('\r'));
                }
            }
            else
            {
                builder.AppendLine("  (no exception captured)");
            }

            builder.AppendLine();
        }

        private static void AppendRecentEvents(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("RECENT EVENTS:");
            foreach (LogEntry entry in client.Console.RecentProblems(RecentErrorLines))
            {
                builder.AppendLine("  " + entry.FormatLine());
            }

            builder.AppendLine();
        }

        private static void AppendLogs(StringBuilder builder, MultiplayerClient client)
        {
            builder.AppendLine("LOGS:");
            foreach (LogEntry entry in client.Console.RecentEntries(RecentLogLines))
            {
                builder.AppendLine("  " + entry.FormatLine());
            }

            builder.AppendLine();
        }

        private static string GuessSubsystem(MultiplayerClient client)
        {
            LogEntry? problem = client.Console.LastProblem();
            return problem?.Category.ToString() ?? "Client";
        }

        private static string Describe(bool present, string version) =>
            present ? (string.IsNullOrEmpty(version) ? "present" : version) : "not installed";
    }
}
