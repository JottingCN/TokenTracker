using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;
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

    [Fact]
    public void ModelRowsCarryReasoningTokensAndCostBreakdown()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");
        var model = "breakdown-model";

        // Per 1M tokens: 1 cached input, 2 uncached input, 4 output.
        ModelPricing.SetOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>(
                model,
                new ModelPrice(
                    new PriceTier(1m, 2m, 4m),
                    new PriceTier(1m, 2m, 4m),
                    PeakAware: false)),
        });

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1",
                    "s1",
                    "session-1",
                    model,
                    1_000,
                    1_000,
                    400,
                    500,
                    cost: null,
                    reasoningTokens: 120),
                Span(
                    "t2",
                    "s2",
                    "session-1",
                    model,
                    2_000,
                    2_000,
                    0,
                    300,
                    cost: null,
                    reasoningTokens: 80),
            });
            Assert.Equal(2, store.RecomputeCosts(model));

            var row = Assert.Single(store.GetModels(0));
            Assert.Equal(2, row.Calls);
            Assert.Equal(3_000, row.InputTokens);
            Assert.Equal(400, row.CacheReadTokens);
            Assert.Equal(800, row.OutputTokens);
            Assert.Equal(200, row.ReasoningTokens);
            Assert.Equal("200", row.ReasoningText);

            // The dashboard shows these buckets next to the token counts, so the
            // aggregate must be split exactly like the session rows.
            Assert.Equal(400d * 1 / 1_000_000, row.CacheCostCny, 10);
            Assert.Equal(2_600d * 2 / 1_000_000, row.UncachedCostCny, 10);
            Assert.Equal(800d * 4 / 1_000_000, row.OutputCostCny, 10);
            Assert.Equal(
                row.CacheCostCny + row.UncachedCostCny + row.OutputCostCny,
                row.CostCny,
                10);
        }
        finally
        {
            ModelPricing.SetOverrides(
                Array.Empty<KeyValuePair<string, ModelPrice>>());
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void TraceTopologyMergesCrossSourceDuplicatesAndCapsNewestNodes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // trace-old (copilot-chat) and trace-dup (agent-host) are the same
                // call reported twice; only one chat node may survive.
                command.CommandText = """
                    INSERT INTO spans (
                        trace_id, span_id, parent_span_id, name, start_time_ms,
                        end_time_ms, service_name, otel_source, model, session_id,
                        is_main, input_tokens, output_tokens, received_at_ms
                    ) VALUES
                        ('trace-old', 'span-old', NULL, 'chat', 2000, 4000,
                         'copilot-chat', 'copilot-chat', 'deepseek-flash',
                         'session-1', 1, 100, 10, 2000),
                        ('trace-dup', 'span-dup', NULL, 'chat', 2000, 4000,
                         'github-copilot', 'agent-host', 'deepseek-flash',
                         'session-1', 1, 100, 10, 2000),
                        ('trace-new', 'span-new', NULL, 'chat', 5000, 6000,
                         'copilot-chat', 'copilot-chat', 'deepseek-flash',
                         'session-1', 1, 100, 10, 5000);
                    INSERT INTO session_events (
                        trace_id, span_id, parent_span_id, operation, session_id,
                        service_name, otel_source, start_time_ms, received_at_ms
                    ) VALUES
                        ('trace-old', 'span-tool', 'span-old', 'execute_tool',
                         'session-1', 'copilot-chat', 'copilot-chat', 3000, 3000);
                    """;
                command.ExecuteNonQuery();
            }

            var nodes = store.GetTraceNodes("session-1");
            Assert.Equal(
                new[] { "trace-old:span-old", "trace-old:span-tool", "trace-new:span-new" },
                nodes.Select(node => node.Id).ToArray());
            var tool = nodes.Single(node => node.Kind == "event");
            Assert.Equal("trace-old:span-old", tool.ParentId);
            Assert.Equal(1, tool.Depth);
            Assert.Equal(
                0,
                nodes.Single(node => node.Id == "trace-old:span-old").Depth);

            // The node cap keeps the newest window instead of dropping the tail.
            var capped = store.GetTraceNodes("session-1", limit: 2);
            Assert.Equal(
                new[] { "trace-old:span-tool", "trace-new:span-new" },
                capped.Select(node => node.Id).ToArray());
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
    public void TraceBlocksAreFifoAndCarryAncestorsForSplitTraces()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            var spans = new List<OtlpSpanRecord>();
            // One long trace interleaved with short ones. A block boundary will fall
            // inside the long trace, so the child rows land in a later block.
            for (var index = 0; index < 10; index++)
            {
                spans.Add(Span(
                    "trace-long",
                    index == 0 ? "root" : $"child-{index:00}",
                    "session-1",
                    "m",
                    1_000 + index * 100,
                    100,
                    0,
                    10,
                    null,
                    parentSpanId: index == 0 ? null : "root"));
                spans.Add(Span(
                    $"trace-{index:00}",
                    $"span-{index:00}",
                    "session-1",
                    "m",
                    1_050 + index * 100,
                    100,
                    0,
                    10,
                    null));
            }

            store.InsertMany(spans);

            var seen = new List<string>();
            var previous = (Start: long.MinValue, Id: string.Empty);
            var splitChild = false;
            var blocks = 0;
            string? cursor = null;
            var more = true;
            while (more)
            {
                var page = store.GetTracePage(
                    "session-1",
                    cursor: cursor,
                    budget: 4);
                Assert.NotEmpty(page.Nodes);
                foreach (var node in page.Nodes)
                {
                    if (node.IsContext)
                    {
                        // Context rows only repeat ancestors, so they must not be
                        // counted as new stream rows.
                        Assert.Equal("context", node.Kind);
                        Assert.Equal(0, node.InputTokens + node.OutputTokens);
                        continue;
                    }

                    Assert.True(
                        node.StartTimeMs > previous.Start ||
                        (node.StartTimeMs == previous.Start &&
                         string.CompareOrdinal(node.Id, previous.Id) > 0),
                        $"row {node.Id} breaks FIFO after {previous.Id}");
                    previous = (node.StartTimeMs, node.Id);
                    seen.Add(node.Id);
                }

                // A child whose root lives in an earlier block needs a context row
                // so the indentation still reads correctly.
                if (page.Nodes.Any(node => node.IsContext))
                {
                    splitChild = true;
                    Assert.Contains(
                        page.Nodes,
                        node => node.IsContext && node.Id == "trace-long:root");
                    Assert.All(
                        page.Nodes.Where(node => node.IsContext),
                        node => Assert.Equal(0, node.Depth));
                    Assert.All(
                        page.Nodes.Where(node =>
                            !node.IsContext &&
                            node.ParentId == "trace-long:root"),
                        node => Assert.Equal(1, node.Depth));
                }

                blocks++;
                cursor = page.NextCursor;
                more = page.HasMore;
            }

            Assert.True(blocks > 1);
            Assert.True(splitChild, "the long trace was never split across blocks");
            Assert.Equal(20, seen.Count);
            Assert.Equal(20, seen.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("trace-long:root", seen[0]);
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
    public void TraceTopologyIsSortedByStartTime()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            var spans = new List<OtlpSpanRecord>();
            // Interleaved traces: trace-00 spans 1000..9000 while the later traces
            // start inside it, so ordering must come from start times, not traces.
            for (var index = 0; index < 6; index++)
            {
                spans.Add(Span(
                    "trace-long",
                    $"long-{index}",
                    "session-1",
                    "m",
                    1_000 + index * 500,
                    100,
                    0,
                    10,
                    null));
                spans.Add(Span(
                    $"trace-{index:00}",
                    $"span-{index:00}",
                    "session-1",
                    "m",
                    1_200 + index * 500,
                    100,
                    0,
                    10,
                    null));
            }

            store.InsertMany(spans);

            var nodes = store.GetTraceNodes("session-1");
            Assert.Equal(12, nodes.Count);
            // FIFO: strictly ascending by start time, traces interleaved as needed.
            Assert.Equal(
                nodes.Select(node => node.StartTimeMs).OrderBy(value => value).ToList(),
                nodes.Select(node => node.StartTimeMs).ToList());
            Assert.Equal("trace-long:long-0", nodes[0].Id);

            // The node cap keeps the newest window.
            var capped = store.GetTraceNodes("session-1", limit: 3);
            Assert.Equal(3, capped.Count);
            Assert.Equal(nodes[^1].Id, capped[^1].Id);
            Assert.Equal(
                capped.Select(node => node.StartTimeMs).ToList(),
                capped.Select(node => node.StartTimeMs).OrderBy(value => value).ToList());
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
    public void TraceTopologyCollapsesCrossSourceDuplicates()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // The same call reported by both pipelines; only the extension-host
                // copy may survive, whichever order the rows were read in.
                command.CommandText = """
                    INSERT INTO spans (
                        trace_id, span_id, parent_span_id, name, start_time_ms,
                        end_time_ms, service_name, otel_source, model, session_id,
                        is_main, input_tokens, output_tokens, received_at_ms
                    ) VALUES
                        ('trace-a', 'span-a', NULL, 'chat', 9000, 9100,
                         'copilot-chat', 'copilot-chat', 'm', 'session-1', 1, 100, 10, 9000),
                        ('trace-b', 'span-b', NULL, 'chat', 5000, 5100,
                         'copilot-chat', 'copilot-chat', 'm', 'session-1', 1, 100, 10, 5000),
                        ('trace-c', 'span-c', NULL, 'chat', 5000, 5100,
                         'github-copilot', 'agent-host', 'm', 'session-1', 1, 100, 10, 5000);
                    """;
                command.ExecuteNonQuery();
            }

            var nodes = store.GetTraceNodes("session-1");
            Assert.Equal(
                new[] { "trace-b:span-b", "trace-a:span-a" },
                nodes.Select(node => node.Id).ToArray());
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
    public void UpgradesALegacyDatabaseInPlaceWithoutLosingRows()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            // Shape of a pre-versioning database: no newer columns, no
            // model_profiles/session_workspaces, plus a leftover table the app no
            // longer writes.
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE spans (
                        trace_id TEXT NOT NULL,
                        span_id TEXT NOT NULL,
                        name TEXT,
                        start_time_ms INTEGER NOT NULL,
                        end_time_ms INTEGER,
                        service_name TEXT,
                        model TEXT,
                        session_id TEXT,
                        is_main INTEGER NOT NULL,
                        input_tokens INTEGER NOT NULL DEFAULT 0,
                        output_tokens INTEGER NOT NULL DEFAULT 0,
                        cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                        cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                        reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                        hit_rate REAL,
                        cost_cny REAL,
                        pricing_period TEXT,
                        received_at_ms INTEGER NOT NULL,
                        PRIMARY KEY (trace_id, span_id)
                    );
                    INSERT INTO spans (
                        trace_id, span_id, name, start_time_ms, end_time_ms,
                        service_name, model, session_id, is_main,
                        input_tokens, output_tokens, cache_read_tokens,
                        reasoning_tokens, cost_cny, received_at_ms
                    ) VALUES (
                        'legacy-trace', 'legacy-span', 'chat', 1000, 2000,
                        'copilot-chat', 'legacy-model', 'legacy-session', 1,
                        500, 50, 100, 0, 0.25, 2000
                    );
                    CREATE TABLE otel_log_events (body TEXT);
                    INSERT INTO otel_log_events (body) VALUES ('keep me');
                    """;
                command.ExecuteNonQuery();
            }

            var store = new SpanStore(databasePath);

            // Data survives, and the schema is stamped so the next start is a no-op.
            Assert.Equal(SpanStore.SchemaVersion, store.GetSchemaVersion());
            Assert.False(store.IsNewerSchema);
            var summary = store.GetSummary(0);
            Assert.Equal(1, summary.Calls);
            Assert.Equal(500, summary.InputTokens);
            Assert.Equal(0.25, summary.CostCny, 8);

            var call = Assert.Single(store.GetCallsForSession("legacy-session"));
            Assert.Equal("legacy-model", call.Model);

            using var connection2 = new SqliteConnection($"Data Source={databasePath}");
            connection2.Open();
            using (var command = connection2.CreateCommand())
            {
                // Additive migrations only: the legacy table is left alone instead
                // of being dropped during an upgrade.
                command.CommandText = "SELECT COUNT(*) FROM otel_log_events;";
                Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));

                command.CommandText = "PRAGMA table_info(spans);";
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1));
                }

                Assert.Contains("otel_source", columns);
                Assert.Contains("ttft_ms", columns);
                Assert.Contains("window_session_id", columns);
                Assert.Contains("uncached_cost_cny", columns);
            }

            // Reopening an already-upgraded database must not touch the rows.
            var reopened = new SpanStore(databasePath);
            Assert.Equal(SpanStore.SchemaVersion, reopened.GetSchemaVersion());
            Assert.Equal(1, reopened.GetSummary(0).Calls);

            var stats = reopened.GetStorageStats();
            Assert.Equal(SpanStore.SchemaVersion, stats.SchemaVersion);
            Assert.Equal(SpanStore.SchemaVersion, stats.SupportedSchemaVersion);
            Assert.False(stats.SchemaFromNewerBuild);
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
    public void LeavesANewerSchemaUntouched()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            Directory.CreateDirectory(directory);
            var futureVersion = SpanStore.SchemaVersion + 5;
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {futureVersion};";
                command.ExecuteNonQuery();
            }

            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span("t1", "s1", "session-1", "m", 1_000, 100, 0, 10, 0.5),
            });

            // A newer file keeps its version and still serves reads.
            Assert.Equal(futureVersion, store.GetSchemaVersion());
            Assert.True(store.IsNewerSchema);
            Assert.Equal(1, store.GetSummary(0).Calls);
            Assert.True(store.GetStorageStats().SchemaFromNewerBuild);
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
    public void CallSamplesMatchTheFullCallProjection()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span("t1", "s1", "session-1", "m", 1_000, 100, 20, 30, 0.5,
                    ttftMs: 200, durationMs: 1_200),
                Span("t2", "s2", "session-1", "m", 2_000, 50, 0, 10, null,
                    ttftMs: 100, durationMs: 900),
                Span("t3", "s3", "session-1", "m", 3_000, 80, 10, 5, 0.25,
                    ttftMs: null, durationMs: 400),
                Span("t4", "s4", "session-2", "m", 4_000, 999, 0, 999, 9,
                    ttftMs: 50, durationMs: 500),
            });

            var calls = store.GetCallsForSession(
                "session-1",
                source: "copilot-chat");
            var samples = store.GetSessionCallSamples(
                "session-1",
                0,
                long.MaxValue,
                "copilot-chat");

            Assert.Equal(calls.Count, samples.Count);
            Assert.Equal(calls.Sum(call => call.InputTokens),
                samples.Sum(sample => sample.InputTokens));
            Assert.Equal(calls.Sum(call => call.CacheReadTokens),
                samples.Sum(sample => sample.CacheReadTokens));
            Assert.Equal(calls.Sum(call => call.OutputTokens),
                samples.Sum(sample => sample.OutputTokens));
            Assert.Equal(calls.Sum(call => call.CostCny ?? 0),
                samples.Sum(sample => sample.CostCny ?? 0), 10);
            Assert.Equal(calls.Count(call => call.CostCny is null),
                samples.Count(sample => sample.CostCny is null));
            Assert.Equal(
                calls.Where(call => call.TtftMs is > 0)
                    .Select(call => call.TtftMs)
                    .OrderBy(value => value)
                    .ToList(),
                samples.Where(sample => sample.TtftMs is > 0)
                    .Select(sample => sample.TtftMs)
                    .OrderBy(value => value)
                    .ToList());
            Assert.Equal(
                calls.Select(call => call.DurationMs).OrderBy(value => value)
                    .ToList(),
                samples.Select(sample => sample.DurationMs).OrderBy(value => value)
                    .ToList());
            Assert.Equal(
                calls.Select(call => call.TokensPerSecond).OrderBy(value => value)
                    .ToList(),
                samples.Select(sample => sample.OutputTokens >= 5 &&
                        sample.DurationMs is > 0 &&
                        sample.TtftMs is > 0 &&
                        sample.DurationMs.Value - sample.TtftMs.Value >= 50
                    ? sample.OutputTokens * 1_000d /
                        (sample.DurationMs.Value - sample.TtftMs!.Value)
                    : (double?)null)
                    .OrderBy(value => value)
                    .ToList());
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
    public void KeepsCrossSourceSpansSeparate()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            var start = 1793350721268L;
            var agentHost = Span(
                "trace-agent", "span-agent", "session-1", "deepseek-flash",
                start, 27_473, 0, 30, null,
                serviceName: "github-copilot",
                reasoningTokens: 0,
                source: "agent-host");
            var extension = Span(
                "trace-extension", "span-extension", "session-1", "deepseek-flash",
                start + 304, 27_473, 1_152, 30, null,
                serviceName: "copilot-chat",
                reasoningTokens: 28);

            store.InsertMany(new[] { agentHost });
            store.InsertMany(new[] { extension });

            var calls = store.GetCalls(0, includeUtility: true);
            Assert.Equal(2, calls.Count);
            Assert.Contains(calls, call => call.Source == "agent-host");
            Assert.Contains(calls, call => call.Source == "copilot-chat");
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
    public void DeduplicatesToolEventsByCallIdWithinSource()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertEvents(new[]
            {
                Event("e1", "s1", "agent-host", "call-1", "read_file", 1_000),
                Event("e2", "s2", "agent-host", "call-1", "read_file", 1_100),
                Event("e3", "s3", "copilot-chat", "call-1", "read_file", 1_200),
            });

            var events = store.GetSessionEvents("session-1");
            Assert.Contains(
                events,
                row => row.Source == "agent-host" && row.Count == 1);
            Assert.Contains(
                events,
                row => row.Source == "copilot-chat" && row.Count == 1);

            var tools = store.GetSessionTools("session-1");
            Assert.Equal(2, tools.Count);
            Assert.Contains(
                tools,
                row => row.Source == "agent-host" && row.Calls == 1);
            Assert.Contains(
                tools,
                row => row.Source == "copilot-chat" && row.Calls == 1);
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
    public void MergesCrossSourceCallsForCombinedSource()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t-copilot", "s-copilot", "session-1", "deepseek-flash",
                    1_000, 1_000, 50, 100, 0.2,
                    ttftMs: 100,
                    source: "copilot-chat"),
                Span(
                    "t-agent", "s-agent", "session-1", "deepseek-flash",
                    1_050, 1_000, 0, 100, null,
                    serviceName: "github-copilot",
                    source: "agent-host"),
            });

            store.EnsureCombinedSpans();

            var combined = Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Source = "combined",
            }));
            Assert.Equal("combined", combined.Source);
            Assert.Equal(100, combined.TtftMs);
            Assert.Equal(50, combined.CacheReadTokens);
            Assert.Null(combined.CostCny);
            Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Source = "copilot-chat",
            }));
            Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Source = "agent-host",
            }));
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
    public void CombinedMergeUsesIntervalOverlapAndRecomputesCost()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");
        var price = new ModelPrice(
            new PriceTier(0.02m, 1m, 4m),
            new PriceTier(0.02m, 1m, 4m),
            PeakAware: false);

        ModelPricing.SetOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>("deepseek-flash", price),
        });

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO spans (
                        trace_id, span_id, name, start_time_ms, end_time_ms,
                        service_name, otel_source, model, session_id, is_main,
                        input_tokens, output_tokens, cache_read_tokens,
                        reasoning_tokens, received_at_ms
                    ) VALUES
                        ('trace-a', 'span-a', 'chat', 1000, 5010,
                         'github-copilot', 'agent-host', 'deepseek-flash',
                         'session-1', 1, 29726, 61, 0, 0, 1000),
                        ('trace-b', 'span-b', 'chat', 3956, 4969,
                         'copilot-chat', 'copilot-chat', 'deepseek-flash',
                         'session-1', 1, 29726, 61, 8576, 12, 3956);
                    """;
                command.ExecuteNonQuery();
            }

            store.EnsureCombinedSpans();

            var combined = Assert.Single(store.GetCallsForSession(
                "session-1",
                source: "combined"));
            Assert.Equal(8576, combined.CacheReadTokens);
            Assert.Equal(
                0.02156552,
                Assert.IsType<double>(combined.CostCny),
                8);

            Assert.Single(store.GetCallsForSession(
                "session-1",
                source: "copilot-chat"));
            Assert.Single(store.GetCallsForSession(
                "session-1",
                source: "agent-host"));
        }
        finally
        {
            ModelPricing.SetOverrides(
                Array.Empty<KeyValuePair<string, ModelPrice>>());
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RecomputeUsesEndTimeForPeakPeriod()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");
        var model = "boundary-recompute-model";
        // 2026-11-04 is a Wednesday; the request crosses 14:00 Beijing
        // (afternoon peak start) and must settle at the peak rate.
        var startMs = new DateTimeOffset(2026, 11, 4, 5, 59, 55, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var endMs = new DateTimeOffset(2026, 11, 4, 6, 0, 8, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        ModelPricing.SetOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>(
                model,
                new ModelPrice(
                    new PriceTier(0.02m, 1m, 4m),
                    new PriceTier(0.04m, 2m, 8m))),
        });

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO spans (
                        trace_id, span_id, name, start_time_ms, end_time_ms,
                        service_name, otel_source, model, session_id, is_main,
                        input_tokens, output_tokens, cache_read_tokens,
                        reasoning_tokens, received_at_ms, pricing_period, cost_cny
                    ) VALUES (
                        'trace-b', 'span-b', 'chat', @start, @end,
                        'copilot-chat', 'copilot-chat', @model,
                        'session-1', 1, 1000, 10, 0, 0, @start, 'idle', 0.0014
                    );
                    """;
                command.Parameters.AddWithValue("@start", startMs);
                command.Parameters.AddWithValue("@end", endMs);
                command.Parameters.AddWithValue("@model", model);
                command.ExecuteNonQuery();
            }

            var stale = Assert.Single(store.GetCallsForSession(
                "session-1",
                source: "copilot-chat"));
            Assert.Equal("idle", stale.PricingPeriod);

            Assert.Equal(1, store.RecomputeCosts(model));

            var row = Assert.Single(store.GetCallsForSession(
                "session-1",
                source: "copilot-chat"));
            Assert.Equal("peak", row.PricingPeriod);
            Assert.Equal(
                0.00208,
                Assert.IsType<double>(row.CostCny),
                8);
        }
        finally
        {
            ModelPricing.SetOverrides(
                Array.Empty<KeyValuePair<string, ModelPrice>>());
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ModelSummaryReportsExtremesForSuccessfulCallsOnly()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                // Successful: TTFT 100/900 ms, generation speed differs by duration.
                Span("t1", "s1", "s1", "extremes-model", 1_000,
                    1_000, 0, 100, 0.1, ttftMs: 100, durationMs: 2_100),
                Span("t2", "s2", "s1", "extremes-model", 2_000,
                    1_000, 0, 100, 0.1, ttftMs: 900, durationMs: 5_900),
                // Failed call with an extreme TTFT must not become the minimum.
                Span("t3", "s3", "s1", "extremes-model", 3_000,
                    1_000, 0, 100, 0.1, ttftMs: 5, durationMs: 10_000,
                    errorType: "ToolError"),
            });

            var summary = store.GetModelSummary(
                "extremes-model",
                0,
                includeUtility: true);

            Assert.Equal(3, summary.Calls);
            Assert.Equal(3, summary.TtftSamples);
            Assert.Equal(100, summary.MinTtftMs);
            Assert.Equal(900, summary.MaxTtftMs);

            // Speed = output / (duration - ttft): 100/2000 = 50, 100/5000 = 20.
            Assert.Equal(20, summary.MinTokensPerSecond!.Value, 3);
            Assert.Equal(50, summary.MaxTokensPerSecond!.Value, 3);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CallTextQueryMatchesModelSessionAndSessionList()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1", "s1", "session-abc", "deepseek-flash",
                    1_000, 100, 0, 10, null,
                    source: "copilot-chat"),
                Span(
                    "t2", "s2", "session-def", "gpt-4o-mini-2024-07-18",
                    2_000, 100, 0, 10, null,
                    source: "copilot-chat"),
            });

            Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Query = "deepseek",
            }));
            Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Query = "abc",
            }));
            Assert.Single(store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Query = "not-in-model",
                SessionIds = new[] { "session-def" },
            }));
            Assert.Equal(2, store.CountCalls(new CallQuery
            {
                FromMs = 0,
                Query = "session",
            }));
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
    public void CombinedRebuildsOnlyDirtySessions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "ta-1", "sa-1", "session-a", "deepseek-flash",
                    1_000, 100, 50, 10, 0.1,
                    ttftMs: 100,
                    source: "copilot-chat"),
                Span(
                    "ta-2", "sa-2", "session-a", "deepseek-flash",
                    1_050, 100, 50, 10, 0.2,
                    serviceName: "github-copilot",
                    source: "agent-host"),
            });
            store.EnsureCombinedSpans();
            Assert.Single(store.GetCallsForSession(
                "session-a",
                source: "combined"));

            store.InsertMany(new[]
            {
                Span(
                    "tb-1", "sb-1", "session-b", "deepseek-flash",
                    2_000, 100, 0, 10, 0.3,
                    source: "copilot-chat"),
            });
            store.EnsureCombinedSpans();

            Assert.Single(store.GetCallsForSession(
                "session-a",
                source: "combined"));
            Assert.Single(store.GetCallsForSession(
                "session-b",
                source: "combined"));
            Assert.Equal(2, store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Source = "combined",
            }).Count);
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
    public void CombinedUtilityWindowMatchesFullRebuild()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "tu-1", "su-1", sessionId: null, "deepseek-flash",
                    1_000, 100, 50, 10, 0.1,
                    source: "copilot-chat"),
                Span(
                    "tu-2", "su-2", sessionId: null, "deepseek-flash",
                    1_050, 100, 50, 10, 0.2,
                    serviceName: "github-copilot",
                    source: "agent-host"),
                Span(
                    "ts-1", "ss-1", "session-s", "deepseek-flash",
                    2_000, 100, 0, 10, 0.3,
                    source: "copilot-chat"),
            });
            store.EnsureCombinedSpans();

            store.InsertMany(new[]
            {
                Span(
                    "tv-1", "sv-1", sessionId: null, "kimi-k3.1-pro",
                    500_000, 200, 100, 20, 0.4,
                    source: "copilot-chat"),
                Span(
                    "tv-2", "sv-2", sessionId: null, "kimi-k3.1-pro",
                    500_100, 200, 100, 20, 0.5,
                    serviceName: "codex-app-server",
                    source: "agent-host"),
            });
            store.EnsureCombinedSpans();

            var incremental = SnapshotCombined(store);

            // A fresh instance rebuilds every derived row from scratch.
            var reopened = new SpanStore(databasePath);
            reopened.EnsureCombinedSpans();

            Assert.Equal(SnapshotCombined(reopened), incremental);
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
    public void SessionWorkspaceCacheSurvivesAndIsPrunedWithSpans()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "tw-1", "sw-1", "session-w", "deepseek-flash",
                    1_000, 100, 0, 10, 0.1),
            });
            store.UpsertSessionWorkspaces(
                new Dictionary<string, string> { ["session-w"] = @"d:\source\demo" });

            Assert.Equal(@"d:\source\demo", store.GetSessionWorkspace("session-w"));
            Assert.Null(store.GetSessionWorkspace("session-unknown"));

            // Purging the session's spans drops its cached workspace as well.
            store.DeleteOlderThan(5_000);

            Assert.Null(store.GetSessionWorkspace("session-w"));
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

    private static List<(long StartMs, string Model, long Input, long Output, string? Session)>
        SnapshotCombined(SpanStore store) =>
        store.GetCalls(new CallQuery
        {
            FromMs = 0,
            Source = "combined",
            Limit = 500,
        })
        .Select(row => (row.StartTimeMs, row.Model, row.InputTokens, row.OutputTokens, row.SessionId))
        .OrderBy(item => item.StartTimeMs)
        .ToList();

    [Fact]
    public void TraceNodesMergeCrossSourceCallsAndDropCombinedRows()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO spans (
                        trace_id, span_id, parent_span_id, name, start_time_ms,
                        service_name, otel_source, model, session_id, is_main,
                        input_tokens, output_tokens, received_at_ms
                    ) VALUES
                        ('trace-c', 'span-c', NULL, 'chat', 1000, 'copilot-chat',
                         'copilot-chat', 'deepseek-flash', 'session-1', 1,
                         100, 10, 1000),
                        ('trace-a', 'span-a', NULL, 'chat', 1304, 'github-copilot',
                         'agent-host', 'deepseek-flash', 'session-1', 1,
                         100, 10, 1304),
                        ('combined', 'span-x', NULL, 'chat', 1000, 'combined',
                         'combined', 'deepseek-flash', 'session-1', 1,
                         100, 10, 1000);
                    INSERT INTO session_events (
                        trace_id, span_id, parent_span_id, operation, session_id,
                        service_name, otel_source, start_time_ms, received_at_ms
                    ) VALUES
                        ('trace-a', 'span-e', 'span-a', 'execute_tool',
                         'session-1', 'github-copilot', 'agent-host', 1400, 1400);
                    """;
                command.ExecuteNonQuery();
            }

            var nodes = store.GetTraceNodes("session-1");
            Assert.Equal(2, nodes.Count);
            var chat = Assert.Single(nodes, node => node.Kind == "chat");
            var tool = Assert.Single(nodes, node => node.Kind == "event");
            Assert.Equal(chat.Id, tool.ParentId);

            var copilotOnly = store.GetTraceNodes("session-1", "copilot-chat");
            Assert.Single(copilotOnly);
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
    public void AggregatesSourcesLatencyAndToolEvents()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1", "s1", "session-1", "deepseek-flash",
                    1_000, 1_000, 0, 500, 0.1,
                    ttftMs: 200,
                    durationMs: 1_000),
                Span(
                    "t2", "s2", "session-2", "deepseek-flash",
                    3_000, 1_000, 0, 500, 0.1,
                    serviceName: "github-copilot",
                    source: "agent-host"),
            });
            store.InsertEvents(new[]
            {
                new OtlpEventRecord
                {
                    TraceId = "t3",
                    SpanId = "s3",
                    Operation = "execute_tool",
                    ToolName = "read_file",
                    ToolType = "function",
                    EditType = "create",
                    SkillName = "review",
                    SessionId = "session-1",
                    ServiceName = "copilot-chat",
                    StartTimeMs = 1_500,
                    EndTimeMs = 2_000,
                    ReceivedAtMs = 2_000,
                },
                new OtlpEventRecord
                {
                    TraceId = "t4",
                    SpanId = "s4",
                    Operation = "execute_tool",
                    ToolName = "read_file",
                    ToolType = "extension",
                    EditType = "update",
                    SessionId = "session-1",
                    ServiceName = "copilot-chat",
                    StartTimeMs = 2_500,
                    EndTimeMs = 3_500,
                    IsError = true,
                    ReceivedAtMs = 3_500,
                },
                new OtlpEventRecord
                {
                    TraceId = "t5",
                    SpanId = "s5",
                    Operation = "invoke_agent",
                    SessionId = "session-1",
                    ServiceName = "copilot-chat",
                    Source = "agent-host",
                    StartTimeMs = 4_000,
                    EndTimeMs = 5_000,
                    ReceivedAtMs = 5_000,
                },
            });

            var calls = store.GetCalls(new CallQuery { FromMs = 0 });
            Assert.Equal(2, calls.Count);
            Assert.Equal("agent-host", calls[0].Source);
            Assert.Equal("copilot-chat", calls[1].Source);
            Assert.Equal(200L, calls[1].TtftMs!.Value);
            Assert.Equal(625d, calls[1].TokensPerSecond!.Value, 3);

            Assert.Equal(
                1,
                store.GetSummary(
                    0,
                    includeUtility: true,
                    source: "agent-host").Calls);
            Assert.Equal(2, store.CountCalls(new CallQuery { FromMs = 0 }));
            var page = store.GetCalls(new CallQuery
            {
                FromMs = 0,
                Source = "copilot-chat",
                Limit = 1,
                Offset = 0,
            });
            Assert.Single(page);
            Assert.Equal("copilot-chat", page[0].Source);

            var events = store.GetSessionEvents("session-1");
            var tools = Assert.Single(
                events,
                row => row.Operation == "execute_tool");
            Assert.Equal(2, tools.Count);
            Assert.Equal(1, tools.ErrorCount);
            Assert.Equal(750d, tools.AvgDurationMs!.Value, 3);
            Assert.Contains(
                events,
                row => row.Operation == "invoke_agent" &&
                       row.Count == 1 &&
                       row.Source == "agent-host");

            var toolRows = store.GetSessionTools("session-1");
            var tool = Assert.Single(toolRows);
            Assert.Equal("read_file", tool.Tool);
            Assert.Equal(2, tool.Calls);
            Assert.Equal(1_000d, tool.MaxDurationMs!.Value, 3);
            Assert.Equal(1, tool.ErrorCount);
            Assert.Contains("function", tool.ToolTypes);
            Assert.Contains("extension", tool.ToolTypes);
            Assert.Contains("create", tool.EditTypes);
            Assert.Contains("update", tool.EditTypes);
            Assert.Equal(new[] { "review" }, tool.SkillNames);

            store.InsertAgentRuns(new[]
            {
                new AgentRunRecord
                {
                    TraceId = "t1",
                    SpanId = "s1",
                    IsRoot = true,
                    SessionId = "session-1",
                    AgentName = "copilot",
                    AgentType = "builtin",
                    ProviderName = "github",
                    Source = "copilot-chat",
                    StartTimeMs = 1_000,
                    EndTimeMs = 2_000,
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheReadTokens = 20,
                    ReceivedAtMs = 2_000,
                },
            });
            var agentRun = Assert.Single(store.GetAgentRuns("session-1"));
            Assert.Equal("copilot", agentRun.AgentName);
            Assert.Equal(100, agentRun.InputTokens);
            Assert.Equal(20, agentRun.CacheReadTokens);

            var trace = store.GetTraceNodes("session-1");
            Assert.Contains(trace, node => node.Kind == "chat");
            Assert.Contains(
                trace,
                node => node.Kind == "event" && node.Tool == "read_file");

            var agentEvents = store.GetSessionEventAggregates(
                0,
                long.MaxValue,
                "agent-host");
            Assert.Contains(
                agentEvents,
                row => row.SessionId == "session-1" && row.Events == 1);
            var extensionEvents = store.GetSessionEventAggregates(
                0,
                long.MaxValue,
                "copilot-chat");
            Assert.Contains(
                extensionEvents,
                row => row.SessionId == "session-1" && row.Events == 2);
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
    public void TurnStartsOnlyIncludeRootAgentInvocations()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertEvents(new[]
            {
                // Root turn: its parent is the host session span, which is not
                // stored, so the turn has no local ancestor.
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "a1",
                    ParentSpanId = "host-session",
                    Operation = "invoke_agent",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_000,
                    EndTimeMs = 2_000,
                    ReceivedAtMs = 2_000,
                },
                // Subagent: hangs below a stored tool span, so it is not a turn.
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "tool-1",
                    ParentSpanId = "a1",
                    Operation = "execute_tool",
                    ToolName = "task",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_100,
                    EndTimeMs = 1_200,
                    ReceivedAtMs = 1_200,
                },
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "sub-1",
                    ParentSpanId = "tool-1",
                    Operation = "invoke_agent",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_150,
                    EndTimeMs = 1_900,
                    ReceivedAtMs = 1_900,
                },
                new OtlpEventRecord
                {
                    TraceId = "t2",
                    SpanId = "a2",
                    ParentSpanId = "host-session",
                    Operation = "invoke_agent",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 10_000,
                    EndTimeMs = 11_000,
                    ReceivedAtMs = 11_000,
                },
            });

            Assert.Equal(
                new (long, long?)[]
                {
                    (1_000L, 2_000L),
                    (10_000L, 11_000L),
                },
                store.GetSessionTurns("session-1"));
            Assert.Equal(
                new (long, long?)[] { (10_000L, 11_000L) },
                store.GetSessionTurns("session-1", null, 5_000, 20_000));
            Assert.Empty(store.GetSessionTurns("session-1", "copilot-chat"));
            Assert.Equal(
                new[] { new ToolRunSample(1_100, 1_200) },
                store.GetSessionToolRuns("session-1"));
            Assert.Equal(
                new (long, long?)[] { (10_000L, 11_000L) },
                store.GetSessionTurns("session-1", null, 9_000, 20_000));
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
    public void TraceTopologyShowsApprovalSpansWithKindAndResult()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertEvents(new[]
            {
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "tool-1",
                    Operation = "execute_tool",
                    ToolName = "powershell",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_000,
                    EndTimeMs = 2_000,
                    ReceivedAtMs = 2_000,
                },
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "perm-1",
                    ParentSpanId = "tool-1",
                    Operation = "permission",
                    PermissionKind = "shell",
                    PermissionResult = "approved",
                    ToolName = "powershell",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_050,
                    EndTimeMs = 1_150,
                    ReceivedAtMs = 1_150,
                },
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "perm-2",
                    ParentSpanId = "tool-1",
                    Operation = "permission",
                    PermissionKind = "write",
                    PermissionResult = "denied-interactively-by-user",
                    ToolName = "edit",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_200,
                    EndTimeMs = 1_300,
                    ReceivedAtMs = 1_300,
                    IsError = true,
                    ErrorType = "denied",
                },
            });

            var nodes = store.GetTraceNodes("session-1");
            Assert.Equal(3, nodes.Count);
            var approved = Assert.Single(nodes, node => node.Id == "t1:perm-1");
            Assert.Equal("permission", approved.Kind);
            Assert.Equal("permission", approved.Operation);
            Assert.Equal("shell · approved · powershell", approved.Label);
            Assert.Equal("t1:tool-1", approved.ParentId);
            Assert.Equal(1, approved.Depth);
            Assert.False(approved.IsError);

            var denied = Assert.Single(nodes, node => node.Id == "t1:perm-2");
            Assert.Equal("write · denied-interactively-by-user · edit", denied.Label);
            Assert.True(denied.IsError);
            Assert.Equal("denied", denied.ErrorType);

            // Approvals take part in the range window like any other row.
            var windowed = store.GetTracePage(
                "session-1",
                fromMs: 1_140,
                toMs: 1_400);
            Assert.Equal(1, windowed.Total);
            Assert.Contains(windowed.Nodes, node => node.Id == "t1:perm-2");
            Assert.DoesNotContain(windowed.Nodes, node => node.Id == "t1:perm-1");
            // The parent tool started before the window, so it comes back as a
            // context row instead of disappearing.
            var parent = Assert.Single(
                windowed.Nodes,
                node => node.Id == "t1:tool-1");
            Assert.True(parent.IsContext);
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
    public void TracePageFollowsTheSelectedWindow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1", "s1", "session-1", "deepseek-flash",
                    1_000, 10, 0, 5, 0.1,
                    durationMs: 400),
                Span(
                    "t2", "s2", "session-1", "deepseek-flash",
                    10_000, 10, 0, 5, 0.1,
                    durationMs: 400),
            });
            store.InsertEvents(new[]
            {
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "e1",
                    ParentSpanId = "s1",
                    Operation = "execute_tool",
                    ToolName = "view",
                    SessionId = "session-1",
                    ServiceName = "copilot-chat",
                    StartTimeMs = 1_200,
                    EndTimeMs = 1_300,
                    ReceivedAtMs = 1_300,
                },
            });

            var all = store.GetTracePage("session-1");
            Assert.Equal(3, all.Total);
            Assert.Equal(3, all.Nodes.Count);

            // Only the later span starts inside the window; the earlier trace is
            // gone completely, so nothing is reported as context.
            var late = store.GetTracePage(
                "session-1",
                fromMs: 9_000,
                toMs: 12_000);
            Assert.Equal(1, late.Total);
            var lateNode = Assert.Single(late.Nodes);
            Assert.Equal("t2:s2", lateNode.Id);
            Assert.False(lateNode.IsContext);

            // The tool starts inside the window but its parent does not: the parent
            // comes back as a context row with no counts.
            var mid = store.GetTracePage(
                "session-1",
                fromMs: 1_150,
                toMs: 2_000);
            Assert.Equal(1, mid.Total);
            Assert.Equal(2, mid.Nodes.Count);
            var context = mid.Nodes[0];
            Assert.True(context.IsContext);
            Assert.Equal("context", context.Kind);
            Assert.Equal("t1:s1", context.Id);
            Assert.Equal(0, context.InputTokens);
            Assert.Null(context.DurationMs);
            Assert.Equal(0, context.Depth);
            Assert.Equal(1, mid.Nodes[1].Depth);
            Assert.Equal("t1:e1", mid.Nodes[1].Id);

            // An empty window reports nothing instead of falling back to all rows.
            var empty = store.GetTracePage(
                "session-1",
                fromMs: 20_000,
                toMs: 21_000);
            Assert.Equal(0, empty.Total);
            Assert.Empty(empty.Nodes);
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
    public void SessionAggregatesOnlyCountRowsInsideTheWindow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertEvents(new[]
            {
                new OtlpEventRecord
                {
                    TraceId = "t1",
                    SpanId = "s1",
                    Operation = "execute_tool",
                    ToolName = "powershell",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_000,
                    EndTimeMs = 1_500,
                    ReceivedAtMs = 1_500,
                },
                new OtlpEventRecord
                {
                    TraceId = "t2",
                    SpanId = "s2",
                    Operation = "execute_tool",
                    ToolName = "powershell",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 10_000,
                    EndTimeMs = 10_500,
                    ReceivedAtMs = 10_500,
                },
                new OtlpEventRecord
                {
                    TraceId = "t3",
                    SpanId = "s3",
                    Operation = "execute_hook",
                    HookType = "preToolUse",
                    HookResultKind = "applied",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 1_200,
                    EndTimeMs = 1_300,
                    ReceivedAtMs = 1_300,
                },
                new OtlpEventRecord
                {
                    TraceId = "t4",
                    SpanId = "s4",
                    Operation = "execute_hook",
                    HookType = "preToolUse",
                    HookResultKind = "applied",
                    SessionId = "session-1",
                    ServiceName = "github-copilot",
                    Source = "agent-host",
                    StartTimeMs = 10_200,
                    EndTimeMs = 10_300,
                    ReceivedAtMs = 10_300,
                },
            });

            Assert.Equal(2, Assert.Single(store.GetSessionTools("session-1")).Calls);
            Assert.Equal(
                2,
                Assert.Single(store.GetHookSummaries("session-1")).Count);
            Assert.All(
                store.GetSessionEvents("session-1"),
                row => Assert.Equal(2, row.Count));

            var tools = Assert.Single(
                store.GetSessionTools("session-1", null, 10_000, 12_000));
            Assert.Equal(1, tools.Calls);
            Assert.Equal(10_000, tools.LastSeenMs);
            Assert.Equal(
                1,
                Assert.Single(
                    store.GetHookSummaries("session-1", null, 10_000, 12_000))
                    .Count);
            var events = Assert.Single(
                store.GetSessionEvents("session-1", null, 10_000, 12_000),
                row => row.Operation == "execute_tool");
            Assert.Equal(1, events.Count);
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
    public void ResolvesSubagentToolSessionsFromParents()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "trace-sub", "chat", "call_00_sub", "deepseek-flash",
                    1_150, 100, 0, 10, null,
                    parentSpanId: "sub"),
            });
            store.InsertEvents(new[]
            {
                EventRecord("root", null, "invoke_agent", null, "root-session", 1_000),
                EventRecord("sub", "root", "invoke_agent", null, "call_00_sub", 1_100, "Explore"),
                EventRecord("tool", "sub", "execute_tool", "read_file", "call_00_sub", 1_200),
                EventRecord("root-tool", "root", "execute_tool", "runSubagent", "root-session", 1_300),
            });

            Assert.Equal(0, store.ResolveSubagentSessions());

            var events = store.GetSessionEvents("root-session");
            Assert.Equal(2, events.Count);
            Assert.Equal(4, events.Sum(row => row.Count));
            Assert.Empty(store.GetSessionEvents("call_00_sub"));

            var chat = Assert.Single(store.GetCallsForSession(
                "root-session",
                source: "copilot-chat"));
            Assert.Equal("Explore", chat.SubagentName);
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
    public void ResolveSubagentSessionsRepairsStoredRows()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO session_events (
                        trace_id, span_id, parent_span_id, operation, session_id,
                        service_name, otel_source, start_time_ms, received_at_ms
                    ) VALUES
                        ('trace-sub', 'root', NULL, 'invoke_agent',
                         'root-session', 'copilot-chat', 'copilot-chat', 1000, 1000),
                        ('trace-sub', 'sub', 'root', 'invoke_agent',
                         'call_00_sub', 'copilot-chat', 'copilot-chat', 1100, 1100),
                        ('trace-sub', 'tool', 'sub', 'execute_tool',
                         'call_00_sub', 'copilot-chat', 'copilot-chat', 1200, 1200);
                    """;
                command.ExecuteNonQuery();
            }

            Assert.Equal(2, store.ResolveSubagentSessions());

            var events = store.GetSessionEvents("root-session");
            Assert.Equal(3, events.Sum(row => row.Count));
            Assert.Empty(store.GetSessionEvents("call_00_sub"));
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
    public void CountsEditAcceptanceOutcomeMetrics()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            Assert.Equal(3, store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 3, 0, 0),
                MetricPoint("accepted", 5, 60_000, 0),
                MetricPoint("accepted", 1, 120_000, 120_000),
            }));

            var summary = store.GetOutcomeSummary(0, long.MaxValue);
            Assert.True(summary.HasData);
            Assert.Equal(6, summary.EditAccepted);
            Assert.Equal(0, summary.EditRejected);
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
    public void MergesCumulativeSamplesInsideTheSamplingInterval()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);

            // The SDK re-exports cumulative counters every 10 s and the host stops before the
            // next minute boundary, so dropping the in-interval samples would report 3.
            Assert.Equal(3, store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 3, 1_000, 0),
                MetricPoint("accepted", 7, 11_000, 0),
                MetricPoint("accepted", 9, 21_000, 0),
            }));
            Assert.Equal(9, store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);

            // A replayed older total must not walk the stored value back.
            Assert.Equal(
                0,
                store.InsertMetricPoints(new[] { MetricPoint("accepted", 5, 15_000, 0), }));
            Assert.Equal(9, store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);

            // Past the interval a new row starts, and the delta between rows stays correct.
            Assert.Equal(
                1,
                store.InsertMetricPoints(new[] { MetricPoint("accepted", 12, 95_000, 0), }));
            Assert.Equal(12, store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);
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
    public void AttributesWindowMetricsOnlyWhenSessionIsUnique()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1", "s1", "session-1", "deepseek-flash",
                    1_000, 100, 0, 10, null,
                    windowSessionId: "win-1"),
                Span(
                    "t2", "s2", "session-2", "deepseek-flash",
                    1_000, 100, 0, 10, null,
                    windowSessionId: "win-1"),
            });
            store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 4, 2_000, 0, "win-1"),
            });

            Assert.Equal(
                0,
                store.GetOutcomeSummary(0, long.MaxValue, null, "session-1")
                    .EditAccepted);
            Assert.Equal(
                0,
                store.GetOutcomeSummary(0, long.MaxValue, null, "session-2")
                    .EditAccepted);
            Assert.Equal(
                4,
                store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);
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
    public void AttributesWindowMetricToUniqueSession()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMany(new[]
            {
                Span(
                    "t1", "s1", "session-1", "deepseek-flash",
                    1_000, 100, 0, 10, null,
                    windowSessionId: "win-1"),
            });
            store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 2, 2_000, 0, "win-1"),
            });

            Assert.Equal(
                2,
                store.GetOutcomeSummary(0, long.MaxValue, null, "session-1")
                    .EditAccepted);
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
    public void CountsMetricDeltasAcrossWindowBoundary()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 10, 0, 0),
                MetricPoint("accepted", 14, 60_000, 0),
                MetricPoint("accepted", 20, 120_000, 0),
            });

            // The window is aggregated on its own, so the sample just before the
            // window still has to be joined back for counters.
            Assert.Equal(
                20,
                store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);
            Assert.Equal(
                10,
                store.GetOutcomeSummary(60_000, long.MaxValue).EditAccepted);
            Assert.Equal(
                6,
                store.GetOutcomeSummary(60_001, long.MaxValue).EditAccepted);

            // A counter reset (different start time) restarts from its value.
            store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 3, 180_000, 180_000),
            });
            Assert.Equal(
                23,
                store.GetOutcomeSummary(0, long.MaxValue).EditAccepted);
            Assert.Equal(
                3,
                store.GetOutcomeSummary(120_001, long.MaxValue).EditAccepted);
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
    public void UpsertsAndDeletesModelProfiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.UpsertModelProfile(new StoredModelProfile(
                "DeepSeek/deepseek-flash",
                "DeepSeek",
                "letter",
                "D",
                "#4D6BFE",
                null,
                1_000));

            var stored = Assert.Single(store.GetModelProfiles());
            Assert.Equal("DeepSeek/deepseek-flash", stored.Model);
            Assert.Equal("DeepSeek", stored.Provider);
            Assert.Equal("D", stored.IconLetter);

            // The model id is the unique key and is matched case-insensitively,
            // so re-saving with different casing must update instead of insert.
            store.UpsertModelProfile(new StoredModelProfile(
                "deepseek/DeepSeek-Flash",
                "DeepSeek",
                "image",
                null,
                null,
                "data:image/png;base64,AAAA",
                2_000));
            stored = Assert.Single(store.GetModelProfiles());
            Assert.Equal("image", stored.IconKind);
            Assert.Equal(2_000, stored.UpdatedAtMs);

            // Clearing the provider keeps the row but drops the value.
            store.UpsertModelProfile(new StoredModelProfile(
                "deepseek/deepseek-flash",
                null,
                null,
                null,
                null,
                null,
                3_000));
            stored = Assert.Single(store.GetModelProfiles());
            Assert.Null(stored.Provider);
            Assert.Null(stored.IconImage);

            Assert.True(store.DeleteModelProfile("DeepSeek/DeepSeek-Flash"));
            Assert.Empty(store.GetModelProfiles());
            Assert.False(store.DeleteModelProfile("deepseek/deepseek-flash"));
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
    public void SignalHealthHonoursTimeRange()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            var store = new SpanStore(databasePath);
            store.InsertMetricPoints(new[]
            {
                MetricPoint("accepted", 1, 1_000, 0),
                MetricPoint("accepted", 2, 200_000, 0),
            });

            var all = store.GetSignalHealth();
            Assert.Equal(2, all.MetricPoints);
            Assert.Equal(200_000, all.LastSignalMs);

            var early = store.GetSignalHealth(fromMs: 0, toMs: 100_000);
            Assert.Equal(1, early.MetricPoints);
            Assert.Equal(1_000, early.LastSignalMs);

            var late = store.GetSignalHealth(
                fromMs: 100_000,
                toMs: long.MaxValue);
            Assert.Equal(1, late.MetricPoints);
            Assert.Equal(200_000, late.LastSignalMs);
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

    private static OtlpMetricPointRecord MetricPoint(
        string outcome,
        double value,
        long endMs,
        long startMs,
        string? windowSessionId = null,
        string? toolCallId = null,
        string metricName = "copilot_chat.edit.acceptance.count",
        string? attributes = null) =>
        new()
        {
            Source = "copilot-chat",
            SeriesKey = $"{metricName}-{outcome}-{windowSessionId}",
            MetricName = metricName,
            MetricKind = "sum",
            Attributes = attributes ?? $"copilot_chat.edit.outcome={outcome}",
            WindowSessionId = windowSessionId,
            ToolCallId = toolCallId,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            Value = value,
            ReceivedAtMs = endMs,
        };

    private static OtlpEventRecord EventRecord(
        string spanId,
        string? parentSpanId,
        string operation,
        string? toolName,
        string sessionId,
        long startMs,
        string? agentName = null) =>
        new()
        {
            TraceId = "trace-sub",
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Operation = operation,
            ToolName = toolName,
            SessionId = sessionId,
            AgentName = agentName,
            Source = "copilot-chat",
            StartTimeMs = startMs,
            EndTimeMs = startMs + 100,
            ReceivedAtMs = startMs,
        };

    private static OtlpEventRecord Event(
        string traceId,
        string spanId,
        string source,
        string callId,
        string toolName,
        long startMs) =>
        new()
        {
            TraceId = traceId,
            SpanId = spanId,
            Operation = "execute_tool",
            ToolName = toolName,
            CallId = callId,
            SessionId = "session-1",
            Source = source,
            StartTimeMs = startMs,
            EndTimeMs = startMs + 100,
            ReceivedAtMs = startMs,
        };

    private static OtlpSpanRecord Span(
        string traceId,
        string spanId,
        string? sessionId,
        string model,
        long startMs,
        long inputTokens,
        long cacheReadTokens,
        long outputTokens,
        double? cost,
        string serviceName = "copilot-chat",
        long reasoningTokens = 0,
        long? ttftMs = null,
        string source = "copilot-chat",
        long durationMs = 100,
        string? parentSpanId = null,
        string? windowSessionId = null,
        string? errorType = null) =>
        new()
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            WindowSessionId = windowSessionId,
            Name = "chat test",
            StartTimeMs = startMs,
            EndTimeMs = startMs + durationMs,
            ServiceName = serviceName,
            Source = source,
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = inputTokens,
            CacheReadTokens = cacheReadTokens,
            OutputTokens = outputTokens,
            ReasoningTokens = reasoningTokens,
            TtftMs = ttftMs,
            HitRate = inputTokens > 0 ? (double)cacheReadTokens / inputTokens : null,
            CostCny = cost,
            PricingPeriod = "peak",
            ErrorType = errorType,
            ReceivedAtMs = startMs,
        };
}
