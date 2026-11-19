using System.Text;
using Microsoft.Data.Sqlite;
using TokenTracker.Core.Storage;
using TokenTracker.Server;

namespace TokenTracker.Core.Tests;

public sealed class SessionEventLogMonitorTests
{
    [Fact]
    public async Task ReadsAppendTailAndDeduplicatesEventIds()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(directory, "session-1");
        var file = Path.Combine(sessionDirectory, "events.jsonl");

        try
        {
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(
                file,
                "{\"id\":\"event-1\",\"type\":\"tool.start\"}\n",
                TestContext.Current.CancellationToken);

            var hub = new RealtimeHub();
            var monitor = new SessionEventLogMonitor(directory, hub);

            await monitor.RefreshAsync(TestContext.Current.CancellationToken);
            var first = monitor.GetStatus();
            Assert.Equal(1, first.MonitoredSessions);
            Assert.Equal(0, first.EventsRead);
            Assert.Equal(0, first.UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-2\",\"type\":\"tool.end\",\"timestamp\":\"2026",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);

            var tail = monitor.GetStatus();
            Assert.Equal(0, tail.UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "-01-01T00:00:00Z\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);

            var complete = monitor.GetStatus();
            Assert.Equal(1, complete.EventsRead);
            Assert.Equal(1, complete.UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-2\",\"type\":\"tool.end\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);

            await monitor.RefreshAsync(TestContext.Current.CancellationToken);
            var repeated = monitor.GetStatus();
            Assert.Equal(2, repeated.EventsRead);
            Assert.Equal(1, repeated.UniqueEvents);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResetsOffsetWhenEventLogIsTruncated()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(directory, "session-1");
        var file = Path.Combine(sessionDirectory, "events.jsonl");

        try
        {
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(
                file,
                "{\"id\":\"event-old-and-longer\",\"type\":\"session.start\"}\n",
                TestContext.Current.CancellationToken);

            var monitor = new SessionEventLogMonitor(
                directory,
                new RealtimeHub());
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                file,
                "{\"id\":\"event-new\",\"type\":\"session.start\"}\n",
                TestContext.Current.CancellationToken);
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);

            var status = monitor.GetStatus();
            Assert.True(
                status.EventsRead == 1 && status.UniqueEvents == 1,
                $"eventsRead={status.EventsRead}, uniqueEvents={status.UniqueEvents}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResumesFromPersistedCursorWithoutReplayingHistory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(directory, "session-1");
        var file = Path.Combine(sessionDirectory, "events.jsonl");
        var store = new SpanStore(Path.Combine(directory, "tokentracker.db"));

        try
        {
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(
                file,
                "{\"id\":\"event-history\",\"type\":\"session.start\"}\n",
                TestContext.Current.CancellationToken);

            var firstMonitor = new SessionEventLogMonitor(
                directory,
                new RealtimeHub(),
                store);
            await firstMonitor.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, firstMonitor.GetStatus().EventsRead);

            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-1\",\"type\":\"tool.end\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            await firstMonitor.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, firstMonitor.GetStatus().UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-2\",\"type\":\"tool.end\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            var resumedMonitor = new SessionEventLogMonitor(
                directory,
                new RealtimeHub(),
                store);
            await resumedMonitor.RefreshAsync(TestContext.Current.CancellationToken);

            var resumed = resumedMonitor.GetStatus();
            Assert.Equal(1, resumed.EventsRead);
            Assert.Equal(1, resumed.UniqueEvents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SkipsOversizedLinesAndContinuesWithFollowingEvents()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(directory, "session-1");
        var file = Path.Combine(sessionDirectory, "events.jsonl");

        try
        {
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(
                file,
                string.Empty,
                TestContext.Current.CancellationToken);

            var store = new SpanStore(Path.Combine(directory, "tokentracker.db"));
            var monitor = new SessionEventLogMonitor(
                directory,
                new RealtimeHub(),
                store);
            await monitor.RefreshAsync(TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"oversized\",\"payload\":\"" +
                new string('x', 3 * 1024 * 1024) +
                "\"}\n{\"id\":\"event-after-oversized\",\"type\":\"tool.end\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);

            for (var attempt = 0;
                 attempt < 20 && monitor.GetStatus().UniqueEvents == 0;
                 attempt++)
            {
                await monitor.RefreshAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, monitor.GetStatus().UniqueEvents);

            // The skipped line must not push the saved cursor past real data:
            // after a restart the next event has to be read again.
            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-after-restart\",\"type\":\"tool.end\"}\n",
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            var restarted = new SessionEventLogMonitor(
                directory,
                new RealtimeHub(),
                store);
            for (var attempt = 0;
                 attempt < 20 && restarted.GetStatus().UniqueEvents == 0;
                 attempt++)
            {
                await restarted.RefreshAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, restarted.GetStatus().UniqueEvents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
