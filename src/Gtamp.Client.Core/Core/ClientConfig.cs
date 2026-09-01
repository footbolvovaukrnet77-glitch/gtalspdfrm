using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;

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
        /// This installation's public identity: base64 of an ECDSA P-256 public key.
        /// It names the player to the server and is not a secret — publishing it lets
        /// somebody address you, not impersonate you.
        /// </summary>
        public string IdentityToken { get; set; } = string.Empty;

        /// <summary>
        /// The private half of that key. Generated on first run and never sent
        /// anywhere: the server proves the player by asking for a signature, not by
        /// being told the secret.
        /// <para>
        /// Copying this line to another machine moves the character with it. Losing it
        /// loses the character, exactly as losing the old identity token did — but
        /// leaking it no longer lets a bystander who watched one handshake become you.
        /// </para>
        /// </summary>
        public string IdentitySecret { get; set; } = string.Empty;

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

        /// <summary>
        /// How far the local health may differ from the server's before the client
        /// snaps to it. Set above the damage a player can plausibly take between two
        /// snapshots, or ordinary combat produces constant corrections.
        /// </summary>
        public int HealthCorrectionThreshold { get; set; } = 20;

        public bool ShowNetworkOverlay { get; set; }

        /// <summary>
        /// A map blip per remote player, and their name over their head. Both default
        /// to on: a co-op session in which you cannot find or identify anybody is not
        /// a session, and the cost is one blip and one string per player.
        /// </summary>
        public bool ShowPlayerBlips { get; set; } = true;

        public bool ShowPlayerNames { get; set; } = true;

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

            if (config.EnsureIdentity())
            {
                config.Save(path);
            }

            return config;
        }

        /// <summary>
        /// Makes sure this installation has a usable keypair, generating one on first
        /// run or when the stored blob is unreadable. Returns true when the file needs
        /// writing back.
        /// <para>
        /// An unreadable secret produces a <b>new identity with a warning</b>, not a
        /// crash and not a silent fresh start. The player loses their character either
        /// way; what the warning buys is that they find out why, and can restore the
        /// line from a backup before playing.
        /// </para>
        /// </summary>
        public bool EnsureIdentity()
        {
            IdentityKey? key = IdentityKey.TryImport(IdentitySecret);
            if (key != null)
            {
                // A public key that does not match the private one would make every
                // handshake fail with "the proof names a different identity", so it is
                // recomputed rather than trusted.
                bool changed = !string.Equals(IdentityToken, key.PublicKey, StringComparison.Ordinal);
                IdentityToken = key.PublicKey;
                key.Dispose();
                return changed;
            }

            IdentityRegenerated = !string.IsNullOrWhiteSpace(IdentitySecret);

            using IdentityKey created = IdentityKey.Create();
            IdentitySecret = created.ExportPrivateBlob();
            IdentityToken = created.PublicKey;
            return true;
        }

        /// <summary>
        /// True when the stored secret was present but unusable, so a new identity was
        /// generated. The client logs this at warning level rather than letting a lost
        /// character look like a server-side problem.
        /// </summary>
        public bool IdentityRegenerated { get; private set; }

        /// <summary>Loads the signing key. The caller owns it and must dispose it.</summary>
        public IdentityKey? LoadIdentity() => IdentityKey.TryImport(IdentitySecret);

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
                "; Your public identity. Safe to share; it is how the server knows you.",
                $"IdentityToken={IdentityToken}",
                "; The private half. Never share this, and never send it anywhere.",
                $"IdentitySecret={IdentitySecret}",
                "; Virtual key code. 119 = F8, 192 = tilde.",
                $"ConsoleKey={ConsoleKey.ToString(CultureInfo.InvariantCulture)}",
                $"InterpolationDelay={InterpolationDelay.ToString("0.###", CultureInfo.InvariantCulture)}",
                $"CorrectionThreshold={CorrectionThreshold.ToString("0.###", CultureInfo.InvariantCulture)}",
                $"HealthCorrectionThreshold={HealthCorrectionThreshold.ToString(CultureInfo.InvariantCulture)}",
                $"ShowNetworkOverlay={ShowNetworkOverlay}",
                $"ShowPlayerBlips={ShowPlayerBlips}",
                $"ShowPlayerNames={ShowPlayerNames}",
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
                case "identitysecret":
                    IdentitySecret = value;
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
                case "healthcorrectionthreshold":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int healthThreshold))
                    {
                        HealthCorrectionThreshold = healthThreshold;
                    }

                    break;
                case "shownetworkoverlay":
                    ShowNetworkOverlay = ParseBool(value);
                    break;
                case "showplayerblips":
                    ShowPlayerBlips = ParseBool(value);
                    break;
                case "showplayernames":
                    ShowPlayerNames = ParseBool(value);
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
