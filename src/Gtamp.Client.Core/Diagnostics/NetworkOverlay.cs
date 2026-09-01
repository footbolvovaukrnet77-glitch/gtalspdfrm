using System;
using System.Collections.Generic;
using System.Globalization;
using Gtamp.Client.Core;
using Gtamp.Shared.Net;

namespace Gtamp.Client.Diagnostics
{
    /// <summary>How alarming a line is, so the renderer can colour it without parsing text.</summary>
    public enum OverlaySeverity : byte
    {
        Normal = 0,
        Warning = 1,
        Bad = 2,
    }

    public readonly struct OverlayLine
    {
        public OverlayLine(string text, OverlaySeverity severity)
        {
            Text = text;
            Severity = severity;
        }

        public string Text { get; }

        public OverlaySeverity Severity { get; }
    }

    /// <summary>
    /// The always-on network readout, as data rather than as drawing.
    /// <para>
    /// It exists because <c>ShowNetworkOverlay</c> was a setting in <c>client.ini</c>
    /// that nothing read: a switch that looks supported and does nothing is worse than
    /// no switch, because it costs a player the time to find out.
    /// </para>
    /// <para>
    /// The split is the same one the console uses: the model lives here and is
    /// testable with no game running, the renderer in the ScriptHookVDotNet layer
    /// draws it and holds no state. What it shows is chosen for the question a player
    /// actually asks mid-session — "is it me or the server?" — so loss, ping and
    /// snapshot health lead, and the thresholds that colour them are the ones at which
    /// the symptom becomes visible rather than round numbers.
    /// </para>
    /// </summary>
    public static class NetworkOverlay
    {
        /// <summary>Above this, remote players visibly lag their real position.</summary>
        public const int HighPingMilliseconds = 150;

        public const int SeverePingMilliseconds = 300;

        /// <summary>Above this, unreliable state updates thin out enough to see.</summary>
        public const double HighPacketLoss = 0.05;

        public const double SeverePacketLoss = 0.15;

        public static List<OverlayLine> Build(MultiplayerClient client)
        {
            var lines = new List<OverlayLine>(6);

            if (!client.IsConnected || client.Connection.Peer == null)
            {
                lines.Add(new OverlayLine("GTAMP — not connected", OverlaySeverity.Warning));
                return lines;
            }

            NetStats stats = client.Connection.Peer.Stats;
            int ping = stats.PingMilliseconds;
            double loss = stats.PacketLoss;

            lines.Add(new OverlayLine(
                $"GTAMP  {client.Connection.Accept?.ServerName ?? "server"}  " +
                $"{client.RemotePlayers.Count + 1} player(s)",
                OverlaySeverity.Normal));

            lines.Add(new OverlayLine(
                $"ping {ping} ms   loss {loss * 100:0.0}%",
                Worse(PingSeverity(ping), LossSeverity(loss))));

            lines.Add(new OverlayLine(
                $"snapshots {client.ReplicatedWorld.SnapshotsApplied} applied, " +
                $"{client.ReplicatedWorld.SnapshotsDropped} dropped",
                client.ReplicatedWorld.SnapshotsDropped > 0 ? OverlaySeverity.Warning : OverlaySeverity.Normal));

            // A resync is recoverable but never routine: it means a delta could not be
            // decoded, which is a version or mod mismatch far more often than congestion.
            lines.Add(new OverlayLine(
                $"resyncs {client.ResyncsRequested}   corrections {client.CorrectionsApplied}",
                client.ResyncsRequested > 0 ? OverlaySeverity.Bad : OverlaySeverity.Normal));

            lines.Add(new OverlayLine(
                $"shots {client.ShotsFired} fired   {client.ShotsSeen} seen   hits {client.HitsReported}",
                OverlaySeverity.Normal));

            lines.Add(new OverlayLine(
                $"entities {client.ReplicatedWorld.EntityCount}   " +
                $"vehicles {client.RemoteEntities.VehicleCount}   objects {client.RemoteEntities.ObjectCount}",
                OverlaySeverity.Normal));

            // Whether the session is encrypted is a fact about the session, not a live
            // metric, but it belongs on the same surface: a player who was told their
            // traffic is protected should be able to see that it is, and a player on a
            // server that turned it off is the one person who must not have to guess.
            if (!client.Connection.IsEncrypted)
            {
                lines.Add(new OverlayLine("session NOT encrypted — traffic is readable", OverlaySeverity.Warning));
            }
            else if (client.Connection.RejectedPackets > 0)
            {
                // Not loss. A packet the network damaged is dropped by the UDP checksum
                // long before it reaches the MAC, so anything counted here was shaped
                // like a valid packet and carried the wrong tag.
                lines.Add(new OverlayLine(
                    $"session encrypted — {client.Connection.RejectedPackets} forged packet(s) rejected",
                    OverlaySeverity.Bad));
            }
            else
            {
                lines.Add(new OverlayLine("session encrypted", OverlaySeverity.Normal));
            }

            if (!client.MissingContent.IsEmpty)
            {
                // Surfaced here as well as in /diagnostics because this is the one that
                // is on screen when the player is looking at the gap.
                lines.Add(new OverlayLine(
                    $"missing content: {client.MissingContent.Count} model(s) not installed",
                    OverlaySeverity.Bad));
            }

            return lines;
        }

        private static OverlaySeverity PingSeverity(int ping) => ping switch
        {
            >= SeverePingMilliseconds => OverlaySeverity.Bad,
            >= HighPingMilliseconds => OverlaySeverity.Warning,
            _ => OverlaySeverity.Normal,
        };

        private static OverlaySeverity LossSeverity(double loss)
        {
            if (loss >= SeverePacketLoss)
            {
                return OverlaySeverity.Bad;
            }

            return loss >= HighPacketLoss ? OverlaySeverity.Warning : OverlaySeverity.Normal;
        }

        private static OverlaySeverity Worse(OverlaySeverity a, OverlaySeverity b) => a > b ? a : b;

        /// <summary>Plain text, for tests and for the bundle.</summary>
        public static string Format(IReadOnlyList<OverlayLine> lines)
        {
            var builder = new System.Text.StringBuilder();
            foreach (OverlayLine line in lines)
            {
                builder.AppendLine(line.Text);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
