using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Storage;

public sealed record LocalImportResult(
    long Scanned,
    long Matched,
    long Inserted,
    long Skipped,
    long EventsMatched,
    long EventsInserted,
    IReadOnlyList<string> Errors);

public static class LocalTraceImporter
{
    private static readonly HashSet<string> AllowedFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "agent-traces.db",
            "agent-host-traces.db",
            "spans.db",
        };

    public static LocalImportResult Import(
        SpanStore store,
        IEnumerable<string>? paths)
    {
        long scanned = 0;
        long matched = 0;
        long inserted = 0;
        long skipped = 0;
        long eventsMatched = 0;
        long eventsInserted = 0;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storePath = Path.GetFullPath(store.DatabasePath);

        foreach (var raw in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch (Exception exception)
            {
                errors.Add($"{raw}: {exception.Message}");
                continue;
            }

            if (!seen.Add(path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (!AllowedFileNames.Contains(fileName))
            {
                errors.Add($"{fileName}: unsupported trace database");
                continue;
            }

            if (!File.Exists(path))
            {
                errors.Add($"{fileName}: file not found");
                continue;
            }

            if (string.Equals(path, storePath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{fileName}: is the Token Tracker database");
                continue;
            }

            try
            {
                var (fileScanned, records, events, agentRuns) =
                    ReadDatabase(path);
                scanned += fileScanned;
                matched += records.Count;
                var fileInserted = store.InsertMany(records);
                inserted += fileInserted;
                skipped += records.Count - fileInserted;
                eventsMatched += events.Count;
                eventsInserted += store.InsertEvents(events);
                store.InsertAgentRuns(agentRuns);
            }
            catch (Exception exception)
            {
                errors.Add($"{fileName}: {exception.Message}");
            }
        }

        return new LocalImportResult(
            scanned,
            matched,
            inserted,
            skipped,
            eventsMatched,
            eventsInserted,
            errors);
    }

    private static (
        long Scanned,
        List<OtlpSpanRecord> Records,
        List<OtlpEventRecord> Events,
        List<AgentRunRecord> AgentRuns) ReadDatabase(string path)
    {
        var records = new List<OtlpSpanRecord>();
        var events = new List<OtlpEventRecord>();
        var agentRuns = new List<AgentRunRecord>();
        var source = Path.GetFileName(path).Equals(
            "agent-host-traces.db",
            StringComparison.OrdinalIgnoreCase)
            ? "agent-host"
            : "copilot-chat";

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.trace_id, s.span_id, s.name, s.start_time_ms, s.end_time_ms,
                   s.provider_name, s.conversation_id,
                   s.request_model, s.response_model,
                   s.input_tokens, s.output_tokens, s.cached_tokens,
                   s.reasoning_tokens, s.chat_session_id,
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'gen_ai.usage.cache_creation.input_tokens'
                    LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'copilot_chat.copilot_usage_nano_aiu'
                    LIMIT 1),
                   s.parent_span_id
            FROM spans s
            WHERE s.operation_name = 'chat' OR s.name LIKE 'chat %'
            ORDER BY s.start_time_ms;
            """;

        long scanned = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                scanned++;
                var record = ReadRecord(reader, source);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
        }

        using var eventCommand = connection.CreateCommand();
        eventCommand.CommandText = """
            SELECT s.trace_id, s.span_id, s.operation_name, s.name,
                   s.start_time_ms, s.end_time_ms, s.status_code,
                   s.provider_name, s.conversation_id, s.chat_session_id,
                   s.tool_name, s.tool_call_id, s.tool_type,
                   COALESCE(
                       (SELECT a.value FROM span_attributes a
                        WHERE a.span_id = s.span_id
                          AND a.key = 'github.copilot.tool.parameters.edit_type'
                        LIMIT 1),
                       (SELECT a.value FROM span_attributes a
                        WHERE a.span_id = s.span_id
                          AND a.key = 'copilot_chat.tool.parameters.edit_type'
                        LIMIT 1)),
                   COALESCE(
                       (SELECT a.value FROM span_attributes a
                        WHERE a.span_id = s.span_id
                          AND a.key = 'github.copilot.tool.parameters.skill_name'
                        LIMIT 1),
                       (SELECT a.value FROM span_attributes a
                        WHERE a.span_id = s.span_id
                          AND a.key = 'copilot_chat.tool.parameters.skill_name'
                        LIMIT 1)),
                   s.parent_span_id,
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'error.type' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'gen_ai.agent.name' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.agent.type' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.tool.parameters.mcp_server_name_hash'
                    LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.tool.parameters.mcp_tool_name'
                    LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.hook.decision' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.hook.duration' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.hook.tool_names' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'copilot_chat.hook_type' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'copilot_chat.hook_result_kind' LIMIT 1)
            FROM spans s
            WHERE s.operation_name IN ('execute_tool', 'invoke_agent')
            ORDER BY s.start_time_ms;
            """;
        using (var reader = eventCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                scanned++;
                var item = ReadEvent(reader, source);
                if (item is not null)
                {
                    events.Add(item);
                }
            }
        }

        using var agentCommand = connection.CreateCommand();
        agentCommand.CommandText = """
            SELECT s.trace_id, s.span_id, s.parent_span_id, s.agent_name,
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.agent.type' LIMIT 1),
                   s.provider_name, s.conversation_id, s.chat_session_id,
                   s.request_model, s.response_model, s.input_tokens,
                   s.output_tokens, s.cached_tokens, s.reasoning_tokens,
                   s.start_time_ms, s.end_time_ms, s.status_code,
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'gen_ai.usage.cache_creation.input_tokens'
                    LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.git.repository' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.git.branch' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.git.commit_sha' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'github.copilot.github.org' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'copilot_chat.turn_count' LIMIT 1),
                   (SELECT a.value FROM span_attributes a
                    WHERE a.span_id = s.span_id
                      AND a.key = 'error.type' LIMIT 1)
            FROM spans s
            WHERE s.operation_name = 'invoke_agent'
            ORDER BY s.start_time_ms;
            """;
        using (var reader = agentCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                var item = ReadAgentRun(reader, source);
                if (item is not null)
                {
                    agentRuns.Add(item);
                }
            }
        }

        return (scanned, records, events, agentRuns);
    }

    private static AgentRunRecord? ReadAgentRun(
        SqliteDataReader reader,
        string source)
    {
        var traceId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var spanId = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(traceId) ||
            string.IsNullOrWhiteSpace(spanId))
        {
            return null;
        }

        var startMs = reader.GetInt64(14);
        var errorType = reader.IsDBNull(23)
            ? null
            : reader.GetString(23);
        var isError = (!reader.IsDBNull(16) && reader.GetInt64(16) == 2) ||
            !string.IsNullOrWhiteSpace(errorType);
        if (isError && string.IsNullOrWhiteSpace(errorType))
        {
            errorType = "Error";
        }

        return new AgentRunRecord
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = reader.IsDBNull(2) ? null : reader.GetString(2),
            IsRoot = reader.IsDBNull(2),
            SessionId = FirstNonEmpty(
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(6) ? null : reader.GetString(6)),
            AgentName = reader.IsDBNull(3) ? null : reader.GetString(3),
            AgentType = reader.IsDBNull(4) ? null : reader.GetString(4),
            ProviderName = reader.IsDBNull(5) ? null : reader.GetString(5),
            RequestModel = NormalizeModel(
                reader.IsDBNull(8) ? null : reader.GetString(8),
                source),
            ResponseModel = NormalizeModel(
                reader.IsDBNull(9) ? null : reader.GetString(9),
                source),
            Source = source == "agent-host" ? "agent-host" : "copilot-chat",
            GitRepository = OtlpTraceParser.NormalizeGitRepository(
                reader.IsDBNull(18) ? null : reader.GetString(18)),
            GitBranch = reader.IsDBNull(19) ? null : reader.GetString(19),
            GitCommit = reader.IsDBNull(20) ? null : reader.GetString(20),
            GithubOrg = reader.IsDBNull(21) ? null : reader.GetString(21),
            TurnCount = reader.IsDBNull(22)
                ? null
                : ParseLong(reader.GetString(22)),
            InputTokens = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
            OutputTokens = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
            CacheReadTokens = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
            CacheCreationTokens = reader.IsDBNull(17)
                ? 0
                : ParseLong(reader.GetString(17)) ?? 0,
            ReasoningTokens = reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
            StartTimeMs = startMs,
            EndTimeMs = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            IsError = isError,
            ErrorType = errorType,
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static string? NormalizeModel(string? model, string source) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : ModelPricing.NormalizeModel(model, source);

    private static OtlpEventRecord? ReadEvent(
        SqliteDataReader reader,
        string source)
    {
        var traceId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var spanId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var operation = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (string.IsNullOrWhiteSpace(traceId) ||
            string.IsNullOrWhiteSpace(spanId) ||
            string.IsNullOrWhiteSpace(operation))
        {
            return null;
        }

        var startMs = reader.GetInt64(4);
        var errorType = reader.IsDBNull(16) ? null : reader.GetString(16);
        var isError = (!reader.IsDBNull(6) && reader.GetInt64(6) == 2) ||
            !string.IsNullOrWhiteSpace(errorType);
        if (isError && string.IsNullOrWhiteSpace(errorType))
        {
            errorType = "Error";
        }

        var hookSeconds = reader.IsDBNull(22)
            ? null
            : ParseLong(reader.GetString(22));
        return new OtlpEventRecord
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = reader.IsDBNull(15) ? null : reader.GetString(15),
            Operation = operation.ToLowerInvariant(),
            Name = reader.IsDBNull(3) ? null : reader.GetString(3),
            ToolName = reader.IsDBNull(10) ? null : reader.GetString(10),
            CallId = reader.IsDBNull(11) ? null : reader.GetString(11),
            ToolType = reader.IsDBNull(12) ? null : reader.GetString(12),
            EditType = reader.IsDBNull(13) ? null : reader.GetString(13),
            SkillName = reader.IsDBNull(14) ? null : reader.GetString(14),
            SessionId = FirstNonEmpty(
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(8) ? null : reader.GetString(8)),
            ServiceName = reader.IsDBNull(7) ? null : reader.GetString(7),
            Source = source == "agent-host" ? "agent-host" : "copilot-chat",
            StartTimeMs = startMs,
            EndTimeMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            IsError = isError,
            ErrorType = errorType,
            AgentName = reader.IsDBNull(17) ? null : reader.GetString(17),
            AgentType = reader.IsDBNull(18) ? null : reader.GetString(18),
            ProviderName = reader.IsDBNull(7) ? null : reader.GetString(7),
            McpServerHash = reader.IsDBNull(19) ? null : reader.GetString(19),
            McpToolName = reader.IsDBNull(20) ? null : reader.GetString(20),
            HookDecision = reader.IsDBNull(21) ? null : reader.GetString(21),
            HookDurationMs = hookSeconds is null ? null : hookSeconds * 1000d,
            HookToolNames = reader.IsDBNull(23) ? null : reader.GetString(23),
            HookType = reader.IsDBNull(24) ? null : reader.GetString(24),
            HookResultKind = reader.IsDBNull(25) ? null : reader.GetString(25),
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static OtlpSpanRecord? ReadRecord(SqliteDataReader reader, string source)
    {
        var traceId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var spanId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var model = FirstNonEmpty(
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(7) ? null : reader.GetString(7));
        if (string.IsNullOrWhiteSpace(traceId) ||
            string.IsNullOrWhiteSpace(spanId) ||
            string.IsNullOrWhiteSpace(model) ||
            reader.IsDBNull(9))
        {
            return null;
        }

        model = ModelPricing.NormalizeModel(model, source);

        var inputTokens = reader.GetInt64(9);
        var outputTokens = reader.IsDBNull(10) ? 0 : reader.GetInt64(10);
        var cacheReadTokens = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
        var reasoningTokens = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
        var sessionId = FirstNonEmpty(
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        var cacheCreationTokens =
            ParseLong(reader.IsDBNull(14) ? null : reader.GetString(14)) ?? 0;
        var nanoAiu = ParseLong(reader.IsDBNull(15) ? null : reader.GetString(15));
        var provider =
            (reader.IsDBNull(5) ? null : reader.GetString(5)) ?? source;
        var totalInputTokens = TokenSemantics.TotalInputTokens(
            provider,
            inputTokens,
            cacheReadTokens,
            cacheCreationTokens);
        var startMs = reader.GetInt64(3);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        var breakdown = ModelPricing.EvaluateDetailed(
            ModelPricing.GetPrice(model),
            totalInputTokens,
            cacheReadTokens,
            outputTokens,
            reasoningTokens,
            timestamp,
            cacheCreationTokens);

        return new OtlpSpanRecord
        {
            TraceId = traceId!,
            SpanId = spanId!,
            ParentSpanId = reader.IsDBNull(16) ? null : reader.GetString(16),
            Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            StartTimeMs = startMs,
            EndTimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ServiceName = provider,
            Source = source == "agent-host" ? "agent-host" : "copilot-chat",
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = totalInputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheCreationTokens = cacheCreationTokens,
            ReasoningTokens = reasoningTokens,
            NanoAiu = nanoAiu,
            HitRate = totalInputTokens > 0
                ? (double)cacheReadTokens / totalInputTokens
                : null,
            CostCny = (double?)breakdown.CostCny,
            CacheCostCny = (double?)breakdown.CacheCost,
            UncachedCostCny = (double?)breakdown.UncachedCost,
            OutputCostCny = (double?)breakdown.OutputCost,
            ReasoningCostCny = (double?)breakdown.ReasoningCost,
            PricingPeriod = breakdown.Period,
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first)
            ? first.Trim()
            : !string.IsNullOrWhiteSpace(second) ? second.Trim() : null;

    private static long? ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var integer))
        {
            return integer;
        }

        return double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) &&
            double.IsFinite(number)
            ? (long)Math.Round(number)
            : null;
    }
}
