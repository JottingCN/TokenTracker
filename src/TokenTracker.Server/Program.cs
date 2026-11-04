using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Pricing;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;
using TokenTracker.Server;

var portText = Environment.GetEnvironmentVariable("TOKENTRACKER_PORT");
var port = int.TryParse(portText, out var parsedPort) ? parsedPort : 4318;

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "TokenTracker");
}
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

var startedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
var buildId = Environment.GetEnvironmentVariable("TOKENTRACKER_BUILD_ID");
var serverInfo = new ServerInfoPayload(
    "TokenTracker.Server",
    "1.0.11",
    1,
    port,
    startedAt,
    string.IsNullOrWhiteSpace(buildId) ? null : buildId,
    ["dashboard", "calls", "events", "tools", "prices", "settings", "holidays", "breakdown", "source-paths", "source-copilot-chat", "otel-metrics", "agent-runs", "trace-tree", "otel-metrics-store", "ws"]);

var dataDirectory = Environment.GetEnvironmentVariable("TOKENTRACKER_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    dataDirectory = Path.Combine(DefaultDataRoot(), "TokenTracker");
}

Directory.CreateDirectory(dataDirectory);
var captureOtelMetrics = string.Equals(
    Environment.GetEnvironmentVariable("TOKENTRACKER_CAPTURE_OTEL_METRICS"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var otelMetricCaptureDirectory = Path.Combine(dataDirectory, "otel-metrics");
var otelCaptureLock = new object();
var store = new SpanStore(Path.Combine(dataDirectory, "tokentracker.db"));

const string retentionKey = "retention_days";
var defaultRetentionDays = 90;
if (int.TryParse(
        Environment.GetEnvironmentVariable("TOKENTRACKER_RETENTION_DAYS"),
        out var configuredRetentionDays) &&
    configuredRetentionDays >= 0)
{
    defaultRetentionDays = Math.Min(
        configuredRetentionDays,
        ServerSettings.MaxRetentionDays);
}

var storedRetentionDays = store.GetSetting(retentionKey);
var retentionDays = int.TryParse(storedRetentionDays, out var parsedRetentionDays) &&
                    parsedRetentionDays >= 0
    ? Math.Min(parsedRetentionDays, ServerSettings.MaxRetentionDays)
    : defaultRetentionDays;
var settings = new ServerSettings(retentionDays);
try
{
    settings.ApplyRetention(store);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"retention cleanup failed: {exception.Message}");
}

var sessionStateDirectory =
    Environment.GetEnvironmentVariable("TOKENTRACKER_SESSION_STATE_DIR");
var metadata = new SessionMetadataProvider(
    string.IsNullOrWhiteSpace(sessionStateDirectory) ? null : sessionStateDirectory);
var counters = new ReceiverCounters();
var endpoint = $"http://127.0.0.1:{port}";
var service = new DashboardService(store, metadata, counters, settings, endpoint);
service.ApplyOverrides();

if (store.GetSetting("storage_slim_v1") != "1")
{
    var removed = store.SlimStorage();
    store.SetSetting("storage_slim_v1", "1");
    Console.WriteLine($"slimmed storage: {removed} rows removed");
}

if (store.GetSetting("subagent_sessions_v1") != "1")
{
    var resolved = store.ResolveSubagentSessions();
    store.SetSetting("subagent_sessions_v1", "1");
    Console.WriteLine($"resolved subagent sessions for {resolved} rows");
}

if (store.GetSetting("subagent_names_v2") != "1")
{
    var named = store.BackfillSubagentNames();
    store.SetSetting("subagent_names_v2", "1");
    Console.WriteLine($"backfilled subagent names for {named} spans");
}

if (store.GetSetting("metric_attribution_v1") != "1")
{
    var attributed = store.ReconcileMetricAttribution();
    store.SetSetting("metric_attribution_v1", "1");
    Console.WriteLine($"attributed {attributed} metric points to sessions");
}

var hub = new RealtimeHub();
metadata.AhpStatusChanged += hub.NotifyChanged;
var idleExitSeconds = 0;
if (int.TryParse(
        Environment.GetEnvironmentVariable("TOKENTRACKER_IDLE_EXIT_SECONDS"),
        out var configuredIdleSeconds) &&
    configuredIdleSeconds > 0)
{
    idleExitSeconds = configuredIdleSeconds;
}

var holidayService = new HolidayService(
    store,
    urlTemplate: Environment.GetEnvironmentVariable("TOKENTRACKER_HOLIDAY_API_URL"));
var beijingNow = BeijingCalendar.ToBeijing(DateTimeOffset.UtcNow);
var holidayYears = new HashSet<int> { beijingNow.Year, beijingNow.Year + 1 };
foreach (var year in store.GetSpanYears())
{
    if (year >= 2000 && year <= beijingNow.Year + 2)
    {
        holidayYears.Add(year);
    }
}

using (var holidayTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
{
    await holidayService.EnsureYearsAsync(holidayYears, holidayTimeout.Token);
}

var backfilled = store.BackfillCostBreakdown();
if (backfilled > 0)
{
    Console.WriteLine($"backfilled cost breakdown for {backfilled} spans");
}

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromHours(12));
        var year = BeijingCalendar.ToBeijing(DateTimeOffset.UtcNow).Year;
        await holidayService.EnsureYearsAsync(new[] { year, year + 1 });
    }
});

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromHours(6));
        try
        {
            if (settings.ApplyRetention(store) > 0)
            {
                hub.NotifyChanged();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"retention cleanup failed: {exception.Message}");
        }
    }
});

if (idleExitSeconds > 0)
{
    _ = Task.Run(async () =>
    {
        var idleSince = DateTime.UtcNow;
        var hadClients = false;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idleExitSeconds)));
            if (hub.ActiveConnections > 0)
            {
                hadClients = true;
                idleSince = DateTime.UtcNow;
                continue;
            }

            if (hadClients)
            {
                // Start the grace period when the last client disconnects,
                // so reloading a VS Code window does not kill the server.
                hadClients = false;
                idleSince = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - idleSince).TotalSeconds >= idleExitSeconds)
            {
                Console.WriteLine(
                    $"no active clients for {idleExitSeconds}s; shutting down");
                app.Lifetime.StopApplication();
                return;
            }
        }
    });
}

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/api/version", () =>
    Results.Json(serverInfo, AppJsonContext.Default.ServerInfoPayload));

app.MapGet("/api/system", () =>
{
    var ahp = metadata.GetAhpStatus();
    var lastReceived = store.GetLastReceivedAtMs();
    var access = metadata.GetAccessInfo();
    var watchedPaths = new List<SystemPathInfo>();

    void AddPath(string key, string? path, string kind, string mode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            watchedPaths.Add(new SystemPathInfo(
                key,
                "-",
                kind,
                false,
                null,
                "unused"));
            return;
        }

        var exists = kind == "directory"
            ? Directory.Exists(path)
            : File.Exists(path);
        string? modified = null;
        if (exists)
        {
            try
            {
                modified = (kind == "directory"
                        ? Directory.GetLastWriteTime(path)
                        : File.GetLastWriteTime(path))
                    .ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        watchedPaths.Add(new SystemPathInfo(
            key,
            path,
            kind,
            exists,
            modified,
            exists ? MonitorKey(mode) : "unused"));
    }

    string MonitorKey(string mode) => mode switch
    {
        "watch" => ahp.Connected ? "paused-ahp" : "watching",
        "poll" => "polling",
        "import" => "import-only",
        _ => "on-query",
    };

    AddPath(
        "session-state",
        access.SessionRoot,
        "directory",
        "watch");
    AddPath(
        "agent-host-db",
        access.AgentHostDatabase,
        "file",
        "watch");
    foreach (var directory in access.CopilotUserDataDirectories)
    {
        AddPath(
            "chat-title-index",
            Path.Combine(directory, "globalStorage", "state.vscdb"),
            "file",
            "read");
        AddPath(
            "workspace-title-index",
            Path.Combine(directory, "workspaceStorage"),
            "directory",
            "read");
        AddPath(
            "copilot-trace-db",
            Path.Combine(
                directory,
                "globalStorage",
                "github.copilot-chat",
                "agent-traces.db"),
            "file",
            "import");
    }

    foreach (var directory in access.AhpUserDataDirectories)
    {
        AddPath(
            "ahp-endpoint-dir",
            Path.Combine(
                directory,
                "agent-host",
                "local-endpoint",
                "entries"),
            "directory",
            "poll");
        AddPath(
            "agent-host-trace-db",
            Path.Combine(directory, "agent-host", "otel", "agent-host-traces.db"),
            "file",
            "import");
    }

    var holidayApi =
        Environment.GetEnvironmentVariable("TOKENTRACKER_HOLIDAY_API_URL") ??
        HolidayService.DefaultUrlTemplate;
    var accesses = new List<SystemAccessInfo>
    {
        new("holiday-api", holidayApi, "http"),
        new("otlp-endpoint", endpoint, "http"),
        new("ahp-title-sync", "named pipe / UDS / TCP", "ipc"),
    };

    return Results.Json(
        new SystemInfoPayload(
            serverInfo,
            store.GetStorageStats(),
            counters.Snapshot(endpoint, running: true),
            settings.RetentionDays,
            dataDirectory,
            lastReceived > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastReceived)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss")
                : "-",
            new SystemAhpInfo(ahp.State, ahp.Connected, ahp.Sessions),
            watchedPaths,
            accesses),
        AppJsonContext.Default.SystemInfoPayload);
});

app.MapGet("/api/dashboard", (
    int? range,
    bool? utility,
    long? from,
    long? to,
    string? source) =>
    Results.Json(
        from is > 0 && to > from
            ? service.GetDashboard(from.Value, to.Value, utility ?? false, source)
            : service.GetDashboard(range ?? 2, utility ?? false, source),
        AppJsonContext.Default.DashboardPayload));

app.MapGet("/api/model/{model}", (
    string model,
    int? range,
    long? from,
    long? to,
    string? source) =>
    Results.Json(
        from is > 0 && to > from
            ? service.GetModel(model, from.Value, to.Value, source)
            : service.GetModel(model, range ?? 2, source),
        AppJsonContext.Default.ModelPayload));

app.MapGet("/api/session/{sessionId}", (string sessionId, string? source) =>
    Results.Json(
        service.GetSession(sessionId, source),
        AppJsonContext.Default.SessionPayload));

app.MapGet("/api/session/{sessionId}/trace", (
    string sessionId,
    string? source) =>
    Results.Json(
        service.GetTrace(sessionId, source),
        AppJsonContext.Default.TracePayload));

app.MapGet("/api/calls", (
    int? range,
    bool? utility,
    long? from,
    long? to,
    string? model,
    string? source,
    string? query,
    string? kind,
    bool? priced,
    string? period,
    bool? hasError,
    string? agent,
    int? offset,
    int? limit) =>
    Results.Json(
        from is > 0 && to > from
            ? service.GetCalls(
                from.Value,
                to.Value,
                utility ?? true,
                model,
                source,
                query,
                kind,
                priced,
                period,
                hasError,
                agent,
                offset ?? 0,
                limit ?? 50)
            : service.GetCalls(
                range ?? 2,
                utility ?? true,
                model,
                source,
                query,
                kind,
                priced,
                period,
                hasError,
                agent,
                offset ?? 0,
                limit ?? 50),
        AppJsonContext.Default.CallsPayload));

app.MapGet("/api/prices", () =>
    Results.Json(
        service.GetPrices(),
        AppJsonContext.Default.ListModelPriceRowPayload));

app.MapPost("/api/prices", (PriceUpdateRequest request) =>
{
    var row = service.SavePrice(request);
    if (row is not null)
    {
        hub.NotifyChanged();
    }

    return row is null
        ? Results.BadRequest("invalid model or price")
        : Results.Json(row, AppJsonContext.Default.ModelPriceRowPayload);
});

app.MapDelete("/api/prices", (string model) =>
{
    service.DeletePrice(model);
    hub.NotifyChanged();
    return Results.NoContent();
});

app.MapPost("/api/metadata-changed", () =>
{
    hub.NotifyChanged();
    return Results.NoContent();
});

app.MapGet("/api/settings", () =>
    Results.Json(
        new SettingsPayload(settings.RetentionDays),
        AppJsonContext.Default.SettingsPayload));

app.MapPost("/api/settings", (SettingsUpdateRequest request) =>
{
    if (request.RetentionDays is not int days ||
        days < 0 ||
        days > ServerSettings.MaxRetentionDays)
    {
        return Results.BadRequest("invalid retention days");
    }

    settings.SetRetentionDays(days);
    store.SetSetting("retention_days", days.ToString());
    try
    {
        settings.ApplyRetention(store);
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message);
    }

    hub.NotifyChanged();
    return Results.Json(
        new SettingsPayload(days),
        AppJsonContext.Default.SettingsPayload);
});

app.MapPost("/api/import-local", (LocalImportRequest request) =>
{
    var result = LocalTraceImporter.Import(store, request.Paths ?? []);
    if (result.Inserted > 0)
    {
        hub.NotifyChanged();
    }

    return Results.Json(
        new LocalImportPayload(
            result.Scanned,
            result.Matched,
            result.Inserted,
            result.Skipped,
            result.EventsMatched,
            result.EventsInserted,
            result.Errors.ToArray()),
        AppJsonContext.Default.LocalImportPayload);
});

app.MapPost("/v1/traces", async (HttpContext context) =>
    await IngestTraces(context, store, counters, hub, sourceOverride: null));

app.MapPost("/otlp/{source}", async (HttpContext context, string source) =>
{
    var normalized = NormalizeIngestSource(source);
    if (normalized is null)
    {
        return Results.NotFound();
    }

    return await IngestTraces(context, store, counters, hub, normalized);
});

app.MapPost("/otlp/{source}/v1/traces", async (HttpContext context, string source) =>
{
    var normalized = NormalizeIngestSource(source);
    if (normalized is null)
    {
        return Results.NotFound();
    }

    return await IngestTraces(context, store, counters, hub, normalized);
});

static string? NormalizeIngestSource(string source) =>
    source.Trim().ToLowerInvariant() switch
    {
        "copilot-chat" or "extension-host" => "copilot-chat",
        "agent-host" => "agent-host",
        _ => null,
    };

static async Task<IResult> IngestTraces(
    HttpContext context,
    SpanStore store,
    ReceiverCounters counters,
    RealtimeHub hub,
    string? sourceOverride)
{
    try
    {
        var document = await ReadJsonAsync(context.Request);
        if (document is null)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var parsed = OtlpTraceParser.Parse(
                document,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sourceOverride);
            if (parsed.Spans.Count > 0 ||
                parsed.Events.Count > 0 ||
                parsed.AgentRuns.Count > 0)
            {
                store.InsertMany(parsed.Spans);
                store.InsertEvents(parsed.Events);
                store.InsertAgentRuns(parsed.AgentRuns);
                counters.Record(
                    parsed.Spans.Count +
                    parsed.Events.Count +
                    parsed.AgentRuns.Count);
                hub.NotifyChanged();
            }
        }

        return Results.Ok();
    }
    catch (Exception exception)
    {
        counters.RecordError(exception.Message);
        return Results.Problem(exception.Message);
    }
}

static async Task<IResult> IngestOtelMetrics(
    HttpContext context,
    string? sourceOverride,
    bool captureEnabled,
    string captureDirectory,
    object captureLock,
    SpanStore store,
    ReceiverCounters counters,
    RealtimeHub hub)
{
    try
    {
        var document = await ReadJsonAsync(context.Request);
        if (document is null)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var receivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var parsed = OtlpMetricParser.Parse(
                document,
                receivedAtMs,
                sourceOverride);
            var inserted = parsed.Count > 0
                ? store.InsertMetricPoints(parsed)
                : 0;
            if (inserted > 0)
            {
                counters.Record(inserted);
                hub.NotifyChanged();
            }

            if (captureEnabled)
            {
                var source = sourceOverride ??
                    parsed.FirstOrDefault()?.Source ??
                    "direct";
                var raw = document.RootElement.GetRawText();
                Directory.CreateDirectory(captureDirectory);
                var fileName =
                    $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{source}-{Guid.NewGuid():N}.json";
                await File.WriteAllTextAsync(
                    Path.Combine(captureDirectory, fileName),
                    raw);
                TrimCapturedSignals(captureDirectory);
                var line =
                    $"{DateTimeOffset.UtcNow:O} source={source} " +
                    $"points={parsed.Count} bytes={raw.Length} " +
                    $"file={fileName}";
                lock (captureLock)
                {
                    File.AppendAllText(
                        Path.Combine(captureDirectory, "capture.log"),
                        line + Environment.NewLine);
                }

                Console.WriteLine($"OTLP metrics captured: {line}");
            }
        }

        return Results.Ok();
    }
    catch (Exception exception)
    {
        counters.RecordError(exception.Message);
        Console.Error.WriteLine(
            $"OTLP metrics ingestion failed: {exception.Message}");
        return Results.Problem(exception.Message);
    }
}

static void TrimCapturedSignals(string captureDirectory)
{
    try
    {
        foreach (var file in Directory
                     .GetFiles(captureDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(50))
        {
            File.Delete(file);
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"OTLP logs cleanup failed: {exception.Message}");
    }
}

app.MapPost("/v1/metrics", async (HttpContext context) =>
    await IngestOtelMetrics(
        context,
        sourceOverride: null,
        captureOtelMetrics,
        otelMetricCaptureDirectory,
        otelCaptureLock,
        store,
        counters,
        hub));

app.MapPost("/otlp/{source}/v1/metrics", async (HttpContext context, string source) =>
{
    var normalized = NormalizeIngestSource(source);
    if (normalized is null)
    {
        return Results.NotFound();
    }

    return await IngestOtelMetrics(
        context,
        normalized,
        captureOtelMetrics,
        otelMetricCaptureDirectory,
        otelCaptureLock,
        store,
        counters,
        hub);
});

app.MapGet("/ws", (HttpContext context) =>
    WsEndpoint.HandleAsync(context, service, hub));

app.MapFallbackToFile("index.html");

app.Run();

static async Task<JsonDocument?> ReadJsonAsync(HttpRequest request)
{
    Stream body = request.Body;
    GZipStream? gzip = null;
    if (request.Headers.ContentEncoding.ToString()
        .Contains("gzip", StringComparison.OrdinalIgnoreCase))
    {
        gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true);
        body = gzip;
    }

    try
    {
        return await JsonDocument.ParseAsync(body);
    }
    finally
    {
        gzip?.Dispose();
    }
}

static string DefaultDataRoot()
{
    if (OperatingSystem.IsMacOS())
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support");
    }

    var localAppData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);
    return string.IsNullOrWhiteSpace(localAppData)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : localAppData;
}
