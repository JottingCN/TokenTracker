using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Storage;

namespace TokenTracker_App.Services;

public sealed class OtlpReceiverHost : IAsyncDisposable
{
    private readonly SpanStore _store;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _application;
    private long _receivedBatches;
    private long _receivedSpans;

    public OtlpReceiverHost(SpanStore store, int port = 4318)
    {
        _store = store;
        _port = port;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}";
    public long ReceivedBatches => Interlocked.Read(ref _receivedBatches);
    public long ReceivedSpans => Interlocked.Read(ref _receivedSpans);
    public long LastReceivedAtMs { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
            {
                return;
            }

            AppLog.Write("receiver: creating builder");
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
                options.Listen(IPAddress.Loopback, _port);
            });

            AppLog.Write("receiver: building application");
            var application = builder.Build();
            application.MapPost("/v1/traces", HandleTracesAsync);
            application.MapPost("/v1/metrics", HandleMetricsAsync);
            application.MapGet("/healthz", HandleHealthAsync);

            AppLog.Write($"receiver: starting on {Endpoint}");
            await application.StartAsync(cancellationToken);
            _application = application;
            IsRunning = true;
            LastError = null;
            AppLog.Write("receiver: started");
        }
        catch (Exception exception)
        {
            IsRunning = false;
            LastError = exception.Message;
            AppLog.Write($"receiver: start failed: {exception}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_application is null)
            {
                return;
            }

            await _application.StopAsync();
            await _application.DisposeAsync();
            _application = null;
            IsRunning = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private async Task HandleTracesAsync(HttpContext context)
    {
        try
        {
            if (!IsJsonContentType(context.Request.ContentType))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            var gzip = context.Request.Headers.ContentEncoding
                .ToString()
                .Contains("gzip", StringComparison.OrdinalIgnoreCase);
            using var decompressed = gzip
                ? new GZipStream(context.Request.Body, CompressionMode.Decompress)
                : null;
            var body = (Stream?)decompressed ?? context.Request.Body;

            using var document = await JsonDocument.ParseAsync(
                body,
                cancellationToken: context.RequestAborted);
            var spans = OtlpTraceParser.ParseTraces(
                document,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var inserted = await Task.Run(
                () => _store.InsertMany(spans),
                context.RequestAborted);

            Interlocked.Add(ref _receivedSpans, inserted);
            Interlocked.Increment(ref _receivedBatches);
            LastReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = 0;
        }
        catch (OperationCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private async Task HandleMetricsAsync(HttpContext context)
    {
        await context.Request.Body.CopyToAsync(
            Stream.Null,
            context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = 0;
    }

    private static async Task HandleHealthAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync("ok", context.RequestAborted);
    }

    private static bool IsJsonContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));
}
