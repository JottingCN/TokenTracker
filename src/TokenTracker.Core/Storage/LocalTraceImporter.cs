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
    IReadOnlyList<string> Errors);

public static class LocalTraceImporter
{
    private static readonly HashSet<string> AllowedFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "agent-traces.db",
            "agent-host-traces.db",
        };

    public static LocalImportResult Import(
        SpanStore store,
        IEnumerable<string>? paths)
    {
        long scanned = 0;
        long matched = 0;
        long inserted = 0;
        long skipped = 0;
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
                var (fileScanned, records) = ReadDatabase(path);
                scanned += fileScanned;
                matched += records.Count;
                var fileInserted = store.InsertMany(records);
                inserted += fileInserted;
                skipped += records.Count - fileInserted;
            }
            catch (Exception exception)
            {
                errors.Add($"{fileName}: {exception.Message}");
            }
        }

        return new LocalImportResult(scanned, matched, inserted, skipped, errors);
    }

    private static (long Scanned, List<OtlpSpanRecord> Records) ReadDatabase(string path)
    {
        var records = new List<OtlpSpanRecord>();
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
                    LIMIT 1)
            FROM spans s
            WHERE s.operation_name = 'chat' OR s.name LIKE 'chat %'
            ORDER BY s.start_time_ms;
            """;

        long scanned = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            scanned++;
            var record = ReadRecord(reader, source);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return (scanned, records);
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
            Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            StartTimeMs = startMs,
            EndTimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ServiceName = provider,
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
