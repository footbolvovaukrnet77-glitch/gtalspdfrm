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

        /// <summary>Scans a GTA V installation directory. Never throws; unreadable paths are skipped.</summary>
        public static ModEnvironment Detect(string gameDirectory)
        {
            var environment = new ModEnvironment { GameDirectory = gameDirectory };

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

            string lspdfr = Path.Combine(gameDirectory, "LSPD First Response.dll");
            if (File.Exists(lspdfr))
            {
                environment.Lspdfr = true;
                environment.LspdfrVersion = FileVersion(lspdfr);
            }

            CollectFiles(gameDirectory, "*.asi", environment.AsiPlugins);
            CollectFiles(Path.Combine(gameDirectory, "scripts"), "*.dll", environment.Scripts);
            CollectFiles(Path.Combine(gameDirectory, "plugins", "LSPDFR"), "*.dll", environment.LspdfrPlugins);

            environment.BuildManifestEntries();
            return environment;
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
