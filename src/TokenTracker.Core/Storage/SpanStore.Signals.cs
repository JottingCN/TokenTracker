using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Storage;

public sealed partial class SpanStore
{
    public int InsertAgentRuns(IReadOnlyList<AgentRunRecord> runs)
    {
        if (runs.Count == 0)
        {
            return 0;
        }

        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO agent_runs (
                    otel_source, trace_id, span_id, parent_span_id, is_root,
                    session_id, agent_name, agent_type, provider_name,
                    request_model, response_model, service_name, service_namespace,
                    git_repository, git_branch, git_commit, github_org,
                    turn_count, input_tokens, output_tokens, cache_read_tokens,
                    cache_creation_tokens, reasoning_tokens, start_time_ms,
                    end_time_ms, is_error, error_type, received_at_ms
                ) VALUES (
                    @otel_source, @trace_id, @span_id, @parent_span_id, @is_root,
                    @session_id, @agent_name, @agent_type, @provider_name,
                    @request_model, @response_model, @service_name,
                    @service_namespace, @git_repository, @git_branch,
                    @git_commit, @github_org, @turn_count, @input_tokens,
                    @output_tokens, @cache_read_tokens, @cache_creation_tokens,
                    @reasoning_tokens, @start_time_ms, @end_time_ms, @is_error,
                    @error_type, @received_at_ms
                );
                """;

            AddParameter(command, "@otel_source", SqliteType.Text);
            AddParameter(command, "@trace_id", SqliteType.Text);
            AddParameter(command, "@span_id", SqliteType.Text);
            AddParameter(command, "@parent_span_id", SqliteType.Text);
            AddParameter(command, "@is_root", SqliteType.Integer);
            AddParameter(command, "@session_id", SqliteType.Text);
            AddParameter(command, "@agent_name", SqliteType.Text);
            AddParameter(command, "@agent_type", SqliteType.Text);
            AddParameter(command, "@provider_name", SqliteType.Text);
            AddParameter(command, "@request_model", SqliteType.Text);
            AddParameter(command, "@response_model", SqliteType.Text);
            AddParameter(command, "@service_name", SqliteType.Text);
            AddParameter(command, "@service_namespace", SqliteType.Text);
            AddParameter(command, "@git_repository", SqliteType.Text);
            AddParameter(command, "@git_branch", SqliteType.Text);
            AddParameter(command, "@git_commit", SqliteType.Text);
            AddParameter(command, "@github_org", SqliteType.Text);
            AddParameter(command, "@turn_count", SqliteType.Integer);
            AddParameter(command, "@input_tokens", SqliteType.Integer);
            AddParameter(command, "@output_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_read_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_creation_tokens", SqliteType.Integer);
            AddParameter(command, "@reasoning_tokens", SqliteType.Integer);
            AddParameter(command, "@start_time_ms", SqliteType.Integer);
            AddParameter(command, "@end_time_ms", SqliteType.Integer);
            AddParameter(command, "@is_error", SqliteType.Integer);
            AddParameter(command, "@error_type", SqliteType.Text);
            AddParameter(command, "@received_at_ms", SqliteType.Integer);

            var inserted = 0;
            foreach (var run in runs)
            {
                command.Parameters["@otel_source"].Value =
                    (object?)run.Source ?? "unknown";
                command.Parameters["@trace_id"].Value = run.TraceId;
                command.Parameters["@span_id"].Value = run.SpanId;
                command.Parameters["@parent_span_id"].Value =
                    (object?)run.ParentSpanId ?? DBNull.Value;
                command.Parameters["@is_root"].Value = run.IsRoot ? 1 : 0;
                command.Parameters["@session_id"].Value =
                    (object?)run.SessionId ?? DBNull.Value;
                command.Parameters["@agent_name"].Value =
                    (object?)run.AgentName ?? DBNull.Value;
                command.Parameters["@agent_type"].Value =
                    (object?)run.AgentType ?? DBNull.Value;
                command.Parameters["@provider_name"].Value =
                    (object?)run.ProviderName ?? DBNull.Value;
                command.Parameters["@request_model"].Value =
                    (object?)run.RequestModel ?? DBNull.Value;
                command.Parameters["@response_model"].Value =
                    (object?)run.ResponseModel ?? DBNull.Value;
                command.Parameters["@service_name"].Value =
                    (object?)run.ServiceName ?? DBNull.Value;
                command.Parameters["@service_namespace"].Value =
                    (object?)run.ServiceNamespace ?? DBNull.Value;
                command.Parameters["@git_repository"].Value =
                    (object?)run.GitRepository ?? DBNull.Value;
                command.Parameters["@git_branch"].Value =
                    (object?)run.GitBranch ?? DBNull.Value;
                command.Parameters["@git_commit"].Value =
                    (object?)run.GitCommit ?? DBNull.Value;
                command.Parameters["@github_org"].Value =
                    (object?)run.GithubOrg ?? DBNull.Value;
                command.Parameters["@turn_count"].Value =
                    run.TurnCount is null ? DBNull.Value : run.TurnCount.Value;
                command.Parameters["@input_tokens"].Value = run.InputTokens;
                command.Parameters["@output_tokens"].Value = run.OutputTokens;
                command.Parameters["@cache_read_tokens"].Value =
                    run.CacheReadTokens;
                command.Parameters["@cache_creation_tokens"].Value =
                    run.CacheCreationTokens;
                command.Parameters["@reasoning_tokens"].Value =
                    run.ReasoningTokens;
                command.Parameters["@start_time_ms"].Value = run.StartTimeMs;
                command.Parameters["@end_time_ms"].Value =
                    run.EndTimeMs is null ? DBNull.Value : run.EndTimeMs.Value;
                command.Parameters["@is_error"].Value = run.IsError ? 1 : 0;
                command.Parameters["@error_type"].Value =
                    (object?)run.ErrorType ?? DBNull.Value;
                command.Parameters["@received_at_ms"].Value = run.ReceivedAtMs;
                inserted += command.ExecuteNonQuery();
            }

            var resolveTraceIds = runs
                .Where(run =>
                    (string.IsNullOrWhiteSpace(run.SessionId) ||
                     run.SessionId.TrimStart().StartsWith(
                         "call_",
                         StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(run.ParentSpanId))
                .Select(run => run.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (resolveTraceIds.Count > 0)
            {
                ResolveSessionIdsForTraces(
                    connection,
                    transaction,
                    resolveTraceIds);
            }

            transaction.Commit();
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public int InsertMetricPoints(IReadOnlyList<OtlpMetricPointRecord> points)
    {
        if (points.Count == 0)
        {
            return 0;
        }

        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO metric_points (
                    otel_source, series_key, metric_name, metric_kind,
                    temporality, service_name, scope_name, attrs,
                    window_session_id, attributed_session_id,
                    attribution_method, attribution_confidence,
                    start_time_ms, end_time_ms, value, count, sum, min, max,
                    bucket_counts, explicit_bounds, received_at_ms
                ) VALUES (
                    @otel_source, @series_key, @metric_name, @metric_kind,
                    @temporality, @service_name, @scope_name, @attrs,
                    @window_session_id, @attributed_session_id,
                    @attribution_method, @attribution_confidence,
                    @start_time_ms, @end_time_ms, @value, @count, @sum, @min,
                    @max, @bucket_counts, @explicit_bounds, @received_at_ms
                );
                """;

            AddParameter(command, "@otel_source", SqliteType.Text);
            AddParameter(command, "@series_key", SqliteType.Text);
            AddParameter(command, "@metric_name", SqliteType.Text);
            AddParameter(command, "@metric_kind", SqliteType.Text);
            AddParameter(command, "@temporality", SqliteType.Text);
            AddParameter(command, "@service_name", SqliteType.Text);
            AddParameter(command, "@scope_name", SqliteType.Text);
            AddParameter(command, "@attrs", SqliteType.Text);
            AddParameter(command, "@window_session_id", SqliteType.Text);
            AddParameter(command, "@attributed_session_id", SqliteType.Text);
            AddParameter(command, "@attribution_method", SqliteType.Text);
            AddParameter(command, "@attribution_confidence", SqliteType.Real);
            AddParameter(command, "@start_time_ms", SqliteType.Integer);
            AddParameter(command, "@end_time_ms", SqliteType.Integer);
            AddParameter(command, "@value", SqliteType.Real);
            AddParameter(command, "@count", SqliteType.Integer);
            AddParameter(command, "@sum", SqliteType.Real);
            AddParameter(command, "@min", SqliteType.Real);
            AddParameter(command, "@max", SqliteType.Real);
            AddParameter(command, "@bucket_counts", SqliteType.Text);
            AddParameter(command, "@explicit_bounds", SqliteType.Text);
            AddParameter(command, "@received_at_ms", SqliteType.Integer);

            var lastEndBySeries = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            using (var lastCommand = connection.CreateCommand())
            {
                lastCommand.Transaction = transaction;
                lastCommand.CommandText = """
                    SELECT COALESCE(MAX(end_time_ms), 0)
                    FROM metric_points
                    WHERE otel_source = @source
                      AND series_key = @series;
                    """;
                lastCommand.Parameters.AddWithValue("@source", string.Empty);
                lastCommand.Parameters.AddWithValue("@series", string.Empty);

                foreach (var point in points)
                {
                    var key = $"{point.Source}\u001f{point.SeriesKey}";
                    if (lastEndBySeries.ContainsKey(key))
                    {
                        continue;
                    }

                    lastCommand.Parameters["@source"].Value = point.Source;
                    lastCommand.Parameters["@series"].Value = point.SeriesKey;
                    lastEndBySeries[key] = Convert.ToInt64(
                        lastCommand.ExecuteScalar() ?? 0L);
                }
            }

            const long sampleIntervalMs = 60_000;
            var callSessions = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);
            var windowCandidates = new Dictionary<string, (int Count, string? Session)>(
                StringComparer.OrdinalIgnoreCase);
            var inserted = 0;
            foreach (var point in points)
            {
                var seriesKey = $"{point.Source}\u001f{point.SeriesKey}";
                if (lastEndBySeries.TryGetValue(seriesKey, out var lastEnd) &&
                    lastEnd > 0 &&
                    point.EndTimeMs - lastEnd < sampleIntervalMs)
                {
                    continue;
                }

                lastEndBySeries[seriesKey] = point.EndTimeMs;
                command.Parameters["@otel_source"].Value = point.Source;
                command.Parameters["@series_key"].Value = point.SeriesKey;
                command.Parameters["@metric_name"].Value = point.MetricName;
                command.Parameters["@metric_kind"].Value = point.MetricKind;
                command.Parameters["@temporality"].Value =
                    (object?)point.Temporality ?? DBNull.Value;
                command.Parameters["@service_name"].Value =
                    (object?)point.ServiceName ?? DBNull.Value;
                command.Parameters["@scope_name"].Value =
                    (object?)point.ScopeName ?? DBNull.Value;
                command.Parameters["@attrs"].Value =
                    (object?)point.Attributes ?? DBNull.Value;
                command.Parameters["@window_session_id"].Value =
                    (object?)point.WindowSessionId ?? DBNull.Value;
                var attribution = ResolveMetricAttribution(
                    connection,
                    transaction,
                    point,
                    callSessions,
                    windowCandidates);
                command.Parameters["@attributed_session_id"].Value =
                    (object?)attribution.SessionId ?? DBNull.Value;
                command.Parameters["@attribution_method"].Value =
                    (object?)attribution.Method ?? DBNull.Value;
                command.Parameters["@attribution_confidence"].Value =
                    attribution.Confidence is null
                        ? DBNull.Value
                        : attribution.Confidence.Value;
                command.Parameters["@start_time_ms"].Value = point.StartTimeMs;
                command.Parameters["@end_time_ms"].Value = point.EndTimeMs;
                command.Parameters["@value"].Value =
                    point.Value is null ? DBNull.Value : point.Value.Value;
                command.Parameters["@count"].Value =
                    point.Count is null ? DBNull.Value : point.Count.Value;
                command.Parameters["@sum"].Value =
                    point.Sum is null ? DBNull.Value : point.Sum.Value;
                command.Parameters["@min"].Value =
                    point.Min is null ? DBNull.Value : point.Min.Value;
                command.Parameters["@max"].Value =
                    point.Max is null ? DBNull.Value : point.Max.Value;
                command.Parameters["@bucket_counts"].Value =
                    (object?)point.BucketCounts ?? DBNull.Value;
                command.Parameters["@explicit_bounds"].Value =
                    (object?)point.ExplicitBounds ?? DBNull.Value;
                command.Parameters["@received_at_ms"].Value = point.ReceivedAtMs;
                inserted += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static (
        string? SessionId,
        string? Method,
        double? Confidence) ResolveMetricAttribution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OtlpMetricPointRecord point,
        Dictionary<string, string?> callSessions,
        Dictionary<string, (int Count, string? Session)> windowCandidates)
    {
        if (!string.IsNullOrWhiteSpace(point.DirectSessionId))
        {
            return (point.DirectSessionId, "direct", 1.0);
        }

        if (!string.IsNullOrWhiteSpace(point.ToolCallId))
        {
            if (!callSessions.TryGetValue(point.ToolCallId, out var sessionId))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT session_id FROM session_events
                    WHERE call_id = @call
                      AND session_id IS NOT NULL
                      AND lower(substr(session_id, 1, 5)) <> 'call_'
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("@call", point.ToolCallId);
                sessionId = command.ExecuteScalar() as string;
                callSessions[point.ToolCallId] = sessionId;
            }

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return (sessionId, "tool-call", 1.0);
            }
        }

        if (!string.IsNullOrWhiteSpace(point.WindowSessionId))
        {
            if (!windowCandidates.TryGetValue(
                    point.WindowSessionId,
                    out var candidates))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT COUNT(DISTINCT chat_session_id),
                           MIN(chat_session_id)
                    FROM window_session_links
                    WHERE window_session_id = @window;
                    """;
                command.Parameters.AddWithValue(
                    "@window",
                    point.WindowSessionId);
                using var reader = command.ExecuteReader();
                reader.Read();
                candidates = (
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
                windowCandidates[point.WindowSessionId] = candidates;
            }

            if (candidates.Count == 1 &&
                !string.IsNullOrWhiteSpace(candidates.Session))
            {
                return (candidates.Session, "window", 0.6);
            }
        }

        return (null, "window-only", null);
    }

    public int ReconcileMetricAttribution()
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var changed = ReconcileMetricAttribution(
                connection,
                transaction,
                windowSessionIds: null);
            transaction.Commit();
            return changed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int ReconcileMetricAttribution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string>? windowSessionIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var windowFilter = string.Empty;
        if (windowSessionIds is { Count: > 0 })
        {
            var parameters = new List<string>(windowSessionIds.Count);
            var index = 0;
            foreach (var windowId in windowSessionIds)
            {
                var parameter = $"@window_{index++}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, windowId);
            }

            windowFilter =
                $" AND window_session_id IN ({string.Join(", ", parameters)})";
        }

        command.CommandText = $"""
            UPDATE metric_points
            SET attributed_session_id = (
                    SELECT MIN(l.chat_session_id)
                    FROM window_session_links l
                    WHERE l.window_session_id =
                        metric_points.window_session_id
                ),
                attribution_method = 'window',
                attribution_confidence = 0.6
            WHERE attributed_session_id IS NULL
              AND window_session_id IS NOT NULL{windowFilter}
              AND (
                    SELECT COUNT(DISTINCT l.chat_session_id)
                    FROM window_session_links l
                    WHERE l.window_session_id =
                        metric_points.window_session_id
                ) = 1;
            """;
        return command.ExecuteNonQuery();
    }

    public int CompactMetricPoints()
    {        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM metric_points
                WHERE rowid IN (
                    SELECT rowid FROM (
                        SELECT rowid,
                               ROW_NUMBER() OVER (
                                   PARTITION BY otel_source, series_key,
                                                end_time_ms / 60000
                                   ORDER BY end_time_ms DESC, rowid DESC) AS rn
                        FROM metric_points
                    )
                    WHERE rn > 1
                );
                """;
            var removed = command.ExecuteNonQuery();
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<AgentRunRow> GetAgentRuns(
        string sessionId,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : " AND otel_source = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT trace_id, span_id, parent_span_id, is_root, session_id,
                   agent_name, agent_type, provider_name, request_model,
                   response_model, otel_source, git_repository, git_branch,
                   git_commit, github_org, turn_count, input_tokens,
                   output_tokens, cache_read_tokens, cache_creation_tokens,
                   reasoning_tokens, start_time_ms, end_time_ms, is_error,
                   error_type
            FROM agent_runs
            WHERE session_id = @session_id{sourceFilter}
            ORDER BY start_time_ms, trace_id, span_id;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new List<AgentRunRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AgentRunRow
            {
                TraceId = reader.GetString(0),
                SpanId = reader.GetString(1),
                ParentSpanId = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsRoot = reader.GetInt64(3) != 0,
                SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                AgentName = reader.IsDBNull(5) ? null : reader.GetString(5),
                AgentType = reader.IsDBNull(6) ? null : reader.GetString(6),
                ProviderName = reader.IsDBNull(7) ? null : reader.GetString(7),
                RequestModel = reader.IsDBNull(8) ? null : reader.GetString(8),
                ResponseModel = reader.IsDBNull(9) ? null : reader.GetString(9),
                Source = reader.IsDBNull(10) ? null : reader.GetString(10),
                GitRepository = reader.IsDBNull(11) ? null : reader.GetString(11),
                GitBranch = reader.IsDBNull(12) ? null : reader.GetString(12),
                GitCommit = reader.IsDBNull(13) ? null : reader.GetString(13),
                GithubOrg = reader.IsDBNull(14) ? null : reader.GetString(14),
                TurnCount = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                InputTokens = reader.GetInt64(16),
                OutputTokens = reader.GetInt64(17),
                CacheReadTokens = reader.GetInt64(18),
                CacheCreationTokens = reader.GetInt64(19),
                ReasoningTokens = reader.GetInt64(20),
                StartTimeMs = reader.GetInt64(21),
                EndTimeMs = reader.IsDBNull(22) ? null : reader.GetInt64(22),
                IsError = reader.GetInt64(23) != 0,
                ErrorType = reader.IsDBNull(24) ? null : reader.GetString(24),
            });
        }

        return rows;
    }

    public IReadOnlyList<HookSummaryRow> GetHookSummaries(
        string sessionId,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(hook_type, ''), COALESCE(hook_decision, ''),
                   COALESCE(hook_result_kind, ''), {SourceSql} AS source,
                   start_time_ms, end_time_ms, is_error, hook_tool_names
            FROM session_events
            WHERE session_id = @session_id
              AND operation = 'execute_hook'{sourceFilter}
            ORDER BY start_time_ms;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new Dictionary<string, HookAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hookType = reader.GetString(0);
            var decision = reader.GetString(1);
            var resultKind = reader.GetString(2);
            var hookSource = reader.GetString(3);
            var key = $"{hookSource}\u001f{hookType}\u001f{decision}\u001f{resultKind}";
            if (!rows.TryGetValue(key, out var row))
            {
                row = new HookAccumulator(
                    hookType,
                    decision,
                    resultKind,
                    hookSource);
                rows[key] = row;
            }

            row.Add(
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        return rows.Values
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.HookType, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Build())
            .ToList();
    }

    public IReadOnlyList<TraceNodeRow> GetTraceNodes(
        string sessionId,
        string? source = null,
        int limit = 5_000)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT * FROM (
                SELECT trace_id, span_id, parent_span_id, 'chat' AS kind,
                       'chat' AS operation, name, NULL AS tool,
                       {SourceSql} AS source, start_time_ms, end_time_ms,
                       error_type,
                       CASE WHEN error_type IS NULL THEN 0 ELSE 1 END AS is_error,
                       model, input_tokens, output_tokens, is_main,
                       cache_read_tokens
                FROM spans
                WHERE session_id = @session_id
                  AND COALESCE(otel_source, '') <> 'combined'{sourceFilter}
                UNION ALL
                SELECT trace_id, span_id, parent_span_id, 'event' AS kind,
                       operation, name, tool_name, {SourceSql} AS source,
                       start_time_ms, end_time_ms, error_type, is_error,
                       NULL, 0, 0, 1, 0
                FROM session_events
                WHERE session_id = @session_id{sourceFilter}
                ORDER BY start_time_ms DESC, trace_id DESC, span_id DESC
                LIMIT @limit
            )
            ORDER BY start_time_ms ASC, trace_id ASC, span_id ASC;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue(
            "@limit",
            Math.Clamp(limit, 1, 5_000));
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var nodes = new List<TraceNodeRow>();
        var mergeKeys = new Dictionary<string, TraceMergeKey>(
            StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var traceId = reader.GetString(0);
            var spanId = reader.GetString(1);
            var parentId = reader.IsDBNull(2) ? null : reader.GetString(2);
            var node = new TraceNodeRow
            {
                Id = $"{traceId}:{spanId}",
                ParentId = parentId is null ? null : $"{traceId}:{parentId}",
                Kind = reader.GetString(3),
                Operation = reader.GetString(4),
                Name = reader.IsDBNull(5) ? null : reader.GetString(5),
                Tool = reader.IsDBNull(6) ? null : reader.GetString(6),
                Source = reader.IsDBNull(7) ? null : reader.GetString(7),
                StartTimeMs = reader.GetInt64(8),
                DurationMs = reader.IsDBNull(9)
                    ? null
                    : Math.Max(0, reader.GetInt64(9) - reader.GetInt64(8)),
                ErrorType = reader.IsDBNull(10) ? null : reader.GetString(10),
                IsError = reader.GetInt64(11) != 0,
                InputTokens = reader.GetInt64(13),
                OutputTokens = reader.GetInt64(14),
                CacheReadTokens = reader.GetInt64(16),
            };
            nodes.Add(node);
            mergeKeys[node.Id] = new TraceMergeKey(
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15) != 0);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            nodes = MergeCrossSourceTraceNodes(nodes, mergeKeys);
        }

        var byId = nodes.ToDictionary(
            node => node.Id,
            StringComparer.OrdinalIgnoreCase);
        return nodes
            .Select(node => node with
            {
                Depth = Depth(node, byId, 0),
            })
            .OrderBy(node => node.StartTimeMs)
            .ToList();
    }

    private static List<TraceNodeRow> MergeCrossSourceTraceNodes(
        List<TraceNodeRow> nodes,
        IReadOnlyDictionary<string, TraceMergeKey> keys)
    {
        var groups = new List<List<TraceNodeRow>>();
        foreach (var node in nodes
                     .Where(node => node.Kind == "chat")
                     .OrderBy(node => node.StartTimeMs))
        {
            if (!keys.TryGetValue(node.Id, out var key))
            {
                continue;
            }

            var group = groups.FirstOrDefault(candidate =>
                keys.TryGetValue(candidate[0].Id, out var firstKey) &&
                firstKey.Equals(key) &&
                candidate.Any(item => TraceIntervalsOverlap(item, node)) &&
                candidate.All(item => !string.Equals(
                    item.Source,
                    node.Source,
                    StringComparison.OrdinalIgnoreCase)));
            if (group is null)
            {
                groups.Add(new List<TraceNodeRow> { node });
            }
            else
            {
                group.Add(node);
            }
        }

        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Count < 2)
            {
                continue;
            }

            var primary = group
                .OrderByDescending(node => string.Equals(
                    node.Source,
                    "copilot-chat",
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(node => node.StartTimeMs)
                .First();
            foreach (var node in group)
            {
                if (string.Equals(node.Id, primary.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                dropped.Add(node.Id);
                remap[node.Id] = primary.Id;
            }
        }

        if (dropped.Count == 0)
        {
            return nodes;
        }

        var result = new List<TraceNodeRow>(nodes.Count - dropped.Count);
        foreach (var node in nodes)
        {
            if (dropped.Contains(node.Id))
            {
                continue;
            }

            var updated = node;
            if (node.ParentId is not null &&
                remap.TryGetValue(node.ParentId, out var mappedParent))
            {
                updated = updated with { ParentId = mappedParent };
            }

            result.Add(updated);
        }

        return result;
    }

    private static bool TraceIntervalsOverlap(TraceNodeRow left, TraceNodeRow right)
    {
        if (left.DurationMs is long leftDuration &&
            right.DurationMs is long rightDuration)
        {
            return left.StartTimeMs <= right.StartTimeMs + rightDuration &&
                right.StartTimeMs <= left.StartTimeMs + leftDuration;
        }

        return Math.Abs(left.StartTimeMs - right.StartTimeMs) <= 2_000;
    }

    private readonly record struct TraceMergeKey(
        string? Model,
        long InputTokens,
        long OutputTokens,
        bool IsMain);

    public SignalHealthRow GetSignalHealth(
        string? sessionId = null,
        string? source = null,
        long fromMs = 0,
        long toMs = long.MaxValue)
    {
        using var connection = OpenConnection();
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : " AND otel_source = @source";
        var sessionFilter = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : " AND session_id = @session_id";
        var agentRangeFilter =
            " AND start_time_ms >= @from AND start_time_ms < @to";
        var metricRangeFilter =
            " AND end_time_ms >= @from AND end_time_ms < @to";

        long agentRuns;
        long agentLast;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*), COALESCE(MAX(start_time_ms), 0) " +
                "FROM agent_runs WHERE 1 = 1" +
                $"{agentRangeFilter}{sessionFilter}{sourceFilter};";
            AddScopeParameters(command, sessionId, source);
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            reader.Read();
            agentRuns = reader.GetInt64(0);
            agentLast = reader.GetInt64(1);
        }

        long hookEvents;
        long hookLast;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*), COALESCE(MAX(start_time_ms), 0) " +
                "FROM session_events WHERE operation = 'execute_hook'" +
                $"{agentRangeFilter}{sessionFilter}{sourceFilter};";
            AddScopeParameters(command, sessionId, source);
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            reader.Read();
            hookEvents = reader.GetInt64(0);
            hookLast = reader.GetInt64(1);
        }

        long metricPoints;
        long metricLast;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*), COALESCE(MAX(end_time_ms), 0) " +
                "FROM metric_points WHERE 1 = 1" +
                $"{metricRangeFilter}{sourceFilter};";
            AddScopeParameters(command, null, source);
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            reader.Read();
            metricPoints = reader.GetInt64(0);
            metricLast = reader.GetInt64(1);
        }

        return new SignalHealthRow
        {
            AgentRuns = agentRuns,
            HookEvents = hookEvents,
            MetricPoints = metricPoints,
            LastSignalMs = new[]
            {
                agentLast,
                hookLast,
                metricLast,
            }.Max(),
        };
    }

    private static readonly IReadOnlyCollection<string> OutcomeMetricNames =
        MetricNames.Outcome;

    public OutcomeSummary GetOutcomeSummary(
        long fromMs,
        long toMs,
        string? source = null,
        string? sessionId = null)
    {
        var deltas = GetMetricDeltas(
            fromMs,
            toMs,
            source,
            sessionId,
            OutcomeMetricNames);
        long editAccepted = 0;
        long editRejected = 0;
        long chatEditAccepted = 0;
        long chatEditRejected = 0;
        long chatEditSaved = 0;
        long linesAdded = 0;
        long linesRemoved = 0;
        long userCopy = 0;
        long userInsert = 0;
        long userApply = 0;
        long userFollowup = 0;
        long feedbackPositive = 0;
        long feedbackNegative = 0;
        long agentEditSuccess = 0;
        long agentEditError = 0;
        long summarizationApplied = 0;
        long summarizationFailed = 0;
        long pullRequests = 0;
        long cloudSessions = 0;
        long cloudPrReady = 0;
        long cloudOperations = 0;
        long cloudErrors = 0;
        double cloudDurationSum = 0;
        long cloudDurationCount = 0;
        long toolCalls = 0;
        double survivalFourGramSum = 0;
        long survivalFourGramCount = 0;
        double survivalNoRevertSum = 0;
        long survivalNoRevertCount = 0;
        double toolDurationSum = 0;
        long toolDurationCount = 0;

        foreach (var delta in deltas)
        {
            var value = delta.Value ?? delta.Count ?? 0;
            var count = (long)Math.Round(value);
            var attributes = ParseMetricAttributes(delta.Attributes);
            switch (delta.MetricName)
            {
                case "copilot_chat.edit.acceptance.count":
                    switch (Get(attributes, "copilot_chat.edit.outcome"))
                    {
                        case "accepted":
                            editAccepted += count;
                            break;
                        case "rejected":
                            editRejected += count;
                            break;
                    }
                    break;
                case "copilot_chat.chat_edit.outcome.count":
                    switch (Get(attributes, "copilot_chat.edit.outcome"))
                    {
                        case "accepted":
                            chatEditAccepted += count;
                            break;
                        case "rejected":
                            chatEditRejected += count;
                            break;
                        case "saved":
                            chatEditSaved += count;
                            break;
                    }
                    break;
                case "copilot_chat.lines_of_code.count":
                    switch (Get(attributes, "type"))
                    {
                        case "added":
                            linesAdded += count;
                            break;
                        case "removed":
                            linesRemoved += count;
                            break;
                    }
                    break;
                case "copilot_chat.user.action.count":
                    switch (Get(attributes, "action"))
                    {
                        case "copy":
                            userCopy += count;
                            break;
                        case "insert":
                            userInsert += count;
                            break;
                        case "apply":
                            userApply += count;
                            break;
                        case "followup":
                            userFollowup += count;
                            break;
                    }
                    break;
                case "copilot_chat.user.feedback.count":
                    switch (Get(attributes, "rating"))
                    {
                        case "positive":
                            feedbackPositive += count;
                            break;
                        case "negative":
                            feedbackNegative += count;
                            break;
                    }
                    break;
                case "copilot_chat.agent.edit_response.count":
                    switch (Get(attributes, "outcome"))
                    {
                        case "success":
                            agentEditSuccess += count;
                            break;
                        case "error":
                            agentEditError += count;
                            break;
                    }
                    break;
                case "copilot_chat.agent.summarization.count":
                    switch (Get(attributes, "outcome"))
                    {
                        case "applied":
                            summarizationApplied += count;
                            break;
                        case "failed":
                            summarizationFailed += count;
                            break;
                    }
                    break;
                case "copilot_chat.pull_request.count":
                    pullRequests += count;
                    break;
                case "copilot_chat.cloud.session.count":
                    cloudSessions += count;
                    break;
                case "copilot_chat.cloud.pr_ready.count":
                    cloudPrReady += count;
                    break;
                case "copilot_chat.cloud.operation.count":
                    cloudOperations += count;
                    break;
                case "copilot_chat.cloud.error.count":
                    cloudErrors += count;
                    break;
                case "copilot_chat.cloud.operation.duration":
                    cloudDurationSum += delta.Sum ?? 0;
                    cloudDurationCount += delta.Count ?? 0;
                    break;
                case "copilot_chat.tool.call.count":
                    toolCalls += count;
                    break;
                case "copilot_chat.edit.survival.four_gram":
                    survivalFourGramSum += delta.Sum ?? 0;
                    survivalFourGramCount += delta.Count ?? 0;
                    break;
                case "copilot_chat.edit.survival.no_revert":
                    survivalNoRevertSum += delta.Sum ?? 0;
                    survivalNoRevertCount += delta.Count ?? 0;
                    break;
                case "copilot_chat.tool.call.duration":
                    toolDurationSum += delta.Sum ?? 0;
                    toolDurationCount += delta.Count ?? 0;
                    break;
            }
        }

        return new OutcomeSummary
        {
            EditAccepted = editAccepted,
            EditRejected = editRejected,
            ChatEditAccepted = chatEditAccepted,
            ChatEditRejected = chatEditRejected,
            ChatEditSaved = chatEditSaved,
            LinesAdded = linesAdded,
            LinesRemoved = linesRemoved,
            SurvivalFourGram = survivalFourGramCount > 0
                ? survivalFourGramSum / survivalFourGramCount
                : null,
            SurvivalNoRevert = survivalNoRevertCount > 0
                ? survivalNoRevertSum / survivalNoRevertCount
                : null,
            UserCopy = userCopy,
            UserInsert = userInsert,
            UserApply = userApply,
            UserFollowup = userFollowup,
            FeedbackPositive = feedbackPositive,
            FeedbackNegative = feedbackNegative,
            AgentEditSuccess = agentEditSuccess,
            AgentEditError = agentEditError,
            SummarizationApplied = summarizationApplied,
            SummarizationFailed = summarizationFailed,
            PullRequests = pullRequests,
            CloudSessions = cloudSessions,
            CloudPrReady = cloudPrReady,
            CloudOperations = cloudOperations,
            CloudErrors = cloudErrors,
            CloudOperationAvgMs = cloudDurationCount > 0
                ? cloudDurationSum / cloudDurationCount
                : null,
            ToolMetricCalls = toolCalls,
            ToolMetricAvgMs = toolDurationCount > 0
                ? toolDurationSum / toolDurationCount
                : null,
        };
    }

    private IReadOnlyList<MetricDeltaRow> GetMetricDeltas(
        long fromMs,
        long toMs,
        string? source,
        string? sessionId,
        IReadOnlyCollection<string> metricNames)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : " AND otel_source = @source";
        var sessionFilter = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : """
               AND (
                   attributed_session_id = @outcome_session
                   OR (
                       attributed_session_id IS NULL
                       AND window_session_id IS NOT NULL
                       AND window_session_id IN (
                           SELECT window_session_id
                           FROM window_session_links
                           WHERE chat_session_id = @outcome_session)
                       AND (
                           SELECT COUNT(DISTINCT l.chat_session_id)
                           FROM window_session_links l
                           WHERE l.window_session_id =
                               metric_points.window_session_id
                       ) = 1
                   )
               )
              """;
        var metricFilter = string.Empty;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (metricNames.Count > 0)
        {
            var parameters = new List<string>(metricNames.Count);
            var index = 0;
            foreach (var metricName in metricNames)
            {
                var parameter = $"@metric_{index++}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, metricName);
            }

            metricFilter = $" AND metric_name IN ({string.Join(", ", parameters)})";
        }

        // Only the requested window is aggregated; the one sample immediately
        // before @from is joined back per series so counter deltas stay correct.
        // Previously the window function ordered the whole table (< @to) and
        // discarded the rows before @from afterwards, which made every refresh
        // scan the entire metric history.
        command.CommandText = $"""
            WITH windowed AS (
                SELECT metric_name, attrs, metric_kind, start_time_ms,
                       end_time_ms, value, count, sum,
                       otel_source, series_key
                FROM metric_points
                WHERE end_time_ms >= @from
                  AND end_time_ms < @to{sourceFilter}{metricFilter}{sessionFilter}
            ),
            lagged AS (
                SELECT w.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY w.otel_source, w.series_key
                           ORDER BY w.end_time_ms) AS rn,
                       LAG(w.value) OVER (
                           PARTITION BY w.otel_source, w.series_key
                           ORDER BY w.end_time_ms) AS prev_value,
                       LAG(w.count) OVER (
                           PARTITION BY w.otel_source, w.series_key
                           ORDER BY w.end_time_ms) AS prev_count,
                       LAG(w.sum) OVER (
                           PARTITION BY w.otel_source, w.series_key
                           ORDER BY w.end_time_ms) AS prev_sum,
                       LAG(w.start_time_ms) OVER (
                           PARTITION BY w.otel_source, w.series_key
                           ORDER BY w.end_time_ms) AS prev_start
                FROM windowed w
            ),
            prior AS (
                SELECT l.otel_source, l.series_key, p.value, p.count, p.sum,
                       p.start_time_ms
                FROM lagged l
                JOIN metric_points p
                  ON p.otel_source = l.otel_source
                 AND p.series_key = l.series_key
                 AND p.end_time_ms = (
                     SELECT MAX(q.end_time_ms)
                     FROM metric_points q
                     WHERE q.otel_source = l.otel_source
                       AND q.series_key = l.series_key
                       AND q.end_time_ms < @from)
                WHERE l.rn = 1
            )
            SELECT l.metric_name, l.attrs,
                   CASE WHEN l.metric_kind = 'gauge' THEN l.value
                        WHEN l.rn > 1 THEN
                            CASE WHEN l.start_time_ms <> l.prev_start
                                   OR l.value < l.prev_value THEN l.value
                                 ELSE l.value - l.prev_value END
                        WHEN pr.start_time_ms IS NULL THEN l.value
                        WHEN l.start_time_ms <> pr.start_time_ms
                             OR l.value < pr.value THEN l.value
                        ELSE l.value - pr.value END AS delta_value,
                   CASE WHEN l.rn > 1 THEN
                            CASE WHEN l.prev_count IS NULL
                                   OR l.start_time_ms <> l.prev_start
                                   OR l.count < l.prev_count THEN l.count
                                 ELSE l.count - l.prev_count END
                        WHEN pr.start_time_ms IS NULL OR pr.count IS NULL
                            THEN l.count
                        WHEN l.start_time_ms <> pr.start_time_ms
                             OR l.count < pr.count THEN l.count
                        ELSE l.count - pr.count END AS delta_count,
                   CASE WHEN l.rn > 1 THEN
                            CASE WHEN l.prev_sum IS NULL
                                   OR l.start_time_ms <> l.prev_start
                                   OR l.sum < l.prev_sum THEN l.sum
                                 ELSE l.sum - l.prev_sum END
                        WHEN pr.start_time_ms IS NULL OR pr.sum IS NULL
                            THEN l.sum
                        WHEN l.start_time_ms <> pr.start_time_ms
                             OR l.sum < pr.sum THEN l.sum
                        ELSE l.sum - pr.sum END AS delta_sum
            FROM lagged l
            LEFT JOIN prior pr
              ON pr.otel_source = l.otel_source
             AND pr.series_key = l.series_key
            ORDER BY l.end_time_ms;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            command.Parameters.AddWithValue("@outcome_session", sessionId);
        }

        var rows = new List<MetricDeltaRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MetricDeltaRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return rows;
    }

    private static Dictionary<string, string> ParseMetricAttributes(
        string? attributes)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(attributes))
        {
            return result;
        }

        foreach (var line in attributes.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[line[..separator].Trim()] =
                line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private sealed record MetricDeltaRow(
        string MetricName,
        string? Attributes,
        double? Value,
        long? Count,
        double? Sum);

    private static void AddScopeParameters(
        SqliteCommand command,
        string? sessionId,
        string? source)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            command.Parameters.AddWithValue("@session_id", sessionId);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }
    }

    private static int Depth(
        TraceNodeRow node,
        IReadOnlyDictionary<string, TraceNodeRow> byId,
        int guard)
    {
        if (guard >= 32 ||
            node.ParentId is null ||
            !byId.TryGetValue(node.ParentId, out var parent))
        {
            return 0;
        }

        return 1 + Depth(parent, byId, guard + 1);
    }

    private sealed class HookAccumulator
    {
        private readonly HashSet<string> _toolNames = new(
            StringComparer.OrdinalIgnoreCase);
        private double _totalDurationMs;
        private long _durationSamples;

        public HookAccumulator(
            string hookType,
            string decision,
            string resultKind,
            string source)
        {
            HookType = hookType;
            Decision = decision;
            ResultKind = resultKind;
            Source = source;
        }

        public string HookType { get; }
        public string Decision { get; }
        public string ResultKind { get; }
        public string Source { get; }
        public long Count { get; private set; }
        public long ErrorCount { get; private set; }
        public long LastSeenMs { get; private set; }

        public void Add(
            long startMs,
            long? endMs,
            bool isError,
            string? toolNames)
        {
            Count++;
            LastSeenMs = Math.Max(LastSeenMs, startMs);
            if (endMs is > 0 && endMs.Value > startMs)
            {
                _totalDurationMs += endMs.Value - startMs;
                _durationSamples++;
            }

            if (isError)
            {
                ErrorCount++;
            }

            if (!string.IsNullOrWhiteSpace(toolNames))
            {
                foreach (var tool in toolNames.Split(
                             ',',
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
                {
                    _toolNames.Add(tool);
                }
            }
        }

        public HookSummaryRow Build() => new()
        {
            HookType = HookType,
            Decision = Decision,
            ResultKind = ResultKind,
            Source = Source,
            Count = Count,
            AvgDurationMs = _durationSamples > 0
                ? _totalDurationMs / _durationSamples
                : null,
            ErrorCount = ErrorCount,
            ToolNames = _toolNames
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LastSeenMs = LastSeenMs,
        };
    }
}
