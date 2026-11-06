using System.Globalization;
using Microsoft.Data.Sqlite;
using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Tests;

public sealed class LocalTraceImporterTests
{
    [Fact]
    public void ImportsChatSpansOnceAndNormalizesProviderUsage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(directory, "agent-traces.db");
        var storePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            Directory.CreateDirectory(directory);
            var startMs = DateTimeOffset
                .Parse(
                    "2026-10-27T02:00:00Z",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal)
                .ToUnixTimeMilliseconds();
            CreateSourceDatabase(sourcePath, startMs);
            var store = new SpanStore(storePath);

            var first = LocalTraceImporter.Import(store, new[] { sourcePath });

            Assert.Equal(6, first.Scanned);
            Assert.Equal(2, first.Matched);
            Assert.Equal(2, first.Inserted);
            Assert.Equal(0, first.Skipped);
            Assert.Equal(4, first.EventsMatched);
            Assert.Equal(4, first.EventsInserted);
            Assert.Empty(first.Errors);

            var calls = store.GetCalls(0, includeUtility: true);
            var call = Assert.Single(
                calls,
                row => row.Model == "deepseek-flash");
            Assert.Equal("deepseek-flash", call.Model);
            Assert.Equal("session-import", call.SessionId);
            Assert.Equal(1_300, call.InputTokens);
            Assert.Equal(200, call.CacheReadTokens);
            // The Agent Host exporter leaves spans.reasoning_tokens NULL and only writes
            // the attribute, so the import must fall back to it.
            Assert.Equal(64L, call.ReasoningTokens);
            Assert.Equal(1_500_000_000, call.NanoAiu);
            Assert.Equal("1.5 AIC", call.AiuText);
            Assert.Null(call.CostCny);
            Assert.Equal(startMs, call.StartTimeMs);
            Assert.Equal(250, call.TtftMs);
            Assert.Equal("""["stop"]""", call.FinishReasons);
            Assert.Equal("vscode-copilot", call.AgentName);
            Assert.Null(call.ErrorType);

            var buffered = Assert.Single(
                calls,
                row => row.Model == "kimi-k3.1-pro");
            Assert.Equal("root-session", buffered.SessionId);
            Assert.Equal("Error", buffered.ErrorType);
            // A first-chunk value equal to the whole duration is buffering, not TTFT.
            Assert.Null(buffered.TtftMs);

            var tool = Assert.Single(store.GetSessionTools("session-import"));
            Assert.Equal("read_file", tool.Tool);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(1, tool.ErrorCount);
            Assert.Equal(new[] { "function" }, tool.ToolTypes);
            Assert.Equal(new[] { "str_replace" }, tool.EditTypes);
            Assert.Equal(new[] { "review" }, tool.SkillNames);

            // Hooks are span events in both exporters, never execute_hook spans.
            var hooks = store.GetHookSummaries("session-import");
            Assert.Equal(2, hooks.Count);
            var pre = Assert.Single(hooks, row => row.HookType == "preToolUse");
            Assert.Equal(1, pre.Count);
            Assert.Equal(500d, pre.AvgDurationMs);
            var post = Assert.Single(hooks, row => row.HookType == "postToolUse");
            Assert.Equal(1, post.Count);
            Assert.Equal(1_500d, post.AvgDurationMs);

            var second = LocalTraceImporter.Import(store, new[] { sourcePath });

            Assert.Equal(6, second.Scanned);
            Assert.Equal(2, second.Matched);
            Assert.Equal(0, second.Inserted);
            Assert.Equal(2, second.Skipped);
            Assert.Equal(4, second.EventsMatched);
            Assert.Equal(0, second.EventsInserted);
            Assert.Equal(2, store.GetHookSummaries("session-import").Count);
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

    private static void CreateSourceDatabase(string path, long startMs)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE spans (
                span_id TEXT PRIMARY KEY,
                trace_id TEXT NOT NULL,
                parent_span_id TEXT,
                name TEXT NOT NULL,
                start_time_ms INTEGER NOT NULL,
                end_time_ms INTEGER NOT NULL,
                status_code INTEGER NOT NULL DEFAULT 0,
                status_message TEXT,
                operation_name TEXT,
                provider_name TEXT,
                agent_name TEXT,
                conversation_id TEXT,
                request_model TEXT,
                response_model TEXT,
                input_tokens INTEGER,
                output_tokens INTEGER,
                cached_tokens INTEGER,
                reasoning_tokens INTEGER,
                tool_name TEXT,
                tool_call_id TEXT,
                tool_type TEXT,
                chat_session_id TEXT,
                turn_index INTEGER,
                ttft_ms REAL
            );

            CREATE TABLE span_attributes (
                span_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT,
                PRIMARY KEY (span_id, key)
            );

            CREATE TABLE span_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                span_id TEXT NOT NULL,
                name TEXT NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                attributes TEXT
            );

            INSERT INTO spans (
                span_id, trace_id, name, start_time_ms, end_time_ms,
                operation_name, provider_name, conversation_id,
                request_model, response_model,
                input_tokens, output_tokens, cached_tokens, reasoning_tokens,
                chat_session_id, agent_name, ttft_ms
            ) VALUES (
                'span-import', 'trace-import', 'chat', {{startMs}}, {{startMs + 1000}},
                'chat', 'anthropic', 'conversation-import',
                'auto', 'deepseek-flash',
                1000, 100, 200, NULL,
                'session-import', 'copilot', 250
            );

            INSERT INTO spans (
                span_id, trace_id, name, start_time_ms, end_time_ms,
                status_code, operation_name, provider_name,
                conversation_id, request_model, response_model,
                input_tokens, output_tokens, cached_tokens, ttft_ms
            ) VALUES (
                'span-buffered', 'trace-buffered', 'chat', {{startMs + 3000}},
                {{startMs + 4000}}, 2, 'chat', 'deepseek', 'conversation-import',
                'auto', 'kimi-k3.1-pro', 500, 50, 0, 1000
            );

            INSERT INTO spans (
                span_id, trace_id, name, start_time_ms, end_time_ms,
                status_code, operation_name, provider_name,
                conversation_id, chat_session_id, tool_name, tool_call_id,
                tool_type
            ) VALUES (
                'span-tool', 'trace-tool', 'execute_tool read_file',
                {{startMs + 1500}}, {{startMs + 2000}}, 2, 'execute_tool',
                'anthropic', 'conversation-import', 'session-import',
                'read_file', 'call-import', 'function'
            );

            INSERT INTO spans (
                span_id, trace_id, name, start_time_ms, end_time_ms,
                status_code, operation_name, provider_name,
                conversation_id, chat_session_id, agent_name
            ) VALUES (
                'span-agent', 'trace-import', 'invoke_agent explore',
                {{startMs}}, {{startMs + 5000}}, 1, 'invoke_agent',
                'anthropic', 'conversation-import', 'session-import', 'explore'
            );

            INSERT INTO span_attributes (span_id, key, value) VALUES
                ('span-import', 'gen_ai.usage.cache_creation.input_tokens', '100'),
                ('span-import', 'gen_ai.usage.reasoning.output_tokens', '64'),
                ('span-import', 'copilot_chat.copilot_usage_nano_aiu', '1500000000'),
                ('span-import', 'copilot_chat.time_to_first_token', '250'),
                ('span-import', 'gen_ai.response.finish_reasons', '["stop"]'),
                ('span-import', 'gen_ai.agent.name', 'vscode-copilot'),
                ('span-import', 'service.namespace', 'vscode.chat'),
                ('span-buffered', 'gen_ai.response.time_to_first_chunk', '1.0'),
                ('span-buffered', 'copilot_chat.parent_chat_session_id', 'root-session'),
                ('span-tool', 'github.copilot.tool.parameters.edit_type', 'str_replace'),
                ('span-tool', 'github.copilot.tool.parameters.skill_name', 'review');

            INSERT INTO span_events (span_id, name, timestamp_ms, attributes) VALUES
                ('span-agent', 'github.copilot.hook.start', {{startMs + 100}},
                 '{"github.copilot.hook.type":"preToolUse","github.copilot.hook.invocation_id":"hook-1"}'),
                ('span-agent', 'github.copilot.hook.end', {{startMs + 100}},
                 '{"github.copilot.hook.type":"preToolUse","github.copilot.hook.invocation_id":"hook-1","github.copilot.hook.duration":0.5}'),
                ('span-agent', 'github.copilot.hook.end', {{startMs + 900}},
                 '{"github.copilot.hook.type":"postToolUse","github.copilot.hook.invocation_id":"hook-2","github.copilot.hook.duration":1.5}');
            """;
        command.ExecuteNonQuery();
    }
}
