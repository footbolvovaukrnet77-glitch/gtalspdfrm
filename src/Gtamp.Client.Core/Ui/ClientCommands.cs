using System;
using System.Globalization;
using System.Text;
using Gtamp.Client.Core;
using Gtamp.Client.Diagnostics;
using Gtamp.Client.Mods;
using Gtamp.Client.Players;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;

namespace Gtamp.Client.Ui
{
    /// <summary>
    /// Wires the console's command table up to a live client. Kept separate from
    /// <see cref="DeveloperConsole"/> so the console itself stays a pure UI model
    /// with no knowledge of networking.
    /// </summary>
    public static class ClientCommands
    {
        public static void Register(MultiplayerClient client)
        {
            DeveloperConsole console = client.Console;

            console.RegisterCommand(new ConsoleCommand(
                "connect",
                "connect [host] [port]",
                "connect to a server; defaults come from the config file",
                context =>
                {
                    string host = context.Arguments.Count > 0 ? context.Argument(0) : client.Config.ServerAddress;
                    int port = client.Config.ServerPort;
                    if (context.Arguments.Count > 1 && int.TryParse(context.Argument(1), out int parsed))
                    {
                        port = parsed;
                    }

                    client.Connect(host, port);
                    return $"Connecting to {host}:{port}...";
                }));

            console.RegisterCommand(new ConsoleCommand(
                "disconnect",
                "disconnect",
                "leave the current server",
                _ =>
                {
                    client.Disconnect("player used the console");
                    return "Disconnected.";
                }));

            console.RegisterCommand(new ConsoleCommand(
                "status",
                "status",
                "connection, world and replication summary",
                _ => Status(client)));

            console.RegisterCommand(new ConsoleCommand(
                "players",
                "players",
                "list the players in the replicated world",
                _ => PlayersList(client)));

            console.RegisterCommand(new ConsoleCommand(
                "entity",
                "entity <id>",
                "inspect one entity, server state against local state",
                context => Entity(client, context)));

            console.RegisterCommand(new ConsoleCommand(
                "net",
                "net",
                "network debugger: ping, loss, bandwidth, snapshots",
                _ => Network(client)));

            console.RegisterCommand(new ConsoleCommand(
                "admin",
                "admin <command...>",
                "run a server command, if the server lets you",
                context => Admin(client, context.RawArguments)));

            console.RegisterCommand(new ConsoleCommand(
                "mods",
                "mods",
                "list detected mods and adapter status",
                _ => Mods(client)));

            console.RegisterCommand(new ConsoleCommand(
                "diagnostics",
                "diagnostics",
                "check the installation and the current session",
                _ => DiagnosticsRunner.Format(DiagnosticsRunner.Run(client))));

            console.RegisterCommand(new ConsoleCommand(
                "report",
                "report <what went wrong>",
                "build a bug report and copy it to the clipboard",
                context =>
                {
                    string text = BugReportBuilder.Build(client, context.RawArguments);
                    console.Clipboard.SetText(text);
                    return text + Environment.NewLine + "(copied to the clipboard)";
                }));

            console.RegisterCommand(new ConsoleCommand(
                "say",
                "say <text>",
                "send a chat message",
                context =>
                {
                    if (context.RawArguments.Length == 0)
                    {
                        return "Usage: say <text>";
                    }

                    if (!client.IsConnected)
                    {
                        return "Not connected.";
                    }

                    var chat = new Shared.Protocol.ChatMessage
                    {
                        PlayerId = client.LocalPlayerId,
                        SenderName = client.Config.PlayerName,
                        Text = context.RawArguments,
                    };

                    client.Connection.Peer!.Send(
                        Shared.Protocol.NetMessageType.ChatMessage, chat.Serialize(), DeliveryMethod.ReliableOrdered);

                    return string.Empty;
                }));

            console.RegisterCommand(new ConsoleCommand(
                "dev",
                "dev [on|off]",
                "toggle developer mode, which unlocks the developer commands",
                context =>
                {
                    if (context.Arguments.Count > 0)
                    {
                        console.DeveloperMode = context.Argument(0).Equals("on", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        console.DeveloperMode = !console.DeveloperMode;
                    }

                    return $"Developer mode is {(console.DeveloperMode ? "ON" : "OFF")}.";
                }));

            // --- developer-only ------------------------------------------------
            console.RegisterCommand(new ConsoleCommand(
                "resync",
                "resync",
                "throw away the replicated world and ask for a full snapshot",
                _ =>
                {
                    if (!client.IsConnected)
                    {
                        return "Not connected.";
                    }

                    var request = new Shared.Protocol.ResyncRequestMessage
                    {
                        Reason = "requested from the developer console",
                        LastAppliedSnapshotId = client.ReplicatedWorld.LastAppliedSnapshotId,
                    };

                    client.Connection.Peer!.Send(
                        Shared.Protocol.NetMessageType.ResyncRequest, request.Serialize(), DeliveryMethod.ReliableOrdered);

                    client.ReplicatedWorld.Reset();
                    client.RemotePlayers.Clear();
                    return "Resync requested.";
                },
                developerOnly: true));

            console.RegisterCommand(new ConsoleCommand(
                "reload",
                "reload <config>",
                "reload the client configuration from disk",
                context => context.Argument(0).ToLowerInvariant() switch
                {
                    "config" => "Reloading the config requires a restart of the script in this build; " +
                                "the host reloads it on script reinitialise. See docs/ROADMAP.md (Phase 11).",
                    _ => "Usage: reload config",
                },
                developerOnly: true));

            console.RegisterCommand(new ConsoleCommand(
                "schema",
                "schema",
                "list registered entity types and their replicated fields",
                _ => Schema(client),
                developerOnly: true));
        }

        private static string Status(MultiplayerClient client)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"connection      {client.Connection.State}");
            builder.AppendLine($"server          {client.Connection.ServerEndPoint?.ToString() ?? "(none)"}");
            builder.AppendLine($"player          {client.Config.PlayerName} (id {client.LocalPlayerId}, entity {client.LocalEntityId})");
            builder.AppendLine($"entities        {client.ReplicatedWorld.EntityCount}");
            builder.AppendLine($"remote players  {client.RemotePlayers.Count}");
            builder.AppendLine($"snapshot        {client.ReplicatedWorld.LastAppliedSnapshotId} " +
                               $"({client.ReplicatedWorld.SnapshotsApplied} applied, {client.ReplicatedWorld.SnapshotsDropped} dropped)");
            builder.AppendLine($"server time     {client.ReplicatedWorld.ServerTime:0.00}");
            builder.Append($"world clock     {client.ReplicatedWorld.Environment.Hours:00}:{client.ReplicatedWorld.Environment.Minutes:00}");
            return builder.ToString();
        }

        private static string PlayersList(MultiplayerClient client)
        {
            var builder = new StringBuilder();
            builder.AppendLine("  entity   playerId  name                 health  position");
            foreach (PlayerEntity player in AllPlayers(client))
            {
                bool isLocal = player.Id == client.LocalEntityId;
                builder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,-8} {1,-9} {2,-20} {3,-7} {4}{5}",
                    player.Id,
                    player.PlayerId,
                    player.Name,
                    player.Health,
                    player.Position,
                    isLocal ? "  (you)" : string.Empty));
            }

            return builder.ToString().TrimEnd();
        }

        private static System.Collections.Generic.IEnumerable<PlayerEntity> AllPlayers(MultiplayerClient client)
        {
            foreach (NetEntity entity in client.ReplicatedWorld.Current.Entities)
            {
                if (entity is PlayerEntity player)
                {
                    yield return player;
                }
            }
        }

        private static string Entity(MultiplayerClient client, ConsoleCommandContext context)
        {
            if (!context.TryUInt(0, out uint id))
            {
                return "Usage: entity <id>";
            }

            var entityId = new EntityId(id);
            if (!client.ReplicatedWorld.TryGet(entityId, out NetEntity entity))
            {
                return $"No entity {entityId} in the replicated world.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Entity {entity.Id} ({entity.Type})");
            builder.AppendLine($"  owner           {entity.OwnerId}");
            builder.AppendLine($"  position        {entity.Position}");
            builder.AppendLine($"  velocity        {entity.Velocity}");
            builder.AppendLine($"  heading         {entity.Heading:0.##}");
            builder.AppendLine($"  dimension       {entity.Dimension}");
            builder.AppendLine($"  interior        {entity.InteriorId}");
            builder.AppendLine($"  networkVersion  {entity.NetworkVersion}");
            builder.AppendLine($"  lastUpdateTick  {entity.LastUpdateTick}");

            if (entity is PlayerEntity player)
            {
                builder.AppendLine($"  name            {player.Name}");
                builder.AppendLine($"  health/armor    {player.Health}/{player.MaxHealth}, {player.Armor}");
                builder.AppendLine($"  flags           {player.Flags}");
                builder.AppendLine($"  movement        {player.Movement}");
                builder.AppendLine($"  weapon          0x{player.CurrentWeaponHash:X8} ammo {player.Ammo}");
            }

            foreach (var pair in entity.CustomData)
            {
                builder.AppendLine($"  custom[{pair.Key}] = {pair.Value}");
            }

            if (client.RemotePlayers.TryGet(entityId, out RemotePlayer remote))
            {
                builder.AppendLine($"  pedHandle       {remote.PedHandle}");
                builder.AppendLine($"  samples         {remote.SampleCount} (newest at t={remote.NewestSampleTime:0.00})");
            }

            builder.Append(
                "  NOTE: this is the replicated (server-authoritative) state. " +
                "Local game state is compared against it in Phase 11's inspector.");

            return builder.ToString();
        }

        private static string Network(MultiplayerClient client)
        {
            NetStats? stats = client.Connection.Peer?.Stats;
            if (stats == null)
            {
                return "No active session.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"  ping            {stats.PingMilliseconds} ms (rtt {stats.RoundTripTime * 1000:0.0} ms, var {stats.RoundTripVariance * 1000:0.0} ms)");
            builder.AppendLine($"  packet loss     {stats.PacketLoss * 100:0.00}%");
            builder.AppendLine($"  packets         {stats.PacketsSent} sent / {stats.PacketsReceived} received / {stats.PacketsLost} lost");
            builder.AppendLine($"  bytes           {stats.BytesSent} sent / {stats.BytesReceived} received");
            builder.AppendLine($"  retransmits     {stats.ReliableRetransmits}");
            builder.AppendLine($"  unacked         {client.Connection.Peer!.UnackedReliableCount} reliable message(s)");
            builder.AppendLine($"  snapshots       {client.ReplicatedWorld.SnapshotsApplied} applied / {client.ReplicatedWorld.SnapshotsDropped} dropped");
            builder.Append($"  resyncs         {client.ResyncsRequested}");
            return builder.ToString();
        }

        /// <summary>
        /// Forwards a command to the server. Deliberately not gated behind developer
        /// mode: developer mode is a local switch and gates nothing an attacker could
        /// not flip. The only gate that means anything is the server's, and it is the
        /// server that applies it.
        /// </summary>
        private static string Admin(MultiplayerClient client, string rawArguments)
        {
            string line = rawArguments?.Trim() ?? string.Empty;
            if (line.Length == 0)
            {
                return "Usage: admin <command...>   e.g. admin players, admin kick 3, admin ban 3 60 griefing";
            }

            if (!client.SendAdminCommand(line))
            {
                return "Not connected to a server.";
            }

            // The answer arrives as a security notice and is printed by the log, so
            // the reply here says only that the request left. Pretending to have an
            // answer that has not arrived is worse than saying nothing.
            return $"Sent '{line}' to the server.";
        }

        private static string Mods(MultiplayerClient client)
        {
            ModEnvironment environment = client.Environment;
            var builder = new StringBuilder();
            builder.AppendLine($"game directory  {environment.GameDirectory}");
            builder.AppendLine($"ScriptHookV     {(environment.ScriptHookV ? "yes" : "no")}");
            builder.AppendLine($"SHVDN           {(environment.ScriptHookVDotNet ? environment.ScriptHookVDotNetVersion : "no")}");
            builder.AppendLine($"RAGE Plugin Hook{(environment.RagePluginHook ? " " + environment.RagePluginHookVersion : " no")}");
            builder.AppendLine($"LSPDFR          {(environment.Lspdfr ? environment.LspdfrVersion : "no")}");
            builder.AppendLine($"adapters        {client.Adapters.Active.Count} active, {client.Adapters.Skipped.Count} inactive, {client.Adapters.Failed.Count} failed");
            if (!client.MissingContent.IsEmpty)
            {
                builder.AppendLine($"missing content  {client.MissingContent.Count} model(s) this client cannot resolve:");
                foreach (string line in client.MissingContent.Describe())
                {
                    builder.AppendLine("  " + line);
                }
            }

            builder.AppendLine("detected mods:");
            foreach (ModDescriptor mod in environment.Mods)
            {
                builder.AppendLine($"  {mod.Requirement,-11} {mod.Id,-40} {mod.Version}");
            }

            return builder.ToString().TrimEnd();
        }

        private static string Schema(MultiplayerClient client)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"schema hash 0x{client.Registry.ComputeSchemaHash():X8}");
            foreach (INetEntitySerializer serializer in client.Registry.Serializers)
            {
                builder.AppendLine($"  [{serializer.TypeId}] {serializer.TypeName}");
                foreach (string field in serializer.FieldNames)
                {
                    builder.AppendLine($"        {field}");
                }
            }

            return builder.ToString().TrimEnd();
        }
    }
}
