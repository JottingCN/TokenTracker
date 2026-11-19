using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Tests;

public sealed class WorkspaceStatsTests
{
    [Fact]
    public void SummaryCarriesTheLatencyTheDashboardCardsRender()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SpanStore(Path.Combine(directory, "tokentracker.db"));
            store.InsertMany(new[]
            {
                Span("t1", "s1", "session-1", 1_000, 100, 2_100),
                Span("t2", "s2", "session-1", 2_000, 900, 5_900),
            });

            // The dashboard reads GetSummary, not GetModelSummary: TTFT and the
            // generation speed have to come from this projection too, or its
            // latency cards stay empty while the per-model page has numbers.
            var summary = store.GetSummary(0, includeUtility: true);

            Assert.Equal(2, summary.TtftSamples);
            Assert.Equal(500, summary.AvgTtftMs!.Value, 3);
            Assert.Equal(100, summary.MinTtftMs);
            Assert.Equal(900, summary.MaxTtftMs);
            // Generation speed = output / (duration - ttft):
            // 100/1000 ms and 100/3000 ms, weighted to 200/4000 ms.
            Assert.Equal(33.333, summary.MinTokensPerSecond!.Value, 2);
            Assert.Equal(100, summary.MaxTokensPerSecond!.Value, 2);
            Assert.Equal(50, summary.TokensPerSecond!.Value, 2);
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
    public void SessionModelUsageGroupsMainCallsBySessionAndModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SpanStore(Path.Combine(directory, "tokentracker.db"));
            store.InsertMany(new[]
            {
                Span("t1", "s1", "session-a", 1_000, 100, 2_100, model: "model-x"),
                Span("t2", "s2", "session-a", 5_000, 100, 2_100, model: "model-y"),
                Span("t3", "s3", "session-b", 9_000, 100, 2_100, model: "model-x"),
                // Utility calls never count towards workspace usage.
                Span("t4", "s4", "session-a", 1_500, 100, 2_100,
                    model: "model-x", isMain: false),
            });
            store.UpsertSessionWorkspaces(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["session-a"] = @"d:\work\alpha",
                    ["session-b"] = @"d:\work\beta",
                });

            var usage = store.GetSessionModelUsage(0, long.MaxValue);

            Assert.Equal(3, usage.Count);
            var sessionAModelX = Assert.Single(
                usage,
                row => row.SessionId == "session-a" && row.Model == "model-x");
            Assert.Equal(1, sessionAModelX.Calls);
            var sessionAModelY = Assert.Single(
                usage,
                row => row.SessionId == "session-a" && row.Model == "model-y");
            Assert.Equal(1, sessionAModelY.Calls);
            Assert.DoesNotContain(usage, row => row.Calls > 1);

            // The dashboard totals include utility calls (summary and by-model
            // cards do), so the workspace card follows the same rule.
            var withUtility = store.GetSessionModelUsage(
                0,
                long.MaxValue,
                includeUtility: true);
            var utilityIncluded = Assert.Single(
                withUtility,
                row => row.SessionId == "session-a" && row.Model == "model-x");
            Assert.Equal(2, utilityIncluded.Calls);

            var workspaces = store.GetSessionWorkspaces();
            Assert.Equal(@"d:\work\alpha", workspaces["session-a"]);
            Assert.Equal(@"d:\work\beta", workspaces["session-b"]);
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

    private static OtlpSpanRecord Span(
        string traceId,
        string spanId,
        string sessionId,
        long startMs,
        long ttftMs,
        long endMs,
        string model = "workspace-model",
        bool isMain = true) =>
        new()
        {
            TraceId = traceId,
            SpanId = spanId,
            Name = $"chat {model}",
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            ServiceName = "copilot-chat",
            Source = "copilot-chat",
            Model = model,
            SessionId = sessionId,
            IsMain = isMain,
            InputTokens = 1_000,
            OutputTokens = 100,
            CacheReadTokens = 500,
            TtftMs = ttftMs,
            CostCny = 0.1,
            ReceivedAtMs = endMs,
        };
}
