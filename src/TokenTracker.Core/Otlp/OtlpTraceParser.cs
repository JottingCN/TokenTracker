using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Otlp;

public static class OtlpTraceParser
{
    private const int MaxAttributesLength = 4_000;

    public static IReadOnlyList<OtlpSpanRecord> ParseTraces(
        JsonDocument document,
        long receivedAtMs)
    {
        var result = new List<OtlpSpanRecord>();
        var root = document.RootElement;

        if (!TryGet(root, "resourceSpans", out var resourceSpans) ||
            resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = TryGet(resourceSpan, "resource", out var resource)
                ? ParseAttributes(resource, "attributes")
                : EmptyAttributes;
            var serviceName = GetString(resourceAttributes, "service.name");

            if (!TryGet(resourceSpan, "scopeSpans", out var scopeSpans) ||
                scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (!TryGet(scopeSpan, "spans", out var spans) ||
                    spans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var span in spans.EnumerateArray())
                {
                    var parsed = ParseSpan(span, serviceName, receivedAtMs);
                    if (parsed is not null)
                    {
                        result.Add(parsed);
                    }
                }
            }
        }

        return result;
    }

    private static OtlpSpanRecord? ParseSpan(
        JsonElement span,
        string? serviceName,
        long receivedAtMs)
    {
        var traceId = TryGet(span, "traceId", out var traceElement) &&
                      traceElement.ValueKind == JsonValueKind.String
            ? traceElement.GetString()
            : null;
        var spanId = TryGet(span, "spanId", out var spanIdElement) &&
                     spanIdElement.ValueKind == JsonValueKind.String
            ? spanIdElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId))
        {
            return null;
        }

        var name = TryGet(span, "name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;

        var attributes = ParseAttributes(span, "attributes");
        var operation = GetString(attributes, "gen_ai.operation.name");

        if (!string.Equals(operation, "chat", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("chat ", StringComparison.Ordinal))
        {
            return null;
        }

        var model =
            GetString(attributes, "gen_ai.request.model") ??
            GetString(attributes, "gen_ai.response.model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var inputTokens = GetLong(attributes, "gen_ai.usage.input_tokens");
        if (inputTokens is null)
        {
            return null;
        }

        var outputTokens = GetLong(attributes, "gen_ai.usage.output_tokens") ?? 0;
        var cacheReadTokens =
            GetLong(attributes, "gen_ai.usage.cache_read.input_tokens") ?? 0;
        var cacheCreationTokens =
            GetLong(attributes, "gen_ai.usage.cache_creation.input_tokens") ?? 0;
        var reasoningTokens =
            GetLong(attributes, "gen_ai.usage.reasoning_tokens") ?? 0;

        var sessionId =
            GetString(attributes, "copilot_chat.session_id") ??
            GetString(attributes, "gen_ai.conversation.id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = null;
        }

        var startMs = ParseUnixNanoMilliseconds(
            span,
            "startTimeUnixNano",
            receivedAtMs);
        var endMs = TryGet(span, "endTimeUnixNano", out _)
            ? ParseUnixNanoMilliseconds(span, "endTimeUnixNano", startMs)
            : (long?)null;

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        var hitRate = inputTokens.Value > 0
            ? (double)cacheReadTokens / inputTokens.Value
            : (double?)null;
        var breakdown = ModelPricing.EvaluateDetailed(
            ModelPricing.GetPrice(model),
            inputTokens.Value,
            cacheReadTokens,
            outputTokens,
            reasoningTokens,
            timestamp);

        return new OtlpSpanRecord
        {
            TraceId = traceId!,
            SpanId = spanId!,
            Name = name,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            ServiceName = serviceName,
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = inputTokens.Value,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheCreationTokens = cacheCreationTokens,
            ReasoningTokens = reasoningTokens,
            HitRate = hitRate,
            CostCny = (double?)breakdown.CostCny,
            CacheCostCny = (double?)breakdown.CacheCost,
            UncachedCostCny = (double?)breakdown.UncachedCost,
            OutputCostCny = (double?)breakdown.OutputCost,
            ReasoningCostCny = (double?)breakdown.ReasoningCost,
            PricingPeriod = breakdown.Period,
            Attributes = SerializeAttributes(attributes),
            ReceivedAtMs = receivedAtMs,
        };
    }

    private static Dictionary<string, string> ParseAttributes(
        JsonElement parent,
        string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!TryGet(parent, propertyName, out var attributes) ||
            attributes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            var key = TryGet(attribute, "key", out var keyElement) &&
                      keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!TryGet(attribute, "value", out var value) ||
                value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = AttributeValueToString(value);
            if (text is not null)
            {
                result[key] = text;
            }
        }

        return result;
    }

    private static string? AttributeValueToString(JsonElement value)
    {
        if (TryGet(value, "stringValue", out var stringValue) &&
            stringValue.ValueKind == JsonValueKind.String)
        {
            return stringValue.GetString();
        }

        if (TryGet(value, "intValue", out var intValue))
        {
            if (intValue.ValueKind == JsonValueKind.String)
            {
                return intValue.GetString();
            }
            if (intValue.ValueKind == JsonValueKind.Number &&
                intValue.TryGetInt64(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (TryGet(value, "doubleValue", out var doubleValue) &&
            doubleValue.ValueKind == JsonValueKind.Number)
        {
            return doubleValue.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        }

        if (TryGet(value, "boolValue", out var boolValue) &&
            boolValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return boolValue.GetBoolean() ? "true" : "false";
        }

        return null;
    }

    private static long ParseUnixNanoMilliseconds(
        JsonElement span,
        string propertyName,
        long fallbackMs)
    {
        if (!TryGet(span, propertyName, out var value))
        {
            return fallbackMs;
        }

        long nanos;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out nanos))
            {
                return fallbackMs;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out nanos))
            {
                return fallbackMs;
            }
        }
        else
        {
            return fallbackMs;
        }

        return nanos > 0 ? nanos / 1_000_000 : fallbackMs;
    }

    private static string? SerializeAttributes(Dictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(MaxAttributesLength, 256));
        foreach (var (key, value) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (builder.Length > MaxAttributesLength)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(key).Append('=').Append(value);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool TryGet(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(
        Dictionary<string, string> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static long? GetLong(
        Dictionary<string, string> attributes,
        string key)
    {
        if (!attributes.TryGetValue(key, out var text))
        {
            return null;
        }

        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static readonly Dictionary<string, string> EmptyAttributes =
        new(StringComparer.Ordinal);
}
