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

            // Cumulative samples are re-exported every ~10 s, so only one row per minute per
            // series is kept to bound growth. A newer sample has to overwrite that row instead
            // of being dropped: dropping it loses everything between the last stored row and
            // the moment the extension host exits, which for rare events such as edit
            // acceptances means losing the ones that just happened.
            using var mergeCommand = connection.CreateCommand();
            mergeCommand.Transaction = transaction;
            mergeCommand.CommandText = """
                UPDATE metric_points
                   SET value = @value, count = @count, sum = @sum, min = @min,
                       max = @max, bucket_counts = @bucket_counts,
                       explicit_bounds = @explicit_bounds,
                       window_session_id = @window_session_id,
                       attributed_session_id = @attributed_session_id,
                       attribution_method = @attribution_method,
                       attribution_confidence = @attribution_confidence,
                       end_time_ms = @end_time_ms, received_at_ms = @received_at_ms
                 WHERE otel_source = @otel_source
                   AND series_key = @series_key
                   AND end_time_ms = @last_end_time_ms
                   AND start_time_ms = @start_time_ms
                   AND NOT EXISTS (
                       SELECT 1 FROM metric_points same
                       WHERE same.otel_source = @otel_source
                         AND same.series_key = @series_key
                         AND same.end_time_ms = @end_time_ms);
                """;
            AddParameter(mergeCommand, "@otel_source", SqliteType.Text);
            AddParameter(mergeCommand, "@series_key", SqliteType.Text);
            AddParameter(mergeCommand, "@last_end_time_ms", SqliteType.Integer);
            AddParameter(mergeCommand, "@start_time_ms", SqliteType.Integer);
            AddParameter(mergeCommand, "@window_session_id", SqliteType.Text);
            AddParameter(mergeCommand, "@attributed_session_id", SqliteType.Text);
            AddParameter(mergeCommand, "@attribution_method", SqliteType.Text);
            AddParameter(mergeCommand, "@attribution_confidence", SqliteType.Real);
            AddParameter(mergeCommand, "@end_time_ms", SqliteType.Integer);
            AddParameter(mergeCommand, "@value", SqliteType.Real);
            AddParameter(mergeCommand, "@count", SqliteType.Integer);
            AddParameter(mergeCommand, "@sum", SqliteType.Real);
            AddParameter(mergeCommand, "@min", SqliteType.Real);
            AddParameter(mergeCommand, "@max", SqliteType.Real);
            AddParameter(mergeCommand, "@bucket_counts", SqliteType.Text);
            AddParameter(mergeCommand, "@explicit_bounds", SqliteType.Text);
            AddParameter(mergeCommand, "@received_at_ms", SqliteType.Integer);

            var inserted = 0;
            foreach (var point in points)
            {
                var seriesKey = $"{point.Source}\u001f{point.SeriesKey}";
                lastEndBySeries.TryGetValue(seriesKey, out var lastEnd);
                if (lastEnd > 0 && point.EndTimeMs <= lastEnd)
                {
                    // An older or replayed sample holds nothing the stored row lacks.
                    continue;
                }

                var attribution = ResolveMetricAttribution(
                    connection,
                    transaction,
                    point,
                    callSessions,
                    windowCandidates);

                if (lastEnd > 0 && point.EndTimeMs - lastEnd < sampleIntervalMs)
                {
                    mergeCommand.Parameters["@otel_source"].Value = point.Source;
                    mergeCommand.Parameters["@series_key"].Value = point.SeriesKey;
                    mergeCommand.Parameters["@last_end_time_ms"].Value = lastEnd;
                    BindMetricSample(mergeCommand, point, attribution);
                    if (mergeCommand.ExecuteNonQuery() > 0)
                    {
                        lastEndBySeries[seriesKey] = point.EndTimeMs;
                        inserted++;
                        continue;
                    }

                    // The aggregation epoch changed (counter restart) or that timestamp is
                    // already taken: keep this sample as its own row instead.
                }

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
                BindMetricSample(command, point, attribution);
                var changes = command.ExecuteNonQuery();
                if (changes > 0)
                {
                    lastEndBySeries[seriesKey] = point.EndTimeMs;
                }

                inserted += changes;
            }

            transaction.Commit();
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Binds the columns shared by inserting a sample and merging a newer cumulative sample
    /// of the same series into the stored row.
    /// </summary>
    private static void BindMetricSample(
        SqliteCommand command,
        OtlpMetricPointRecord point,
        (
            string? SessionId,
            string? Method,
            double? Confidence) attribution)
    {
        command.Parameters["@window_session_id"].Value =
            (object?)point.WindowSessionId ?? DBNull.Value;
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

    /// <summary>
    /// Start and end of the session's turns. Agent Host opens exactly one root
    /// <c>invoke_agent</c> span per user message and it only arrives once the turn
    /// finished, so the end time is also the only way to tell that newer activity
    /// belongs to a turn that is still running. A subagent's invoke_agent hangs
    /// below a stored tool span, so requiring the parent to be unknown keeps the
    /// list to real turns. The span id is returned because it is what a tool's
    /// parent points at, so the turn builder can recognise a not-yet-exported turn.
    /// </summary>
    public IReadOnlyList<(long StartTimeMs, long? EndTimeMs, string SpanId)> GetSessionTurns(
        string sessionId,
        string? source = null,
        long fromMs = 0,
        long toMs = long.MaxValue)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT start_time_ms, end_time_ms, span_id
            FROM session_events e
            WHERE e.session_id = @session_id
              AND e.operation = 'invoke_agent'
              AND e.start_time_ms >= @from
              AND e.start_time_ms < @to{sourceFilter}
              AND NOT EXISTS (
                  SELECT 1 FROM session_events p
                  WHERE p.session_id = e.session_id
                    AND p.span_id = e.parent_span_id)
            ORDER BY e.start_time_ms;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new List<(long StartTimeMs, long? EndTimeMs, string SpanId)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Agent runs of a session. The stored <c>is_root</c> flag cannot be trusted for
    /// the Agent Host pipeline: a turn's root <c>invoke_agent</c> does have a parent
    /// (the host session span, which is never ingested), so every turn used to be
    /// reported as a subagent. Root therefore means "the parent span is not stored",
    /// the same rule the turn list uses. The run name falls back to the span name
    /// because Agent Host only sets <c>gen_ai.agent.name</c> for some runs, the model
    /// falls back to the dominant chat model of the same trace (the
    /// <c>invoke_agent</c> span carries none), and the turn count falls back to the
    /// chat spans that started inside the run.
    /// </summary>
    public IReadOnlyList<AgentRunRow> GetAgentRuns(
        string sessionId,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : " AND a.otel_source = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.trace_id, a.span_id, a.parent_span_id,
                   CASE WHEN a.is_root <> 0
                             OR a.parent_span_id IS NULL
                             OR NOT EXISTS (
                                 SELECT 1 FROM session_events p
                                 WHERE p.session_id = a.session_id
                                   AND p.span_id = a.parent_span_id)
                        THEN 1 ELSE 0 END AS is_root,
                   a.session_id,
                   a.agent_name, a.agent_type, a.provider_name,
                   COALESCE(
                       a.request_model,
                       (SELECT s.model FROM spans s
                        WHERE s.trace_id = a.trace_id AND s.model IS NOT NULL
                          AND s.start_time_ms >= a.start_time_ms
                          AND (a.end_time_ms IS NULL
                               OR s.start_time_ms < a.end_time_ms)
                        GROUP BY s.model
                        ORDER BY COUNT(*) DESC, s.model
                        LIMIT 1)) AS request_model,
                   a.response_model, a.otel_source, a.git_repository, a.git_branch,
                   a.git_commit, a.github_org,
                   COALESCE(
                       a.turn_count,
                       (SELECT COUNT(*) FROM spans s
                        WHERE s.trace_id = a.trace_id
                          AND s.model IS NOT NULL
                          AND s.start_time_ms >= a.start_time_ms
                          AND (a.end_time_ms IS NULL
                               OR s.start_time_ms < a.end_time_ms))) AS turn_count,
                   a.input_tokens,
                   a.output_tokens, a.cache_read_tokens, a.cache_creation_tokens,
                   a.reasoning_tokens, a.start_time_ms, a.end_time_ms, a.is_error,
                   a.error_type,
                   (SELECT e.name FROM session_events e
                    WHERE e.session_id = a.session_id
                      AND e.span_id = a.span_id
                      AND e.operation = 'invoke_agent'
                    LIMIT 1) AS span_name
            FROM agent_runs a
            WHERE a.session_id = @session_id{sourceFilter}
            ORDER BY a.start_time_ms, a.trace_id, a.span_id;
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
            var agentName = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (string.IsNullOrWhiteSpace(agentName))
            {
                agentName = Otlp.OtlpTraceParser.AgentNameFromSpanName(
                    reader.IsDBNull(25) ? null : reader.GetString(25));
            }

            rows.Add(new AgentRunRow
            {
                TraceId = reader.GetString(0),
                SpanId = reader.GetString(1),
                ParentSpanId = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsRoot = reader.GetInt64(3) != 0,
                SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                AgentName = agentName,
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

    /// <summary>
    /// Aggregates hook runs per hook inside the given time window. Like tool
    /// aggregates, the window has to be applied before grouping; filtering the
    /// completed rows by last-seen time would count runs outside the range.
    /// </summary>
    public IReadOnlyList<HookSummaryRow> GetHookSummaries(
        string sessionId,
        string? source = null,
        long fromMs = 0,
        long toMs = long.MaxValue)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(hook_type, ''), COALESCE(hook_decision, ''),
                   COALESCE(hook_result_kind, ''), {SourceSql} AS source,
                   start_time_ms, end_time_ms, is_error, hook_tool_names,
                   hook_duration_ms
            FROM session_events
            WHERE session_id = @session_id
              AND operation = 'execute_hook'
              AND start_time_ms >= @from
              AND start_time_ms < @to{sourceFilter}
            ORDER BY start_time_ms;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
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
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8));
        }

        return rows.Values
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.HookType, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Build())
            .ToList();
    }

    /// <summary>
    /// Returns one block of the session topology in chronological (FIFO) order:
    /// the first block holds the oldest traces, and the cursor walks forward so
    /// callers append newer blocks. Blocks are anchored on whole traces, so parent
    /// links stay intact.
    /// </summary>
    /// <summary>
    /// Returns the session topology as one list sorted by start time (FIFO). Traces
    /// may interleave; depth comes from parent links, not from adjacency.
    /// </summary>
    public IReadOnlyList<TraceNodeRow> GetTraceNodes(
        string sessionId,
        string? source = null,
        int limit = 5_000)
    {
        var maxNodes = Math.Clamp(limit, 1, 5_000);
        var nodes = BuildTraceNodes(sessionId, source);
        if (nodes.Count > maxNodes)
        {
            // A pathological session would otherwise serialize tens of megabytes;
            // keep the newest window instead of dropping the tail.
            nodes.RemoveRange(0, nodes.Count - maxNodes);
            ApplyDepths(nodes);
        }

        return nodes;
    }

    /// <summary>
    /// Returns one slice of the FIFO node stream. Slicing happens after the merge and
    /// the depth pass, so block boundaries never change parent links or ordering;
    /// ancestors that live in an earlier block are repeated as context rows so the
    /// indentation never breaks.
    /// </summary>
    public TracePage GetTracePage(
        string sessionId,
        string? source = null,
        string? cursor = null,
        int budget = 400,
        long fromMs = 0,
        long toMs = long.MaxValue)
    {
        var pageSize = Math.Clamp(budget, 1, 2_000);
        var all = BuildTraceNodes(sessionId, source);
        if (all.Count == 0)
        {
            return new TracePage(Array.Empty<TraceNodeRow>(), false, null, 0);
        }

        // The topology follows the selected range like every other session view.
        // Ancestors that started outside the window stay in as context rows so the
        // indentation keeps working; they carry no tokens and are not counted.
        var nodes = WindowTraceNodes(all, fromMs, toMs, out var total);
        var startIndex = 0;
        if (TryParseCursor(cursor, out var cursorMs, out var cursorId))
        {
            while (startIndex < nodes.Count &&
                   CompareToCursor(nodes[startIndex], cursorMs, cursorId) <= 0)
            {
                startIndex++;
            }
        }

        if (startIndex >= nodes.Count)
        {
            return new TracePage(
                Array.Empty<TraceNodeRow>(),
                false,
                null,
                total);
        }

        var take = Math.Min(pageSize, nodes.Count - startIndex);
        var slice = new List<TraceNodeRow>(take + 16);
        slice.AddRange(nodes.GetRange(startIndex, take));
        PrependContextAncestors(nodes, startIndex, slice);
        ApplyDepths(slice);

        var hasMore = startIndex + take < nodes.Count;
        var last = slice[^1];
        return new TracePage(
            slice,
            hasMore,
            hasMore ? FormatCursor(last) : null,
            total);
    }

    /// <summary>
    /// Keeps the rows that started inside the window plus the ancestors needed to
    /// indent them. Ancestors outside the window become context rows: they carry no
    /// tokens, errors or duration so range totals never include them.
    /// </summary>
    private static List<TraceNodeRow> WindowTraceNodes(
        List<TraceNodeRow> all,
        long fromMs,
        long toMs,
        out int total)
    {
        if (fromMs <= 0 && toMs == long.MaxValue)
        {
            total = all.Count;
            return all;
        }

        var indexById = new Dictionary<string, int>(
            all.Count,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < all.Count; index++)
        {
            indexById[all[index].Id] = index;
        }

        var visible = new HashSet<int>();
        for (var index = 0; index < all.Count; index++)
        {
            if (all[index].StartTimeMs >= fromMs && all[index].StartTimeMs < toMs)
            {
                visible.Add(index);
            }
        }

        if (visible.Count == 0)
        {
            total = 0;
            return new List<TraceNodeRow>();
        }

        var keep = new HashSet<int>(visible);
        var contextRows = 0;
        foreach (var index in visible)
        {
            var parentId = all[index].ParentId;
            var guard = 0;
            while (parentId is not null &&
                   guard++ < 32 &&
                   indexById.TryGetValue(parentId, out var parentIndex) &&
                   parentIndex < index)
            {
                if (!keep.Contains(parentIndex))
                {
                    if (contextRows >= MaxContextAncestors)
                    {
                        break;
                    }

                    keep.Add(parentIndex);
                    contextRows++;
                }

                parentId = all[parentIndex].ParentId;
            }

            if (contextRows >= MaxContextAncestors)
            {
                break;
            }
        }

        var nodes = new List<TraceNodeRow>(keep.Count);
        for (var index = 0; index < all.Count; index++)
        {
            if (!keep.Contains(index))
            {
                continue;
            }

            var node = all[index];
            nodes.Add(visible.Contains(index)
                ? node
                : node with
                {
                    Kind = "context",
                    IsContext = true,
                    DurationMs = null,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    IsError = false,
                    ErrorType = null,
                });
        }

        total = visible.Count;
        return nodes;
    }

    private List<TraceNodeRow> BuildTraceNodes(string sessionId, string? source)
    {
        // Combined view merges both pipelines; a single source skips the merge.
        var mergeAcrossSources = string.IsNullOrWhiteSpace(source);
        var sourceFilter = mergeAcrossSources
            ? string.Empty
            : $" AND {SourceSql} = @source";

        using var connection = OpenConnection();
        var spans = ReadTraceSpans(connection, sessionId, source, sourceFilter);
        var events = ReadTraceEvents(connection, sessionId, source, sourceFilter);

        var dropped = new HashSet<SpanKey>();
        var remap = new Dictionary<SpanKey, SpanKey>();
        if (mergeAcrossSources && (spans.Count > 1 || events.Count > 0))
        {
            ComputeTraceMerge(spans, dropped, remap);
        }

        var nodes = new List<TraceNodeRow>(spans.Count + events.Count);
        foreach (var span in spans)
        {
            if (dropped.Contains(new SpanKey(span.TraceId, span.SpanId)))
            {
                continue;
            }

            nodes.Add(new TraceNodeRow
            {
                Id = SpanId(span.TraceId, span.SpanId),
                ParentId = ResolveParentId(span.TraceId, span.ParentSpanId, remap),
                Kind = "chat",
                Operation = "chat",
                Name = span.Name,
                // The span name ("chat DeepSeek/deepseek-flash") carries the raw
                // provider prefix and repeats the operation; the normalized model
                // column is what the rest of the UI shows.
                Model = span.Model,
                Source = span.Source,
                StartTimeMs = span.StartTimeMs,
                DurationMs = span.EndTimeMs is long spanEnd
                    ? Math.Max(0, spanEnd - span.StartTimeMs)
                    : null,
                ErrorType = span.ErrorType,
                IsError = span.ErrorType is not null,
                InputTokens = span.InputTokens,
                OutputTokens = span.OutputTokens,
                CacheReadTokens = span.CacheReadTokens,
            });
        }

        foreach (var item in events)
        {
            if (dropped.Contains(new SpanKey(item.TraceId, item.SpanId)))
            {
                continue;
            }

            nodes.Add(new TraceNodeRow
            {
                Id = SpanId(item.TraceId, item.SpanId),
                ParentId = ResolveParentId(item.TraceId, item.ParentSpanId, remap),
                Kind = item.Operation == "permission" ? "permission" : "event",
                Operation = item.Operation,
                // A permission span has no tool-name attribute of its own beyond
                // permission.tool_name, and its kind/result explain why the tool
                // waited; fold both into the label because the tree renders one
                // label per row.
                Name = item.Operation == "permission"
                    ? PermissionLabel(item)
                    : item.Name,
                // Permission labels already carry the tool name, so keeping Tool
                // would repeat it through TraceNodeRow.Label. The field is still
                // filled so AttachApprovalTools can tell whether the span named the
                // tool itself.
                Tool = item.Tool,
                Source = item.Source,
                StartTimeMs = item.StartTimeMs,
                DurationMs = Math.Max(0, item.EndTimeMs - item.StartTimeMs),
                ErrorType = item.ErrorType,
                IsError = item.IsError,
            });
        }

        if (nodes.Count == 0)
        {
            return nodes;
        }

        NestedToolsUnderChat(nodes);
        AttachApprovalTools(nodes);
        nodes.Sort(CompareNodes);
        ApplyDepths(nodes);
        return nodes;
    }

    /// <summary>
    /// Agent Host reports every <c>execute_tool</c> under the turn's
    /// <c>invoke_agent</c>, next to the model calls instead of under them, so the
    /// tree renders a tool as a sibling of the chat span that requested it
    /// (invoke_agent → chat, invoke_agent → execute_tool → permission).
    /// <para>
    /// The span's own parent cannot be used to fix this: the chat spans of a turn get
    /// dropped or remapped when the two pipelines' duplicates merge, so a tool's
    /// trace often no longer contains the chat that caused it (measured: one trace
    /// holds 85 tools and 0 chats). The topology is a single session sorted by start
    /// time, so the model call that requested a tool is the nearest preceding chat
    /// span of the same turn; that is what the tool is re-hung under.
    /// </para>
    /// <para>
    /// The merge can drop the tool's parent as well (measured: 77 tools point at an
    /// <c>invoke_agent</c> row that no longer exists), which used to leave them at
    /// depth 0, floating next to the model calls. An unreadable parent is treated
    /// like <c>invoke_agent</c>: only a parent that is present and is something else
    /// (a nested tool, an agent) keeps the tool where the host put it.
    /// </para>
    /// </summary>
    private static void NestedToolsUnderChat(List<TraceNodeRow> nodes)
    {
        var operations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < nodes.Count; index++)
        {
            operations[nodes[index].Id] = nodes[index].Operation;
            positions[nodes[index].Id] = index;
        }

        var ordered = nodes.OrderBy(node => node.StartTimeMs).ToList();
        var turnStart = long.MinValue;
        TraceNodeRow? lastChat = null;
        foreach (var node in ordered)
        {
            if (node.Kind != "permission" && node.Operation == "invoke_agent")
            {
                turnStart = node.StartTimeMs;
                lastChat = null;
                continue;
            }

            if (node.Kind == "chat")
            {
                lastChat = node;
                continue;
            }

            if (node.Kind != "event" ||
                node.Operation != "execute_tool" ||
                node.ParentId is null)
            {
                continue;
            }

            var parentKnown =
                operations.TryGetValue(node.ParentId, out var parentOperation);
            if (parentKnown && parentOperation != "invoke_agent")
            {
                continue;
            }

            if (lastChat is null ||
                lastChat.StartTimeMs < turnStart ||
                lastChat.StartTimeMs > node.StartTimeMs)
            {
                continue;
            }

            // Positions are looked up by id: two rows can compare equal (a context
            // row repeats a real row), and IndexOf uses value equality.
            nodes[positions[node.Id]] = node with { ParentId = lastChat.Id };
        }
    }

    /// <summary>
    /// A real permission span carries <c>permission.kind/result</c> but usually no
    /// <c>permission.tool_name</c> (measured: the column is NULL for every row), so
    /// the label alone says "shell · approved" without telling which tool was
    /// approved. The approval hangs below its <c>execute_tool</c> span, so the parent
    /// supplies the tool name. Runs before sorting: the parent can start earlier and
    /// still be stored in any order.
    /// </summary>
    private static void AttachApprovalTools(List<TraceNodeRow> nodes)
    {
        Dictionary<string, TraceNodeRow>? byId = null;
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.Kind != "permission" ||
                node.ParentId is null ||
                !string.IsNullOrWhiteSpace(node.Tool))
            {
                continue;
            }

            byId ??= nodes
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            if (!byId.TryGetValue(node.ParentId, out var parent))
            {
                continue;
            }

            var tool = parent.Tool;
            if (string.IsNullOrWhiteSpace(tool) && parent.Kind == "event")
            {
                // execute_tool rows keep the tool in Tool; fall back to the parsed
                // name ("execute_tool powershell") when only that exists.
                var name = parent.Name ?? string.Empty;
                tool = name.StartsWith("execute_tool ", StringComparison.Ordinal)
                    ? name["execute_tool ".Length..].Trim()
                    : null;
            }

            if (string.IsNullOrWhiteSpace(tool))
            {
                continue;
            }

            var display = Otlp.ToolNameResolver.Display(tool);
            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            nodes[index] = node with
            {
                Name = $"{node.Name} · {display}",
                // Remembered so a later pass cannot append a second tool.
                Tool = tool,
            };
        }
    }

    private const int MaxContextAncestors = 64;

    /// <summary>
    /// Describes an approval span, e.g. <c>shell · approved · powershell</c>. The
    /// runtime only emits <c>approved</c> and <c>denied-interactively-by-user</c>,
    /// so there is no explicit "auto" marker: a missing span means the tool needed
    /// no approval, a short span means the policy let it through immediately, and a
    /// long span is time spent waiting for a person.
    /// </summary>
    private static string PermissionLabel(TraceEvent item)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(item.PermissionKind))
        {
            parts.Add(item.PermissionKind);
        }

        if (!string.IsNullOrWhiteSpace(item.PermissionResult))
        {
            parts.Add(item.PermissionResult);
        }

        if (!string.IsNullOrWhiteSpace(item.Tool))
        {
            parts.Add(Otlp.ToolNameResolver.Display(item.Tool));
        }

        return parts.Count == 0 ? "permission" : string.Join(" · ", parts);
    }

    // A block can start in the middle of a trace; repeating the missing ancestors as
    // context rows keeps the indentation readable without counting them twice.
    private static void PrependContextAncestors(
        List<TraceNodeRow> all,
        int startIndex,
        List<TraceNodeRow> slice)
    {
        if (startIndex == 0)
        {
            return;
        }

        var indexById = new Dictionary<string, int>(
            all.Count,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < all.Count; index++)
        {
            indexById[all[index].Id] = index;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in slice)
        {
            present.Add(node.Id);
        }

        var missing = new SortedDictionary<int, TraceNodeRow>();
        foreach (var node in slice.ToArray())
        {
            var parentId = node.ParentId;
            var guard = 0;
            while (parentId is not null &&
                   guard++ < 32 &&
                   !present.Contains(parentId) &&
                   indexById.TryGetValue(parentId, out var parentIndex) &&
                   parentIndex < startIndex)
            {
                var parent = all[parentIndex];
                missing[parentIndex] = parent with
                {
                    Kind = "context",
                    IsContext = true,
                    DurationMs = null,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    IsError = false,
                    ErrorType = null,
                };
                if (missing.Count >= MaxContextAncestors)
                {
                    break;
                }

                parentId = parent.ParentId;
            }

            if (missing.Count >= MaxContextAncestors)
            {
                break;
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        slice.InsertRange(0, missing.Values);
    }

    private static bool TryParseCursor(
        string? cursor,
        out long startMs,
        out string nodeId)
    {
        startMs = 0;
        nodeId = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf(':');
        if (separator <= 0 ||
            !long.TryParse(
                cursor.AsSpan(0, separator),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out startMs))
        {
            return false;
        }

        nodeId = cursor[(separator + 1)..];
        return nodeId.Length > 0;
    }

    private static string FormatCursor(TraceNodeRow node) =>
        $"{node.StartTimeMs}:{node.Id}";

    private static int CompareToCursor(
        TraceNodeRow node,
        long startMs,
        string nodeId)
    {
        var byTime = node.StartTimeMs.CompareTo(startMs);
        return byTime != 0
            ? byTime
            : string.CompareOrdinal(node.Id, nodeId);
    }

    private static int CompareNodes(TraceNodeRow left, TraceNodeRow right)
    {
        var byTime = left.StartTimeMs.CompareTo(right.StartTimeMs);
        return byTime != 0
            ? byTime
            : string.CompareOrdinal(left.Id, right.Id);
    }

    private static void ApplyDepths(List<TraceNodeRow> nodes)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var byId = new Dictionary<string, TraceNodeRow>(
            nodes.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            byId[node.Id] = node;
        }

        var depths = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            nodes[index] = nodes[index] with
            {
                Depth = ResolveDepth(nodes[index].Id, byId, depths, 0),
            };
        }
    }

    private static string? ResolveParentId(
        string traceId,
        string? parentSpanId,
        IReadOnlyDictionary<SpanKey, SpanKey> remap)
    {
        if (parentSpanId is null)
        {
            return null;
        }

        return remap.TryGetValue(
            new SpanKey(traceId, parentSpanId),
            out var mapped)
            ? SpanId(mapped.TraceId, mapped.SpanId)
            : SpanId(traceId, parentSpanId);
    }

    private static string SpanId(string traceId, string spanId) =>
        string.Concat(traceId, ":", spanId);

    // Memoized so a deep chain is walked once instead of once per row.
    private static int ResolveDepth(
        string id,
        IReadOnlyDictionary<string, TraceNodeRow> byId,
        Dictionary<string, int> cache,
        int guard)
    {
        if (cache.TryGetValue(id, out var known))
        {
            return known;
        }

        var depth = 0;
        if (guard < 32 &&
            byId.TryGetValue(id, out var node) &&
            node.ParentId is not null &&
            byId.ContainsKey(node.ParentId))
        {
            depth = 1 + ResolveDepth(node.ParentId, byId, cache, guard + 1);
        }

        cache[id] = depth;
        return depth;
    }

    private readonly record struct SpanKey(string TraceId, string SpanId);

    private readonly record struct TraceSpan(
        string TraceId,
        string SpanId,
        string? ParentSpanId,
        string? Source,
        string? Name,
        string? Model,
        string? ErrorType,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long StartTimeMs,
        long? EndTimeMs,
        bool IsMain);

    private readonly record struct TraceEvent(
        string TraceId,
        string SpanId,
        string? ParentSpanId,
        string Operation,
        string? Name,
        string? Tool,
        string? Source,
        long StartTimeMs,
        long EndTimeMs,
        string? ErrorType,
        bool IsError,
        string? PermissionKind,
        string? PermissionResult);

    private static List<TraceSpan> ReadTraceSpans(
        SqliteConnection connection,
        string sessionId,
        string? source,
        string sourceFilter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT trace_id, span_id, parent_span_id, {SourceSql} AS source,
                   name, model, error_type, input_tokens, output_tokens,
                   cache_read_tokens, start_time_ms, end_time_ms, is_main
            FROM spans
            WHERE session_id = @session_id
              AND COALESCE(otel_source, '') <> 'combined'{sourceFilter};
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var spans = new List<TraceSpan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var startMs = reader.GetInt64(10);
            spans.Add(new TraceSpan(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                startMs,
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.GetInt64(12) != 0));
        }

        return spans;
    }

    private static List<TraceEvent> ReadTraceEvents(
        SqliteConnection connection,
        string sessionId,
        string? source,
        string sourceFilter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT trace_id, span_id, parent_span_id, operation, name, tool_name,
                   {SourceSql} AS source, start_time_ms, end_time_ms, error_type,
                   is_error, permission_kind, permission_result
            FROM session_events
            WHERE session_id = @session_id{sourceFilter}
            ORDER BY start_time_ms, trace_id, span_id;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var events = new List<TraceEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var startMs = reader.GetInt64(7);
            events.Add(new TraceEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                startMs,
                reader.IsDBNull(8) ? startMs : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10) != 0,
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return events;
    }

    private static void ComputeTraceMerge(
        IReadOnlyList<TraceSpan> spans,
        HashSet<SpanKey> dropped,
        Dictionary<SpanKey, SpanKey> remap)
    {
        // Bucketing by the documented merge key is equivalent to the previous
        // scan-everything loop (it required an equal key anyway) but avoids the
        // O(n^2) sweep over every chat node in the session.
        var buckets = new Dictionary<TraceMergeKey, List<List<TraceSpan>>>();
        foreach (var span in spans)
        {
            var key = new TraceMergeKey(
                span.Model,
                span.InputTokens,
                span.OutputTokens,
                span.IsMain);
            if (!buckets.TryGetValue(key, out var groups))
            {
                groups = new List<List<TraceSpan>>();
                buckets[key] = groups;
            }

            List<TraceSpan>? group = null;
            foreach (var candidate in groups)
            {
                var overlaps = false;
                var sameSource = false;
                foreach (var item in candidate)
                {
                    if (SpanIntervalsOverlap(item, span))
                    {
                        overlaps = true;
                    }

                    if (string.Equals(
                            item.Source,
                            span.Source,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        sameSource = true;
                    }

                    if (overlaps && sameSource)
                    {
                        break;
                    }
                }

                if (overlaps && !sameSource)
                {
                    group = candidate;
                    break;
                }
            }

            if (group is null)
            {
                groups.Add(new List<TraceSpan> { span });
            }
            else
            {
                group.Add(span);
            }
        }

        foreach (var group in buckets.Values)
        {
            foreach (var candidate in group)
            {
                if (candidate.Count < 2)
                {
                    continue;
                }

                // Keep the extension-host row as the primary node, matching the
                // previous behaviour, so token counts stay on the richer source.
                TraceSpan? primary = null;
                foreach (var item in candidate)
                {
                    if (primary is null ||
                        (string.Equals(
                             item.Source,
                             "copilot-chat",
                             StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(
                             primary.Value.Source,
                             "copilot-chat",
                             StringComparison.OrdinalIgnoreCase)) ||
                        (string.Equals(
                             item.Source,
                             "copilot-chat",
                             StringComparison.OrdinalIgnoreCase) ==
                         string.Equals(
                             primary.Value.Source,
                             "copilot-chat",
                             StringComparison.OrdinalIgnoreCase) &&
                         item.StartTimeMs < primary.Value.StartTimeMs))
                    {
                        primary = item;
                    }
                }

                if (primary is null)
                {
                    continue;
                }

                foreach (var item in candidate)
                {
                    if (string.Equals(item.SpanId, primary.Value.SpanId, StringComparison.Ordinal) &&
                        string.Equals(item.TraceId, primary.Value.TraceId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var key = new SpanKey(item.TraceId, item.SpanId);
                    dropped.Add(key);
                    remap[key] = new SpanKey(
                        primary.Value.TraceId,
                        primary.Value.SpanId);
                }
            }
        }
    }

    private static bool SpanIntervalsOverlap(TraceSpan left, TraceSpan right)
    {
        if (left.EndTimeMs is long leftEnd && right.EndTimeMs is long rightEnd)
        {
            return left.StartTimeMs <= rightEnd && right.StartTimeMs <= leftEnd;
        }

        // Rows without an end time (imported or still open) cannot prove overlap,
        // so keep the previous two-second tolerance.
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
            string? toolNames,
            double? durationMs = null)
        {
            Count++;
            LastSeenMs = Math.Max(LastSeenMs, startMs);
            if (durationMs is > 0)
            {
                // The stored duration is authoritative: hooks routinely take less
                // than a millisecond (measured 0.0004 s), so end - start rounds to
                // zero and the row would read "-" despite being measured.
                _totalDurationMs += durationMs.Value;
                _durationSamples++;
            }
            else if (endMs is > 0 && endMs.Value > startMs)
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
