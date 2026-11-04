using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Storage;

public sealed partial class SpanStore
{
    private volatile bool _combinedDirty = true;
    private bool _combinedAllDirty = true;
    private readonly HashSet<string> _combinedDirtySessions =
        new(StringComparer.Ordinal);
    private long _combinedDirtyFromMs = long.MaxValue;
    private long _combinedDirtyToMs = long.MinValue;

    // Auxiliary calls carry no session id, so they cannot be rebuilt per session.
    // Rebuilding the batch window is enough: a merge group is built from rows whose
    // intervals overlap, and any row overlapping an in-window row overlaps the window.
    private void MarkCombinedWindow(long startMs, long endMs)
    {
        if (startMs < _combinedDirtyFromMs)
        {
            _combinedDirtyFromMs = startMs;
        }

        if (endMs > _combinedDirtyToMs)
        {
            _combinedDirtyToMs = endMs;
        }

        _combinedDirty = true;
    }

    public void EnsureCombinedSpans()
    {
        if (!_combinedDirty)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            if (!_combinedDirty)
            {
                return;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (_combinedAllDirty)
            {
                var raw = ReadRawSpans(connection, transaction, sessionId: null);
                DeleteCombinedSpans(connection, transaction, sessionId: null);
                InsertCombinedSpans(
                    connection,
                    transaction,
                    MergeRawSpans(raw));
            }
            else
            {
                foreach (var sessionId in _combinedDirtySessions.ToList())
                {
                    var raw = ReadRawSpans(connection, transaction, sessionId);
                    DeleteCombinedSpans(connection, transaction, sessionId);
                    InsertCombinedSpans(
                        connection,
                        transaction,
                        MergeRawSpans(raw));
                }
            }

            if (!_combinedAllDirty && _combinedDirtyToMs >= _combinedDirtyFromMs)
            {
                var fromMs = _combinedDirtyFromMs;
                var toMs = _combinedDirtyToMs;
                var rebuilt = false;
                for (var pass = 0; pass < CombinedWindowMaxPasses && !rebuilt; pass++)
                {
                    var raw = ReadRawSpans(
                        connection,
                        transaction,
                        sessionId: null,
                        fromMs,
                        toMs);
                    var (expandedFrom, expandedTo) = ExtendWindow(raw, fromMs, toMs);
                    if (expandedTo - expandedFrom > CombinedWindowMaxSpanMs)
                    {
                        break;
                    }

                    if (expandedFrom == fromMs && expandedTo == toMs)
                    {
                        DeleteCombinedSpans(
                            connection,
                            transaction,
                            sessionId: null,
                            fromMs,
                            toMs);
                        InsertCombinedSpans(
                            connection,
                            transaction,
                            MergeRawSpans(raw));
                        rebuilt = true;
                        break;
                    }

                    // Merge groups chain through overlapping intervals, so the
                    // window has to grow until it contains every affected group.
                    fromMs = expandedFrom;
                    toMs = expandedTo;
                }

                if (!rebuilt)
                {
                    var all = ReadRawSpans(connection, transaction, sessionId: null);
                    DeleteCombinedSpans(connection, transaction, sessionId: null);
                    InsertCombinedSpans(connection, transaction, MergeRawSpans(all));
                }
            }

            transaction.Commit();
            _combinedAllDirty = false;
            _combinedDirtySessions.Clear();
            _combinedDirtyFromMs = long.MaxValue;
            _combinedDirtyToMs = long.MinValue;
            _combinedDirty = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private const int CombinedWindowMaxPasses = 4;
    private const long CombinedWindowMaxSpanMs = 12 * 60 * 60 * 1000;

    private static (long FromMs, long ToMs) ExtendWindow(
        IReadOnlyList<CombinedSpan> rows,
        long fromMs,
        long toMs)
    {
        var lowest = fromMs;
        var highest = toMs;
        foreach (var row in rows)
        {
            if (row.StartTimeMs < lowest)
            {
                lowest = row.StartTimeMs;
            }

            var endMs = row.EndTimeMs ?? row.StartTimeMs;
            if (endMs > highest)
            {
                highest = endMs;
            }
        }

        return (lowest, highest);
    }

    private static void DeleteCombinedSpans(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? sessionId,
        long fromMs = 0,
        long toMs = 0)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        if (toMs > 0)
        {
            delete.CommandText = """
                DELETE FROM spans
                WHERE otel_source = 'combined'
                  AND (
                        (start_time_ms BETWEEN @window_from AND @window_to)
                        OR (end_time_ms >= @window_from AND start_time_ms < @window_from)
                      );
                """;
            delete.Parameters.AddWithValue("@window_from", fromMs);
            delete.Parameters.AddWithValue("@window_to", toMs);
        }
        else if (sessionId is null)
        {
            delete.CommandText = "DELETE FROM spans WHERE otel_source = 'combined';";
        }
        else
        {
            delete.CommandText = """
                DELETE FROM spans
                WHERE otel_source = 'combined' AND session_id = @session;
                """;
            delete.Parameters.AddWithValue("@session", sessionId);
        }

        delete.ExecuteNonQuery();
    }

    private static void InsertCombinedSpans(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CombinedSpan> merged)
    {
        if (merged.Count == 0)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO spans (
                trace_id, span_id, parent_span_id, name, start_time_ms,
                end_time_ms, service_name, service_namespace, scope_name,
                otel_source, model, session_id, is_main, input_tokens,
                output_tokens, cache_read_tokens, cache_creation_tokens,
                reasoning_tokens, ttft_ms, nano_aiu, cache_cost_cny,
                uncached_cost_cny, output_cost_cny, reasoning_cost_cny,
                hit_rate, cost_cny, pricing_period, error_type,
                provider_name, agent_name, subagent_name, finish_reasons,
                received_at_ms
            ) VALUES (
                @trace_id, @span_id, NULL, @name, @start_time_ms,
                @end_time_ms, @service_name, NULL, NULL, @otel_source,
                @model, @session_id, @is_main, @input_tokens,
                @output_tokens, @cache_read_tokens, @cache_creation_tokens,
                @reasoning_tokens, @ttft_ms, @nano_aiu, @cache_cost_cny,
                @uncached_cost_cny, @output_cost_cny, @reasoning_cost_cny,
                @hit_rate, @cost_cny, @pricing_period, @error_type,
                @provider_name, @agent_name, @subagent_name, @finish_reasons,
                @received_at_ms
            );
            """;
        AddParameter(insert, "@trace_id", SqliteType.Text);
        AddParameter(insert, "@span_id", SqliteType.Text);
        AddParameter(insert, "@name", SqliteType.Text);
        AddParameter(insert, "@start_time_ms", SqliteType.Integer);
        AddParameter(insert, "@end_time_ms", SqliteType.Integer);
        AddParameter(insert, "@service_name", SqliteType.Text);
        AddParameter(insert, "@otel_source", SqliteType.Text);
        AddParameter(insert, "@model", SqliteType.Text);
        AddParameter(insert, "@session_id", SqliteType.Text);
        AddParameter(insert, "@is_main", SqliteType.Integer);
        AddParameter(insert, "@input_tokens", SqliteType.Integer);
        AddParameter(insert, "@output_tokens", SqliteType.Integer);
        AddParameter(insert, "@cache_read_tokens", SqliteType.Integer);
        AddParameter(insert, "@cache_creation_tokens", SqliteType.Integer);
        AddParameter(insert, "@reasoning_tokens", SqliteType.Integer);
        AddParameter(insert, "@ttft_ms", SqliteType.Integer);
        AddParameter(insert, "@nano_aiu", SqliteType.Integer);
        AddParameter(insert, "@cache_cost_cny", SqliteType.Real);
        AddParameter(insert, "@uncached_cost_cny", SqliteType.Real);
        AddParameter(insert, "@output_cost_cny", SqliteType.Real);
        AddParameter(insert, "@reasoning_cost_cny", SqliteType.Real);
        AddParameter(insert, "@hit_rate", SqliteType.Real);
        AddParameter(insert, "@cost_cny", SqliteType.Real);
        AddParameter(insert, "@pricing_period", SqliteType.Text);
        AddParameter(insert, "@error_type", SqliteType.Text);
        AddParameter(insert, "@provider_name", SqliteType.Text);
        AddParameter(insert, "@agent_name", SqliteType.Text);
        AddParameter(insert, "@subagent_name", SqliteType.Text);
        AddParameter(insert, "@finish_reasons", SqliteType.Text);
        AddParameter(insert, "@received_at_ms", SqliteType.Integer);

        foreach (var row in merged)
        {
            insert.Parameters["@trace_id"].Value = "combined";
            insert.Parameters["@span_id"].Value = row.SpanId;
            insert.Parameters["@name"].Value =
                (object?)row.Name ?? DBNull.Value;
            insert.Parameters["@start_time_ms"].Value = row.StartTimeMs;
            insert.Parameters["@end_time_ms"].Value =
                row.EndTimeMs is null ? DBNull.Value : row.EndTimeMs.Value;
            insert.Parameters["@service_name"].Value = "combined";
            insert.Parameters["@otel_source"].Value = "combined";
            insert.Parameters["@model"].Value =
                (object?)row.Model ?? DBNull.Value;
            insert.Parameters["@session_id"].Value =
                (object?)row.SessionId ?? DBNull.Value;
            insert.Parameters["@is_main"].Value = row.IsMain ? 1 : 0;
            insert.Parameters["@input_tokens"].Value = row.InputTokens;
            insert.Parameters["@output_tokens"].Value = row.OutputTokens;
            insert.Parameters["@cache_read_tokens"].Value = row.CacheReadTokens;
            insert.Parameters["@cache_creation_tokens"].Value =
                row.CacheCreationTokens;
            insert.Parameters["@reasoning_tokens"].Value = row.ReasoningTokens;
            insert.Parameters["@ttft_ms"].Value =
                row.TtftMs is null ? DBNull.Value : row.TtftMs.Value;
            insert.Parameters["@nano_aiu"].Value =
                row.NanoAiu is null ? DBNull.Value : row.NanoAiu.Value;
            insert.Parameters["@cache_cost_cny"].Value =
                row.CacheCostCny is null ? DBNull.Value : row.CacheCostCny.Value;
            insert.Parameters["@uncached_cost_cny"].Value =
                row.UncachedCostCny is null
                    ? DBNull.Value
                    : row.UncachedCostCny.Value;
            insert.Parameters["@output_cost_cny"].Value =
                row.OutputCostCny is null
                    ? DBNull.Value
                    : row.OutputCostCny.Value;
            insert.Parameters["@reasoning_cost_cny"].Value =
                row.ReasoningCostCny is null
                    ? DBNull.Value
                    : row.ReasoningCostCny.Value;
            insert.Parameters["@hit_rate"].Value =
                row.InputTokens > 0
                    ? (double)row.CacheReadTokens / row.InputTokens
                    : DBNull.Value;
            insert.Parameters["@cost_cny"].Value =
                row.CostCny is null ? DBNull.Value : row.CostCny.Value;
            insert.Parameters["@pricing_period"].Value =
                (object?)row.PricingPeriod ?? DBNull.Value;
            insert.Parameters["@error_type"].Value =
                (object?)row.ErrorType ?? DBNull.Value;
            insert.Parameters["@provider_name"].Value =
                (object?)row.ProviderName ?? DBNull.Value;
            insert.Parameters["@agent_name"].Value =
                (object?)row.AgentName ?? DBNull.Value;
            insert.Parameters["@subagent_name"].Value =
                (object?)row.SubagentName ?? DBNull.Value;
            insert.Parameters["@finish_reasons"].Value =
                (object?)row.FinishReasons ?? DBNull.Value;
            insert.Parameters["@received_at_ms"].Value = row.ReceivedAtMs;
            insert.ExecuteNonQuery();
        }
    }

    private static List<CombinedSpan> ReadRawSpans(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? sessionId,
        long fromMs = 0,
        long toMs = 0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sessionFilter = sessionId is null
            ? string.Empty
            : " AND session_id = @session";
        var windowFilter = toMs > 0
            ? """
              AND (
                    (start_time_ms BETWEEN @window_from AND @window_to)
                    OR (end_time_ms >= @window_from AND start_time_ms < @window_from)
                  )
              """
            : string.Empty;
        command.CommandText = $"""
            SELECT trace_id, span_id, name, service_name, otel_source, model,
                   session_id, is_main, input_tokens, output_tokens,
                   cache_read_tokens, cache_creation_tokens, reasoning_tokens,
                   ttft_ms, nano_aiu, cache_cost_cny, uncached_cost_cny,
                   output_cost_cny, reasoning_cost_cny, cost_cny,
                   pricing_period, error_type, provider_name, agent_name,
                   subagent_name, finish_reasons, start_time_ms, end_time_ms,
                   received_at_ms
            FROM spans
            WHERE COALESCE(otel_source, '') <> 'combined'{sessionFilter}{windowFilter}
            ORDER BY start_time_ms;
            """;
        if (sessionId is not null)
        {
            command.Parameters.AddWithValue("@session", sessionId);
        }
        if (toMs > 0)
        {
            command.Parameters.AddWithValue("@window_from", fromMs);
            command.Parameters.AddWithValue("@window_to", toMs);
        }
        var rows = new List<CombinedSpan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CombinedSpan(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7) != 0,
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14),
                reader.IsDBNull(15) ? null : reader.GetDouble(15),
                reader.IsDBNull(16) ? null : reader.GetDouble(16),
                reader.IsDBNull(17) ? null : reader.GetDouble(17),
                reader.IsDBNull(18) ? null : reader.GetDouble(18),
                reader.IsDBNull(19) ? null : reader.GetDouble(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetString(25),
                reader.GetInt64(26),
                reader.IsDBNull(27) ? null : reader.GetInt64(27),
                reader.GetInt64(28)));
        }

        return rows;
    }

    private static List<CombinedSpan> MergeRawSpans(
        IReadOnlyList<CombinedSpan> raw)
    {
        var groups = new Dictionary<string, List<List<CombinedSpan>>>(
            StringComparer.Ordinal);
        foreach (var row in raw)
        {
            var key =
                $"{row.SessionId}\u001f{row.Model}\u001f{row.InputTokens}\u001f" +
                $"{row.OutputTokens}\u001f{row.IsMain}";
            if (!groups.TryGetValue(key, out var candidates))
            {
                candidates = new List<List<CombinedSpan>>();
                groups[key] = candidates;
            }

            var group = candidates.FirstOrDefault(candidate =>
                candidate.Any(item => IntervalsOverlap(item, row)) &&
                candidate.All(item => !string.Equals(
                    item.Source,
                    row.Source,
                    StringComparison.OrdinalIgnoreCase)));
            if (group is null)
            {
                candidates.Add(new List<CombinedSpan> { row });
            }
            else
            {
                group.Add(row);
            }
        }

        var merged = new List<CombinedSpan>(raw.Count);
        foreach (var entry in groups)
        {
            foreach (var group in entry.Value)
            {
                var primary = group
                    .OrderByDescending(row => row.TtftMs is > 0)
                    .ThenByDescending(row => string.Equals(
                        row.Source,
                        "copilot-chat",
                        StringComparison.OrdinalIgnoreCase))
                    .ThenBy(row => row.StartTimeMs)
                    .First();
                var cacheRead = group.Max(row => row.CacheReadTokens);
                var cacheCreation = group.Max(row => row.CacheCreationTokens);
                var reasoning = group.Max(row => row.ReasoningTokens);
                var ttft = primary.TtftMs ??
                    group
                        .Where(row => row.TtftMs is > 0)
                        .Select(row => row.TtftMs)
                        .FirstOrDefault();
                var endMs = primary.EndTimeMs ??
                    group
                        .Where(row => row.EndTimeMs is not null)
                        .Select(row => row.EndTimeMs)
                        .FirstOrDefault();
                // Cost is derived from the documented token counts and the
                // configured price, never inherited from one pipeline: the
                // Agent Host row may lack cache tokens and overstate cost.
                var evaluation = ModelPricing.EvaluateDetailed(
                    ModelPricing.GetPrice(primary.Model ?? string.Empty),
                    primary.InputTokens,
                    cacheRead,
                    primary.OutputTokens,
                    reasoning,
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        primary.EndTimeMs ?? primary.StartTimeMs),
                    cacheCreation);
                var cost = (double?)evaluation.CostCny;
                var cacheCost = (double?)evaluation.CacheCost;
                var uncachedCost = (double?)evaluation.UncachedCost;
                var outputCost = (double?)evaluation.OutputCost;
                var reasoningCost = (double?)evaluation.ReasoningCost;
                var nanoAiu = MaxNullableLong(group.Select(row => row.NanoAiu));
                var spanId = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(
                            $"{entry.Key}\u001f{primary.StartTimeMs}\u001f" +
                            $"{primary.TraceId}\u001f{primary.SpanId}")))
                    .ToLowerInvariant();

                merged.Add(primary with
                {
                    SpanId = spanId,
                    Source = "combined",
                    CacheReadTokens = cacheRead,
                    CacheCreationTokens = cacheCreation,
                    ReasoningTokens = reasoning,
                    TtftMs = ttft,
                    EndTimeMs = endMs,
                    NanoAiu = nanoAiu,
                    CostCny = cost,
                    CacheCostCny = cacheCost,
                    UncachedCostCny = uncachedCost,
                    OutputCostCny = outputCost,
                    ReasoningCostCny = reasoningCost,
                    PricingPeriod = evaluation.Period,
                    ErrorType = primary.ErrorType ??
                        group.Select(row => row.ErrorType)
                            .FirstOrDefault(value => value is not null),
                    ProviderName = primary.ProviderName ??
                        group.Select(row => row.ProviderName)
                            .FirstOrDefault(value => value is not null),
                    AgentName = primary.AgentName ??
                        group.Select(row => row.AgentName)
                            .FirstOrDefault(value => value is not null),
                    SubagentName = primary.SubagentName ??
                        group.Select(row => row.SubagentName)
                            .FirstOrDefault(value => value is not null),
                    FinishReasons = primary.FinishReasons ??
                        group.Select(row => row.FinishReasons)
                            .FirstOrDefault(value => value is not null),
                    ReceivedAtMs = group.Max(row => row.ReceivedAtMs),
                });
            }
        }

        return merged;
    }

    // Two pipelines observing the same call may start at different moments
    // but their span intervals must overlap; an arbitrary clock window would
    // miss long wrapper spans and merge unrelated sequential calls.
    private static bool IntervalsOverlap(CombinedSpan left, CombinedSpan right)
    {
        if (left.EndTimeMs is long leftEnd && right.EndTimeMs is long rightEnd)
        {
            return left.StartTimeMs <= rightEnd && right.StartTimeMs <= leftEnd;
        }

        return Math.Abs(left.StartTimeMs - right.StartTimeMs) <= 2_000;
    }

    private static long? MaxNullableLong(IEnumerable<long?> values) =>
        values.Where(value => value.HasValue).Max();

    private sealed record CombinedSpan(
        string TraceId,
        string SpanId,
        string? Name,
        string? ServiceName,
        string? Source,
        string? Model,
        string? SessionId,
        bool IsMain,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        long ReasoningTokens,
        long? TtftMs,
        long? NanoAiu,
        double? CacheCostCny,
        double? UncachedCostCny,
        double? OutputCostCny,
        double? ReasoningCostCny,
        double? CostCny,
        string? PricingPeriod,
        string? ErrorType,
        string? ProviderName,
        string? AgentName,
        string? SubagentName,
        string? FinishReasons,
        long StartTimeMs,
        long? EndTimeMs,
        long ReceivedAtMs);
}
