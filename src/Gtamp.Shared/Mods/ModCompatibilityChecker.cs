using System;
using System.Collections.Generic;

namespace Gtamp.Shared.Mods
{
    /// <summary>
    /// Compares a client manifest with the server's and produces a per-mod verdict.
    /// <para>
    /// The policy is deliberately permissive: only a mod the server marks
    /// <see cref="ModNetworkRequirement.Required"/> can block a connection. Everything
    /// else is reported and the player joins anyway, because the master prompt is
    /// explicit that a missing mod must not take the session down.
    /// </para>
    /// </summary>
    public static class ModCompatibilityChecker
    {
        public static List<ModCompatibilityEntry> Compare(ModManifest server, ModManifest client)
        {
            var report = new List<ModCompatibilityEntry>();

            foreach (ModDescriptor serverMod in server.Mods)
            {
                if (serverMod.Requirement == ModNetworkRequirement.ClientOnly)
                {
                    continue;
                }

                ModDescriptor? clientMod = client.Find(serverMod.Id);
                bool required = serverMod.Requirement == ModNetworkRequirement.Required;

                if (clientMod == null)
                {
                    report.Add(new ModCompatibilityEntry
                    {
                        ModId = serverMod.Id,
                        Status = ModCompatibility.Missing,
                        Detail = $"'{serverMod.Name}' {serverMod.Version} is loaded on the server but not on this client.",
                        BlocksConnection = required,
                    });
                    continue;
                }

                if (!string.Equals(serverMod.Version, clientMod.Version, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add(new ModCompatibilityEntry
                    {
                        ModId = serverMod.Id,
                        Status = ModCompatibility.WrongVersion,
                        Detail = $"server has {serverMod.Version}, client has {clientMod.Version}.",
                        BlocksConnection = required,
                    });
                    continue;
                }

                if (!string.IsNullOrEmpty(serverMod.Hash)
                    && !string.IsNullOrEmpty(clientMod.Hash)
                    && !string.Equals(serverMod.Hash, clientMod.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add(new ModCompatibilityEntry
                    {
                        ModId = serverMod.Id,
                        Status = ModCompatibility.HashMismatch,
                        Detail = "same version but different file contents; one side has an edited build.",
                        BlocksConnection = required,
                    });
                    continue;
                }

                List<string> missingDependencies = FindMissingDependencies(serverMod, client);
                if (missingDependencies.Count > 0)
                {
                    report.Add(new ModCompatibilityEntry
                    {
                        ModId = serverMod.Id,
                        Status = ModCompatibility.PartiallyCompatible,
                        Detail = "missing dependencies: " + string.Join(", ", missingDependencies.ToArray()),
                        BlocksConnection = false,
                    });
                    continue;
                }

                report.Add(new ModCompatibilityEntry
                {
                    ModId = serverMod.Id,
                    Status = ModCompatibility.Compatible,
                    Detail = string.Empty,
                    BlocksConnection = false,
                });
            }

            // Mods the client has that the server does not know about are reported so
            // the player can see them in /diagnostics, but they never block.
            foreach (ModDescriptor clientMod in client.Mods)
            {
                if (clientMod.Requirement == ModNetworkRequirement.ClientOnly || server.Find(clientMod.Id) != null)
                {
                    continue;
                }

                report.Add(new ModCompatibilityEntry
                {
                    ModId = clientMod.Id,
                    Status = ModCompatibility.Unsupported,
                    Detail = $"'{clientMod.Name}' is loaded on this client but the server has no adapter for it; its state stays local.",
                    BlocksConnection = false,
                });
            }

            return report;
        }

        public static bool HasBlockingIssue(IEnumerable<ModCompatibilityEntry> report)
        {
            foreach (ModCompatibilityEntry entry in report)
            {
                if (entry.BlocksConnection)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> FindMissingDependencies(ModDescriptor serverMod, ModManifest client)
        {
            var missing = new List<string>();
            foreach (string dependency in serverMod.Dependencies)
            {
                if (client.Find(dependency) == null)
                {
                    missing.Add(dependency);
                }
            }

            return missing;
        }
    }
}
