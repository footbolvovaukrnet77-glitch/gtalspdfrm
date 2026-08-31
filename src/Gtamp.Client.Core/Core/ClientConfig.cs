using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gtamp.Shared.Protocol;

namespace Gtamp.Client.Core
{
    /// <summary>
    /// Client settings, stored next to the assembly as a plain INI-style file.
    /// <para>
    /// INI rather than JSON on purpose: this file lives in the GTA V directory where
    /// players edit it by hand, and the client targets .NET Framework 4.8 where
    /// System.Text.Json is not available without dragging extra assemblies into the
    /// game process.
    /// </para>
    /// </summary>
    public sealed class ClientConfig
    {
        public string PlayerName { get; set; } = "Player";

        public string ServerAddress { get; set; } = "127.0.0.1";

        public int ServerPort { get; set; } = ProtocolConstants.DefaultPort;

        public string ServerPassword { get; set; } = string.Empty;

        /// <summary>
        /// Stable per-installation secret. Generated on first run; it is what lets the
        /// server give this player their character back after a reconnect.
        /// </summary>
        public string IdentityToken { get; set; } = string.Empty;

        /// <summary>Virtual key code that opens the developer console. 119 is F8.</summary>
        public int ConsoleKey { get; set; } = 119;

        /// <summary>
        /// How far behind the newest snapshot remote players are rendered, in seconds.
        /// Two snapshot intervals plus a jitter margin; lowering it makes other players
        /// stutter, raising it makes them lag behind their real position.
        /// </summary>
        public double InterpolationDelay { get; set; } = 0.12;

        /// <summary>
        /// How far the local player may drift from the server's authoritative position
        /// before the client snaps to it. Too small and normal latency causes constant
        /// rubber-banding; too large and a rejected movement leaves the player playing
        /// a position the server does not agree with.
        /// </summary>
        public double CorrectionThreshold { get; set; } = 3.0;

        public bool ShowNetworkOverlay { get; set; }

        public bool VerboseLogging { get; set; }

        public bool AutoConnectOnStart { get; set; }

        public static ClientConfig Load(string path)
        {
            var config = new ClientConfig();
            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
                    {
                        continue;
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    config.Apply(key, value);
                }
            }

            if (string.IsNullOrWhiteSpace(config.IdentityToken))
            {
                config.IdentityToken = Guid.NewGuid().ToString("N");
                config.Save(path);
            }

            return config;
        }

        public void Save(string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = new List<string>
            {
                "; GTAMP client configuration.",
                "; Edit while the game is closed, or run 'reload config' in the F8 console.",
                "[client]",
                $"PlayerName={PlayerName}",
                $"ServerAddress={ServerAddress}",
                $"ServerPort={ServerPort.ToString(CultureInfo.InvariantCulture)}",
                $"ServerPassword={ServerPassword}",
                $"IdentityToken={IdentityToken}",
                "; Virtual key code. 119 = F8, 192 = tilde.",
                $"ConsoleKey={ConsoleKey.ToString(CultureInfo.InvariantCulture)}",
                $"InterpolationDelay={InterpolationDelay.ToString("0.###", CultureInfo.InvariantCulture)}",
                $"CorrectionThreshold={CorrectionThreshold.ToString("0.###", CultureInfo.InvariantCulture)}",
                $"ShowNetworkOverlay={ShowNetworkOverlay}",
                $"VerboseLogging={VerboseLogging}",
                $"AutoConnectOnStart={AutoConnectOnStart}",
            };

            File.WriteAllLines(path, lines);
        }

        private void Apply(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "playername":
                    PlayerName = value;
                    break;
                case "serveraddress":
                    ServerAddress = value;
                    break;
                case "serverport":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                    {
                        ServerPort = port;
                    }

                    break;
                case "serverpassword":
                    ServerPassword = value;
                    break;
                case "identitytoken":
                    IdentityToken = value;
                    break;
                case "consolekey":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int consoleKey))
                    {
                        ConsoleKey = consoleKey;
                    }

                    break;
                case "interpolationdelay":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double delay))
                    {
                        InterpolationDelay = delay;
                    }

                    break;
                case "correctionthreshold":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
                    {
                        CorrectionThreshold = threshold;
                    }

                    break;
                case "shownetworkoverlay":
                    ShowNetworkOverlay = ParseBool(value);
                    break;
                case "verboselogging":
                    VerboseLogging = ParseBool(value);
                    break;
                case "autoconnectonstart":
                    AutoConnectOnStart = ParseBool(value);
                    break;
            }
        }

        private static bool ParseBool(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
