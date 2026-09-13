using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Gtamp.Shared.Mods;

namespace Gtamp.Client.Mods
{
    /// <summary>
    /// What is actually installed next to GTA V.
    /// <para>
    /// Detection is by file presence and, where possible, by whether the assembly is
    /// already loaded in the process. Nothing here loads a mod or takes a hard
    /// dependency on one, so a client with no mods at all produces an empty
    /// environment and carries on.
    /// </para>
    /// </summary>
    public sealed class ModEnvironment
    {
        public string GameDirectory { get; private set; } = string.Empty;

        /// <summary>
        /// The game build, read from the executable itself, or empty if it could not
        /// be read.
        /// <para>
        /// ScriptHookVDotNet also has an opinion, and on a game newer than the SHVDN
        /// build it is wrong: <c>Game.Version</c> is an enum whose highest member is
        /// whatever existed when that SHVDN was released, so a 3889 install is
        /// reported as the newest build SHVDN knows. Diagnostics printed that with a
        /// tick beside it, which is worse than printing nothing: the single field a
        /// bug report exists to get right was confidently wrong, and it was wrong
        /// precisely on the installs where it matters, since a game newer than SHVDN
        /// is exactly when SHVDN's pattern scanning starts to fail.
        /// </para>
        /// </summary>
        public string GameBuild { get; private set; } = string.Empty;

        public bool ScriptHookV { get; private set; }

        public bool ScriptHookVDotNet { get; private set; }

        public string ScriptHookVDotNetVersion { get; private set; } = string.Empty;

        public bool RagePluginHook { get; private set; }

        public string RagePluginHookVersion { get; private set; } = string.Empty;

        public bool Lspdfr { get; private set; }

        public string LspdfrVersion { get; private set; } = string.Empty;

        public List<ModDescriptor> Mods { get; } = new List<ModDescriptor>();

        public List<string> AsiPlugins { get; } = new List<string>();

        public List<string> LspdfrPlugins { get; } = new List<string>();

        public List<string> Scripts { get; } = new List<string>();

        /// <summary>
        /// This framework's own assemblies, which live in <c>scripts\</c> like any other
        /// SHVDN script and are not mods.
        /// <para>
        /// They used to be scanned as third-party mods and sent in the handshake
        /// manifest, so every connection warned three times that the server had no
        /// adapter for Gtamp.Client.Core, Gtamp.Client.Shv and Gtamp.Shared. The
        /// framework is not a mod of itself: an adapter for it would be an adapter for
        /// the thing asking the question.
        /// </para>
        /// </summary>
        private static readonly string[] OwnAssemblies =
        {
            "Gtamp.Client.Shv",
            "Gtamp.Client.Core",
            "Gtamp.Shared",
            "Gtamp.RphBridge",
        };

        /// <summary>
        /// True when the file is one of this framework's own assemblies.
        /// <para>
        /// The name is taken by hand rather than with <see cref="Path"/>, for the reason
        /// <c>GameDirectory</c> records: every path here is a Windows path, and the tests
        /// run on Linux where <see cref="Path"/> does not treat a backslash as a
        /// separator and would agree with the wrong answer.
        /// </para>
        /// </summary>
        public static bool IsOwnAssembly(string path)
        {
            string file = path ?? string.Empty;
            int separator = file.LastIndexOfAny(new[] { '\\', '/' });
            if (separator >= 0)
            {
                file = file.Substring(separator + 1);
            }

            int dot = file.LastIndexOf('.');
            string name = dot > 0 ? file.Substring(0, dot) : file;
            for (int i = 0; i < OwnAssemblies.Length; i++)
            {
                if (string.Equals(name, OwnAssemblies[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How the game build should be reported, given what the script host thinks.
        /// <para>
        /// The executable is the authority; the script host's answer is only worth
        /// printing when it disagrees, and then it is worth printing loudly, because
        /// a host that has the build wrong is a host whose pattern scanning is about
        /// to fail. When the executable cannot be read at all — an install layout this
        /// does not know — the host's answer is all there is, and it is labelled as
        /// coming from the host rather than passed off as the build.
        /// </para>
        /// </summary>
        public string DescribeGameBuild(string? scriptHostVersion)
        {
            string host = (scriptHostVersion ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(GameBuild))
            {
                return host.Length == 0
                    ? "unknown — neither the executable nor the script host could be read"
                    : host + " (as ScriptHookVDotNet sees it; the executable could not be read)";
            }

            if (host.Length == 0 || BuildsAgree(GameBuild, host))
            {
                return GameBuild;
            }

            return $"{GameBuild} — ScriptHookVDotNet reports {host}, so it is older than this game build";
        }

        /// <summary>
        /// Whether two build strings name the same build. SHVDN says
        /// <c>v1_0_3889_0</c> where the executable says <c>1.0.3889.0</c>, and neither
        /// spelling is wrong — so they are compared by their digits rather than as
        /// text, and a mismatch reported only when the numbers really differ.
        /// </summary>
        private static bool BuildsAgree(string executable, string host)
        {
            return Digits(executable) == Digits(host);
        }

        private static string Digits(string value)
        {
            var digits = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9')
                {
                    digits.Append(c);
                }
            }

            return digits.ToString();
        }

        /// <summary>Scans a GTA V installation directory. Never throws; unreadable paths are skipped.</summary>
        public static ModEnvironment Detect(string gameDirectory)
        {
            var environment = new ModEnvironment { GameDirectory = gameDirectory };

            // Legacy and Enhanced ship different executables and only one is present.
            foreach (string executable in new[] { "GTA5.exe", "GTA5_Enhanced.exe", "PlayGTAV.exe" })
            {
                string candidate = Path.Combine(gameDirectory, executable);
                if (File.Exists(candidate))
                {
                    string version = FileVersion(candidate);
                    if (version != "unknown")
                    {
                        environment.GameBuild = version;
                        break;
                    }
                }
            }

            environment.ScriptHookV = File.Exists(Path.Combine(gameDirectory, "ScriptHookV.dll"));

            string shvdn = Path.Combine(gameDirectory, "ScriptHookVDotNet3.dll");
            if (File.Exists(shvdn))
            {
                environment.ScriptHookVDotNet = true;
                environment.ScriptHookVDotNetVersion = FileVersion(shvdn);
            }

            string rph = Path.Combine(gameDirectory, "RAGEPluginHook.exe");
            if (File.Exists(rph))
            {
                environment.RagePluginHook = true;
                environment.RagePluginHookVersion = FileVersion(rph);
            }

            // LSPDFR is a RAGE Plugin Hook plugin, so it lives in RPH's plugins folder.
            // This looked in the game root for the life of the project and therefore
            // reported LSPDFR=no on a machine that was running it -- the RPH log names
            // the real path on every start: Plugins\LSPD First Response.dll. The root is
            // still checked second, because an unusual install costs one File.Exists.
            foreach (string lspdfr in new[]
            {
                Path.Combine(gameDirectory, "Plugins", "LSPD First Response.dll"),
                Path.Combine(gameDirectory, "LSPD First Response.dll"),
            })
            {
                if (File.Exists(lspdfr))
                {
                    environment.Lspdfr = true;
                    environment.LspdfrVersion = FileVersion(lspdfr);
                    break;
                }
            }

            CollectFiles(gameDirectory, "*.asi", environment.AsiPlugins);
            CollectFiles(Path.Combine(gameDirectory, "scripts"), "*.dll", environment.Scripts);
            CollectFiles(Path.Combine(gameDirectory, "plugins", "LSPDFR"), "*.dll", environment.LspdfrPlugins);

            environment.BuildManifestEntries();
            return environment;
        }

        /// <summary>
        /// Adds a mod an adapter discovered at runtime rather than by scanning files —
        /// an RPH plugin, say, which is only visible once RPH has loaded it.
        /// Replaces an existing entry with the same id.
        /// </summary>
        public void AddDetectedMod(ModDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Id))
            {
                return;
            }

            for (int i = 0; i < Mods.Count; i++)
            {
                if (string.Equals(Mods[i].Id, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Mods[i] = descriptor;
                    return;
                }
            }

            Mods.Add(descriptor);
        }

        /// <summary>Builds the manifest sent to the server during the handshake.</summary>
        public ModManifest ToManifest(uint schemaHash)
        {
            var manifest = new ModManifest
            {
                SchemaHash = schemaHash,
                RagePluginHookPresent = RagePluginHook,
                RagePluginHookVersion = RagePluginHookVersion,
                LspdfrPresent = Lspdfr,
                LspdfrVersion = LspdfrVersion,
                ScriptHookVPresent = ScriptHookV,
            };

            manifest.Mods.AddRange(Mods);
            return manifest;
        }

        private void BuildManifestEntries()
        {
            if (ScriptHookV)
            {
                Add("scripthookv", "ScriptHookV", FileVersion(Path.Combine(GameDirectory, "ScriptHookV.dll")), ModNetworkRequirement.ClientOnly);
            }

            if (ScriptHookVDotNet)
            {
                Add("scripthookvdotnet", "ScriptHookVDotNet", ScriptHookVDotNetVersion, ModNetworkRequirement.ClientOnly);
            }

            if (RagePluginHook)
            {
                Add("rageluginhook", "RAGE Plugin Hook", RagePluginHookVersion, ModNetworkRequirement.Optional);
            }

            if (Lspdfr)
            {
                Add("lspdfr", "LSPD First Response", LspdfrVersion, ModNetworkRequirement.Optional);
            }

            foreach (string path in LspdfrPlugins)
            {
                Add("lspdfr.plugin." + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                    Path.GetFileNameWithoutExtension(path),
                    FileVersion(path),
                    ModNetworkRequirement.Optional,
                    path);
            }

            foreach (string path in Scripts)
            {
                if (IsOwnAssembly(path))
                {
                    continue;
                }

                Add("script." + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                    Path.GetFileNameWithoutExtension(path),
                    FileVersion(path),
                    ModNetworkRequirement.Optional,
                    path);
            }

            foreach (string path in AsiPlugins)
            {
                Add("asi." + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                    Path.GetFileName(path),
                    FileVersion(path),
                    ModNetworkRequirement.ClientOnly,
                    path);
            }
        }

        private void Add(string id, string name, string version, ModNetworkRequirement requirement, string? hashSource = null)
        {
            Mods.Add(new ModDescriptor
            {
                Id = id,
                Name = name,
                Version = string.IsNullOrEmpty(version) ? "unknown" : version,
                Hash = hashSource != null ? ShortHash(hashSource) : string.Empty,
                Requirement = requirement,
            });
        }

        private static void CollectFiles(string directory, string pattern, List<string> into)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    into.Add(file);
                }

                into.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // An unreadable mod folder is reported as "no mods found there",
                // never as a startup failure.
            }
        }

        private static string FileVersion(string path)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(info.FileVersion) ? "unknown" : info.FileVersion!;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>First 16 hex characters of the file's SHA-256. Enough to spot an edited build.</summary>
        public static string ShortHash(string path)
        {
            try
            {
                using var sha = SHA256.Create();
                using FileStream stream = File.OpenRead(path);
                byte[] hash = sha.ComputeHash(stream);
                var builder = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
