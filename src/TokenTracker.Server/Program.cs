using System.Net;
using Microsoft.Extensions.Hosting;
using TokenTracker.Core;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Pricing;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;
using TokenTracker.Server;

// Dashboard-style views default to the last 7 days. The session snapshot keeps
// its all-time default because the VS Code tip aggregates the whole session
// client-side and would silently lose older calls.
const int DefaultDashboardRange = 2;
const int DefaultSessionRange = 4;

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
    "1.0.20",
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

// Health probes must not keep an otherwise-unused server alive, but every other
// request (including polling clients without a WebSocket) counts as activity.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/healthz"))
    {
        hub.RecordHttpActivity();
    }

    await next();
});
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

// Holiday data and the one-time cost backfill must not delay the port becoming
// reachable: the extension only waits 25s for /healthz and the holiday fetch
// alone can block for 8s. HolidayService recomputes the affected year once the
// data arrives, so pricing self-heals right after startup.
_ = Task.Run(async () =>
{
    try
    {
        var beijingNow = BeijingCalendar.ToBeijing(DateTimeOffset.UtcNow);
        var holidayYears = new HashSet<int>
        {
            beijingNow.Year,
            beijingNow.Year + 1,
        };
        foreach (var year in store.GetSpanYears())
        {
            if (year >= 2000 && year <= beijingNow.Year + 2)
            {
                holidayYears.Add(year);
            }
        }

        using var holidayTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await holidayService.EnsureYearsAsync(
            holidayYears,
            holidayTimeout.Token);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"holiday warm-up failed: {exception.Message}");
    }
});

_ = Task.Run(() =>
{
    try
    {
        var backfilled = store.BackfillCostBreakdown();
        if (backfilled > 0)
        {
            Console.WriteLine(
                $"backfilled cost breakdown for {backfilled} spans");
        }

        // The merged view is rebuilt from the raw spans, and the backfill above
        // invalidates it anyway. Doing it here keeps the first session/dashboard
        // request from paying the whole rebuild (4.3 s on a 15k-span database).
        store.EnsureCombinedSpans();
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"cost backfill failed: {exception.Message}");
    }
});

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
                service.InvalidateOutcomes();
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
    // A polling client issues a request every few seconds and never shows up in
    // ActiveConnections, so HTTP activity within this window also counts.
    var activityWindow = TimeSpan.FromSeconds(
        Math.Max(20, idleExitSeconds * 2));
    _ = Task.Run(async () =>
    {
        var idleSince = DateTime.UtcNow;
        var hadClients = false;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idleExitSeconds)));
            if (hub.ActiveConnections > 0 ||
                hub.HasRecentHttpActivity(activityWindow))
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
    Results.Json(
        SystemInfoBuilder.Build(
            serverInfo,
            metadata,
            store,
            counters,
            settings,
            dataDirectory,
            endpoint),
        AppJsonContext.Default.SystemInfoPayload));

app.MapGet("/api/dashboard", (
    int? range,
    bool? utility,
    long? from,
    long? to,
    string? source) =>
{
    if (InvalidRange(from, to) is { } invalid)
    {
        return invalid;
    }

    return Results.Json(
        from is > 0
            ? service.GetDashboard(
                from.Value,
                to!.Value,
                utility ?? false,
                source)
            : service.GetDashboard(
                range ?? DefaultDashboardRange,
                utility ?? false,
                source),
        AppJsonContext.Default.DashboardPayload);
});

app.MapGet("/api/model/{model}", (
    string model,
    int? range,
    long? from,
    long? to,
    string? source) =>
{
    if (InvalidRange(from, to) is { } invalid)
    {
        return invalid;
    }

    return Results.Json(
        from is > 0
            ? service.GetModel(model, from.Value, to!.Value, source)
            : service.GetModel(model, range ?? DefaultDashboardRange, source),
        AppJsonContext.Default.ModelPayload);
});

app.MapGet("/api/session/{sessionId}", (
    string sessionId,
    int? range,
    long? from,
    long? to,
    bool? includeCalls,
    string? source) =>
{
    if (InvalidRange(from, to) is { } invalid)
    {
        return invalid;
    }

    return Results.Json(
        from is > 0
            ? service.GetSession(
                sessionId,
                source,
                from.Value,
                to!.Value,
                includeCalls ?? true)
            : service.GetSession(
                sessionId,
                range ?? DefaultSessionRange,
                source,
                includeCalls ?? true),
        AppJsonContext.Default.SessionPayload);
});

// FIFO blocks: `cursor` continues after the last returned row, `budget` caps the
// rows per response. Traces may span blocks; the server repeats missing ancestors.
// The topology honours the same range/from-to as the rest of the session view.
app.MapGet("/api/session/{sessionId}/trace", (
    string sessionId,
    string? source,
    string? cursor,
    int? budget,
    int? range,
    long? from,
    long? to) =>
{
    if (InvalidRange(from, to) is { } invalid)
    {
        return invalid;
    }

    var (fromMs, toMs) = from is > 0
        ? (from.Value, to!.Value)
        : RangeTime.Resolve(
            range ?? DefaultSessionRange,
            DateTimeOffset.UtcNow);
    return Results.Json(
        service.GetTrace(
            sessionId,
            source,
            cursor,
            budget ?? DashboardService.TraceBudget,
            fromMs,
            toMs),
        AppJsonContext.Default.TracePayload);
});

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
{
    if (InvalidRange(from, to) is { } invalid)
    {
        return invalid;
    }

    return Results.Json(
        from is > 0
            ? service.GetCalls(
                from.Value,
                to!.Value,
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
                range ?? DefaultDashboardRange,
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
        AppJsonContext.Default.CallsPayload);
});

app.MapGet("/api/models", () =>
    Results.Json(
        service.GetModels(),
        AppJsonContext.Default.ListModelSettingsRowPayload));

app.MapPost("/api/models", (ModelSettingsUpdateRequest request) =>
{
    var row = service.SaveModel(request);
    if (row is not null)
    {
        hub.NotifyChanged();
    }

    return row is null
        ? Fail("invalid model settings", StatusCodes.Status400BadRequest)
        : Results.Json(row, AppJsonContext.Default.ModelSettingsRowPayload);
});

app.MapDelete("/api/models", (string model) =>
{
    service.DeleteModel(model);
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
        if (settings.ApplyRetention(store) > 0)
        {
            service.InvalidateOutcomes();
        }
    }
    catch (Exception exception)
    {
        return Results.Json(
            new ErrorPayload(exception.Message),
            AppJsonContext.Default.ErrorPayload,
            statusCode: StatusCodes.Status500InternalServerError);
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

app.MapPost("/v1/traces", (HttpContext context, CancellationToken token) =>
    OtlpEndpoints.IngestTracesAsync(
        context,
        token,
        store,
        counters,
        hub,
        sourceOverride: null));

app.MapPost("/otlp/{source}", async (
    HttpContext context,
    CancellationToken token,
    string source) =>
{
    var normalized = OtlpEndpoints.NormalizeSource(source);
    return normalized is null
        ? Results.NotFound()
        : await OtlpEndpoints.IngestTracesAsync(
            context,
            token,
            store,
            counters,
            hub,
            normalized);
});

app.MapPost("/otlp/{source}/v1/traces", async (
    HttpContext context,
    CancellationToken token,
    string source) =>
{
    var normalized = OtlpEndpoints.NormalizeSource(source);
    return normalized is null
        ? Results.NotFound()
        : await OtlpEndpoints.IngestTracesAsync(
            context,
            token,
            store,
            counters,
            hub,
            normalized);
});

app.MapPost("/v1/metrics", (HttpContext context, CancellationToken token) =>
    OtlpEndpoints.IngestMetricsAsync(
        context,
        token,
        sourceOverride: null,
        captureOtelMetrics,
        otelMetricCaptureDirectory,
        otelCaptureLock,
        store,
        counters,
        hub,
        service));

app.MapPost("/otlp/{source}/v1/metrics", async (
    HttpContext context,
    CancellationToken token,
    string source) =>
{
    var normalized = OtlpEndpoints.NormalizeSource(source);
    return normalized is null
        ? Results.NotFound()
        : await OtlpEndpoints.IngestMetricsAsync(
            context,
            token,
            normalized,
            captureOtelMetrics,
            otelMetricCaptureDirectory,
            otelCaptureLock,
            store,
            counters,
            hub,
            service);
});

app.MapGet("/ws", (HttpContext context) =>
    WsEndpoint.HandleAsync(context, service, hub));

// Unknown /api and /otlp paths must not fall through to the SPA: a typo would
// otherwise answer 200 with index.html and surface as a JSON parse error.
// MapFallback(pattern) cannot be used here — ASP.NET Core rewrites every
// fallback to {*path:nonfile}, which then collides with MapFallbackToFile.
app.Map("/api/{**rest}", () => Results.NotFound());
// Three segments on purpose: /otlp/{source} already 404s unknown sources, and a
// two-segment catch-all would collide with it.
app.Map("/otlp/{source}/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

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

// A half-specified from/to used to fall back to range without telling the caller,
// so the response looked filtered while it was not. Reject it instead.
static IResult? InvalidRange(long? from, long? to)
{
    if (from is null && to is null)
    {
        return null;
    }

    if (from is > 0 && to > from)
    {
        return null;
    }

    return Fail(
        "from and to must both be set, with to > from",
        StatusCodes.Status400BadRequest);
}

static IResult Fail(string message, int statusCode) =>
    Results.Json(
        new ErrorPayload(message),
        AppJsonContext.Default.ErrorPayload,
        statusCode: statusCode);
