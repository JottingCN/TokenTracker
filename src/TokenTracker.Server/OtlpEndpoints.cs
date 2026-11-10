using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TokenTracker.Core.Models;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Storage;

namespace TokenTracker.Server;

// OTLP receivers return IResult, so they must be registered as a delegate with
// more than one parameter: MapPost(pattern, RequestDelegate) is preferred by
// overload resolution for a lambda that only takes HttpContext, and it silently
// discards the returned IResult (every request would answer 200).
public static class OtlpEndpoints
{
    // One OTLP batch is a few hundred KB; unbounded bodies are a memory hazard.
    private const long MaxPayloadBytes = 16 * 1024 * 1024;
    private const int CaptureFileLimit = 50;

    public static string? NormalizeSource(string source) =>
        source.Trim().ToLowerInvariant() switch
        {
            "copilot-chat" or "extension-host" => "copilot-chat",
            "agent-host" => "agent-host",
            _ => null,
        };

    public static async Task<IResult> IngestTracesAsync(
        HttpContext context,
        CancellationToken token,
        SpanStore store,
        ReceiverCounters counters,
        RealtimeHub hub,
        string? sourceOverride)
    {
        try
        {
            using var document = await ReadJsonAsync(context, token);
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

            return Results.Ok();
        }
        catch (InvalidPayloadException exception)
        {
            counters.RecordError(exception.Message);
            return Failure(exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (BadHttpRequestException exception)
        {
            // Kestrel aborts oversized bodies; report its status (413) instead of
            // turning a client mistake into an opaque 500.
            counters.RecordError(exception.Message);
            return Failure(exception.Message, exception.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return Failure("request aborted", StatusCodes.Status400BadRequest);
        }
        catch (Exception exception)
        {
            counters.RecordError(exception.Message);
            Console.Error.WriteLine(
                $"OTLP trace ingestion failed: {exception.Message}");
            return Failure(
                exception.Message,
                StatusCodes.Status500InternalServerError);
        }
    }

    public static async Task<IResult> IngestMetricsAsync(
        HttpContext context,
        CancellationToken token,
        string? sourceOverride,
        bool captureEnabled,
        string captureDirectory,
        object captureLock,
        SpanStore store,
        ReceiverCounters counters,
        RealtimeHub hub,
        DashboardService service)
    {
        try
        {
            using var document = await ReadJsonAsync(
                context,
                token,
                bufferBody: captureEnabled);
            var receivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var parsed = OtlpMetricParser.Parse(
                document,
                receivedAtMs,
                sourceOverride,
                seenNames);
            var inserted = parsed.Count > 0
                ? store.InsertMetricPoints(parsed)
                : 0;
            counters.RecordMetrics(parsed.Count, inserted, seenNames);
            if (inserted > 0)
            {
                counters.Record(inserted);
                service.InvalidateOutcomes();
                hub.NotifyChanged();
            }

            if (captureEnabled)
            {
                await CaptureAsync(
                    context,
                    sourceOverride,
                    parsed,
                    captureDirectory,
                    captureLock,
                    token);
            }

            return Results.Ok();
        }
        catch (InvalidPayloadException exception)
        {
            counters.RecordError(exception.Message);
            return Failure(exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (BadHttpRequestException exception)
        {
            // Kestrel aborts oversized bodies; report its status (413) instead of
            // turning a client mistake into an opaque 500.
            counters.RecordError(exception.Message);
            return Failure(exception.Message, exception.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return Failure("request aborted", StatusCodes.Status400BadRequest);
        }
        catch (Exception exception)
        {
            counters.RecordError(exception.Message);
            Console.Error.WriteLine(
                $"OTLP metrics ingestion failed: {exception.Message}");
            return Failure(
                exception.Message,
                StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task CaptureAsync(
        HttpContext context,
        string? sourceOverride,
        IReadOnlyList<OtlpMetricPointRecord> parsed,
        string captureDirectory,
        object captureLock,
        CancellationToken token)
    {
        var source = sourceOverride ??
            parsed.FirstOrDefault()?.Source ??
            "direct";
        var raw = await ReadRawAsync(context);
        if (raw is null)
        {
            return;
        }

        Directory.CreateDirectory(captureDirectory);
        var fileName =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{source}-{Guid.NewGuid():N}.json";
        await File.WriteAllTextAsync(
            Path.Combine(captureDirectory, fileName),
            raw,
            token);
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

    private static async Task<string?> ReadRawAsync(HttpContext context)
    {
        try
        {
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(
                context.Request.Body,
                leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpContext context,
        CancellationToken token,
        bool bufferBody = false)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>()
            is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxPayloadBytes;
        }

        if (bufferBody)
        {
            // Capture needs the payload a second time; buffering is opt-in so the
            // normal path keeps streaming straight from the socket.
            context.Request.EnableBuffering();
        }

        Stream body = context.Request.Body;
        GZipStream? gzip = null;
        if (context.Request.Headers.ContentEncoding.ToString()
            .Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true);
            body = gzip;
        }

        try
        {
            return await JsonDocument.ParseAsync(body, cancellationToken: token);
        }
        catch (JsonException exception)
        {
            throw new InvalidPayloadException(exception.Message);
        }
        finally
        {
            gzip?.Dispose();
        }
    }

    private static void TrimCapturedSignals(string captureDirectory)
    {
        try
        {
            foreach (var file in Directory
                         .GetFiles(captureDirectory, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(CaptureFileLimit))
            {
                File.Delete(file);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"OTLP logs cleanup failed: {exception.Message}");
        }
    }

    private static IResult Failure(string message, int statusCode) =>
        Results.Json(
            new ErrorPayload(message),
            AppJsonContext.Default.ErrorPayload,
            statusCode: statusCode);
}

public sealed class InvalidPayloadException : Exception
{
    public InvalidPayloadException(string message) : base(message)
    {
    }
}
