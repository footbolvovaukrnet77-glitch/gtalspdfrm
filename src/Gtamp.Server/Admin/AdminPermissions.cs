using System;
using System.Collections.Generic;
using Gtamp.Server.Players;

namespace Gtamp.Server.Admin
{
    /// <summary>
    /// What a role is allowed to do over the network.
    /// <para>
    /// Flags rather than a list of command names, because the names are a UI detail
    /// and the capability is not: <c>kick</c> and a future <c>kickall</c> are the same
    /// power, and a permission model that has to be edited every time a command is
    /// added stops being edited.
    /// </para>
    /// </summary>
    [Flags]
    public enum AdminPermission
    {
        None = 0,

        /// <summary>Read the world: status, players, entities, net, diagnostics.</summary>
        Inspect = 1 << 0,

        /// <summary>Speak as the server.</summary>
        Announce = 1 << 1,

        /// <summary>Remove a player from the session.</summary>
        Kick = 1 << 2,

        /// <summary>Keep a player out, and let them back in.</summary>
        Ban = 1 << 3,

        /// <summary>Move, kill or respawn a player.</summary>
        AffectPlayers = 1 << 4,

        /// <summary>Change the world clock and weather.</summary>
        World = 1 << 5,

        /// <summary>Force a save, change roles, shut the server down.</summary>
        Server = 1 << 6,
    }

    /// <summary>
    /// Maps roles to permissions, and command names to the permission they need.
    /// <para>
    /// <b>Default deny.</b> A command with no entry in the table needs
    /// <see cref="AdminPermission.Server"/> — the highest — rather than being open.
    /// A permission table where forgetting to add a command makes it public is worse
    /// than no table at all, because it reads as though it is protecting something.
    /// </para>
    /// </summary>
    public static class AdminPermissions
    {
        private static readonly Dictionary<string, AdminPermission> Required =
            new Dictionary<string, AdminPermission>(StringComparer.OrdinalIgnoreCase)
            {
                ["help"] = AdminPermission.Inspect,
                ["status"] = AdminPermission.Inspect,
                ["players"] = AdminPermission.Inspect,
                ["entities"] = AdminPermission.Inspect,
                ["entity"] = AdminPermission.Inspect,
                ["net"] = AdminPermission.Inspect,
                ["diagnostics"] = AdminPermission.Inspect,
                ["bans"] = AdminPermission.Inspect,

                ["say"] = AdminPermission.Announce,

                ["kick"] = AdminPermission.Kick,

                ["ban"] = AdminPermission.Ban,
                ["unban"] = AdminPermission.Ban,

                ["teleport"] = AdminPermission.AffectPlayers,
                ["kill"] = AdminPermission.AffectPlayers,
                ["respawn"] = AdminPermission.AffectPlayers,
                ["wanted"] = AdminPermission.AffectPlayers,
                ["model"] = AdminPermission.AffectPlayers,

                ["time"] = AdminPermission.World,
                ["weather"] = AdminPermission.World,

                ["save"] = AdminPermission.Server,
                ["role"] = AdminPermission.Server,
                ["stop"] = AdminPermission.Server,
            };

        public static AdminPermission For(PlayerRole role) => role switch
        {
            PlayerRole.Admin => AdminPermission.Inspect | AdminPermission.Announce | AdminPermission.Kick
                                | AdminPermission.Ban | AdminPermission.AffectPlayers | AdminPermission.World
                                | AdminPermission.Server,

            // A moderator polices players. They cannot change the world, shut the
            // server down, or hand out roles — including their own.
            PlayerRole.Moderator => AdminPermission.Inspect | AdminPermission.Announce | AdminPermission.Kick
                                    | AdminPermission.Ban | AdminPermission.AffectPlayers,

            _ => AdminPermission.None,
        };

        /// <summary>The permission a command needs. Unknown commands need the highest.</summary>
        public static AdminPermission RequiredFor(string commandLine)
        {
            string name = FirstWord(commandLine);
            return Required.TryGetValue(name, out AdminPermission permission)
                ? permission
                : AdminPermission.Server;
        }

        public static bool IsAllowed(PlayerRole role, string commandLine)
        {
            AdminPermission needed = RequiredFor(commandLine);
            return needed != AdminPermission.None && (For(role) & needed) == needed;
        }

        public static string FirstWord(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return string.Empty;
            }

            string trimmed = commandLine.Trim();
            int space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed.Substring(0, space);
        }

        /// <summary>Every command a role may run, for a readable "permission denied".</summary>
        public static IEnumerable<string> CommandsFor(PlayerRole role)
        {
            AdminPermission held = For(role);
            foreach (KeyValuePair<string, AdminPermission> pair in Required)
            {
                if (pair.Value != AdminPermission.None && (held & pair.Value) == pair.Value)
                {
                    yield return pair.Key;
                }
            }
        }
    }
}
