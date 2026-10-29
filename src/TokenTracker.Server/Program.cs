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
builder.Services.AddWindowsService(options => options.ServiceName = "TokenTracker");
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
    "1.0.0",
    1,
    port,
    startedAt,
    string.IsNullOrWhiteSpace(buildId) ? null : buildId,
    ["dashboard", "prices", "holidays", "breakdown", "ws"]);

var dataDirectory = Environment.GetEnvironmentVariable("TOKENTRACKER_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TokenTracker");
}

Directory.CreateDirectory(dataDirectory);
var store = new SpanStore(Path.Combine(dataDirectory, "tokentracker.db"));
try
{
    store.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds());
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
var service = new DashboardService(store, metadata, counters, endpoint);
service.ApplyOverrides();
var hub = new RealtimeHub();
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

app.MapGet("/api/dashboard", (int? range, bool? utility, long? from, long? to) =>
    Results.Json(
        from is > 0 && to > from
            ? service.GetDashboard(from.Value, to.Value, utility ?? false)
            : service.GetDashboard(range ?? 2, utility ?? false),
        AppJsonContext.Default.DashboardPayload));

app.MapGet("/api/model/{model}", (string model, int? range, long? from, long? to) =>
    Results.Json(
        from is > 0 && to > from
            ? service.GetModel(model, from.Value, to.Value)
            : service.GetModel(model, range ?? 2),
        AppJsonContext.Default.ModelPayload));

app.MapGet("/api/session/{sessionId}", (string sessionId) =>
    Results.Json(
        service.GetSession(sessionId),
        AppJsonContext.Default.SessionPayload));

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

app.MapPost("/v1/traces", async (HttpContext context) =>
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
            var spans = OtlpTraceParser.ParseTraces(
                document,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (spans.Count > 0)
            {
                store.InsertMany(spans);
                counters.Record(spans.Count);
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
});

app.MapPost("/v1/metrics", async (HttpContext context) =>
{
    await DrainAsync(context.Request);
    return Results.Ok();
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

static async Task DrainAsync(HttpRequest request)
{
    var buffer = new byte[8192];
    while (await request.Body.ReadAsync(buffer) > 0)
    {
    }
}
