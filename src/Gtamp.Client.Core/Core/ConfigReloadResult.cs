using System.Collections.Generic;

namespace Gtamp.Client.Core
{
    /// <summary>
    /// What a configuration reload actually did.
    /// <para>
    /// Split three ways on purpose. "Applied" is what changed; "needs reconnect" is
    /// what was edited but cannot take effect on a live session; an error is a file
    /// that could not be read at all. A reload that reported only success would leave
    /// a player believing an address change had taken and wondering why they are
    /// still on the old server.
    /// </para>
    /// </summary>
    public sealed class ConfigReloadResult
    {
        public bool Success { get; set; }

        public string Error { get; set; } = string.Empty;

        public List<string> Applied { get; } = new List<string>();

        public List<string> NeedsReconnect { get; } = new List<string>();
    }
}
