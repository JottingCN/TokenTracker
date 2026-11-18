using System.Text;
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
                "{\"id\":\"event-1\",\"type\":\"tool.start\"}\n");

            var hub = new RealtimeHub();
            var monitor = new SessionEventLogMonitor(directory, hub);

            await monitor.RefreshAsync(CancellationToken.None);
            var first = monitor.GetStatus();
            Assert.Equal(1, first.MonitoredSessions);
            Assert.Equal(1, first.EventsRead);
            Assert.Equal(1, first.UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "{\"id\":\"event-2\",\"type\":\"tool.end\",\"timestamp\":\"2026",
                Encoding.UTF8);
            await monitor.RefreshAsync(CancellationToken.None);

            var tail = monitor.GetStatus();
            Assert.Equal(1, tail.UniqueEvents);

            await File.AppendAllTextAsync(
                file,
                "-01-01T00:00:00Z\"}\n",
                Encoding.UTF8);
            await monitor.RefreshAsync(CancellationToken.None);

            var complete = monitor.GetStatus();
            Assert.Equal(2, complete.EventsRead);
            Assert.Equal(2, complete.UniqueEvents);

            await monitor.RefreshAsync(CancellationToken.None);
            var repeated = monitor.GetStatus();
            Assert.Equal(2, repeated.EventsRead);
            Assert.Equal(2, repeated.UniqueEvents);
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
                "{\"id\":\"event-old\",\"type\":\"session.start\"}\n");

            var monitor = new SessionEventLogMonitor(
                directory,
                new RealtimeHub());
            await monitor.RefreshAsync(CancellationToken.None);

            await File.WriteAllTextAsync(
                file,
                "{\"id\":\"event-new\",\"type\":\"session.start\"}\n");
            await monitor.RefreshAsync(CancellationToken.None);

            var status = monitor.GetStatus();
            Assert.True(
                status.EventsRead == 2 && status.UniqueEvents == 2,
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
}
