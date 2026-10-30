using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Tests;

public sealed class SpanStoreTests
{
    [Fact]
    public void StoresAndAggregatesMainAndUtilitySpans()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            var inserted = store.InsertMany(new[]
            {
                Span("t1", "s1", "session-1", "deepseek-flash", 1_000, 1_000, 500, 100, 0.1),
                Span("t2", "s2", "session-2", "deepseek-v4-pro", 2_000, 2_000, 0, 200, 0.2),
                Span("t3", "s3", null, "deepseek-flash", 3_000, 100, 50, 20, null),
            });

            Assert.Equal(3, inserted);

            var summary = store.GetSummary(0);
            Assert.Equal(2, summary.Calls);
            Assert.Equal(3_000, summary.InputTokens);
            Assert.Equal(500, summary.CacheReadTokens);
            Assert.Equal(300, summary.OutputTokens);
            Assert.Equal(2, summary.Sessions);
            Assert.Equal(0.3, summary.CostCny, 8);
            Assert.Equal(0, summary.UnpricedCalls);
            Assert.Equal(500d / 3_000, summary.HitRate!.Value, 8);

            var withUtility = store.GetSummary(0, includeUtility: true);
            Assert.Equal(3, withUtility.Calls);
            Assert.Equal(3_100, withUtility.InputTokens);
            Assert.Equal(550, withUtility.CacheReadTokens);
            Assert.Equal(320, withUtility.OutputTokens);
            Assert.Equal(1, withUtility.UnpricedCalls);

            Assert.Null(store.GetSetting("retention_days"));
            store.SetSetting("retention_days", "30");
            Assert.Equal("30", store.GetSetting("retention_days"));

            var sessions = store.GetSessions(0);
            Assert.Equal(2, sessions.Count);
            Assert.Equal("session-2", sessions[0].SessionId);
            Assert.Equal("session-1", sessions[1].SessionId);

            var calls = store.GetCalls(0);
            Assert.Equal(2, calls.Count);
            Assert.Equal("session-2", calls[0].SessionId);

            var callsWithUtility = store.GetCalls(0, includeUtility: true);
            Assert.Equal(3, callsWithUtility.Count);
            Assert.Equal("util", callsWithUtility[0].KindText);

            var models = store.GetModels(0);
            Assert.Equal(2, models.Count);
            Assert.Contains(models, row => row.Model == "deepseek-flash");
            Assert.Contains(models, row => row.Model == "deepseek-v4-pro");

            var modelsWithUtility = store.GetModels(0, includeUtility: true);
            Assert.Contains(
                modelsWithUtility,
                row => row.Model == "deepseek-flash" && row.Calls == 2);

            var flashSummary = store.GetModelSummary("deepseek-flash", 0);
            Assert.Equal(1, flashSummary.Calls);
            Assert.Equal(1_000, flashSummary.InputTokens);
            Assert.Equal(500, flashSummary.CacheReadTokens);

            var flashWithUtility = store.GetModelSummary(
                "deepseek-flash",
                0,
                includeUtility: true);
            Assert.Equal(2, flashWithUtility.Calls);
            Assert.Equal(1_100, flashWithUtility.InputTokens);

            var flashSessions = store.GetSessionsForModel("deepseek-flash", 0);
            Assert.Single(flashSessions);
            Assert.Equal("session-1", flashSessions[0].SessionId);
            Assert.Equal(1, flashSessions[0].Calls);

            var proSessions = store.GetSessionsForModel("deepseek-v4-pro", 0);
            Assert.Single(proSessions);
            Assert.Equal("session-2", proSessions[0].SessionId);

            Assert.Equal(1, store.DeleteOlderThan(1_500));
            Assert.Equal(1, store.GetSummary(0).Calls);
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
        string? sessionId,
        string model,
        long startMs,
        long inputTokens,
        long cacheReadTokens,
        long outputTokens,
        double? cost) =>
        new()
        {
            TraceId = traceId,
            SpanId = spanId,
            Name = "chat test",
            StartTimeMs = startMs,
            EndTimeMs = startMs + 100,
            ServiceName = "copilot-chat",
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = inputTokens,
            CacheReadTokens = cacheReadTokens,
            OutputTokens = outputTokens,
            HitRate = inputTokens > 0 ? (double)cacheReadTokens / inputTokens : null,
            CostCny = cost,
            PricingPeriod = "peak",
            ReceivedAtMs = startMs,
        };
}
