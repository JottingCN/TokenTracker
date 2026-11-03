using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenTracker.Core.Models;

namespace TokenTracker.Core.Otlp;

public static class OtlpMetricParser
{
    private static readonly HashSet<string> AllowedMetrics =
        new(MetricNames.Outcome, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<OtlpMetricPointRecord> Parse(
        JsonDocument document,
        long receivedAtMs,
        string? sourceOverride = null)
    {
        var results = new List<OtlpMetricPointRecord>();
        if (!OtlpTraceParser.TryGet(
                document.RootElement,
                "resourceMetrics",
                out var resourceMetrics) ||
            resourceMetrics.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var resourceMetric in resourceMetrics.EnumerateArray())
        {
            var resourceAttributes = OtlpTraceParser.TryGet(
                    resourceMetric,
                    "resource",
                    out var resource)
                ? OtlpTraceParser.ParseAttributes(resource, "attributes")
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var serviceName = OtlpTraceParser.GetString(
                resourceAttributes,
                "service.name");
            var serviceNamespace = OtlpTraceParser.GetString(
                resourceAttributes,
                "service.namespace");
            var windowSessionId = OtlpTraceParser.GetString(
                resourceAttributes,
                "session.id");
            var source = sourceOverride ??
                OtlpTraceParser.ResolveSource(serviceNamespace, serviceName);

            if (!OtlpTraceParser.TryGet(
                    resourceMetric,
                    "scopeMetrics",
                    out var scopeMetrics) ||
                scopeMetrics.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeMetric in scopeMetrics.EnumerateArray())
            {
                var scopeName = OtlpTraceParser.TryGet(
                        scopeMetric,
                        "scope",
                        out var scope)
                    ? GetScopeName(scope)
                    : null;

                if (!OtlpTraceParser.TryGet(
                        scopeMetric,
                        "metrics",
                        out var metrics) ||
                    metrics.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var metric in metrics.EnumerateArray())
                {
                    var metricName = GetString(metric, "name");
                    if (string.IsNullOrWhiteSpace(metricName) ||
                        !AllowedMetrics.Contains(metricName))
                    {
                        continue;
                    }

                    foreach (var kind in new[]
                             {
                                 "sum",
                                 "gauge",
                                 "histogram",
                                 "exponentialHistogram",
                                 "summary",
                             })
                    {
                        if (!OtlpTraceParser.TryGet(metric, kind, out var block) ||
                            block.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var temporality = GetString(
                            block,
                            "aggregationTemporality");
                        if (!OtlpTraceParser.TryGet(
                                block,
                                "dataPoints",
                                out var dataPoints) ||
                            dataPoints.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var dataPoint in dataPoints.EnumerateArray())
                        {
                            var attributes = OtlpTraceParser.ParseAttributes(
                                dataPoint,
                                "attributes");
                            var attrs = OtlpTraceParser.SerializeMetadataAttributes(
                                attributes);
                            var startMs = OtlpTraceParser.ParseUnixNanoMilliseconds(
                                dataPoint,
                                "startTimeUnixNano",
                                0);
                            var endMs = OtlpTraceParser.ParseUnixNanoMilliseconds(
                                dataPoint,
                                "timeUnixNano",
                                receivedAtMs);
                            var seriesKey = SeriesKey(
                                source,
                                metricName,
                                kind,
                                scopeName,
                                serviceName,
                                attrs);

                            results.Add(new OtlpMetricPointRecord
                            {
                                Source = source,
                                SeriesKey = seriesKey,
                                MetricName = metricName,
                                MetricKind = kind,
                                Temporality = temporality,
                                ServiceName = serviceName,
                                ScopeName = scopeName,
                                Attributes = attrs,
                                WindowSessionId = windowSessionId,
                                DirectSessionId =
                                    OtlpTraceParser.ParseSessionId(attributes),
                                ToolCallId = OtlpTraceParser.GetString(
                                    attributes,
                                    "gen_ai.tool.call.id"),
                                StartTimeMs = startMs,
                                EndTimeMs = endMs,
                                Value = ReadMetricValue(dataPoint),
                                Count = ReadLong(dataPoint, "count"),
                                Sum = ReadDouble(dataPoint, "sum"),
                                Min = ReadDouble(dataPoint, "min"),
                                Max = ReadDouble(dataPoint, "max"),
                                BucketCounts = ReadNumberArray(
                                    dataPoint,
                                    "bucketCounts") ??
                                    ReadQuantiles(dataPoint),
                                ExplicitBounds = ReadNumberArray(
                                    dataPoint,
                                    "explicitBounds"),
                                ReceivedAtMs = receivedAtMs,
                            });
                        }
                    }
                }
            }
        }

        return results;
    }

    private static string SeriesKey(
        string source,
        string metricName,
        string kind,
        string? scopeName,
        string? serviceName,
        string? attributes)
    {
        var text =
            $"{source}\u001f{metricName}\u001f{kind}\u001f{scopeName}\u001f" +
            $"{serviceName}\u001f{attributes}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
    }

    private static string? GetScopeName(JsonElement scope)
    {
        var name = GetString(scope, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? GetString(JsonElement element, string name) =>
        OtlpTraceParser.TryGet(element, name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadMetricValue(JsonElement dataPoint)
    {
        var asDouble = ReadDouble(dataPoint, "asDouble");
        if (asDouble is not null)
        {
            return asDouble;
        }

        var asInt = ReadLong(dataPoint, "asInt");
        return asInt is null ? null : asInt.Value;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!OtlpTraceParser.TryGet(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   value.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!OtlpTraceParser.TryGet(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   value.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var number) &&
               double.IsFinite(number)
            ? number
            : null;
    }

    private static string? ReadNumberArray(JsonElement element, string name)
    {
        if (!OtlpTraceParser.TryGet(element, name, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                parts.Add(item.GetDouble().ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            }
            else if (item.ValueKind == JsonValueKind.String &&
                     item.GetString() is { } text)
            {
                parts.Add(text);
            }
        }

        return parts.Count > 0 ? string.Join(",", parts) : null;
    }

    private static string? ReadQuantiles(JsonElement dataPoint)
    {
        if (!OtlpTraceParser.TryGet(
                dataPoint,
                "quantileValues",
                out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in values.EnumerateArray())
        {
            var quantile = ReadDouble(item, "quantile");
            var value = ReadDouble(item, "value");
            if (quantile is not null && value is not null)
            {
                parts.Add(
                    $"{quantile.Value.ToString("R", CultureInfo.InvariantCulture)}" +
                    $"={value.Value.ToString("R", CultureInfo.InvariantCulture)}");
            }
        }

        return parts.Count > 0 ? string.Join(",", parts) : null;
    }
}
