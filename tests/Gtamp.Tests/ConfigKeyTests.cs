using System.Collections.Generic;
using System.IO;
using Gtamp.Client.Core;
using Gtamp.Server.Core;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// A misspelled setting used to be silently ignored.
    /// <para>
    /// The client parser was a <c>switch</c> with no default and the server one is
    /// <c>System.Text.Json</c>, which drops unmapped members. So an operator who
    /// wrote <c>maxPlayerz</c> got the default, was told nothing, and had a setting
    /// that looked applied and did nothing — the same defect this project has found
    /// sixteen times inside the replication layer, wearing a configuration file as a
    /// hat.
    /// </para>
    /// </summary>
    public class ConfigKeyTests
    {
        private static string TempFile(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), $"gtamp-{Path.GetRandomFileName()}{extension}");
            return path;
        }

        [Fact]
        public void EverySettingTheClientWritesIsOneItCanReadBack()
        {
            // The invariant that keeps the known-key set and the parser from drifting.
            // A new setting added to Save and to the switch but forgotten in KnownKeys
            // fails here rather than the first time somebody sets it.
            string path = TempFile(".ini");
            try
            {
                var written = new ClientConfig { PlayerName = "alice", ShowPlayerBlips = false };
                written.Save(path);

                ClientConfig read = ClientConfig.Load(path);

                Assert.Empty(read.UnknownKeys);
                Assert.Equal("alice", read.PlayerName);
                Assert.False(read.ShowPlayerBlips);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AMisspelledClientSettingIsReported()
        {
            string path = TempFile(".ini");
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "PlayerName=alice",
                    "ShowPlayerBlibs=False",
                    "MaxPlayerz=64",
                });

                ClientConfig config = ClientConfig.Load(path);

                Assert.Equal("alice", config.PlayerName);
                Assert.Contains("ShowPlayerBlibs", config.UnknownKeys);
                Assert.Contains("MaxPlayerz", config.UnknownKeys);

                // And the real one it was a typo of is untouched, still at its default.
                Assert.True(config.ShowPlayerBlips);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CommentsAndBlankLinesAreNotMistakenForSettings()
        {
            string path = TempFile(".ini");
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "; a comment",
                    "# another",
                    "[section]",
                    string.Empty,
                    "PlayerName=alice",
                });

                ClientConfig config = ClientConfig.Load(path);

                Assert.Empty(config.UnknownKeys);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AMisspelledServerSettingIsReportedRatherThanRefused()
        {
            // Reported, not fatal. Refusing to start would make an older build unable
            // to read a file written by a newer one, which is a worse failure than the
            // one being prevented.
            string path = TempFile(".json");
            try
            {
                File.WriteAllText(path, "{ \"serverName\": \"test\", \"maxPlayerz\": 64 }");

                ServerConfig config = ServerConfig.LoadOrCreate(path);

                Assert.Equal("test", config.ServerName);
                Assert.Equal(32, config.MaxPlayers);
                Assert.Contains("maxPlayerz", config.UnknownKeys);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void EverySettingTheServerWritesIsOneItCanReadBack()
        {
            string path = TempFile(".json");
            try
            {
                var written = new ServerConfig { ServerName = "test", MaxPlayers = 12 };
                written.Save(path);

                ServerConfig read = ServerConfig.LoadOrCreate(path);

                Assert.Empty(read.UnknownKeys);
                Assert.Equal("test", read.ServerName);
                Assert.Equal(12, read.MaxPlayers);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CaseDoesNotMakeASettingUnknown()
        {
            string path = TempFile(".json");
            try
            {
                File.WriteAllText(path, "{ \"ServerName\": \"test\", \"MAXPLAYERS\": 8 }");

                ServerConfig config = ServerConfig.LoadOrCreate(path);

                Assert.Empty(config.UnknownKeys);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AConfigWithNothingWrongReportsNothing()
        {
            // The check has to be quiet when there is nothing to say, or it becomes a
            // warning people learn to scroll past.
            string path = TempFile(".json");
            try
            {
                File.WriteAllText(path, "{ \"serverName\": \"test\", \"maxPlayers\": 8 }");

                Assert.Empty(ServerConfig.LoadOrCreate(path).UnknownKeys);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ASettingThatPromisedAFeatureThatDoesNotExistIsGone()
        {
            // `Public` was a server setting nothing read, and there is no server
            // browser, no master list and no LAN discovery anywhere in the project —
            // so an operator setting it was being told their server would be listed
            // somewhere, by a bool that did nothing. Removed rather than given a
            // meaning: a discovery protocol is a subsystem, not a flag.
            var known = new List<string>();
            foreach (var property in typeof(ServerConfig).GetProperties())
            {
                known.Add(property.Name);
            }

            Assert.DoesNotContain("Public", known);
        }
    }
}
