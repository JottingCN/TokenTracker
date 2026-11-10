using System.Globalization;
using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Storage;

public sealed record StoredModelPrice(
    string Model,
    ModelPrice Price,
    long UpdatedAtMs);

// Presentation metadata kept separately from pricing so a model can be named and
// iconed before it is priced. model is the natural key (COLLATE NOCASE).
public sealed record StoredModelProfile(
    string Model,
    string? Provider,
    string? IconKind,
    string? IconLetter,
    string? IconColor,
    string? IconImage,
    long UpdatedAtMs);

public sealed partial class SpanStore
{
    private const string SourceCaseSql =
        "CASE " +
        "WHEN lower(COALESCE(service_namespace, '')) LIKE '%agent-host%' THEN 'agent-host' " +
        "WHEN COALESCE(service_namespace, '') <> '' THEN 'copilot-chat' " +
        "WHEN lower(COALESCE(service_name, '')) IN (" +
        "'github-copilot','claude-code','codex-app-server','vscode-agent-host') " +
        "THEN 'agent-host' " +
        "WHEN lower(COALESCE(service_name, '')) = 'copilot-chat' THEN 'copilot-chat' " +
        "ELSE 'unknown' END";

    private const string SourceSql =
        "COALESCE(otel_source, " + SourceCaseSql + ")";

    private const string SpeedSql = """
        CASE WHEN SUM(CASE WHEN end_time_ms > start_time_ms
            AND ttft_ms IS NOT NULL
            AND output_tokens >= 5
            AND end_time_ms - start_time_ms - ttft_ms >= 50
            THEN end_time_ms - start_time_ms - ttft_ms ELSE 0 END) > 0
            THEN SUM(CASE WHEN end_time_ms > start_time_ms
                AND ttft_ms IS NOT NULL
                AND output_tokens >= 5
                AND end_time_ms - start_time_ms - ttft_ms >= 50
                THEN output_tokens ELSE 0 END) * 1000.0 /
                SUM(CASE WHEN end_time_ms > start_time_ms
                    AND ttft_ms IS NOT NULL
                    AND output_tokens >= 5
                    AND end_time_ms - start_time_ms - ttft_ms >= 50
                    THEN end_time_ms - start_time_ms - ttft_ms ELSE 0 END)
            ELSE NULL
        END
        """;

    /// <summary>
    /// Schema generation stored in SQLite's <c>user_version</c>. Bump it when a
    /// release needs a new column/table, and add the change to
    /// <see cref="Initialize"/> using only additive statements: an older build must
    /// still be able to open and read the same file.
    /// </summary>
    public const int SchemaVersion = 3;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Bumped on every write so a WebSocket snapshot can tell whether the payload it
    // already sent is still current. Rebuilding an unchanged dashboard/model payload
    // takes about a second on real data, so repeating it for every ingest batch
    // starved the HTTP endpoints.
    private long _dataVersion;

    /// <summary>Monotonic counter of successful writes (see <see cref="_dataVersion"/>).</summary>
    public long DataVersion => Interlocked.Read(ref _dataVersion);

    private void BumpDataVersion() => Interlocked.Increment(ref _dataVersion);

    // The version found on open, so callers can tell an upgraded file from one that
    // was written by a newer build.
    private int _openedSchemaVersion;

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

    public StorageStats GetStorageStats()
    {
        using var connection = OpenConnection();
        long pageSize;
        long pageCount;
        string journalMode;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA page_size;";
            pageSize = Convert.ToInt64(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA page_count;";
            pageCount = Convert.ToInt64(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode;";
            journalMode = command.ExecuteScalar()?.ToString() ?? "unknown";
        }

        var tables = new List<StorageTableInfo>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(new StorageTableInfo(reader.GetString(0), 0));
            }
        }

        for (var index = 0; index < tables.Count; index++)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM \"{tables[index].Name.Replace("\"", "\"\"")}\";";
            tables[index] = tables[index] with
            {
                Rows = Convert.ToInt64(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture),
            };
        }

        long rawSpans;
        long combinedSpans;
        long outcomeMetricPoints;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN otel_source = 'combined' THEN 0 ELSE 1 END), 0),
                    COALESCE(SUM(CASE WHEN otel_source = 'combined' THEN 1 ELSE 0 END), 0)
                FROM spans;
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            rawSpans = reader.GetInt64(0);
            combinedSpans = reader.GetInt64(1);
        }

        using (var command = connection.CreateCommand())
        {
            var parameters = MetricNames.Outcome
                .Select((_, index) => $"@outcome_{index}")
                .ToList();
            command.CommandText =
                "SELECT COUNT(*) FROM metric_points WHERE metric_name IN " +
                $"({string.Join(", ", parameters)});";
            var index = 0;
            foreach (var name in MetricNames.Outcome)
            {
                command.Parameters.AddWithValue(parameters[index++], name);
            }

            outcomeMetricPoints = Convert.ToInt64(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        long FileSize(string path) =>
            File.Exists(path) ? new FileInfo(path).Length : 0;

        return new StorageStats(
            DatabasePath,
            FileSize(DatabasePath),
            FileSize(DatabasePath + "-wal"),
            FileSize(DatabasePath + "-shm"),
            pageSize,
            pageCount,
            journalMode,
            rawSpans,
            combinedSpans,
            outcomeMetricPoints,
            tables,
            _openedSchemaVersion,
            SchemaVersion,
            _openedSchemaVersion > SchemaVersion);
    }

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
                parent_span_id TEXT,
                name TEXT,
                start_time_ms INTEGER NOT NULL,
                end_time_ms INTEGER,
                service_name TEXT,
                service_namespace TEXT,
                scope_name TEXT,
                otel_source TEXT,
                model TEXT,
                session_id TEXT,
                is_main INTEGER NOT NULL,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                ttft_ms INTEGER,
                nano_aiu INTEGER,
                cache_cost_cny REAL,
                uncached_cost_cny REAL,
                output_cost_cny REAL,
                reasoning_cost_cny REAL,
                hit_rate REAL,
                cost_cny REAL,
                pricing_period TEXT,
                error_type TEXT,
                provider_name TEXT,
                agent_name TEXT,
                subagent_name TEXT,
                window_session_id TEXT,
                finish_reasons TEXT,
                received_at_ms INTEGER NOT NULL,
                PRIMARY KEY (trace_id, span_id)
            );

            CREATE TABLE IF NOT EXISTS session_events (
                trace_id TEXT NOT NULL,
                span_id TEXT NOT NULL,
                parent_span_id TEXT,
                name TEXT,
                operation TEXT NOT NULL,
                tool_name TEXT,
                tool_type TEXT,
                edit_type TEXT,
                skill_name TEXT,
                mcp_server_hash TEXT,
                mcp_tool_name TEXT,
                hook_decision TEXT,
                hook_duration_ms REAL,
                hook_tool_names TEXT,
                hook_type TEXT,
                hook_result_kind TEXT,
                permission_kind TEXT,
                permission_result TEXT,
                call_id TEXT,
                session_id TEXT,
                service_name TEXT,
                service_namespace TEXT,
                scope_name TEXT,
                otel_source TEXT,
                error_type TEXT,
                agent_name TEXT,
                agent_type TEXT,
                provider_name TEXT,
                window_session_id TEXT,
                start_time_ms INTEGER NOT NULL,
                end_time_ms INTEGER,
                is_error INTEGER NOT NULL DEFAULT 0,
                received_at_ms INTEGER NOT NULL,
                PRIMARY KEY (trace_id, span_id)
            );

            CREATE TABLE IF NOT EXISTS agent_runs (
                otel_source TEXT NOT NULL,
                trace_id TEXT NOT NULL,
                span_id TEXT NOT NULL,
                parent_span_id TEXT,
                is_root INTEGER NOT NULL,
                session_id TEXT,
                agent_name TEXT,
                agent_type TEXT,
                provider_name TEXT,
                request_model TEXT,
                response_model TEXT,
                service_name TEXT,
                service_namespace TEXT,
                git_repository TEXT,
                git_branch TEXT,
                git_commit TEXT,
                github_org TEXT,
                turn_count INTEGER,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                start_time_ms INTEGER NOT NULL,
                end_time_ms INTEGER,
                is_error INTEGER NOT NULL DEFAULT 0,
                error_type TEXT,
                received_at_ms INTEGER NOT NULL,
                PRIMARY KEY (otel_source, trace_id, span_id)
            );

            CREATE TABLE IF NOT EXISTS metric_points (
                otel_source TEXT NOT NULL,
                series_key TEXT NOT NULL,
                metric_name TEXT NOT NULL,
                metric_kind TEXT NOT NULL,
                temporality TEXT,
                service_name TEXT,
                scope_name TEXT,
                attrs TEXT,
                window_session_id TEXT,
                attributed_session_id TEXT,
                attribution_method TEXT,
                attribution_confidence REAL,
                start_time_ms INTEGER NOT NULL,
                end_time_ms INTEGER NOT NULL,
                value REAL,
                count INTEGER,
                sum REAL,
                min REAL,
                max REAL,
                bucket_counts TEXT,
                explicit_bounds TEXT,
                received_at_ms INTEGER NOT NULL,
                PRIMARY KEY (otel_source, series_key, end_time_ms)
            );

            CREATE TABLE IF NOT EXISTS window_session_links (                window_session_id TEXT NOT NULL,
                chat_session_id TEXT NOT NULL,
                service_name TEXT,
                first_seen_ms INTEGER NOT NULL,
                last_seen_ms INTEGER NOT NULL,
                PRIMARY KEY (window_session_id, chat_session_id)
            );

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

            CREATE TABLE IF NOT EXISTS model_profiles (
                model TEXT PRIMARY KEY COLLATE NOCASE,
                provider TEXT,
                icon_kind TEXT,
                icon_letter TEXT,
                icon_color TEXT,
                icon_image TEXT,
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

            CREATE TABLE IF NOT EXISTS session_workspaces (
                session_id TEXT PRIMARY KEY,
                workspace_path TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "spans", "cache_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "uncached_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "output_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "reasoning_cost_cny", "REAL");
        EnsureColumn(connection, "spans", "nano_aiu", "INTEGER");
        EnsureColumn(connection, "spans", "ttft_ms", "INTEGER");
        EnsureColumn(connection, "spans", "otel_source", "TEXT");
        EnsureColumn(connection, "spans", "service_namespace", "TEXT");
        EnsureColumn(connection, "spans", "scope_name", "TEXT");
        EnsureColumn(connection, "spans", "parent_span_id", "TEXT");
        EnsureColumn(connection, "spans", "error_type", "TEXT");
        EnsureColumn(connection, "spans", "provider_name", "TEXT");
        EnsureColumn(connection, "spans", "agent_name", "TEXT");
        EnsureColumn(connection, "spans", "subagent_name", "TEXT");
        EnsureColumn(connection, "spans", "finish_reasons", "TEXT");
        EnsureColumn(connection, "spans", "window_session_id", "TEXT");
        EnsureColumn(connection, "session_events", "window_session_id", "TEXT");
        EnsureColumn(connection, "metric_points", "window_session_id", "TEXT");
        EnsureColumn(connection, "metric_points", "attributed_session_id", "TEXT");
        EnsureColumn(connection, "metric_points", "attribution_method", "TEXT");
        EnsureColumn(connection, "metric_points", "attribution_confidence", "REAL");
        EnsureColumn(connection, "session_events", "otel_source", "TEXT");
        EnsureColumn(connection, "session_events", "service_namespace", "TEXT");
        EnsureColumn(connection, "session_events", "scope_name", "TEXT");
        EnsureColumn(connection, "session_events", "call_id", "TEXT");
        EnsureColumn(connection, "session_events", "tool_type", "TEXT");
        EnsureColumn(connection, "session_events", "edit_type", "TEXT");
        EnsureColumn(connection, "session_events", "skill_name", "TEXT");
        EnsureColumn(connection, "session_events", "parent_span_id", "TEXT");
        EnsureColumn(connection, "session_events", "error_type", "TEXT");
        EnsureColumn(connection, "session_events", "agent_name", "TEXT");
        EnsureColumn(connection, "session_events", "agent_type", "TEXT");
        EnsureColumn(connection, "session_events", "provider_name", "TEXT");
        EnsureColumn(connection, "session_events", "mcp_server_hash", "TEXT");
        EnsureColumn(connection, "session_events", "mcp_tool_name", "TEXT");
        EnsureColumn(connection, "session_events", "hook_decision", "TEXT");
        EnsureColumn(connection, "session_events", "hook_duration_ms", "REAL");
        EnsureColumn(connection, "session_events", "hook_tool_names", "TEXT");
        EnsureColumn(connection, "session_events", "hook_type", "TEXT");
        EnsureColumn(connection, "session_events", "hook_result_kind", "TEXT");
        EnsureColumn(connection, "session_events", "permission_kind", "TEXT");
        EnsureColumn(connection, "session_events", "permission_result", "TEXT");

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

        // Indexes last: upgrading from an old database fails if an index references
        // a column that EnsureColumn has not added yet.
        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_spans_start ON spans(start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_spans_end ON spans(end_time_ms);
                CREATE INDEX IF NOT EXISTS ix_spans_session ON spans(session_id);
                CREATE INDEX IF NOT EXISTS ix_spans_model ON spans(model);
                CREATE INDEX IF NOT EXISTS ix_spans_received ON spans(received_at_ms);
                CREATE INDEX IF NOT EXISTS ix_spans_source ON spans(otel_source);
                CREATE INDEX IF NOT EXISTS ix_spans_trace ON spans(trace_id);
                CREATE INDEX IF NOT EXISTS ix_spans_parent ON spans(parent_span_id);
                CREATE INDEX IF NOT EXISTS ix_spans_error ON spans(error_type);
                CREATE INDEX IF NOT EXISTS ix_spans_source_start
                    ON spans(otel_source, start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_spans_session_start
                    ON spans(session_id, start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_spans_model_source_start
                    ON spans(model, otel_source, start_time_ms);

                CREATE INDEX IF NOT EXISTS ix_events_session ON session_events(session_id);
                CREATE INDEX IF NOT EXISTS ix_events_operation ON session_events(operation);
                CREATE INDEX IF NOT EXISTS ix_events_tool ON session_events(tool_name);
                CREATE INDEX IF NOT EXISTS ix_events_call_id ON session_events(call_id);
                CREATE INDEX IF NOT EXISTS ix_events_start ON session_events(start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_events_source ON session_events(otel_source);
                CREATE INDEX IF NOT EXISTS ix_events_parent ON session_events(parent_span_id);
                CREATE INDEX IF NOT EXISTS ix_events_error ON session_events(error_type);
                CREATE INDEX IF NOT EXISTS ix_events_source_start
                    ON session_events(otel_source, start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_events_session_start
                    ON session_events(session_id, start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_events_session_operation
                    ON session_events(session_id, operation, start_time_ms);

                CREATE INDEX IF NOT EXISTS ix_agent_runs_session
                    ON agent_runs(session_id, start_time_ms);
                CREATE INDEX IF NOT EXISTS ix_agent_runs_agent
                    ON agent_runs(agent_name);
                CREATE INDEX IF NOT EXISTS ix_agent_runs_start
                    ON agent_runs(start_time_ms);

                CREATE INDEX IF NOT EXISTS ix_metric_points_name
                    ON metric_points(metric_name, end_time_ms);
                CREATE INDEX IF NOT EXISTS ix_metric_points_source
                    ON metric_points(otel_source, end_time_ms);
                CREATE INDEX IF NOT EXISTS ix_metric_points_series
                    ON metric_points(series_key, end_time_ms);
                CREATE INDEX IF NOT EXISTS ix_metric_points_window
                    ON metric_points(window_session_id, end_time_ms);
                CREATE INDEX IF NOT EXISTS ix_metric_points_attributed
                    ON metric_points(attributed_session_id, end_time_ms);

                CREATE INDEX IF NOT EXISTS ix_window_links_session
                    ON window_session_links(chat_session_id, last_seen_ms);
                """;
            indexes.ExecuteNonQuery();
        }

        // Everything above is additive and idempotent, so an old database is
        // upgraded in place and no row is ever deleted during a schema change.
        var version = ReadSchemaVersion(connection);
        _openedSchemaVersion = version;
        if (version > SchemaVersion)
        {
            // A newer build wrote this file. Keep it untouched: columns we do not
            // know about are simply ignored, and refusing to start would look like
            // data loss to the user.
            Console.Error.WriteLine(
                $"database schema {version} is newer than {SchemaVersion}; " +
                "running in compatibility mode without modifying it");
            return;
        }

        SetSchemaVersion(connection, SchemaVersion);
        // The file is now at the current generation, whether it was created here or
        // upgraded from an older build.
        _openedSchemaVersion = SchemaVersion;
    }

    /// <summary>
    /// SQLite's <c>user_version</c> is the cheap, self-contained schema marker; it
    /// survives VACUUM and needs no extra table row.
    /// </summary>
    public int GetSchemaVersion() => _openedSchemaVersion;

    /// <summary>
    /// True when the file was written by a newer build and is therefore left
    /// untouched. Reads keep working; nothing is migrated or deleted.
    /// </summary>
    public bool IsNewerSchema => _openedSchemaVersion > SchemaVersion;

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetSchemaVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        // PRAGMA does not accept parameters and the value is a compile-time
        // constant, so this interpolation cannot be injected.
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    public int InsertMany(IReadOnlyList<OtlpSpanRecord> spans)
    {
        if (spans.Count == 0)
        {
            return 0;
        }

        BumpDataVersion();
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO spans (
                    trace_id, span_id, parent_span_id, name, start_time_ms, end_time_ms,
                    service_name, service_namespace, scope_name, otel_source,
                    model, session_id, is_main,
                    input_tokens, output_tokens, cache_read_tokens,
                    cache_creation_tokens, reasoning_tokens, ttft_ms, nano_aiu,
                    cache_cost_cny, uncached_cost_cny, output_cost_cny, reasoning_cost_cny,
                    hit_rate, cost_cny, pricing_period, error_type, provider_name,
                    agent_name, window_session_id, finish_reasons, received_at_ms
                ) VALUES (
                    @trace_id, @span_id, @parent_span_id, @name, @start_time_ms, @end_time_ms,
                    @service_name, @service_namespace, @scope_name, @otel_source,
                    @model, @session_id, @is_main,
                    @input_tokens, @output_tokens, @cache_read_tokens,
                    @cache_creation_tokens, @reasoning_tokens, @ttft_ms, @nano_aiu,
                    @cache_cost_cny, @uncached_cost_cny, @output_cost_cny, @reasoning_cost_cny,
                    @hit_rate, @cost_cny, @pricing_period, @error_type, @provider_name,
                    @agent_name, @window_session_id, @finish_reasons, @received_at_ms
                );
                """;

            AddParameter(command, "@trace_id", SqliteType.Text);
            AddParameter(command, "@span_id", SqliteType.Text);
            AddParameter(command, "@parent_span_id", SqliteType.Text);
            AddParameter(command, "@name", SqliteType.Text);
            AddParameter(command, "@start_time_ms", SqliteType.Integer);
            AddParameter(command, "@end_time_ms", SqliteType.Integer);
            AddParameter(command, "@service_name", SqliteType.Text);
            AddParameter(command, "@service_namespace", SqliteType.Text);
            AddParameter(command, "@scope_name", SqliteType.Text);
            AddParameter(command, "@otel_source", SqliteType.Text);
            AddParameter(command, "@model", SqliteType.Text);
            AddParameter(command, "@session_id", SqliteType.Text);
            AddParameter(command, "@is_main", SqliteType.Integer);
            AddParameter(command, "@input_tokens", SqliteType.Integer);
            AddParameter(command, "@output_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_read_tokens", SqliteType.Integer);
            AddParameter(command, "@cache_creation_tokens", SqliteType.Integer);
            AddParameter(command, "@reasoning_tokens", SqliteType.Integer);
            AddParameter(command, "@ttft_ms", SqliteType.Integer);
            AddParameter(command, "@nano_aiu", SqliteType.Integer);
            AddParameter(command, "@cache_cost_cny", SqliteType.Real);
            AddParameter(command, "@uncached_cost_cny", SqliteType.Real);
            AddParameter(command, "@output_cost_cny", SqliteType.Real);
            AddParameter(command, "@reasoning_cost_cny", SqliteType.Real);
            AddParameter(command, "@hit_rate", SqliteType.Real);
            AddParameter(command, "@cost_cny", SqliteType.Real);
            AddParameter(command, "@pricing_period", SqliteType.Text);
            AddParameter(command, "@error_type", SqliteType.Text);
            AddParameter(command, "@provider_name", SqliteType.Text);
            AddParameter(command, "@agent_name", SqliteType.Text);
            AddParameter(command, "@window_session_id", SqliteType.Text);
            AddParameter(command, "@finish_reasons", SqliteType.Text);
            AddParameter(command, "@received_at_ms", SqliteType.Integer);

            var inserted = 0;
            foreach (var span in spans)
            {
                command.Parameters["@trace_id"].Value = span.TraceId;
                command.Parameters["@span_id"].Value = span.SpanId;
                command.Parameters["@parent_span_id"].Value =
                    (object?)span.ParentSpanId ?? DBNull.Value;
                command.Parameters["@name"].Value = (object?)span.Name ?? DBNull.Value;
                command.Parameters["@start_time_ms"].Value = span.StartTimeMs;
                command.Parameters["@end_time_ms"].Value =
                    span.EndTimeMs is null ? DBNull.Value : span.EndTimeMs.Value;
                command.Parameters["@service_name"].Value =
                    (object?)span.ServiceName ?? DBNull.Value;
                command.Parameters["@service_namespace"].Value =
                    (object?)span.ServiceNamespace ?? DBNull.Value;
                command.Parameters["@scope_name"].Value =
                    (object?)span.ScopeName ?? DBNull.Value;
                command.Parameters["@otel_source"].Value =
                    (object?)span.Source ?? DBNull.Value;
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
                command.Parameters["@ttft_ms"].Value =
                    span.TtftMs is null ? DBNull.Value : span.TtftMs.Value;
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
                command.Parameters["@error_type"].Value =
                    (object?)span.ErrorType ?? DBNull.Value;
                command.Parameters["@provider_name"].Value =
                    (object?)span.ProviderName ?? DBNull.Value;
                command.Parameters["@agent_name"].Value =
                    (object?)span.AgentName ?? DBNull.Value;
                command.Parameters["@window_session_id"].Value =
                    (object?)span.WindowSessionId ?? DBNull.Value;
                command.Parameters["@finish_reasons"].Value =
                    (object?)span.FinishReasons ?? DBNull.Value;
                command.Parameters["@received_at_ms"].Value = span.ReceivedAtMs;

                inserted += command.ExecuteNonQuery();
            }

            var resolveTraceIds = spans
                .Where(span =>
                    NeedsSessionResolution(span.SessionId) &&
                    !string.IsNullOrWhiteSpace(span.ParentSpanId))
                .Select(span => span.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var resolved = resolveTraceIds.Count > 0
                ? ResolveSessionIdsForTraces(
                    connection,
                    transaction,
                    resolveTraceIds)
                : 0;

            var nameTraceIds = spans
                .Where(span => !string.IsNullOrWhiteSpace(span.ParentSpanId))
                .Select(span => span.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (nameTraceIds.Count > 0)
            {
                UpdateSubagentNames(connection, transaction, nameTraceIds);
            }

            var windowIds = spans
                .Where(span => !string.IsNullOrWhiteSpace(span.WindowSessionId))
                .Select(span => span.WindowSessionId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            UpsertWindowSessionLinks(
                connection,
                transaction,
                spans
                    .Where(span =>
                        !string.IsNullOrWhiteSpace(span.WindowSessionId) &&
                        !string.IsNullOrWhiteSpace(span.SessionId))
                    .Select(span => (
                        span.WindowSessionId!,
                        span.SessionId!,
                        span.ServiceName,
                        span.StartTimeMs,
                        span.EndTimeMs)));
            if (windowIds.Count > 0)
            {
                ReconcileMetricAttribution(
                    connection,
                    transaction,
                    windowIds);
            }

            transaction.Commit();
            if (resolved > 0)
            {
                // Session ids moved between rows, so stale derived rows can sit
                // anywhere in the affected traces and need a global rebuild.
                _combinedAllDirty = true;
                _combinedDirtySessions.Clear();
                _combinedDirty = true;
            }
            else if (inserted > 0)
            {
                long utilityFromMs = long.MaxValue;
                long utilityToMs = long.MinValue;
                foreach (var span in spans)
                {
                    if (string.IsNullOrWhiteSpace(span.SessionId))
                    {
                        if (span.StartTimeMs < utilityFromMs)
                        {
                            utilityFromMs = span.StartTimeMs;
                        }

                        var endMs = span.EndTimeMs ?? span.StartTimeMs;
                        if (endMs > utilityToMs)
                        {
                            utilityToMs = endMs;
                        }
                    }
                    else
                    {
                        MarkCombinedSession(
                            span.SessionId!,
                            span.StartTimeMs,
                            span.EndTimeMs ?? span.StartTimeMs);
                    }
                }

                if (utilityToMs >= utilityFromMs)
                {
                    MarkCombinedWindow(utilityFromMs, utilityToMs);
                }

                _combinedDirty = true;
            }

            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public int InsertEvents(IReadOnlyList<OtlpEventRecord> events)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        BumpDataVersion();
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO session_events (
                    trace_id, span_id, parent_span_id, name, operation, tool_name,
                    tool_type, edit_type, skill_name, mcp_server_hash, mcp_tool_name,
                    hook_decision, hook_duration_ms, hook_tool_names, hook_type,
                    hook_result_kind, permission_kind, permission_result,
                    call_id, session_id, service_name,
                    service_namespace, scope_name, otel_source, error_type,
                    agent_name, agent_type, provider_name, window_session_id,
                    start_time_ms, end_time_ms, is_error, received_at_ms
                ) VALUES (
                    @trace_id, @span_id, @parent_span_id, @name, @operation,
                    @tool_name, @tool_type, @edit_type, @skill_name,
                    @mcp_server_hash, @mcp_tool_name, @hook_decision,
                    @hook_duration_ms, @hook_tool_names, @hook_type,
                    @hook_result_kind, @permission_kind, @permission_result,
                    @call_id, @session_id, @service_name,
                    @service_namespace, @scope_name, @otel_source, @error_type,
                    @agent_name, @agent_type, @provider_name,
                    @window_session_id, @start_time_ms, @end_time_ms,
                    @is_error, @received_at_ms
                );
                """;

            AddParameter(command, "@trace_id", SqliteType.Text);
            AddParameter(command, "@span_id", SqliteType.Text);
            AddParameter(command, "@parent_span_id", SqliteType.Text);
            AddParameter(command, "@name", SqliteType.Text);
            AddParameter(command, "@operation", SqliteType.Text);
            AddParameter(command, "@tool_name", SqliteType.Text);
            AddParameter(command, "@tool_type", SqliteType.Text);
            AddParameter(command, "@edit_type", SqliteType.Text);
            AddParameter(command, "@skill_name", SqliteType.Text);
            AddParameter(command, "@mcp_server_hash", SqliteType.Text);
            AddParameter(command, "@mcp_tool_name", SqliteType.Text);
            AddParameter(command, "@hook_decision", SqliteType.Text);
            AddParameter(command, "@hook_duration_ms", SqliteType.Real);
            AddParameter(command, "@hook_tool_names", SqliteType.Text);
            AddParameter(command, "@hook_type", SqliteType.Text);
            AddParameter(command, "@hook_result_kind", SqliteType.Text);
            AddParameter(command, "@permission_kind", SqliteType.Text);
            AddParameter(command, "@permission_result", SqliteType.Text);
            AddParameter(command, "@call_id", SqliteType.Text);
            AddParameter(command, "@session_id", SqliteType.Text);
            AddParameter(command, "@service_name", SqliteType.Text);
            AddParameter(command, "@service_namespace", SqliteType.Text);
            AddParameter(command, "@scope_name", SqliteType.Text);
            AddParameter(command, "@otel_source", SqliteType.Text);
            AddParameter(command, "@error_type", SqliteType.Text);
            AddParameter(command, "@agent_name", SqliteType.Text);
            AddParameter(command, "@agent_type", SqliteType.Text);
            AddParameter(command, "@provider_name", SqliteType.Text);
            AddParameter(command, "@window_session_id", SqliteType.Text);
            AddParameter(command, "@start_time_ms", SqliteType.Integer);
            AddParameter(command, "@end_time_ms", SqliteType.Integer);
            AddParameter(command, "@is_error", SqliteType.Integer);
            AddParameter(command, "@received_at_ms", SqliteType.Integer);

            var inserted = 0;
            foreach (var item in events)
            {
                if (!string.IsNullOrWhiteSpace(item.CallId) &&
                    !string.IsNullOrWhiteSpace(item.SessionId) &&
                    EventExists(connection, transaction, item))
                {
                    continue;
                }

                command.Parameters["@trace_id"].Value = item.TraceId;
                command.Parameters["@span_id"].Value = item.SpanId;
                command.Parameters["@parent_span_id"].Value =
                    (object?)item.ParentSpanId ?? DBNull.Value;
                command.Parameters["@name"].Value =
                    (object?)item.Name ?? DBNull.Value;
                command.Parameters["@operation"].Value = item.Operation;
                command.Parameters["@tool_name"].Value =
                    (object?)item.ToolName ?? DBNull.Value;
                command.Parameters["@tool_type"].Value =
                    (object?)item.ToolType ?? DBNull.Value;
                command.Parameters["@edit_type"].Value =
                    (object?)item.EditType ?? DBNull.Value;
                command.Parameters["@skill_name"].Value =
                    (object?)item.SkillName ?? DBNull.Value;
                command.Parameters["@mcp_server_hash"].Value =
                    (object?)item.McpServerHash ?? DBNull.Value;
                command.Parameters["@mcp_tool_name"].Value =
                    (object?)item.McpToolName ?? DBNull.Value;
                command.Parameters["@hook_decision"].Value =
                    (object?)item.HookDecision ?? DBNull.Value;
                command.Parameters["@hook_duration_ms"].Value =
                    item.HookDurationMs is null
                        ? DBNull.Value
                        : item.HookDurationMs.Value;
                command.Parameters["@hook_tool_names"].Value =
                    (object?)item.HookToolNames ?? DBNull.Value;
                command.Parameters["@hook_type"].Value =
                    (object?)item.HookType ?? DBNull.Value;
                command.Parameters["@hook_result_kind"].Value =
                    (object?)item.HookResultKind ?? DBNull.Value;
                command.Parameters["@permission_kind"].Value =
                    (object?)item.PermissionKind ?? DBNull.Value;
                command.Parameters["@permission_result"].Value =
                    (object?)item.PermissionResult ?? DBNull.Value;
                command.Parameters["@call_id"].Value =
                    (object?)item.CallId ?? DBNull.Value;
                command.Parameters["@session_id"].Value =
                    (object?)item.SessionId ?? DBNull.Value;
                command.Parameters["@service_name"].Value =
                    (object?)item.ServiceName ?? DBNull.Value;
                command.Parameters["@service_namespace"].Value =
                    (object?)item.ServiceNamespace ?? DBNull.Value;
                command.Parameters["@scope_name"].Value =
                    (object?)item.ScopeName ?? DBNull.Value;
                command.Parameters["@otel_source"].Value =
                    (object?)item.Source ?? DBNull.Value;
                command.Parameters["@error_type"].Value =
                    (object?)item.ErrorType ?? DBNull.Value;
                command.Parameters["@agent_name"].Value =
                    (object?)item.AgentName ?? DBNull.Value;
                command.Parameters["@agent_type"].Value =
                    (object?)item.AgentType ?? DBNull.Value;
                command.Parameters["@provider_name"].Value =
                    (object?)item.ProviderName ?? DBNull.Value;
                command.Parameters["@window_session_id"].Value =
                    (object?)item.WindowSessionId ?? DBNull.Value;
                command.Parameters["@start_time_ms"].Value = item.StartTimeMs;
                command.Parameters["@end_time_ms"].Value =
                    item.EndTimeMs is null ? DBNull.Value : item.EndTimeMs.Value;
                command.Parameters["@is_error"].Value = item.IsError ? 1 : 0;
                command.Parameters["@received_at_ms"].Value = item.ReceivedAtMs;
                inserted += command.ExecuteNonQuery();
            }

            var resolveTraceIds = events
                .Where(item =>
                    NeedsSessionResolution(item.SessionId) &&
                    !string.IsNullOrWhiteSpace(item.ParentSpanId))
                .Select(item => item.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (resolveTraceIds.Count > 0)
            {
                ResolveSessionIdsForTraces(
                    connection,
                    transaction,
                    resolveTraceIds);
            }

            var nameTraceIds = events
                .Select(item => item.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var namesChanged = nameTraceIds.Count > 0
                ? UpdateSubagentNames(connection, transaction, nameTraceIds)
                : 0;

            var eventWindowIds = events
                .Where(item => !string.IsNullOrWhiteSpace(item.WindowSessionId))
                .Select(item => item.WindowSessionId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            UpsertWindowSessionLinks(
                connection,
                transaction,
                events
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.WindowSessionId) &&
                        !string.IsNullOrWhiteSpace(item.SessionId))
                    .Select(item => (
                        item.WindowSessionId!,
                        item.SessionId!,
                        item.ServiceName,
                        item.StartTimeMs,
                        item.EndTimeMs)));
            if (eventWindowIds.Count > 0)
            {
                ReconcileMetricAttribution(
                    connection,
                    transaction,
                    eventWindowIds);
            }

            transaction.Commit();
            if (namesChanged > 0)
            {
                _combinedAllDirty = true;
                _combinedDirtySessions.Clear();
                _combinedDirty = true;
            }

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

    private static bool EventExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OtlpEventRecord item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM session_events
            WHERE session_id = @session
              AND operation = @operation
              AND call_id = @call_id
              AND COALESCE(tool_name, '') = COALESCE(@tool, '')
              AND COALESCE(otel_source, 'unknown') = COALESCE(@source, 'unknown')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@session", item.SessionId!);
        command.Parameters.AddWithValue("@operation", item.Operation);
        command.Parameters.AddWithValue("@call_id", item.CallId!);
        command.Parameters.AddWithValue(
            "@tool",
            (object?)item.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@source",
            (object?)item.Source ?? DBNull.Value);
        return command.ExecuteScalar() is not null;
    }

    public SummaryDto GetSummary(
        long fromMs,
        bool includeUtility = false,
        long toMs = long.MaxValue,
        string? source = null)
    {
        return ReadSummary(
            fromMs,
            toMs,
            includeUtility ? string.Empty : " AND is_main = 1",
            source);
    }

    private SummaryDto ReadSummary(
        long fromMs,
        long toMs,
        string filter,
        string? source)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
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
              AND start_time_ms < @to{filter}{sourceFilter};
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

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

    public IReadOnlyList<string> GetSessionIds(
        long fromMs,
        long toMs = long.MaxValue,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT session_id
            FROM spans
            WHERE session_id IS NOT NULL
              AND start_time_ms >= @from
              AND start_time_ms < @to{sourceFilter}
            ORDER BY session_id;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public IReadOnlyList<SessionRow> GetSessions(
        long fromMs,
        int limit = 50,
        long toMs = long.MaxValue,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                s.session_id,
                COALESCE((
                    SELECT s2.model
                    FROM spans s2
                    WHERE s2.session_id = s.session_id
                      AND s2.model IS NOT NULL
                      AND COALESCE(s2.otel_source, '') =
                          COALESCE(s.otel_source, '')
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
                COALESCE(SUM(s.reasoning_cost_cny), 0),
                MIN(s.start_time_ms),
                AVG(CASE WHEN s.end_time_ms > s.start_time_ms
                    THEN s.end_time_ms - s.start_time_ms END),
                AVG(s.ttft_ms),
                COUNT(s.ttft_ms),
                {SpeedSql}
            FROM spans s
            WHERE s.is_main = 1
              AND s.start_time_ms >= @from
              AND s.start_time_ms < @to{sourceFilter}
            GROUP BY s.session_id
            ORDER BY MAX(s.start_time_ms) DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

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
                FirstSeenMs = reader.GetInt64(14),
                AvgDurationMs = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                AvgTtftMs = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                TtftSamples = reader.GetInt64(17),
                TokensPerSecond = reader.IsDBNull(18) ? null : reader.GetDouble(18),
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
        long toMs = long.MaxValue) =>
        GetCalls(new CallQuery
        {
            FromMs = fromMs,
            ToMs = toMs,
            IncludeUtility = includeUtility,
            Limit = limit,
        });

    public IReadOnlyList<CallRow> GetCalls(CallQuery query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildCallWhere(command, query);

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
                nano_aiu,
                {SourceSql} AS source,
                end_time_ms,
                ttft_ms,
                trace_id,
                parent_span_id,
                error_type,
                agent_name,
                finish_reasons,
                subagent_name
            FROM spans
            WHERE {where}
            ORDER BY start_time_ms DESC
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, query.Limit));
        command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));

        return ReadCallRows(command);
    }

    public long CountCalls(CallQuery query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildCallWhere(command, query);
        command.CommandText = $"SELECT COUNT(*) FROM spans WHERE {where};";
        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
    }

    private static string BuildCallWhere(SqliteCommand command, CallQuery query)
    {
        var where = new List<string>
        {
            "start_time_ms >= @from",
            "start_time_ms < @to",
        };
        command.Parameters.AddWithValue("@from", query.FromMs);
        command.Parameters.AddWithValue("@to", query.ToMs);

        if (!query.IncludeUtility)
        {
            where.Add("is_main = 1");
        }

        switch (query.Kind)
        {
            case "main":
                where.Add("is_main = 1");
                break;
            case "utility":
                where.Add("is_main = 0");
                break;
        }

        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            where.Add("model = @model");
            command.Parameters.AddWithValue("@model", query.Model);
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            where.Add($"{SourceSql} = @source");
            command.Parameters.AddWithValue("@source", query.Source);
        }

        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Add("session_id = @session_id");
            command.Parameters.AddWithValue("@session_id", query.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var textClauses = new List<string>
            {
                "instr(lower(COALESCE(model, '')), lower(@query)) > 0",
                "instr(lower(COALESCE(session_id, '')), lower(@query)) > 0",
            };
            command.Parameters.AddWithValue("@query", query.Query.Trim());
            if (query.SessionIds is { Count: > 0 } sessionIds)
            {
                var parameters = new List<string>(sessionIds.Count);
                var index = 0;
                foreach (var sessionId in sessionIds)
                {
                    var parameter = $"@query_session_{index++}";
                    parameters.Add(parameter);
                    command.Parameters.AddWithValue(parameter, sessionId);
                }

                textClauses.Add(
                    $"session_id IN ({string.Join(", ", parameters)})");
            }

            where.Add($"({string.Join(" OR ", textClauses)})");
        }

        if (query.Priced is bool priced)
        {
            where.Add(priced ? "cost_cny IS NOT NULL" : "cost_cny IS NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.Period))
        {
            where.Add("pricing_period = @period");
            command.Parameters.AddWithValue("@period", query.Period);
        }

        if (query.HasError is bool hasError)
        {
            where.Add(hasError ? "error_type IS NOT NULL" : "error_type IS NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.Agent))
        {
            where.Add("agent_name = @agent");
            command.Parameters.AddWithValue("@agent", query.Agent);
        }

        return string.Join(" AND ", where);
    }

    public IReadOnlyList<CallRow> GetCallsForModel(
        string model,
        long fromMs,
        long toMs = long.MaxValue,
        int limit = 200) =>
        GetCalls(new CallQuery
        {
            Model = model,
            FromMs = fromMs,
            ToMs = toMs,
            IncludeUtility = true,
            Limit = limit,
        });

    public IReadOnlyList<CallRow> GetCallsForSession(
        string sessionId,
        long fromMs = 0,
        long toMs = long.MaxValue,
        int limit = int.MaxValue,
        string? source = null) =>
        GetCalls(new CallQuery
        {
            SessionId = sessionId,
            FromMs = fromMs,
            ToMs = toMs,
            IncludeUtility = true,
            Source = source,
            Limit = limit,
        });

    public IReadOnlyList<CallSample> GetSessionCallSamples(
        string sessionId,
        long fromMs,
        long toMs,
        string? source)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(model, '-'), {SourceSql} AS source,
                   input_tokens, cache_read_tokens, output_tokens, reasoning_tokens,
                   cost_cny, cache_cost_cny, uncached_cost_cny, output_cost_cny,
                   reasoning_cost_cny, ttft_ms, end_time_ms, error_type,
                   start_time_ms, is_main
            FROM spans
            WHERE session_id = @session_id
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

        var rows = new List<CallSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var startMs = reader.GetInt64(14);
            var endMs = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
            rows.Add(new CallSample(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                endMs > 0 && endMs > startMs ? endMs - startMs : null,
                reader.IsDBNull(13) ? null : reader.GetString(13),
                startMs,
                reader.GetInt64(15) != 0));
        }

        return rows;
    }

    private static IReadOnlyList<CallRow> ReadCallRows(SqliteCommand command)
    {
        var rows = new List<CallRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var startMs = reader.GetInt64(0);
            var endMs = reader.IsDBNull(17) ? (long?)null : reader.GetInt64(17);
            rows.Add(new CallRow
            {
                StartTimeMs = startMs,
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
                Source = reader.IsDBNull(16) ? null : reader.GetString(16),
                DurationMs = endMs is > 0 && endMs > startMs
                    ? endMs.Value - startMs
                    : null,
                TtftMs = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                TraceId = reader.GetString(19),
                ParentSpanId = reader.IsDBNull(20) ? null : reader.GetString(20),
                ErrorType = reader.IsDBNull(21) ? null : reader.GetString(21),
                AgentName = reader.IsDBNull(22) ? null : reader.GetString(22),
                FinishReasons = reader.IsDBNull(23) ? null : reader.GetString(23),
                SubagentName = reader.IsDBNull(24) ? null : reader.GetString(24),
            });
        }

        return rows;
    }

    public IReadOnlyList<SessionEventAggregate> GetSessionEventAggregates(
        long fromMs,
        long toMs,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_id, COUNT(*), MIN(start_time_ms), MAX(start_time_ms)
            FROM session_events
            WHERE session_id IS NOT NULL
              AND start_time_ms >= @from
              AND start_time_ms < @to{sourceFilter}
            GROUP BY session_id;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new List<SessionEventAggregate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SessionEventAggregate(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    /// <summary>
    /// Aggregates session events per operation inside the given time window.
    /// The window must be applied before grouping, otherwise operations used
    /// once inside the range report their lifetime totals.
    /// </summary>
    public IReadOnlyList<SessionEventRow> GetSessionEvents(
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
            SELECT
                operation,
                {SourceSql} AS source,
                COUNT(*),
                AVG(CASE WHEN end_time_ms > start_time_ms
                    THEN end_time_ms - start_time_ms END),
                COALESCE(SUM(is_error), 0),
                MAX(start_time_ms)
            FROM session_events
            WHERE session_id = @session_id
              AND start_time_ms >= @from
              AND start_time_ms < @to{sourceFilter}
            GROUP BY operation, {SourceSql}
            ORDER BY COUNT(*) DESC;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        var rows = new List<SessionEventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SessionEventRow
            {
                Operation = reader.GetString(0),
                Source = reader.GetString(1),
                Count = reader.GetInt64(2),
                AvgDurationMs = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                ErrorCount = reader.GetInt64(4),
                LastSeenMs = reader.GetInt64(5),
            });
        }

        return rows;
    }

    /// <summary>
    /// Aggregates tool calls per tool inside the given time window. The window
    /// must be applied before grouping: filtering the finished aggregates by
    /// their last-seen timestamp would report lifetime counts for any tool that
    /// happens to be used once inside the range.
    /// </summary>
    public IReadOnlyList<ToolCallRow> GetSessionTools(
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
            SELECT
                COALESCE(NULLIF(tool_name, ''), '-'),
                {SourceSql} AS source,
                tool_type,
                edit_type,
                skill_name,
                mcp_server_hash,
                mcp_tool_name,
                error_type,
                COALESCE(
                    agent_name,
                    (SELECT p.agent_name FROM session_events p
                     WHERE p.trace_id = session_events.trace_id
                       AND p.span_id = session_events.parent_span_id
                       AND p.agent_name IS NOT NULL
                     LIMIT 1)) AS agent_name,
                start_time_ms,
                end_time_ms,
                is_error
            FROM session_events
            WHERE session_id = @session_id
              AND operation = 'execute_tool'
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

        var accumulators = new Dictionary<string, ToolAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tool = reader.GetString(0);
            var toolSource = reader.GetString(1);
            var key = $"{toolSource}\u001f{tool}";
            if (!accumulators.TryGetValue(key, out var row))
            {
                row = new ToolAccumulator(tool, toolSource);
                accumulators[key] = row;
            }

            row.Add(
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.GetInt64(11) != 0);
        }

        return accumulators.Values
            .OrderByDescending(row => row.Calls)
            .ThenBy(row => row.Tool, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Build())
            .ToList();
    }

    public IReadOnlyList<ModelRow> GetModels(
        long fromMs,
        long toMs = long.MaxValue,
        bool includeUtility = false,
        string? source = null)
    {
        var utilityFilter = includeUtility ? string.Empty : " AND is_main = 1";
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                model,
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_read_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(reasoning_tokens), 0),
                COALESCE(SUM(cost_cny), 0),
                COALESCE(SUM(cache_cost_cny), 0),
                COALESCE(SUM(uncached_cost_cny), 0),
                COALESCE(SUM(output_cost_cny), 0),
                COALESCE(SUM(reasoning_cost_cny), 0),
                COALESCE(SUM(CASE WHEN cost_cny IS NULL THEN 1 ELSE 0 END), 0),
                AVG(ttft_ms),
                COUNT(ttft_ms),
                {SpeedSql}
            FROM spans
            WHERE start_time_ms >= @from
              AND start_time_ms < @to
              AND model IS NOT NULL{utilityFilter}{sourceFilter}
            GROUP BY model
            ORDER BY COUNT(*) DESC;
            """;
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

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
                ReasoningTokens = reader.GetInt64(5),
                CostCny = reader.GetDouble(6),
                CacheCostCny = reader.GetDouble(7),
                UncachedCostCny = reader.GetDouble(8),
                OutputCostCny = reader.GetDouble(9),
                ReasoningCostCny = reader.GetDouble(10),
                UnpricedCalls = reader.GetInt64(11),
                AvgTtftMs = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                TtftSamples = reader.GetInt64(13),
                TokensPerSecond = reader.IsDBNull(14) ? null : reader.GetDouble(14),
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
        bool includeUtility = false,
        string? source = null)
    {
        var utilityFilter = includeUtility ? string.Empty : " AND is_main = 1";
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
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
                COALESCE(SUM(CASE WHEN cost_cny IS NULL THEN 1 ELSE 0 END), 0),
                AVG(ttft_ms),
                COUNT(ttft_ms),
                {SpeedSql},
                MIN(CASE WHEN error_type IS NULL AND ttft_ms > 0
                    THEN ttft_ms END),
                MAX(CASE WHEN error_type IS NULL AND ttft_ms > 0
                    THEN ttft_ms END),
                MIN(CASE WHEN error_type IS NULL
                    AND end_time_ms > start_time_ms
                    AND ttft_ms IS NOT NULL
                    AND output_tokens >= 5
                    AND end_time_ms - start_time_ms - ttft_ms >= 50
                    THEN output_tokens * 1000.0 /
                        (end_time_ms - start_time_ms - ttft_ms) END),
                MAX(CASE WHEN error_type IS NULL
                    AND end_time_ms > start_time_ms
                    AND ttft_ms IS NOT NULL
                    AND output_tokens >= 5
                    AND end_time_ms - start_time_ms - ttft_ms >= 50
                    THEN output_tokens * 1000.0 /
                        (end_time_ms - start_time_ms - ttft_ms) END)
            FROM spans
            WHERE model = @model
              AND start_time_ms >= @from
              AND start_time_ms < @to{utilityFilter}{sourceFilter};
            """;
        command.Parameters.AddWithValue("@model", model);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

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
            AvgTtftMs = reader.IsDBNull(12) ? null : reader.GetDouble(12),
            TtftSamples = reader.GetInt64(13),
            TokensPerSecond = reader.IsDBNull(14) ? null : reader.GetDouble(14),
            MinTtftMs = reader.IsDBNull(15) ? null : reader.GetDouble(15),
            MaxTtftMs = reader.IsDBNull(16) ? null : reader.GetDouble(16),
            MinTokensPerSecond =
                reader.IsDBNull(17) ? null : reader.GetDouble(17),
            MaxTokensPerSecond =
                reader.IsDBNull(18) ? null : reader.GetDouble(18),
        };
    }

    public IReadOnlyList<SessionRow> GetSessionsForModel(
        string model,
        long fromMs,
        int limit = 50,
        long toMs = long.MaxValue,
        string? source = null)
    {
        var sourceFilter = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : $" AND {SourceSql} = @source";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
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
                COALESCE(SUM(s.reasoning_cost_cny), 0),
                MIN(s.start_time_ms),
                AVG(CASE WHEN s.end_time_ms > s.start_time_ms
                    THEN s.end_time_ms - s.start_time_ms END),
                AVG(s.ttft_ms),
                COUNT(s.ttft_ms),
                {SpeedSql}
            FROM spans s
            WHERE s.is_main = 1
              AND s.model = @model
              AND s.start_time_ms >= @from
              AND s.start_time_ms < @to{sourceFilter}
            GROUP BY s.session_id
            ORDER BY MAX(s.start_time_ms) DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@model", model);
        command.Parameters.AddWithValue("@from", fromMs);
        command.Parameters.AddWithValue("@to", toMs);
        command.Parameters.AddWithValue("@limit", limit);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

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
                FirstSeenMs = reader.GetInt64(14),
                AvgDurationMs = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                AvgTtftMs = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                TtftSamples = reader.GetInt64(17),
                TokensPerSecond = reader.IsDBNull(18) ? null : reader.GetDouble(18),
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
              AND COALESCE(otel_source, '') <> 'combined'
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

    public IReadOnlyDictionary<string, long> GetModelCallCounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, COUNT(*)
            FROM spans
            WHERE model IS NOT NULL
              AND COALESCE(otel_source, '') <> 'combined'
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

    public IReadOnlyList<StoredModelProfile> GetModelProfiles()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, provider, icon_kind, icon_letter, icon_color,
                   icon_image, updated_at_ms
            FROM model_profiles;
            """;

        var rows = new List<StoredModelProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredModelProfile(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6)));
        }

        return rows;
    }

    public void UpsertModelProfile(StoredModelProfile profile)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_profiles (
                    model, provider, icon_kind, icon_letter, icon_color,
                    icon_image, updated_at_ms
                ) VALUES (
                    @model, @provider, @icon_kind, @icon_letter, @icon_color,
                    @icon_image, @updated_at_ms
                )
                ON CONFLICT(model) DO UPDATE SET
                    provider = excluded.provider,
                    icon_kind = excluded.icon_kind,
                    icon_letter = excluded.icon_letter,
                    icon_color = excluded.icon_color,
                    icon_image = excluded.icon_image,
                    updated_at_ms = excluded.updated_at_ms;
                """;
            command.Parameters.AddWithValue("@model", profile.Model);
            command.Parameters.AddWithValue(
                "@provider",
                (object?)profile.Provider ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@icon_kind",
                (object?)profile.IconKind ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@icon_letter",
                (object?)profile.IconLetter ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@icon_color",
                (object?)profile.IconColor ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@icon_image",
                (object?)profile.IconImage ?? DBNull.Value);
            command.Parameters.AddWithValue("@updated_at_ms", profile.UpdatedAtMs);
            command.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool DeleteModelProfile(string model)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM model_profiles WHERE model = @model;";
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
                long EndMs,
                long Input,
                long Cached,
                long CacheCreation,
                long Output,
                long Reasoning)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"""
                    SELECT model, trace_id, span_id, start_time_ms, end_time_ms,
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
                        reader.IsDBNull(4) ? reader.GetInt64(3) : reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9)));
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
                // End-time peak rule; EndMs already falls back to StartMs above.
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(span.EndMs);
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

    public void UpsertSessionWorkspaces(IReadOnlyDictionary<string, string> workspaces)
    {
        if (workspaces.Count == 0)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO session_workspaces (
                    session_id, workspace_path, updated_at_ms
                ) VALUES (@session, @path, @updated);
                """;
            AddParameter(command, "@session", SqliteType.Text);
            AddParameter(command, "@path", SqliteType.Text);
            AddParameter(command, "@updated", SqliteType.Integer);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var entry in workspaces)
            {
                command.Parameters["@session"].Value = entry.Key;
                command.Parameters["@path"].Value = entry.Value;
                command.Parameters["@updated"].Value = nowMs;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public string? GetSessionWorkspace(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_path FROM session_workspaces WHERE session_id = @session;
            """;
        command.Parameters.AddWithValue("@session", sessionId);
        return command.ExecuteScalar() as string;
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
            var removed = command.ExecuteNonQuery();

            using var events = connection.CreateCommand();
            events.CommandText =
                "DELETE FROM session_events WHERE start_time_ms < @cutoff;";
            events.Parameters.AddWithValue("@cutoff", cutoffMs);
            events.ExecuteNonQuery();

            using var agentRuns = connection.CreateCommand();
            agentRuns.CommandText =
                "DELETE FROM agent_runs WHERE start_time_ms < @cutoff;";
            agentRuns.Parameters.AddWithValue("@cutoff", cutoffMs);
            agentRuns.ExecuteNonQuery();

            using var metricPoints = connection.CreateCommand();
            metricPoints.CommandText =
                "DELETE FROM metric_points WHERE end_time_ms < @cutoff;";
            metricPoints.Parameters.AddWithValue("@cutoff", cutoffMs);
            metricPoints.ExecuteNonQuery();

            using var windowLinks = connection.CreateCommand();
            windowLinks.CommandText =
                "DELETE FROM window_session_links WHERE last_seen_ms < @cutoff;";
            windowLinks.Parameters.AddWithValue("@cutoff", cutoffMs);
            windowLinks.ExecuteNonQuery();

            using var sessionWorkspaces = connection.CreateCommand();
            sessionWorkspaces.CommandText = """
                DELETE FROM session_workspaces
                WHERE session_id NOT IN (
                    SELECT DISTINCT session_id FROM spans WHERE session_id IS NOT NULL);
                """;
            sessionWorkspaces.ExecuteNonQuery();

            _combinedAllDirty = true;
            _combinedDirtySessions.Clear();
            _combinedDirty = true;
            return removed;
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
        // Pooled private caches would otherwise reserve SQLite's default 2 MB
        // each; 1 MB is plenty for the small tables this server keeps.
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size = -1024;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void AddParameter(
        SqliteCommand command,
        string name,
        SqliteType type) =>
        command.Parameters.Add(name, type);

    private sealed class ToolAccumulator
    {
        private readonly HashSet<string> _toolTypes = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _editTypes = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skillNames = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mcpServerHashes = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mcpToolNames = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _errorTypes = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _agentNames = new(
            StringComparer.OrdinalIgnoreCase);
        private double _totalDurationMs;
        private long _durationSamples;
        private double? _maxDurationMs;
        private readonly List<double> _durations = new();

        public ToolAccumulator(string tool, string source)
        {
            Tool = tool;
            Source = source;
        }

        public string Tool { get; }
        public string Source { get; }
        public long Calls { get; private set; }
        public long ErrorCount { get; private set; }
        public long LastSeenMs { get; private set; }

        public void Add(
            string? toolType,
            string? editType,
            string? skillName,
            string? mcpServerHash,
            string? mcpToolName,
            string? errorType,
            string? agentName,
            long startMs,
            long? endMs,
            bool isError)
        {
            Calls++;
            LastSeenMs = Math.Max(LastSeenMs, startMs);
            if (endMs is > 0 && endMs.Value > startMs)
            {
                var duration = (double)(endMs.Value - startMs);
                _totalDurationMs += duration;
                _durationSamples++;
                _durations.Add(duration);
                _maxDurationMs = Math.Max(_maxDurationMs ?? 0, duration);
            }

            if (isError)
            {
                ErrorCount++;
            }

            AddValue(_toolTypes, toolType);
            AddValue(_editTypes, editType);
            AddValue(_skillNames, skillName);
            AddValue(_mcpServerHashes, mcpServerHash);
            AddValue(_mcpToolNames, mcpToolName);
            AddValue(_errorTypes, errorType);
            AddValue(_agentNames, agentName);
        }

        public ToolCallRow Build() => new()
        {
            Tool = Tool,
            Source = Source,
            Calls = Calls,
            AvgDurationMs = _durationSamples > 0
                ? _totalDurationMs / _durationSamples
                : null,
            MedianDurationMs = Median(),
            MaxDurationMs = _maxDurationMs,
            ErrorCount = ErrorCount,
            LastSeenMs = LastSeenMs,
            ToolTypes = Sorted(_toolTypes),
            EditTypes = Sorted(_editTypes),
            SkillNames = Sorted(_skillNames),
            McpServerHashes = Sorted(_mcpServerHashes),
            McpToolNames = Sorted(_mcpToolNames),
            ErrorTypes = Sorted(_errorTypes),
            AgentNames = Sorted(_agentNames),
        };

        private double? Median()
        {
            if (_durations.Count == 0)
            {
                return null;
            }

            _durations.Sort();
            var middle = _durations.Count / 2;
            return _durations.Count % 2 == 0
                ? (_durations[middle - 1] + _durations[middle]) / 2
                : _durations[middle];
        }

        private static void AddValue(HashSet<string> values, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        private static List<string> Sorted(HashSet<string> values) =>
            values
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public int ResolveSubagentSessions()
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var changed = 0;
            for (var pass = 0; pass < 6; pass++)
            {
                var updated = ResolveSessionIds(
                    connection,
                    transaction,
                    "spans");
                updated += ResolveSessionIds(
                    connection,
                    transaction,
                    "session_events");
                updated += ResolveSessionIds(
                    connection,
                    transaction,
                    "agent_runs");
                changed += updated;
                if (updated == 0)
                {
                    break;
                }
            }

            transaction.Commit();
            _combinedAllDirty = true;
            _combinedDirtySessions.Clear();
            _combinedDirty = true;
            return changed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int ResolveSessionIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyCollection<string>? traceIds = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var traceFilter = string.Empty;
        if (traceIds is { Count: > 0 })
        {
            var parameters = new List<string>(traceIds.Count);
            var index = 0;
            foreach (var traceId in traceIds)
            {
                var parameter = $"@resolve_trace_{index++}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, traceId);
            }

            traceFilter =
                $" AND {table}.trace_id IN ({string.Join(", ", parameters)})";
        }

        command.CommandText = $"""
            UPDATE {table}
            SET session_id = COALESCE(
                (SELECT p.session_id FROM spans p
                 WHERE p.trace_id = {table}.trace_id
                   AND p.span_id = {table}.parent_span_id
                   AND p.session_id IS NOT NULL
                   AND lower(substr(p.session_id, 1, 5)) <> 'call_'
                 LIMIT 1),
                (SELECT e.session_id FROM session_events e
                 WHERE e.trace_id = {table}.trace_id
                   AND e.span_id = {table}.parent_span_id
                   AND e.session_id IS NOT NULL
                   AND lower(substr(e.session_id, 1, 5)) <> 'call_'
                 LIMIT 1)
            )
            WHERE ({table}.session_id IS NULL
                   OR lower(substr({table}.session_id, 1, 5)) = 'call_'){traceFilter}
              AND COALESCE(
                (SELECT p.session_id FROM spans p
                 WHERE p.trace_id = {table}.trace_id
                   AND p.span_id = {table}.parent_span_id
                   AND p.session_id IS NOT NULL
                   AND lower(substr(p.session_id, 1, 5)) <> 'call_'
                 LIMIT 1),
                (SELECT e.session_id FROM session_events e
                 WHERE e.trace_id = {table}.trace_id
                   AND e.span_id = {table}.parent_span_id
                   AND e.session_id IS NOT NULL
                   AND lower(substr(e.session_id, 1, 5)) <> 'call_'
                 LIMIT 1)
              ) IS NOT NULL;
            """;
        return command.ExecuteNonQuery();
    }

    private static int ResolveSessionIdsForTraces(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> traceIds)
    {
        var changed = 0;
        for (var pass = 0; pass < 6; pass++)
        {
            var updated = ResolveSessionIds(
                connection,
                transaction,
                "spans",
                traceIds);
            updated += ResolveSessionIds(
                connection,
                transaction,
                "session_events",
                traceIds);
            updated += ResolveSessionIds(
                connection,
                transaction,
                "agent_runs",
                traceIds);
            changed += updated;
            if (updated == 0)
            {
                break;
            }
        }

        return changed;
    }

    private static int UpdateSubagentNames(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string>? traceIds = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var traceFilter = string.Empty;
        if (traceIds is { Count: > 0 })
        {
            var parameters = new List<string>(traceIds.Count);
            var index = 0;
            foreach (var traceId in traceIds)
            {
                var parameter = $"@name_trace_{index++}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, traceId);
            }

            traceFilter = $" AND spans.trace_id IN ({string.Join(", ", parameters)})";
        }

        command.CommandText = $"""
            UPDATE spans
            SET subagent_name = (
                SELECT e.agent_name FROM session_events e
                WHERE e.trace_id = spans.trace_id
                  AND e.span_id = spans.parent_span_id
                  AND e.operation = 'invoke_agent'
                  AND e.parent_span_id IS NOT NULL
                  AND e.agent_name IS NOT NULL
                LIMIT 1
            )
            WHERE spans.parent_span_id IS NOT NULL
              AND spans.subagent_name IS NULL{traceFilter};
            """;
        return command.ExecuteNonQuery();
    }

    public int BackfillSubagentNames()
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "UPDATE spans SET subagent_name = NULL;";
                clear.ExecuteNonQuery();
            }

            var changed = UpdateSubagentNames(
                connection,
                transaction,
                traceIds: null);
            transaction.Commit();
            if (changed > 0)
            {
                _combinedAllDirty = true;
                _combinedDirtySessions.Clear();
                _combinedDirty = true;
            }

            return changed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void UpsertWindowSessionLinks(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<(
            string WindowSessionId,
            string SessionId,
            string? ServiceName,
            long StartTimeMs,
            long? EndTimeMs)> links)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO window_session_links (
                window_session_id, chat_session_id, service_name,
                first_seen_ms, last_seen_ms
            ) VALUES (
                @window, @session, @service, @first, @last
            )
            ON CONFLICT(window_session_id, chat_session_id) DO UPDATE SET
                service_name = COALESCE(
                    window_session_links.service_name,
                    excluded.service_name),
                first_seen_ms = MIN(
                    window_session_links.first_seen_ms,
                    excluded.first_seen_ms),
                last_seen_ms = MAX(
                    window_session_links.last_seen_ms,
                    excluded.last_seen_ms);
            """;
        command.Parameters.AddWithValue("@window", "");
        command.Parameters.AddWithValue("@session", "");
        command.Parameters.AddWithValue("@service", DBNull.Value);
        command.Parameters.AddWithValue("@first", 0L);
        command.Parameters.AddWithValue("@last", 0L);
        foreach (var link in links)
        {
            var end = link.EndTimeMs ?? link.StartTimeMs;
            command.Parameters["@window"].Value = link.WindowSessionId;
            command.Parameters["@session"].Value = link.SessionId;
            command.Parameters["@service"].Value =
                (object?)link.ServiceName ?? DBNull.Value;
            command.Parameters["@first"].Value = link.StartTimeMs;
            command.Parameters["@last"].Value = end;
            command.ExecuteNonQuery();
        }
    }

    private static bool NeedsSessionResolution(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ||
        sessionId.TrimStart().StartsWith("call_", StringComparison.OrdinalIgnoreCase);

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
