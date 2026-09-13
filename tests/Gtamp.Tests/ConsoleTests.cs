using System;
using Gtamp.Client.Ui;
using Gtamp.Shared.Diagnostics;
using Xunit;

namespace Gtamp.Tests
{
    public class ConsoleTests
    {
        private static (DeveloperConsole Console, LogBus Log, NullClipboard Clipboard) Build()
        {
            var clipboard = new NullClipboard();
            var console = new DeveloperConsole(clipboard);
            var log = new LogBus();
            log.AddSink(console);
            return (console, log, clipboard);
        }

        [Fact]
        public void LogEntriesReachTheConsole()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            log.Info(LogCategory.Network, "client connected");
            log.Warning(LogCategory.Network, "snapshot delay 120ms");
            log.Error(LogCategory.Entity, "vehicle #152 state mismatch");

            Assert.Equal(3, console.EntryCount);
        }

        [Fact]
        public void SeverityFiltersSelectTheRightLines()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            log.Debug(LogCategory.General, "debug line");
            log.Info(LogCategory.General, "info line");
            log.Warning(LogCategory.General, "warning line");
            log.Error(LogCategory.General, "error line");
            log.Critical(LogCategory.General, "critical line");

            console.Filter = ConsoleFilter.Error;

            // Error selects errors and criticals: when hunting a failure you want both.
            Assert.Equal(2, console.FilteredEntries().Count);

            console.Filter = ConsoleFilter.Critical;
            Assert.Single(console.FilteredEntries());

            console.Filter = ConsoleFilter.Debug;
            Assert.Single(console.FilteredEntries());

            console.Filter = ConsoleFilter.All;
            Assert.Equal(5, console.FilteredEntries().Count);
        }

        [Fact]
        public void CategoryFiltersSelectTheRightSubsystem()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            log.Info(LogCategory.Network, "net line");
            log.Info(LogCategory.Server, "server line");
            log.Info(LogCategory.Mod, "mod line");
            log.Info(LogCategory.Security, "security line");

            console.Filter = ConsoleFilter.Network;
            Assert.Single(console.FilteredEntries());
            Assert.Contains("net line", console.FilteredEntries()[0].Message);

            console.Filter = ConsoleFilter.Security;
            Assert.Contains("security line", console.FilteredEntries()[0].Message);
        }

        [Fact]
        public void SearchMatchesMessageTagLevelAndEntryId()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            LogEntry first = log.Error(LogCategory.Entity, "vehicle #152 state mismatch", detail: null, tag: "entity:152");
            log.Info(LogCategory.Client, "player joined");

            console.SearchQuery = "152";
            Assert.Single(console.FilteredEntries());

            console.SearchQuery = "entity:152";
            Assert.Single(console.FilteredEntries());

            console.SearchQuery = "error";
            Assert.Single(console.FilteredEntries());

            // An all-digit query is an exact error-id lookup.
            console.SearchQuery = first.Id.ToString();
            Assert.Single(console.FilteredEntries());
            Assert.Equal(first.Id, console.FilteredEntries()[0].Id);

            console.SearchQuery = "nothing here";
            Assert.Empty(console.FilteredEntries());
        }

        [Fact]
        public void FilterAndSearchCombine()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            log.Error(LogCategory.Network, "entity 7 desynced");
            log.Info(LogCategory.Network, "entity 7 spawned");

            console.Filter = ConsoleFilter.Error;
            console.SearchQuery = "entity 7";

            Assert.Single(console.FilteredEntries());
            Assert.Contains("desynced", console.FilteredEntries()[0].Message);
        }

        [Fact]
        public void ErrorsAreColouredRedAndCriticalsGetTheirOwnRole()
        {
            (DeveloperConsole console, LogBus log, _) = Build();

            log.Error(LogCategory.Network, "boom");
            log.Critical(LogCategory.Server, "worse");
            log.Warning(LogCategory.Server, "careful");
            log.Success(LogCategory.Server, "fine");

            var lines = console.VisibleLines();
            Assert.Equal(ConsoleColorRole.Error, lines[0].Role);
            Assert.Equal(ConsoleColorRole.Critical, lines[1].Role);
            Assert.Equal(ConsoleColorRole.Warning, lines[2].Role);
            Assert.Equal(ConsoleColorRole.Success, lines[3].Role);

            (byte r, byte g, byte b, _) = ConsolePalette.Rgba(ConsoleColorRole.Error);
            Assert.True(r > 200 && g < 100 && b < 100, "errors must read as red");
        }

        [Fact]
        public void TheBufferIsBoundedAndKeepsTheNewestLines()
        {
            var console = new DeveloperConsole(capacity: 64);
            var log = new LogBus();
            log.AddSink(console);

            for (int i = 0; i < 500; i++)
            {
                log.Info(LogCategory.General, "line " + i);
            }

            Assert.Equal(64, console.EntryCount);
            Assert.Contains("line 499", console.RecentEntries(1)[0].Message);
        }

        [Fact]
        public void ScrollbackIsClampedToTheAvailableLines()
        {
            (DeveloperConsole console, LogBus log, _) = Build();
            console.VisibleLineCount = 5;

            for (int i = 0; i < 20; i++)
            {
                log.Info(LogCategory.General, "line " + i);
            }

            console.Scroll(1000);
            Assert.Equal(15, console.ScrollOffset);

            console.Scroll(-1000);
            Assert.Equal(0, console.ScrollOffset);
            Assert.Equal(5, console.VisibleLines().Count);
        }

        [Fact]
        public void UnknownCommandsAreReportedRatherThanIgnored()
        {
            (DeveloperConsole console, _, _) = Build();
            string result = console.Submit("definitely-not-a-command");
            Assert.Contains("Unknown command", result);
        }

        [Fact]
        public void DeveloperCommandsAreRefusedUntilDeveloperModeIsOn()
        {
            (DeveloperConsole console, _, _) = Build();
            console.RegisterCommand(new ConsoleCommand("danger", "danger", "test", _ => "ran", developerOnly: true));

            Assert.Contains("developer command", console.Submit("danger"));

            console.DeveloperMode = true;
            Assert.Equal("ran", console.Submit("danger"));
        }

        [Fact]
        public void HelpHidesDeveloperCommandsFromOrdinaryPlayers()
        {
            (DeveloperConsole console, _, _) = Build();
            console.RegisterCommand(new ConsoleCommand("danger", "danger", "test", _ => "ran", developerOnly: true));

            Assert.DoesNotContain("danger", console.Submit("help"));

            console.DeveloperMode = true;
            Assert.Contains("danger", console.Submit("help"));
        }

        [Fact]
        public void ACommandThatThrowsIsReportedAndDoesNotEscape()
        {
            (DeveloperConsole console, _, _) = Build();
            console.RegisterCommand(new ConsoleCommand("boom", "boom", "test", _ => throw new InvalidOperationException("bang")));

            string result = console.Submit("boom");
            Assert.Contains("InvalidOperationException", result);
            Assert.Contains("bang", result);
        }

        [Fact]
        public void CopyPutsTheRequestedShapeOnTheClipboard()
        {
            (DeveloperConsole console, LogBus log, NullClipboard clipboard) = Build();
            log.Error(LogCategory.Entity, "failed to deserialize entity", "at Gtamp.Shared.Net.NetReader.Require()");

            console.Submit("copy message");
            Assert.Equal("failed to deserialize entity", clipboard.LastText);

            console.Submit("copy stack");
            Assert.Contains("NetReader.Require", clipboard.LastText);

            console.Submit("copy full");
            Assert.Contains("failed to deserialize entity", clipboard.LastText);
            Assert.Contains("NetReader.Require", clipboard.LastText);
        }

        [Fact]
        public void CopyTargetsTheMostRecentProblemNotTheMostRecentLine()
        {
            (DeveloperConsole console, LogBus log, NullClipboard clipboard) = Build();

            log.Error(LogCategory.Server, "the real problem");
            log.Info(LogCategory.Server, "some chatter afterwards");
            log.Debug(LogCategory.Server, "more chatter");

            console.Submit("copy message");
            Assert.Equal("the real problem", clipboard.LastText);
        }

        [Fact]
        public void FilterAndSearchCommandsDriveTheModel()
        {
            (DeveloperConsole console, LogBus log, _) = Build();
            log.Error(LogCategory.Network, "packet loss spike");
            log.Info(LogCategory.Network, "all good");

            console.Submit("filter error");
            Assert.Equal(ConsoleFilter.Error, console.Filter);
            Assert.Single(console.FilteredEntries());

            console.Submit("filter all");
            console.Submit("search packet loss");
            Assert.Equal("packet loss", console.SearchQuery);

            // The console echoes commands into its own buffer, so the echo of the
            // search itself matches too; what matters is that the log line is found
            // and the unrelated one is not.
            Assert.Contains(console.FilteredEntries(), e => e.Message.Contains("packet loss spike"));
            Assert.DoesNotContain(console.FilteredEntries(), e => e.Message.Contains("all good"));

            console.Submit("search");
            Assert.Equal(string.Empty, console.SearchQuery);
        }

        [Fact]
        public void InputHistoryWalksBackwardsAndForwards()
        {
            (DeveloperConsole console, _, _) = Build();
            console.Submit("help");
            console.Submit("clear");

            Assert.Equal("clear", console.HistoryPrevious());
            Assert.Equal("help", console.HistoryPrevious());
            Assert.Equal("clear", console.HistoryNext());
            Assert.Equal(string.Empty, console.HistoryNext());
        }

        [Fact]
        public void ClearEmptiesTheBuffer()
        {
            (DeveloperConsole console, LogBus log, _) = Build();
            log.Info(LogCategory.General, "something");
            console.Submit("clear");

            // Only the echo of the command itself and its result remain.
            Assert.True(console.EntryCount <= 2);
        }
    }
}
