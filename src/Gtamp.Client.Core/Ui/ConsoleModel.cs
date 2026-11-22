using System;
using System.Collections.Generic;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Ui
{
    /// <summary>Console colour, mapped to an actual colour by whichever renderer draws the console.</summary>
    public enum ConsoleColorRole : byte
    {
        Info,
        Success,
        Warning,
        Error,
        Critical,
        Debug,
        Network,
        Server,
        Client,
        Mod,
        Security,
        Prompt,
    }

    /// <summary>
    /// Filters offered by the console, matching master prompt section 39. A filter
    /// is either a severity or a subsystem; "All" clears both.
    /// </summary>
    public enum ConsoleFilter : byte
    {
        All,
        Info,
        Debug,
        Warning,
        Error,
        Critical,
        Network,
        Server,
        Client,
        Mod,
        Security,
    }

    /// <summary>One rendered console line: the text plus the role its colour comes from.</summary>
    public readonly struct ConsoleLine
    {
        public ConsoleLine(long entryId, string text, ConsoleColorRole role)
        {
            EntryId = entryId;
            Text = text;
            Role = role;
        }

        public long EntryId { get; }

        public string Text { get; }

        public ConsoleColorRole Role { get; }
    }

    /// <summary>
    /// Colour rules from master prompt section 38. Severity wins over subsystem, so
    /// a network error is red rather than network-blue: the operator is looking for
    /// the failure, not for which subsystem produced it.
    /// </summary>
    public static class ConsolePalette
    {
        public static ConsoleColorRole RoleFor(in LogEntry entry) => entry.Level switch
        {
            LogLevel.Critical => ConsoleColorRole.Critical,
            LogLevel.Error => ConsoleColorRole.Error,
            LogLevel.Warning => ConsoleColorRole.Warning,
            LogLevel.Success => ConsoleColorRole.Success,
            LogLevel.Debug => ConsoleColorRole.Debug,
            _ => entry.Category switch
            {
                LogCategory.Network => ConsoleColorRole.Network,
                LogCategory.Server => ConsoleColorRole.Server,
                LogCategory.Client => ConsoleColorRole.Client,
                LogCategory.Mod => ConsoleColorRole.Mod,
                LogCategory.Security => ConsoleColorRole.Security,
                _ => ConsoleColorRole.Info,
            },
        };

        /// <summary>RGBA the renderer can use directly. Errors red, critical maximally loud, warnings yellow, success green.</summary>
        public static (byte R, byte G, byte B, byte A) Rgba(ConsoleColorRole role) => role switch
        {
            ConsoleColorRole.Critical => ((byte)255, (byte)40, (byte)40, (byte)255),
            ConsoleColorRole.Error => ((byte)220, (byte)50, (byte)50, (byte)255),
            ConsoleColorRole.Warning => ((byte)235, (byte)200, (byte)60, (byte)255),
            ConsoleColorRole.Success => ((byte)90, (byte)210, (byte)100, (byte)255),
            ConsoleColorRole.Debug => ((byte)140, (byte)140, (byte)140, (byte)255),
            ConsoleColorRole.Network => ((byte)110, (byte)190, (byte)230, (byte)255),
            ConsoleColorRole.Server => ((byte)170, (byte)150, (byte)230, (byte)255),
            ConsoleColorRole.Client => ((byte)150, (byte)200, (byte)170, (byte)255),
            ConsoleColorRole.Mod => ((byte)230, (byte)160, (byte)90, (byte)255),
            ConsoleColorRole.Security => ((byte)235, (byte)120, (byte)200, (byte)255),
            ConsoleColorRole.Prompt => ((byte)255, (byte)255, (byte)255, (byte)255),
            _ => ((byte)220, (byte)220, (byte)220, (byte)255),
        };
    }

    /// <summary>Copy targets offered per error line (master prompt section 41).</summary>
    public enum CopyMode : byte
    {
        Message,
        StackTrace,
        FullError,
        BugReport,
    }

    /// <summary>Clipboard access, which is host-specific. The console never touches Windows APIs itself.</summary>
    public interface IClipboard
    {
        void SetText(string text);
    }

    /// <summary>Used when no clipboard is available; text is still retrievable for display.</summary>
    public sealed class NullClipboard : IClipboard
    {
        public string LastText { get; private set; } = string.Empty;

        public void SetText(string text) => LastText = text ?? string.Empty;
    }

    /// <summary>What a console command receives when it runs.</summary>
    public sealed class ConsoleCommandContext
    {
        public ConsoleCommandContext(string name, IReadOnlyList<string> arguments, string rawArguments)
        {
            Name = name;
            Arguments = arguments;
            RawArguments = rawArguments;
        }

        public string Name { get; }

        public IReadOnlyList<string> Arguments { get; }

        public string RawArguments { get; }

        public string Argument(int index) => index < Arguments.Count ? Arguments[index] : string.Empty;

        public bool TryUInt(int index, out uint value) =>
            uint.TryParse(Argument(index).TrimStart('#'), out value);
    }

    public sealed class ConsoleCommand
    {
        public ConsoleCommand(string name, string usage, string description, Func<ConsoleCommandContext, string> handler, bool developerOnly = false)
        {
            Name = name;
            Usage = usage;
            Description = description;
            Handler = handler;
            DeveloperOnly = developerOnly;
        }

        public string Name { get; }

        public string Usage { get; }

        public string Description { get; }

        public Func<ConsoleCommandContext, string> Handler { get; }

        /// <summary>
        /// Developer commands are hidden and refused unless developer mode is on
        /// (master prompt section 46: ordinary players must not reach them).
        /// </summary>
        public bool DeveloperOnly { get; }
    }
}
