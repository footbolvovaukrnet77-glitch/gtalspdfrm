using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Gtamp.Server.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Server.Admin
{
    /// <summary>
    /// Command handling for the server's own stdin console. The same command table
    /// is what the in-game developer console forwards privileged commands to, so
    /// there is one implementation rather than two that drift apart.
    /// </summary>
    public sealed class AdminConsole
    {
        private readonly GameServer _server;

        public AdminConsole(GameServer server)
        {
            _server = server;
        }

        public bool StopRequested { get; private set; }

        public string Execute(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].TrimStart('/').ToLowerInvariant();
            string[] args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

            return command switch
            {
                "help" or "?" => Help(),
                "status" => Status(),
                "players" => PlayersList(),
                "entity" => Entity(args),
                "entities" => Entities(),
                "kick" => Kick(args),
                "say" => Say(args),
                "time" => Time(args),
                "weather" => Weather(args),
                "save" => Save(),
                "diagnostics" or "diag" => Diagnostics(),
                "net" => Network(),
                "stop" or "quit" or "exit" => Stop(),
                _ => $"Unknown command '{command}'. Type 'help'.",
            };
        }

        private static string Help() => string.Join(
            Environment.NewLine,
            "Commands:",
            "  status              server, world and tick summary",
            "  players             connected players with ping and loss",
            "  entities            every entity the server is tracking",
            "  entity <id>         full state of one entity",
            "  net                 per-connection network counters",
            "  kick <playerId>     disconnect a player",
            "  say <text>          broadcast a chat message as the server",
            "  time <HH:MM>        set the world clock",
            "  weather <name>      set the weather (e.g. EXTRASUNNY, RAIN, THUNDER)",
            "  save                write the world to persistence now",
            "  diagnostics         run the same checks as the in-game /diagnostics",
            "  stop                save and shut down");

        private string Status()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{_server.Config.ServerName} — {BuildInfo.Describe()}");
            builder.AppendLine($"  players   {_server.Players.Count}/{_server.Config.MaxPlayers}");
            builder.AppendLine($"  entities  {_server.World.EntityCount}");
            builder.AppendLine($"  tick      {_server.World.Tick} @ {_server.Config.TickRate} Hz");
            builder.AppendLine($"  snapshots {_server.Config.SnapshotRate} Hz, budget {_server.Config.SnapshotByteBudget} B");
            builder.AppendLine(
                $"  world     {_server.World.State.Environment.Hours:00}:{_server.World.State.Environment.Minutes:00}, " +
                $"weather 0x{_server.World.State.Environment.WeatherHash:X8}");
            builder.Append($"  uptime    {_server.Now:0} s");
            return builder.ToString();
        }

        private string PlayersList()
        {
            if (_server.Players.Count == 0)
            {
                return "No players connected.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("  id  name                 ping  loss    entity  position");
            foreach (PlayerSession session in _server.Players.Sessions)
            {
                PlayerEntity? entity = _server.World.GetPlayer(session.EntityId);
                builder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,-3} {1,-20} {2,4}  {3,5:0.0}%  {4,-6}  {5}",
                    session.PlayerId,
                    Truncate(session.Name, 20),
                    session.Peer.Stats.PingMilliseconds,
                    session.Peer.Stats.PacketLoss * 100,
                    session.EntityId,
                    entity?.Position.ToString() ?? "-"));
            }

            return builder.ToString().TrimEnd();
        }

        private string Entities()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{_server.World.EntityCount} entit{(_server.World.EntityCount == 1 ? "y" : "ies")}:");
            foreach (NetEntity entity in _server.World.State.Entities)
            {
                builder.AppendLine($"  {entity.Id,-8} {entity.Type,-10} owner={entity.OwnerId,-4} v{entity.NetworkVersion,-6} {entity.Position}");
            }

            return builder.ToString().TrimEnd();
        }

        private string Entity(string[] args)
        {
            if (args.Length < 1 || !uint.TryParse(args[0].TrimStart('#'), out uint id))
            {
                return "Usage: entity <id>";
            }

            if (!_server.World.TryGet(new EntityId(id), out NetEntity entity))
            {
                return $"No entity #{id}.";
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
                builder.AppendLine($"  wantedLevel     {player.WantedLevel}");
            }

            foreach (KeyValuePair<string, string> pair in entity.CustomData)
            {
                builder.AppendLine($"  custom[{pair.Key}] = {pair.Value}");
            }

            return builder.ToString().TrimEnd();
        }

        private string Network()
        {
            if (_server.Players.Count == 0)
            {
                return "No connections.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("  id  name                 ping   rtt-var   sent    recv    lost  retx  snapshots");
            foreach (PlayerSession session in _server.Players.Sessions)
            {
                NetStats stats = session.Peer.Stats;
                builder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,-3} {1,-20} {2,4}  {3,7:0.000}  {4,6}  {5,6}  {6,4}  {7,4}  {8}",
                    session.PlayerId,
                    Truncate(session.Name, 20),
                    stats.PingMilliseconds,
                    stats.RoundTripVariance,
                    stats.PacketsSent,
                    stats.PacketsReceived,
                    stats.PacketsLost,
                    stats.ReliableRetransmits,
                    session.Replication.SnapshotsSent));
            }

            return builder.ToString().TrimEnd();
        }

        private string Kick(string[] args)
        {
            if (args.Length < 1 || !uint.TryParse(args[0], out uint playerId))
            {
                return "Usage: kick <playerId>";
            }

            if (!_server.Players.TryGetByPlayerId(playerId, out PlayerSession session))
            {
                return $"No player with id {playerId}.";
            }

            _server.Kick(session, DisconnectReason.Kicked, "kicked from the server console");
            return $"Kicked {session.Name}.";
        }

        private string Say(string[] args)
        {
            if (args.Length == 0)
            {
                return "Usage: say <text>";
            }

            string text = string.Join(" ", args);
            var chat = new ChatMessage { PlayerId = 0, SenderName = "SERVER", Text = text };
            _server.Broadcast(NetMessageType.ChatMessage, chat.Serialize(), DeliveryMethod.ReliableOrdered);
            return $"[chat] SERVER: {text}";
        }

        private string Time(string[] args)
        {
            if (args.Length < 1)
            {
                return $"World clock is {_server.World.State.Environment.Hours:00}:{_server.World.State.Environment.Minutes:00}.";
            }

            string[] parts = args[0].Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int hours) || !int.TryParse(parts[1], out int minutes))
            {
                return "Usage: time <HH:MM>";
            }

            _server.World.State.Environment.SetTime(hours, minutes, 0);
            return $"World clock set to {hours:00}:{minutes:00}.";
        }

        private string Weather(string[] args)
        {
            if (args.Length < 1)
            {
                return $"Weather is 0x{_server.World.State.Environment.WeatherHash:X8}.";
            }

            uint hash = GameHash.Joaat(args[0]);
            _server.World.State.Environment.WeatherHash = hash;
            _server.World.State.Environment.NextWeatherHash = 0;
            _server.World.State.Environment.WeatherTransition = 0f;
            return $"Weather set to {args[0].ToUpperInvariant()} (0x{hash:X8}).";
        }

        private string Save()
        {
            _server.SaveWorld("console");
            return "World saved.";
        }

        private string Diagnostics()
        {
            var builder = new StringBuilder();
            builder.AppendLine("=== SERVER DIAGNOSTICS ===");
            builder.AppendLine($"{Mark(true)} configuration      {_server.Config.ServerName}, port {_server.Config.Port}");
            builder.AppendLine($"{Mark(_server.IsRunning)} simulation         tick {_server.World.Tick}");
            builder.AppendLine($"{Mark(true)} entity schema      0x{_server.Registry.ComputeSchemaHash():X8}");
            builder.AppendLine($"{Mark(_server.Config.PersistenceEnabled)} persistence        {(_server.Config.PersistenceEnabled ? _server.Config.DatabasePath : "disabled")}");
            builder.AppendLine($"{Mark(_server.Config.AntiCheat != Shared.Security.AntiCheatLevel.Off)} anti-cheat         {_server.Config.AntiCheat}");
            builder.AppendLine($"{Mark(_server.Players.Count > 0)} players            {_server.Players.Count}/{_server.Config.MaxPlayers}");
            builder.Append("=== END DIAGNOSTICS ===");
            return builder.ToString();
        }

        private string Stop()
        {
            StopRequested = true;
            return "Stopping...";
        }

        private static string Mark(bool ok) => ok ? "✓" : "⚠";

        private static string Truncate(string value, int length) =>
            value.Length <= length ? value : value.Substring(0, length - 1) + "…";
    }
}
