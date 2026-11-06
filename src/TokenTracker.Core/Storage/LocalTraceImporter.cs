using System.Text.Json;
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
                   s.parent_span_id, s.status_code, s.ttft_ms, s.agent_name
            FROM spans s
            WHERE s.operation_name = 'chat' OR s.name LIKE 'chat %'
            ORDER BY s.start_time_ms;
            """;

        var attributes = ReadSpanAttributes(connection);

        long scanned = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                scanned++;
                var record = ReadRecord(reader, source, attributes);
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

        // Runtime hooks land in span_events, not in execute_hook spans: neither exporter
        // writes hook spans, so this is the only source of hook statistics offline.
        using var hookCommand = connection.CreateCommand();
        hookCommand.CommandText = """
            SELECT e.rowid, e.span_id, e.timestamp_ms, e.attributes,
                   s.trace_id, s.agent_name, s.provider_name,
                   s.conversation_id, s.chat_session_id
            FROM span_events e
            JOIN spans s ON s.span_id = e.span_id
            WHERE e.name IN ('github.copilot.hook.end', 'copilot_chat.hook.end')
            ORDER BY e.timestamp_ms, e.rowid;
            """;
        using (var reader = hookCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                scanned++;
                var item = ReadHookEvent(reader, source);
                if (item is not null)
                {
                    events.Add(item);
                }
            }
        }

        return (scanned, records, events, agentRuns);
    }

    private static OtlpEventRecord? ReadHookEvent(
        SqliteDataReader reader,
        string source)
    {
        var traceId = reader.IsDBNull(4) ? null : reader.GetString(4);
        var spanId = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(traceId) ||
            string.IsNullOrWhiteSpace(spanId))
        {
            return null;
        }

        var payload = reader.IsDBNull(3) ? null : reader.GetString(3);
        var hookType = TrimToNull(
            ReadJsonField(payload, "github.copilot.hook.type") ??
            ReadJsonField(payload, "copilot_chat.hook.type"));
        var durationMs = ParseSecondsToMs(
            ReadJsonField(payload, "github.copilot.hook.duration") ??
            ReadJsonField(payload, "copilot_chat.hook.duration"));
        // span_events has no stable identity of its own, but session_events is keyed by
        // (trace_id, span_id); re-importing the same file must not duplicate or shift rows.
        var invocationId = TrimToNull(
            ReadJsonField(payload, "github.copilot.hook.invocation_id") ??
            ReadJsonField(payload, "copilot_chat.hook.invocation_id"));
        var eventKey = invocationId ??
            reader.GetInt64(0).ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        var startMs = reader.GetInt64(2);
        return new OtlpEventRecord
        {
            TraceId = traceId,
            SpanId = $"{spanId}#hook#{eventKey}",
            ParentSpanId = spanId,
            Operation = "execute_hook",
            Name = hookType is null ? "execute_hook" : $"execute_hook {hookType}",
            HookType = hookType,
            HookDurationMs = durationMs,
            SessionId = FirstNonEmpty(
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(7) ? null : reader.GetString(7)),
            ServiceName = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProviderName = reader.IsDBNull(6) ? null : reader.GetString(6),
            AgentName = reader.IsDBNull(5) ? null : reader.GetString(5),
            Source = source == "agent-host" ? "agent-host" : "copilot-chat",
            StartTimeMs = startMs,
            EndTimeMs = durationMs is null
                ? null
                : startMs + (long)Math.Round(durationMs.Value),
            IsError = false,
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static string? ReadJsonField(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ParseSecondsToMs(string? value)
    {
        if (!double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds < 0)
        {
            return null;
        }

        return seconds * 1000d;
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        var hookDurationMs = ParseSecondsToMs(
            reader.IsDBNull(22) ? null : reader.GetString(22));
        return new OtlpEventRecord
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = reader.IsDBNull(15) ? null : reader.GetString(15),
            Operation = operation.ToLowerInvariant(),
            Name = reader.IsDBNull(3) ? null : reader.GetString(3),
            ToolName = TrimToNull(reader.IsDBNull(10) ? null : reader.GetString(10)),
            CallId = TrimToNull(reader.IsDBNull(11) ? null : reader.GetString(11)),
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
            HookDurationMs = hookDurationMs,
            HookToolNames = reader.IsDBNull(23) ? null : reader.GetString(23),
            HookType = reader.IsDBNull(24) ? null : reader.GetString(24),
            HookResultKind = reader.IsDBNull(25) ? null : reader.GetString(25),
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static Dictionary<string, Dictionary<string, string>> ReadSpanAttributes(
        SqliteConnection connection)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT span_id, key, value FROM span_attributes
            WHERE key IN (
                'gen_ai.usage.cache_creation.input_tokens',
                'gen_ai.usage.reasoning.output_tokens',
                'gen_ai.usage.reasoning_tokens',
                'copilot_chat.copilot_usage_nano_aiu',
                'error.type',
                'gen_ai.response.finish_reasons',
                'gen_ai.agent.name',
                'gen_ai.provider.name',
                'copilot_chat.time_to_first_token',
                'copilot_chat.time_to_first_token_ms',
                'copilot_chat.ttft_ms',
                'gen_ai.server.time_to_first_token',
                'gen_ai.response.time_to_first_token',
                'gen_ai.response.time_to_first_chunk',
                'service.namespace',
                'copilot_chat.parent_chat_session_id',
                'copilot_chat.session_id',
                'gen_ai.conversation.id',
                'copilot_chat.chat_session_id');
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var spanId = reader.IsDBNull(0) ? null : reader.GetString(0);
            var key = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrEmpty(spanId) || string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!result.TryGetValue(spanId, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                result[spanId] = values;
            }

            values[key] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }

        return result;
    }

    private static OtlpSpanRecord? ReadRecord(
        SqliteDataReader reader,
        string source,
        Dictionary<string, Dictionary<string, string>> attributesBySpan)
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

        var attributes = attributesBySpan.TryGetValue(spanId, out var found)
            ? found
            : new Dictionary<string, string>(StringComparer.Ordinal);
        // The exporter keeps session identifiers both as columns and as attributes.
        SeedAttribute(attributes, "gen_ai.conversation.id", reader.IsDBNull(6) ? null : reader.GetString(6));
        SeedAttribute(attributes, "copilot_chat.session_id", reader.IsDBNull(13) ? null : reader.GetString(13));

        var sessionId = OtlpTraceParser.ParseSessionId(attributes);
        var inputTokens = reader.GetInt64(9);
        var outputTokens = reader.IsDBNull(10) ? 0 : reader.GetInt64(10);
        var cacheReadTokens = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
        var reasoningTokens = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
        if (reasoningTokens == 0)
        {
            // The Agent Host exporter leaves spans.reasoning_tokens NULL while still writing
            // the attribute, so the column alone would silently report zero reasoning tokens.
            reasoningTokens =
                ParseLong(Value(attributes, "gen_ai.usage.reasoning.output_tokens")) ??
                ParseLong(Value(attributes, "gen_ai.usage.reasoning_tokens")) ??
                0;
        }
        var cacheCreationTokens =
            ParseLong(Value(attributes, "gen_ai.usage.cache_creation.input_tokens")) ?? 0;
        var nanoAiu = ParseLong(Value(attributes, "copilot_chat.copilot_usage_nano_aiu"));
        var provider =
            Value(attributes, "gen_ai.provider.name") ??
            (reader.IsDBNull(5) ? null : reader.GetString(5)) ??
            source;
        var totalInputTokens = TokenSemantics.TotalInputTokens(
            provider,
            inputTokens,
            cacheReadTokens,
            cacheCreationTokens);
        var startMs = reader.GetInt64(3);
        var endMs = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
        // Same end-time peak rule as live ingestion.
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMs ?? startMs);
        var breakdown = ModelPricing.EvaluateDetailed(
            ModelPricing.GetPrice(model),
            totalInputTokens,
            cacheReadTokens,
            outputTokens,
            reasoningTokens,
            timestamp,
            cacheCreationTokens);

        var hasExplicitTtft = OtlpTraceParser.HasExplicitTtft(attributes);
        var ttftMs = OtlpTraceParser.ParseTtftMs(attributes);
        if (ttftMs is null && !reader.IsDBNull(16))
        {
            ttftMs = OtlpTraceParser.SanitizeDurationMs(reader.GetDouble(16));
        }

        if (ttftMs is not null &&
            endMs is long end &&
            end > startMs &&
            !hasExplicitTtft &&
            ttftMs.Value >= (end - startMs) * 0.9)
        {
            // Same rule as live ingestion: a first-chunk value that reaches the
            // total duration is buffering, not time to first token.
            ttftMs = null;
        }

        var isError = !reader.IsDBNull(15) && reader.GetInt64(15) == 2;

        return new OtlpSpanRecord
        {
            TraceId = traceId!,
            SpanId = spanId!,
            ParentSpanId = reader.IsDBNull(14) ? null : reader.GetString(14),
            Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            ServiceName = provider,
            ServiceNamespace = Value(attributes, "service.namespace"),
            Source = source == "agent-host" ? "agent-host" : "copilot-chat",
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = totalInputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheCreationTokens = cacheCreationTokens,
            ReasoningTokens = reasoningTokens,
            TtftMs = ttftMs,
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
            ErrorType = OtlpTraceParser.ErrorTypeFrom(
                Value(attributes, "error.type"),
                isError),
            ProviderName = provider,
            AgentName = Value(attributes, "gen_ai.agent.name") ??
                (reader.IsDBNull(17) ? null : reader.GetString(17)),
            FinishReasons = Value(attributes, "gen_ai.response.finish_reasons"),
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static void SeedAttribute(
        Dictionary<string, string> attributes,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !attributes.ContainsKey(key))
        {
            attributes[key] = value;
        }
    }

    private static string? Value(Dictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string? FirstNonEmpty(string? first, string? second) =>
        TrimToNull(first) ?? TrimToNull(second);

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
