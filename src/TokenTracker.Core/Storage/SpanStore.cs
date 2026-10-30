using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Storage;

public sealed record StoredModelPrice(
    string Model,
    ModelPrice Price,
    long UpdatedAtMs);

public sealed class SpanStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SpanStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();

        Initialize();
    }

    public string DatabasePath =>
        new SqliteConnectionStringBuilder(_connectionString).DataSource ?? string.Empty;

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS spans (
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
                nano_aiu INTEGER,
                cache_cost_cny REAL,
                uncached_cost_cny REAL,
                output_cost_cny REAL,
                reasoning_cost_cny REAL,
                hit_rate REAL,
                cost_cny REAL,
                pricing_period TEXT,
                attributes TEXT,
                received_at_ms INTEGER NOT NULL,
                PRIMARY KEY (trace_id, span_id)
            );

            CREATE INDEX IF NOT EXISTS ix_spans_start ON spans(start_time_ms);
            CREATE INDEX IF NOT EXISTS ix_spans_session ON spans(session_id);
            CREATE INDEX IF NOT EXISTS ix_spans_model ON spans(model);
            CREATE INDEX IF NOT EXISTS ix_spans_received ON spans(received_at_ms);

            CREATE TABLE IF NOT EXISTS model_prices (
                model TEXT PRIMARY KEY COLLATE NOCASE,
                cached_input REAL NOT NULL,
                uncached_input REAL NOT NULL,
                output REAL NOT NULL,
                peak_aware INTEGER NOT NULL,
                peak_cached_input REAL,
                peak_uncached_input REAL,
                peak_output REAL,
                long_threshold_tokens INTEGER,
                long_cached_input REAL,
                long_uncached_input REAL,
                long_output REAL,
                long_peak_cached_input REAL,
                long_peak_uncached_input REAL,
                long_peak_output REAL,
                cache_write REAL,
                peak_cache_write REAL,
                long_cache_write REAL,
                long_peak_cache_write REAL,
                updated_at_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS holidays (
                year INTEGER PRIMARY KEY,
                dates TEXT NOT NULL,
                fetched_at_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "spans", "cache_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "uncached_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "output_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "reasoning_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "nano_aiu", "INTEGER");

        EnsureColumn(connection, "model_prices", "long_threshold_tokens", "INTEGER");
        EnsureColumn(connection, "model_prices", "long_cached_input", "REAL");
        EnsureColumn(connection, "model_prices", "long_uncached_input", "REAL");
        EnsureColumn(connection, "model_prices", "long_output", "REAL");
        EnsureColumn(connection, "model_prices", "long_peak_cached_input", "REAL");
        EnsureColumn(connection, "model_prices", "long_peak_uncached_input", "REAL");
        EnsureColumn(connection, "model_prices", "long_peak_output", "REAL");
        EnsureColumn(connection, "model_prices", "cache_write", "REAL");
        EnsureColumn(connection, "model_prices", "peak_cache_write", "REAL");
        EnsureColumn(connection, "model_prices", "long_cache_write", "REAL");
        EnsureColumn(connection, "model_prices", "long_peak_cache_write", "REAL");
    }

    public int InsertMany(IReadOnlyList<OtlpSpanRecord> spans)
    {
        if (spans.Count == 0)
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
                INSERT OR IGNORE INTO spans (
                    trace_id, span_id, name, start_time_ms, end_time_ms,
                    service_name, model, session_id, is_main,
                    input_tokens, output_tokens, cache_read_tokens,
                    cache_creation_tokens, reasoning_tokens, nano_aiu,
                    cache_cost_cny, uncached_cost_cny, output_cost_cny, reasoning_cost_cny,
                    hit_rate, cost_cny, pricing_period, attributes, received_at_ms
                ) VALUES (
                    @trace_id, @span_id, @name, @start_time_ms, @end_time_ms,
                    @service_name, @model, @session_id, @is_main,
                    @input_tokens, @output_tokens, @cache_read_tokens,
                    @cache_creation_tokens, @reasoning_tokens, @nano_aiu,
                    @cache_cost_cny, @uncached_cost_cny, @output_cost_cny, @reasoning_cost_cny,
                    @hit_rate, @cost_cny, @pricing_period, @attributes, @received_at_ms
                );
                """;

            AddParameter(command, "@trace_id", SqliteType.Text);
            AddParameter(command, "@span_id", SqliteType.Text);
            AddParameter(command, "@name", SqliteType.Text);
            AddParameter(command, "@start_time_ms", SqliteType.Integer);
            AddParameter(command, "@end_time_ms", SqliteType.Integer);
            AddParameter(command, "@service_name", SqliteType.Text);
            AddParameter(command, "@model", SqliteType.Text);
            AddParameter(command, "@session_id", SqliteType.Text);
            AddParameter(command, "@is_main", SqliteType.Integer);
            AddParameter(command, "@input_tokens", SqliteType.Integer);
            AddParameter(command, "@output_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_read_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_creation_tokens", SqliteType.Integer);
            AddParameter(command, "@reasoning_tokens", SqliteType.Integer);
            AddParameter(command, "@nano_aiu", SqliteType.Integer);
            AddParameter(command, "@cache_cost_cny", SqliteType.Real);
            AddParameter(command, "@uncached_cost_cny", SqliteType.Real);
            AddParameter(command, "@output_cost_cny", SqliteType.Real);
            AddParameter(command, "@reasoning_cost_cny", SqliteType.Real);
            AddParameter(command, "@hit_rate", SqliteType.Real);
            AddParameter(command, "@cost_cny", SqliteType.Real);
            AddParameter(command, "@pricing_period", SqliteType.Text);
            AddParameter(command, "@attributes", SqliteType.Text);
            AddParameter(command, "@received_at_ms", SqliteType.Integer);

            var inserted = 0;
            foreach (var span in spans)
            {
                if (span.SessionId is not null && span.Model is not null)
                {
                    var duplicate = FindDuplicate(connection, transaction, span);
                    if (duplicate is not null)
                    {
                        if (!IsRicher(span, duplicate))
                        {
                            continue;
                        }

                        DeleteSpan(connection, transaction, duplicate.TraceId, duplicate.SpanId);
                    }
                }

                command.Parameters["@trace_id"].Value = span.TraceId;
                command.Parameters["@span_id"].Value = span.SpanId;
                command.Parameters["@name"].Value = (object?)span.Name ?? DBNull.Value;
                command.Parameters["@start_time_ms"].Value = span.StartTimeMs;
                command.Parameters["@end_time_ms"].Value =
                    span.EndTimeMs is null ? DBNull.Value : span.EndTimeMs.Value;
                command.Parameters["@service_name"].Value =
                    (object?)span.ServiceName ?? DBNull.Value;
                command.Parameters["@model"].Value = (object?)span.Model ?? DBNull.Value;
                command.Parameters["@session_id"].Value =
                    (object?)span.SessionId ?? DBNull.Value;
                command.Parameters["@is_main"].Value = span.IsMain ? 1 : 0;
                command.Parameters["@input_tokens"].Value = span.InputTokens;
                command.Parameters["@output_tokens"].Value = span.OutputTokens;
                command.Parameters["@cache_read_tokens"].Value = span.CacheReadTokens;
                command.Parameters["@cache_creation_tokens"].Value =
                    span.CacheCreationTokens;
                command.Parameters["@reasoning_tokens"].Value = span.ReasoningTokens;
                command.Parameters["@nano_aiu"].Value =
                    span.NanoAiu is null ? DBNull.Value : span.NanoAiu.Value;
                command.Parameters["@cache_cost_cny"].Value =
                    span.CacheCostCny is null ? DBNull.Value : span.CacheCostCny.Value;
                command.Parameters["@uncached_cost_cny"].Value =
                    span.UncachedCostCny is null ? DBNull.Value : span.UncachedCostCny.Value;
                command.Parameters["@output_cost_cny"].Value =
                    span.OutputCostCny is null ? DBNull.Value : span.OutputCostCny.Value;
                command.Parameters["@reasoning_cost_cny"].Value =
                    span.ReasoningCostCny is null ? DBNull.Value : span.ReasoningCostCny.Value;
                command.Parameters["@hit_rate"].Value =
                    span.HitRate is null ? DBNull.Value : span.HitRate.Value;
                command.Parameters["@cost_cny"].Value =
                    span.CostCny is null ? DBNull.Value : span.CostCny.Value;
                command.Parameters["@pricing_period"].Value =
                    (object?)span.PricingPeriod ?? DBNull.Value;
                command.Parameters["@attributes"].Value =
                    (object?)span.Attributes ?? DBNull.Value;
                command.Parameters["@received_at_ms"].Value = span.ReceivedAtMs;

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

    public string? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private static SpanDuplicate? FindDuplicate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OtlpSpanRecord span)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT trace_id, span_id, cache_read_tokens, reasoning_tokens,
                   nano_aiu, service_name
            FROM spans
            WHERE session_id = @session
              AND model = @model
              AND input_tokens = @input
              AND output_tokens = @output
              AND start_time_ms BETWEEN @from AND @to
              AND COALESCE(service_name, '') <> COALESCE(@service, '')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@session", span.SessionId!);
        command.Parameters.AddWithValue("@model", span.Model!);
        command.Parameters.AddWithValue("@input", span.InputTokens);
        command.Parameters.AddWithValue("@output", span.OutputTokens);
        command.Parameters.AddWithValue("@from", span.StartTimeMs - 2_000);
        command.Parameters.AddWithValue("@to", span.StartTimeMs + 2_000);
        command.Parameters.AddWithValue("@service", (object?)span.ServiceName ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SpanDuplicate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static bool IsRicher(OtlpSpanRecord span, SpanDuplicate existing)
    {
        if (span.CacheReadTokens != existing.CacheReadTokens)
        {
            return span.CacheReadTokens > existing.CacheReadTokens;
        }

        if (span.ReasoningTokens != existing.ReasoningTokens)
        {
            return span.ReasoningTokens > existing.ReasoningTokens;
        }

        var nano = span.NanoAiu ?? 0;
        var existingNano = existing.NanoAiu ?? 0;
        if (nano != existingNano)
        {
            return nano > existingNano;
        }

        return string.Equals(
            span.ServiceName,
            "copilot-chat",
            StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                existing.ServiceName,
                "copilot-chat",
                StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteSpan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        string spanId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM spans
            WHERE trace_id = @trace AND span_id = @span;
            """;
        command.Parameters.AddWithValue("@trace", traceId);
        command.Parameters.AddWithValue("@span", spanId);
        command.ExecuteNonQuery();
    }

    public SummaryDto GetSummary(
        long fromMs,
        bool includeUtility = false,
        long toMs = long.MaxValue)
    {
        return ReadSummary(
            fromMs,
            toMs,
            includeUtility ? string.Empty : " AND is_main = 1");
    }

    private SummaryDto ReadSummary(long fromMs, long toMs, string filter)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_read_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(reasoning_tokens), 0),
                COUNT(DISTINCT session_id),
                COALESCE(SUM(cost_cny), 0),
                COALESCE(SUM(cache_cost_cny), 0),
                COALESCE(SUM(uncached_cost_cny), 0),
                COALESCE(SUM(output_cost_cny), 0),
                COALESCE(SUM(reasoning_cost_cny), 0),
                COALESCE(SUM(CASE WHEN cost_cny IS NULL THEN 1 ELSE 0 END), 0)
            FROM spans
            WHERE start_time_ms >= @from
              AND start_time_ms < @to{filter};
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SummaryDto();
        }

        return new SummaryDto
        {
            Calls = reader.GetInt64(0),
            InputTokens = reader.GetInt64(1),
            CacheReadTokens = reader.GetInt64(2),
            OutputTokens = reader.GetInt64(3),
            ReasoningTokens = reader.GetInt64(4),
            Sessions = reader.GetInt64(5),
            CostCny = reader.GetDouble(6),
            CacheCostCny = reader.GetDouble(7),
            UncachedCostCny = reader.GetDouble(8),
            OutputCostCny = reader.GetDouble(9),
            ReasoningCostCny = reader.GetDouble(10),
            UnpricedCalls = reader.GetInt64(11),
        };
    }

    public IReadOnlyList<SessionRow> GetSessions(
        long fromMs,
        int limit = 50,
        long toMs = long.MaxValue)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.session_id,
                COALESCE((
                    SELECT s2.model
                    FROM spans s2
                    WHERE s2.session_id = s.session_id
                      AND s2.model IS NOT NULL
                    ORDER BY s2.start_time_ms DESC
                    LIMIT 1
                ), '-'),
                COUNT(*),
                COALESCE(SUM(s.input_tokens), 0),
                COALESCE(SUM(s.cache_read_tokens), 0),
                COALESCE(SUM(s.output_tokens), 0),
                COALESCE(SUM(s.cost_cny), 0),
                COALESCE(SUM(CASE WHEN s.cost_cny IS NULL THEN 1 ELSE 0 END), 0),
                MAX(s.start_time_ms),
                COALESCE(SUM(s.reasoning_tokens), 0),
                COALESCE(SUM(s.cache_cost_cny), 0),
                COALESCE(SUM(s.uncached_cost_cny), 0),
                COALESCE(SUM(s.output_cost_cny), 0),
                COALESCE(SUM(s.reasoning_cost_cny), 0)
            FROM spans s
            WHERE s.is_main = 1
              AND s.start_time_ms >= @from
              AND s.start_time_ms < @to
            GROUP BY s.session_id
            ORDER BY MAX(s.start_time_ms) DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<SessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var inputTokens = reader.GetInt64(3);
            var cacheReadTokens = reader.GetInt64(4);
            rows.Add(new SessionRow
            {
                SessionId = reader.GetString(0),
                Model = reader.GetString(1),
                Calls = reader.GetInt64(2),
                InputTokens = inputTokens,
                CacheReadTokens = cacheReadTokens,
                OutputTokens = reader.GetInt64(5),
                CostCny = reader.GetDouble(6),
                UnpricedCalls = reader.GetInt64(7),
                LastSeenMs = reader.GetInt64(8),
                ReasoningTokens = reader.GetInt64(9),
                CacheCostCny = reader.GetDouble(10),
                UncachedCostCny = reader.GetDouble(11),
                OutputCostCny = reader.GetDouble(12),
                ReasoningCostCny = reader.GetDouble(13),
                HitRate = inputTokens > 0
                    ? (double)cacheReadTokens / inputTokens
                    : null,
            });
        }

        return rows;
    }

    public IReadOnlyList<CallRow> GetCalls(
        long fromMs,
        bool includeUtility = false,
        int limit = 200,
        long toMs = long.MaxValue)
    {
        var utilityFilter = includeUtility ? string.Empty : " AND is_main = 1";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                start_time_ms,
                COALESCE(model, '-'),
                session_id,
                is_main,
                input_tokens,
                cache_read_tokens,
                output_tokens,
                hit_rate,
                cost_cny,
                pricing_period,
                cache_cost_cny,
                uncached_cost_cny,
                output_cost_cny,
                reasoning_tokens,
                reasoning_cost_cny,
                nano_aiu
            FROM spans
            WHERE start_time_ms >= @from
              AND start_time_ms < @to{utilityFilter}
            ORDER BY start_time_ms DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadCallRows(command);
    }

    public IReadOnlyList<CallRow> GetCallsForModel(
        string model,
        long fromMs,
        long toMs = long.MaxValue,
        int limit = 200)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                start_time_ms,
                COALESCE(model, '-'),
                session_id,
                is_main,
                input_tokens,
                cache_read_tokens,
                output_tokens,
                hit_rate,
                cost_cny,
                pricing_period,
                cache_cost_cny,
                uncached_cost_cny,
                output_cost_cny,
                reasoning_tokens,
                reasoning_cost_cny,
                nano_aiu
            FROM spans
            WHERE model = @model
              AND start_time_ms >= @from
              AND start_time_ms < @to
            ORDER BY start_time_ms DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@model", model);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadCallRows(command);
    }

    public IReadOnlyList<CallRow> GetCallsForSession(
        string sessionId,
        long fromMs = 0,
        int limit = 1000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                start_time_ms,
                COALESCE(model, '-'),
                session_id,
                is_main,
                input_tokens,
                cache_read_tokens,
                output_tokens,
                hit_rate,
                cost_cny,
                pricing_period,
                cache_cost_cny,
                uncached_cost_cny,
                output_cost_cny,
                reasoning_tokens,
                reasoning_cost_cny,
                nano_aiu
            FROM spans
            WHERE session_id = @session_id AND start_time_ms >= @from
            ORDER BY start_time_ms DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadCallRows(command);
    }

    private static IReadOnlyList<CallRow> ReadCallRows(SqliteCommand command)
    {
        var rows = new List<CallRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CallRow
            {
                StartTimeMs = reader.GetInt64(0),
                Model = reader.GetString(1),
                SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsMain = reader.GetInt64(3) != 0,
                InputTokens = reader.GetInt64(4),
                CacheReadTokens = reader.GetInt64(5),
                OutputTokens = reader.GetInt64(6),
                HitRate = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                CostCny = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                PricingPeriod = reader.IsDBNull(9) ? null : reader.GetString(9),
                CacheCostCny = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                UncachedCostCny = reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                OutputCostCny = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                ReasoningTokens = reader.GetInt64(13),
                ReasoningCostCny = reader.IsDBNull(14) ? 0 : reader.GetDouble(14),
                NanoAiu = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            });
        }

        return rows;
    }

    public IReadOnlyList<ModelRow> GetModels(
        long fromMs,
        long toMs = long.MaxValue,
        bool includeUtility = false)
    {
        var utilityFilter = includeUtility ? string.Empty : " AND is_main = 1";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                model,
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_read_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(cost_cny), 0),
                COALESCE(SUM(CASE WHEN cost_cny IS NULL THEN 1 ELSE 0 END), 0)
            FROM spans
            WHERE start_time_ms >= @from
              AND start_time_ms < @to
              AND model IS NOT NULL{utilityFilter}
            GROUP BY model
            ORDER BY COUNT(*) DESC;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);

        var rows = new List<ModelRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var inputTokens = reader.GetInt64(2);
            var cacheReadTokens = reader.GetInt64(3);
            rows.Add(new ModelRow
            {
                Model = reader.GetString(0),
                Calls = reader.GetInt64(1),
                InputTokens = inputTokens,
                CacheReadTokens = cacheReadTokens,
                OutputTokens = reader.GetInt64(4),
                CostCny = reader.GetDouble(5),
                UnpricedCalls = reader.GetInt64(6),
                HitRate = inputTokens > 0
                    ? (double)cacheReadTokens / inputTokens
                    : null,
            });
        }

        return rows;
    }

    public SummaryDto GetModelSummary(
        string model,
        long fromMs,
        long toMs = long.MaxValue,
        bool includeUtility = false)
    {
        var utilityFilter = includeUtility ? string.Empty : " AND is_main = 1";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_read_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(reasoning_tokens), 0),
                COUNT(DISTINCT session_id),
                COALESCE(SUM(cost_cny), 0),
                COALESCE(SUM(cache_cost_cny), 0),
                COALESCE(SUM(uncached_cost_cny), 0),
                COALESCE(SUM(output_cost_cny), 0),
                COALESCE(SUM(reasoning_cost_cny), 0),
                COALESCE(SUM(CASE WHEN cost_cny IS NULL THEN 1 ELSE 0 END), 0)
            FROM spans
            WHERE model = @model
              AND start_time_ms >= @from
              AND start_time_ms < @to{utilityFilter};
            """;
        command.Parameters.AddWithValue("@model", model);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SummaryDto();
        }

        return new SummaryDto
        {
            Calls = reader.GetInt64(0),
            InputTokens = reader.GetInt64(1),
            CacheReadTokens = reader.GetInt64(2),
            OutputTokens = reader.GetInt64(3),
            ReasoningTokens = reader.GetInt64(4),
            Sessions = reader.GetInt64(5),
            CostCny = reader.GetDouble(6),
            CacheCostCny = reader.GetDouble(7),
            UncachedCostCny = reader.GetDouble(8),
            OutputCostCny = reader.GetDouble(9),
            ReasoningCostCny = reader.GetDouble(10),
            UnpricedCalls = reader.GetInt64(11),
        };
    }

    public IReadOnlyList<SessionRow> GetSessionsForModel(
        string model,
        long fromMs,
        int limit = 50,
        long toMs = long.MaxValue)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.session_id,
                @model,
                COUNT(*),
                COALESCE(SUM(s.input_tokens), 0),
                COALESCE(SUM(s.cache_read_tokens), 0),
                COALESCE(SUM(s.output_tokens), 0),
                COALESCE(SUM(s.cost_cny), 0),
                COALESCE(SUM(CASE WHEN s.cost_cny IS NULL THEN 1 ELSE 0 END), 0),
                MAX(s.start_time_ms),
                COALESCE(SUM(s.reasoning_tokens), 0),
                COALESCE(SUM(s.cache_cost_cny), 0),
                COALESCE(SUM(s.uncached_cost_cny), 0),
                COALESCE(SUM(s.output_cost_cny), 0),
                COALESCE(SUM(s.reasoning_cost_cny), 0)
            FROM spans s
            WHERE s.is_main = 1
              AND s.model = @model
              AND s.start_time_ms >= @from
              AND s.start_time_ms < @to
            GROUP BY s.session_id
            ORDER BY MAX(s.start_time_ms) DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@model", model);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<SessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var inputTokens = reader.GetInt64(3);
            var cacheReadTokens = reader.GetInt64(4);
            rows.Add(new SessionRow
            {
                SessionId = reader.GetString(0),
                Model = reader.GetString(1),
                Calls = reader.GetInt64(2),
                InputTokens = inputTokens,
                CacheReadTokens = cacheReadTokens,
                OutputTokens = reader.GetInt64(5),
                CostCny = reader.GetDouble(6),
                UnpricedCalls = reader.GetInt64(7),
                LastSeenMs = reader.GetInt64(8),
                ReasoningTokens = reader.GetInt64(9),
                CacheCostCny = reader.GetDouble(10),
                UncachedCostCny = reader.GetDouble(11),
                OutputCostCny = reader.GetDouble(12),
                ReasoningCostCny = reader.GetDouble(13),
                HitRate = inputTokens > 0
                    ? (double)cacheReadTokens / inputTokens
                    : null,
            });
        }

        return rows;
    }

    public string? GetHolidayYear(int year)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dates FROM holidays WHERE year = @year;";
        command.Parameters.AddWithValue("@year", year);
        return command.ExecuteScalar() as string;
    }

    public void UpsertHolidayYear(int year, string dates, long fetchedAtMs)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO holidays (year, dates, fetched_at_ms)
                VALUES (@year, @dates, @fetched)
                ON CONFLICT(year) DO UPDATE SET
                    dates = excluded.dates,
                    fetched_at_ms = excluded.fetched_at_ms;
                """;
            command.Parameters.AddWithValue("@year", year);
            command.Parameters.AddWithValue("@dates", dates);
            command.Parameters.AddWithValue("@fetched", fetchedAtMs);
            command.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<int> GetSpanYears()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT CAST(strftime('%Y', start_time_ms / 1000, 'unixepoch', '+8 hours') AS INTEGER)
            FROM spans
            WHERE start_time_ms > 0;
            """;

        var years = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                years.Add(reader.GetInt32(0));
            }
        }

        return years;
    }

    public long GetLastReceivedAtMs()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(received_at_ms), 0) FROM spans;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> GetDistinctModels()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT model
            FROM spans
            WHERE model IS NOT NULL
            ORDER BY model;
            """;

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public int DeduplicateSpans()
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            var spans = new List<DedupeSpan>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT trace_id, span_id, session_id, model, start_time_ms,
                           input_tokens, output_tokens, cache_read_tokens,
                           reasoning_tokens, nano_aiu, service_name
                    FROM spans
                    WHERE session_id IS NOT NULL AND model IS NOT NULL
                    ORDER BY session_id, start_time_ms;
                    """;
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    spans.Add(new DedupeSpan(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10)));
                }
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM spans
                WHERE trace_id = @trace AND span_id = @span;
                """;
            delete.Parameters.AddWithValue("@trace", "");
            delete.Parameters.AddWithValue("@span", "");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE spans SET model = @model
                WHERE trace_id = @trace AND span_id = @span;
                """;
            update.Parameters.AddWithValue("@model", "");
            update.Parameters.AddWithValue("@trace", "");
            update.Parameters.AddWithValue("@span", "");

            var removed = 0;
            var groups = new Dictionary<string, List<DedupeSpan>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var span in spans)
            {
                var key =
                    $"{span.SessionId}\u0001{ModelPricing.NormalizeModel(span.Model)}" +
                    $"\u0001{span.InputTokens}\u0001{span.OutputTokens}";
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<DedupeSpan>();
                    groups[key] = group;
                }

                group.Add(span);
            }

            foreach (var group in groups.Values)
            {
                var cluster = new List<DedupeSpan>();
                foreach (var span in group.OrderBy(item => item.StartTimeMs))
                {
                    if (cluster.Count > 0 &&
                        span.StartTimeMs - cluster[^1].StartTimeMs > 2_000)
                    {
                        removed += DeduplicateCluster(
                            cluster,
                            delete,
                            update);
                        cluster.Clear();
                    }

                    cluster.Add(span);
                }

                removed += DeduplicateCluster(cluster, delete, update);
            }

            transaction.Commit();
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int DeduplicateCluster(
        List<DedupeSpan> cluster,
        SqliteCommand delete,
        SqliteCommand update)
    {
        if (cluster.Count == 0)
        {
            return 0;
        }

        var best = cluster[0];
        foreach (var candidate in cluster.Skip(1))
        {
            if (IsRicher(candidate, best))
            {
                best = candidate;
            }
        }

        var canonicalModel = ModelPricing.NormalizeModel(best.Model);
        if (!string.Equals(best.Model, canonicalModel, StringComparison.Ordinal))
        {
            update.Parameters["@model"].Value = canonicalModel;
            update.Parameters["@trace"].Value = best.TraceId;
            update.Parameters["@span"].Value = best.SpanId;
            update.ExecuteNonQuery();
        }

        if (cluster.Count < 2 ||
            cluster.Select(item => item.ServiceName).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() < 2)
        {
            return 0;
        }

        var removed = 0;
        foreach (var span in cluster)
        {
            if (span.TraceId == best.TraceId && span.SpanId == best.SpanId)
            {
                continue;
            }

            delete.Parameters["@trace"].Value = span.TraceId;
            delete.Parameters["@span"].Value = span.SpanId;
            removed += delete.ExecuteNonQuery();
        }

        return removed;
    }

    private static bool IsRicher(DedupeSpan candidate, DedupeSpan existing)
    {
        if (candidate.CacheReadTokens != existing.CacheReadTokens)
        {
            return candidate.CacheReadTokens > existing.CacheReadTokens;
        }

        if (candidate.ReasoningTokens != existing.ReasoningTokens)
        {
            return candidate.ReasoningTokens > existing.ReasoningTokens;
        }

        var candidateNano = candidate.NanoAiu ?? 0;
        var existingNano = existing.NanoAiu ?? 0;
        if (candidateNano != existingNano)
        {
            return candidateNano > existingNano;
        }

        return string.Equals(
            candidate.ServiceName,
            "copilot-chat",
            StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                existing.ServiceName,
                "copilot-chat",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DedupeSpan(
        string TraceId,
        string SpanId,
        string SessionId,
        string Model,
        long StartTimeMs,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long ReasoningTokens,
        long? NanoAiu,
        string? ServiceName);

    public IReadOnlyDictionary<string, long> GetModelCallCounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, COUNT(*)
            FROM spans
            WHERE model IS NOT NULL
            GROUP BY model;
            """;

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var model = reader.GetString(0);
            var count = reader.GetInt64(1);
            counts[model] = counts.TryGetValue(model, out var existing)
                ? existing + count
                : count;
        }

        return counts;
    }

    public IReadOnlyList<StoredModelPrice> GetModelPriceOverrides()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                model, cached_input, uncached_input, output, peak_aware,
                peak_cached_input, peak_uncached_input, peak_output, updated_at_ms,
                long_threshold_tokens, long_cached_input, long_uncached_input, long_output,
                long_peak_cached_input, long_peak_uncached_input, long_peak_output,
                cache_write, peak_cache_write, long_cache_write, long_peak_cache_write
            FROM model_prices;
            """;

        var rows = new List<StoredModelPrice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var idle = new PriceTier(
                (decimal)reader.GetDouble(1),
                (decimal)reader.GetDouble(2),
                (decimal)reader.GetDouble(3),
                reader.IsDBNull(16) ? null : (decimal)reader.GetDouble(16));
            var peakAware = reader.GetInt64(4) != 0;
            var peak = peakAware
                ? new PriceTier(
                    reader.IsDBNull(5) ? idle.CachedInput : (decimal)reader.GetDouble(5),
                    reader.IsDBNull(6) ? idle.UncachedInput : (decimal)reader.GetDouble(6),
                    reader.IsDBNull(7) ? idle.Output : (decimal)reader.GetDouble(7),
                    reader.IsDBNull(17) ? null : (decimal)reader.GetDouble(17))
                : idle;

            LongContextPrice? longContext = null;
            if (!reader.IsDBNull(9) && reader.GetInt64(9) > 0)
            {
                var longIdle = new PriceTier(
                    reader.IsDBNull(10) ? idle.CachedInput : (decimal)reader.GetDouble(10),
                    reader.IsDBNull(11) ? idle.UncachedInput : (decimal)reader.GetDouble(11),
                    reader.IsDBNull(12) ? idle.Output : (decimal)reader.GetDouble(12),
                    reader.IsDBNull(18) ? null : (decimal)reader.GetDouble(18));
                var longPeak = peakAware
                    ? new PriceTier(
                        reader.IsDBNull(13) ? longIdle.CachedInput : (decimal)reader.GetDouble(13),
                        reader.IsDBNull(14) ? longIdle.UncachedInput : (decimal)reader.GetDouble(14),
                        reader.IsDBNull(15) ? longIdle.Output : (decimal)reader.GetDouble(15),
                        reader.IsDBNull(19) ? null : (decimal)reader.GetDouble(19))
                    : longIdle;
                longContext = new LongContextPrice(
                    reader.GetInt64(9),
                    longIdle,
                    longPeak);
            }

            rows.Add(new StoredModelPrice(
                reader.GetString(0),
                new ModelPrice(idle, peak, peakAware, longContext),
                reader.GetInt64(8)));
        }

        return rows;
    }

    public void UpsertModelPrice(string model, ModelPrice price, long updatedAtMs)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_prices (
                    model, cached_input, uncached_input, output, peak_aware,
                    peak_cached_input, peak_uncached_input, peak_output, updated_at_ms,
                    long_threshold_tokens, long_cached_input, long_uncached_input, long_output,
                    long_peak_cached_input, long_peak_uncached_input, long_peak_output,
                    cache_write, peak_cache_write, long_cache_write, long_peak_cache_write
                ) VALUES (
                    @model, @cached, @uncached, @output, @peak_aware,
                    @peak_cached, @peak_uncached, @peak_output, @updated,
                    @long_threshold, @long_cached, @long_uncached, @long_output,
                    @long_peak_cached, @long_peak_uncached, @long_peak_output,
                    @cache_write, @peak_cache_write, @long_cache_write, @long_peak_cache_write
                )
                ON CONFLICT(model) DO UPDATE SET
                    cached_input = excluded.cached_input,
                    uncached_input = excluded.uncached_input,
                    output = excluded.output,
                    peak_aware = excluded.peak_aware,
                    peak_cached_input = excluded.peak_cached_input,
                    peak_uncached_input = excluded.peak_uncached_input,
                    peak_output = excluded.peak_output,
                    updated_at_ms = excluded.updated_at_ms,
                    long_threshold_tokens = excluded.long_threshold_tokens,
                    long_cached_input = excluded.long_cached_input,
                    long_uncached_input = excluded.long_uncached_input,
                    long_output = excluded.long_output,
                    long_peak_cached_input = excluded.long_peak_cached_input,
                    long_peak_uncached_input = excluded.long_peak_uncached_input,
                    long_peak_output = excluded.long_peak_output,
                    cache_write = excluded.cache_write,
                    peak_cache_write = excluded.peak_cache_write,
                    long_cache_write = excluded.long_cache_write,
                    long_peak_cache_write = excluded.long_peak_cache_write;
                """;
            command.Parameters.AddWithValue("@model", model);
            command.Parameters.AddWithValue("@cached", (double)price.Idle.CachedInput);
            command.Parameters.AddWithValue("@uncached", (double)price.Idle.UncachedInput);
            command.Parameters.AddWithValue("@output", (double)price.Idle.Output);
            command.Parameters.AddWithValue("@peak_aware", price.PeakAware ? 1 : 0);
            command.Parameters.AddWithValue(
                "@peak_cached",
                price.PeakAware ? (object)(double)price.Peak.CachedInput : DBNull.Value);
            command.Parameters.AddWithValue(
                "@peak_uncached",
                price.PeakAware ? (object)(double)price.Peak.UncachedInput : DBNull.Value);
            command.Parameters.AddWithValue(
                "@peak_output",
                price.PeakAware ? (object)(double)price.Peak.Output : DBNull.Value);
            command.Parameters.AddWithValue("@updated", updatedAtMs);

            var longContext = price.LongContext;
            command.Parameters.AddWithValue(
                "@long_threshold",
                longContext is null ? DBNull.Value : longContext.Value.ThresholdTokens);
            command.Parameters.AddWithValue(
                "@long_cached",
                longContext is null ? DBNull.Value : (object)(double)longContext.Value.Idle.CachedInput);
            command.Parameters.AddWithValue(
                "@long_uncached",
                longContext is null ? DBNull.Value : (object)(double)longContext.Value.Idle.UncachedInput);
            command.Parameters.AddWithValue(
                "@long_output",
                longContext is null ? DBNull.Value : (object)(double)longContext.Value.Idle.Output);
            command.Parameters.AddWithValue(
                "@long_peak_cached",
                longContext is null || !price.PeakAware
                    ? DBNull.Value
                    : (object)(double)longContext.Value.Peak.CachedInput);
            command.Parameters.AddWithValue(
                "@long_peak_uncached",
                longContext is null || !price.PeakAware
                    ? DBNull.Value
                    : (object)(double)longContext.Value.Peak.UncachedInput);
            command.Parameters.AddWithValue(
                "@long_peak_output",
                longContext is null || !price.PeakAware
                    ? DBNull.Value
                    : (object)(double)longContext.Value.Peak.Output);
            command.Parameters.AddWithValue(
                "@cache_write",
                price.Idle.CacheWrite is null
                    ? DBNull.Value
                    : (object)(double)price.Idle.CacheWrite.Value);
            command.Parameters.AddWithValue(
                "@peak_cache_write",
                !price.PeakAware || price.Peak.CacheWrite is null
                    ? DBNull.Value
                    : (object)(double)price.Peak.CacheWrite.Value);
            command.Parameters.AddWithValue(
                "@long_cache_write",
                longContext is null || longContext.Value.Idle.CacheWrite is null
                    ? DBNull.Value
                    : (object)(double)longContext.Value.Idle.CacheWrite.Value);
            command.Parameters.AddWithValue(
                "@long_peak_cache_write",
                longContext is null ||
                !price.PeakAware ||
                longContext.Value.Peak.CacheWrite is null
                    ? DBNull.Value
                    : (object)(double)longContext.Value.Peak.CacheWrite.Value);
            command.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool DeleteModelPrice(string model)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM model_prices WHERE model = @model;";
            command.Parameters.AddWithValue("@model", model);
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public int RecomputeCosts(string model) =>
        RecomputeCostsCore(
            "WHERE model = @model",
            command => command.Parameters.AddWithValue("@model", model));

    public int RecomputeCostsInRange(long fromMs, long toMs) =>
        RecomputeCostsCore(
            "WHERE start_time_ms >= @from AND start_time_ms < @to",
            command =>
            {
                command.Parameters.AddWithValue("@from", fromMs);
                command.Parameters.AddWithValue("@to", toMs);
            });

    private int RecomputeCostsCore(
        string whereClause,
        Action<SqliteCommand> configure)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            var spans = new List<(
                string Model,
                string TraceId,
                string SpanId,
                long StartMs,
                long Input,
                long Cached,
                long CacheCreation,
                long Output,
                long Reasoning)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"""
                    SELECT model, trace_id, span_id, start_time_ms,
                           input_tokens, cache_read_tokens, cache_creation_tokens,
                           output_tokens, reasoning_tokens
                    FROM spans
                    {whereClause};
                    """;
                configure(select);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    spans.Add((
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)));
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE spans
                SET cost_cny = @cost,
                    pricing_period = @period,
                    cache_cost_cny = @cache_cost,
                    uncached_cost_cny = @uncached_cost,
                    output_cost_cny = @output_cost,
                    reasoning_cost_cny = @reasoning_cost
                WHERE trace_id = @trace AND span_id = @span;
                """;
            AddParameter(update, "@cost", SqliteType.Real);
            AddParameter(update, "@period", SqliteType.Text);
            AddParameter(update, "@cache_cost", SqliteType.Real);
            AddParameter(update, "@uncached_cost", SqliteType.Real);
            AddParameter(update, "@output_cost", SqliteType.Real);
            AddParameter(update, "@reasoning_cost", SqliteType.Real);
            AddParameter(update, "@trace", SqliteType.Text);
            AddParameter(update, "@span", SqliteType.Text);

            var count = 0;
            foreach (var span in spans)
            {
                var price = span.Model.Length == 0
                    ? null
                    : ModelPricing.GetPrice(span.Model);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(span.StartMs);
                var detail = ModelPricing.EvaluateDetailed(
                    price,
                    span.Input,
                    span.Cached,
                    span.Output,
                    span.Reasoning,
                    timestamp,
                    span.CacheCreation);

                update.Parameters["@cost"].Value =
                    detail.CostCny is null ? (object)DBNull.Value : (double)detail.CostCny.Value;
                update.Parameters["@period"].Value = detail.Period;
                update.Parameters["@cache_cost"].Value =
                    detail.CacheCost is null ? (object)DBNull.Value : (double)detail.CacheCost.Value;
                update.Parameters["@uncached_cost"].Value =
                    detail.UncachedCost is null ? (object)DBNull.Value : (double)detail.UncachedCost.Value;
                update.Parameters["@output_cost"].Value =
                    detail.OutputCost is null ? (object)DBNull.Value : (double)detail.OutputCost.Value;
                update.Parameters["@reasoning_cost"].Value =
                    detail.ReasoningCost is null ? (object)DBNull.Value : (double)detail.ReasoningCost.Value;
                update.Parameters["@trace"].Value = span.TraceId;
                update.Parameters["@span"].Value = span.SpanId;
                count += update.ExecuteNonQuery();
            }

            transaction.Commit();
            return count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public int BackfillCostBreakdown()
    {
        using (var connection = OpenConnection())
        {
            using var check = connection.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM spans
                WHERE cost_cny IS NOT NULL AND cache_cost_cny IS NULL;
                """;
            var missing = Convert.ToInt64(
                check.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
            if (missing == 0)
            {
                return 0;
            }
        }

        return RecomputeCostsInRange(0, long.MaxValue);
    }

    public int DeleteOlderThan(long cutoffMs)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM spans WHERE start_time_ms < @cutoff;";
            command.Parameters.AddWithValue("@cutoff", cutoffMs);
            return command.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddParameter(
        SqliteCommand command,
        string name,
        SqliteType type) =>
        command.Parameters.Add(name, type);

    private sealed record SpanDuplicate(
        string TraceId,
        string SpanId,
        long CacheReadTokens,
        long ReasoningTokens,
        long? NanoAiu,
        string? ServiceName);

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }
}
