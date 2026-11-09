using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Otlp;

public static partial class OtlpTraceParser
{
    private const int MaxAttributesLength = 4_000;

    private static readonly HashSet<string> EventOperations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "execute_tool",
            "invoke_agent",
            "create_agent",
            "execute_hook",
            "permission",
        };

    public static IReadOnlyList<OtlpSpanRecord> ParseTraces(
        JsonDocument document,
        long receivedAtMs,
        string? sourceOverride = null) =>
        Parse(document, receivedAtMs, sourceOverride).Spans;

    public static IReadOnlyList<OtlpEventRecord> ParseEvents(
        JsonDocument document,
        long receivedAtMs,
        string? sourceOverride = null) =>
        Parse(document, receivedAtMs, sourceOverride).Events;

    public static OtlpParseResult Parse(
        JsonDocument document,
        long receivedAtMs,
        string? sourceOverride = null)
    {
        var spanRecords = new List<OtlpSpanRecord>();
        var eventRecords = new List<OtlpEventRecord>();
        var agentRunRecords = new List<AgentRunRecord>();
        var root = document.RootElement;

        if (!TryGet(root, "resourceSpans", out var resourceSpans) ||
            resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return new OtlpParseResult(
                spanRecords,
                eventRecords,
                agentRunRecords);
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = TryGet(resourceSpan, "resource", out var resource)
                ? ParseAttributes(resource, "attributes")
                : EmptyAttributes;
            var serviceName = GetString(resourceAttributes, "service.name");
            var serviceNamespace =
                GetString(resourceAttributes, "service.namespace");
            var windowSessionId = GetString(resourceAttributes, "session.id");
            var source = sourceOverride ??
                ResolveSource(serviceNamespace, serviceName);

            if (!TryGet(resourceSpan, "scopeSpans", out var scopeSpans) ||
                scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (!TryGet(scopeSpan, "spans", out var spansElement) ||
                    spansElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var scopeName = GetScopeName(scopeSpan);
                foreach (var span in spansElement.EnumerateArray())
                {
                    var (spanRecord, spanEvents, agentRunRecord) = ParseSpan(
                        span,
                        serviceName,
                        serviceNamespace,
                        scopeName,
                        source,
                        windowSessionId,
                        receivedAtMs);
                    if (spanRecord is not null)
                    {
                        spanRecords.Add(spanRecord);
                    }

                    if (spanEvents is not null)
                    {
                        eventRecords.AddRange(spanEvents);
                    }

                    if (agentRunRecord is not null)
                    {
                        agentRunRecords.Add(agentRunRecord);
                    }
                }
            }
        }

        return new OtlpParseResult(
            spanRecords,
            eventRecords,
            agentRunRecords);
    }

    private static (
        OtlpSpanRecord? Span,
        List<OtlpEventRecord>? Events,
        AgentRunRecord? AgentRun) ParseSpan(
        JsonElement span,
        string? serviceName,
        string? serviceNamespace,
        string? scopeName,
        string source,
        string? windowSessionId,
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
        var parentSpanId = TryGet(span, "parentSpanId", out var parentElement) &&
                           parentElement.ValueKind == JsonValueKind.String
            ? parentElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId))
        {
            return (null, null, null);
        }

        var name = TryGet(span, "name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;

        var attributes = ParseAttributes(span, "attributes");
        var operation = GetString(attributes, "gen_ai.operation.name");
        var toolName = ParseToolName(attributes, name, operation);
        var sessionId = ParseSessionId(attributes);

        // OTel span events carry the hook signal: neither pipeline exports an
        // execute_hook span, so dropping the event array is what made the hook
        // panel read 0 while the client ran thousands of hooks.
        var spanEvents = ParseSpanEvents(
            span,
            traceId,
            spanId,
            source,
            sessionId,
            serviceName,
            serviceNamespace,
            scopeName,
            windowSessionId,
            receivedAtMs);

        if (!string.Equals(operation, "chat", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("chat ", StringComparison.Ordinal))
        {
            var eventRecord = ParseEvent(
                span,
                traceId,
                spanId,
                parentSpanId,
                name,
                attributes,
                operation,
                toolName,
                serviceName,
                serviceNamespace,
                scopeName,
                source,
                windowSessionId,
                receivedAtMs);
            if (eventRecord is not null)
            {
                spanEvents ??= new List<OtlpEventRecord>(1);
                spanEvents.Add(eventRecord);
            }

            var agentRunRecord = eventRecord is { Operation: "invoke_agent" }
                ? BuildAgentRun(
                    span,
                    traceId,
                    spanId,
                    parentSpanId,
                    name,
                    attributes,
                    serviceName,
                    serviceNamespace,
                    source,
                    receivedAtMs)
                : null;
            return (null, spanEvents, agentRunRecord);
        }

        var model =
            GetString(attributes, "gen_ai.response.model") ??
            GetString(attributes, "gen_ai.request.model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return (null, null, null);
        }

        model = ModelPricing.NormalizeModel(model, source);

        var inputTokens = GetLong(attributes, "gen_ai.usage.input_tokens");
        if (inputTokens is null)
        {
            return (null, null, null);
        }

        var outputTokens = GetLong(attributes, "gen_ai.usage.output_tokens") ?? 0;
        var cacheReadTokens =
            GetLong(attributes, "gen_ai.usage.cache_read.input_tokens") ?? 0;
        var cacheCreationTokens =
            GetLong(attributes, "gen_ai.usage.cache_creation.input_tokens") ?? 0;
        var reasoningTokens =
            GetLong(attributes, "gen_ai.usage.reasoning.output_tokens") ??
            GetLong(attributes, "gen_ai.usage.reasoning_tokens") ?? 0;
        var nanoAiu =
            GetLong(attributes, "copilot_chat.copilot_usage_nano_aiu");
        var ttftMs = ParseTtftMs(attributes);

        var provider =
            GetString(attributes, "gen_ai.provider.name") ?? serviceName;
        var totalInputTokens = TokenSemantics.TotalInputTokens(
            provider,
            inputTokens.Value,
            cacheReadTokens,
            cacheCreationTokens);

        var startMs = ParseUnixNanoMilliseconds(
            span,
            "startTimeUnixNano",
            receivedAtMs);
        var endMs = TryGet(span, "endTimeUnixNano", out _)
            ? ParseUnixNanoMilliseconds(span, "endTimeUnixNano", startMs)
            : (long?)null;
        if (ttftMs is not null &&
            endMs is long end &&
            end > startMs &&
            !HasExplicitTtft(attributes) &&
            ttftMs.Value >= (end - startMs) * 0.9)
        {
            // A generic first-chunk timestamp close to the total duration
            // usually means non-streaming or proxy buffering, not TTFT.
            ttftMs = null;
        }

        // Peak/off-peak follows the call's end time: usage settles when the
        // request completes, so one crossing into a peak window bills as peak.
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMs ?? startMs);
        var hitRate = totalInputTokens > 0
            ? (double)cacheReadTokens / totalInputTokens
            : (double?)null;
        var breakdown = ModelPricing.EvaluateDetailed(
            ModelPricing.GetPrice(model),
            totalInputTokens,
            cacheReadTokens,
            outputTokens,
            reasoningTokens,
            timestamp,
            cacheCreationTokens);

        return (new OtlpSpanRecord
        {
            TraceId = traceId!,
            SpanId = spanId!,
            ParentSpanId = parentSpanId,
            Name = name,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            ServiceName = serviceName,
            ServiceNamespace = serviceNamespace,
            ScopeName = scopeName,
            Source = source,
            Model = model,
            SessionId = sessionId,
            IsMain = sessionId is not null,
            InputTokens = totalInputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheCreationTokens = cacheCreationTokens,
            ReasoningTokens = reasoningTokens,
            TtftMs = ttftMs,
            NanoAiu = nanoAiu,
            HitRate = hitRate,
            CostCny = (double?)breakdown.CostCny,
            CacheCostCny = (double?)breakdown.CacheCost,
            UncachedCostCny = (double?)breakdown.UncachedCost,
            OutputCostCny = (double?)breakdown.OutputCost,
            ReasoningCostCny = (double?)breakdown.ReasoningCost,
            PricingPeriod = breakdown.Period,
            ErrorType = ParseErrorType(span, attributes),
            ProviderName = provider,
            AgentName = GetString(attributes, "gen_ai.agent.name"),
            FinishReasons = GetString(
                attributes,
                "gen_ai.response.finish_reasons"),
            WindowSessionId = windowSessionId,
            ReceivedAtMs = receivedAtMs,
        }, spanEvents, null);
    }

    private static OtlpEventRecord? ParseEvent(
        JsonElement span,
        string? traceId,
        string? spanId,
        string? parentSpanId,
        string name,
        Dictionary<string, string> attributes,
        string? operation,
        string? toolName,
        string? serviceName,
        string? serviceNamespace,
        string? scopeName,
        string source,
        string? windowSessionId,
        long receivedAtMs)
    {
        operation ??= InferOperation(name, toolName);
        if (operation is null || !EventOperations.Contains(operation))
        {
            return null;
        }

        var startMs = ParseUnixNanoMilliseconds(
            span,
            "startTimeUnixNano",
            receivedAtMs);
        var endMs = TryGet(span, "endTimeUnixNano", out _)
            ? ParseUnixNanoMilliseconds(span, "endTimeUnixNano", startMs)
            : (long?)null;

        var errorType = ParseErrorType(span, attributes);
        var permissionResult = GetString(
            attributes,
            "github.copilot.permission.result");
        if (errorType is null &&
            permissionResult is not null &&
            permissionResult.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            // A denied approval never surfaces as an error status, but it is the
            // reason the matching execute_tool fails, so surface it as an error.
            errorType = "denied";
        }

        return new OtlpEventRecord
        {
            TraceId = traceId!,
            SpanId = spanId!,
            ParentSpanId = parentSpanId,
            Operation = operation.ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            ToolName = toolName,
            ToolType = GetString(attributes, "gen_ai.tool.type"),
            EditType =
                GetString(
                    attributes,
                    "github.copilot.tool.parameters.edit_type") ??
                GetString(
                    attributes,
                    "copilot_chat.tool.parameters.edit_type"),
            SkillName =
                GetString(
                    attributes,
                    "github.copilot.tool.parameters.skill_name") ??
                GetString(
                    attributes,
                    "copilot_chat.tool.parameters.skill_name"),
            McpServerHash = GetString(
                attributes,
                "github.copilot.tool.parameters.mcp_server_name_hash"),
            McpToolName = GetString(
                attributes,
                "github.copilot.tool.parameters.mcp_tool_name"),
            HookDecision = GetString(
                attributes,
                "github.copilot.hook.decision"),
            HookDurationMs = ParseHookDurationMs(attributes),
            HookToolNames = GetString(
                attributes,
                "github.copilot.hook.tool_names"),
            HookType = GetString(attributes, "copilot_chat.hook_type"),
            HookResultKind = GetString(
                attributes,
                "copilot_chat.hook_result_kind"),
            PermissionKind = GetString(
                attributes,
                "github.copilot.permission.kind"),
            PermissionResult = permissionResult,
            CallId = ParseToolCallId(attributes),
            SessionId = ParseSessionId(attributes),
            ServiceName = serviceName,
            ServiceNamespace = serviceNamespace,
            ScopeName = scopeName,
            Source = source,
            WindowSessionId = windowSessionId,
            ErrorType = errorType,
            AgentName = GetString(attributes, "gen_ai.agent.name"),
            AgentType = GetString(
                attributes,
                "github.copilot.agent.type"),
            ProviderName = GetString(
                    attributes,
                    "gen_ai.provider.name") ??
                serviceName,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            IsError = errorType is not null,
            ReceivedAtMs = receivedAtMs,
        };
    }

    /// <summary>
    /// Turns the span's OTel <c>events</c> array into hook rows. Both Copilot
    /// pipelines emit the hook signal as span events (<c>github.copilot.hook.start</c>
    /// / <c>.end</c>) instead of an <c>execute_hook</c> span, so without this the
    /// hook panel always reads zero. Only <c>end</c> becomes a row: start and end
    /// describe the same run (measured 1988 start vs 1976 end), and the duration
    /// lives on <c>end</c>.
    /// </summary>
    private static List<OtlpEventRecord>? ParseSpanEvents(
        JsonElement span,
        string traceId,
        string spanId,
        string source,
        string? sessionId,
        string? serviceName,
        string? serviceNamespace,
        string? scopeName,
        string? windowSessionId,
        long receivedAtMs)
    {
        if (!TryGet(span, "events", out var events) ||
            events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() == 0)
        {
            return null;
        }

        List<OtlpEventRecord>? rows = null;
        var spanStartMs = ParseUnixNanoMilliseconds(
            span,
            "startTimeUnixNano",
            receivedAtMs);
        var index = 0;
        foreach (var item in events.EnumerateArray())
        {
            index++;
            var eventName = TryGet(item, "name", out var nameElement) &&
                            nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (eventName is null ||
                !eventName.EndsWith("hook.end", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = ParseAttributes(item, "attributes");
            var hookType =
                GetString(attributes, "github.copilot.hook.type") ??
                GetString(attributes, "copilot_chat.hook.type");
            var durationMs = ParseHookDurationMs(attributes);
            var startMs = TryGet(item, "timeUnixNano", out _)
                ? ParseUnixNanoMilliseconds(item, "timeUnixNano", spanStartMs)
                : spanStartMs;
            var invocationId =
                GetString(attributes, "github.copilot.hook.invocation_id") ??
                GetString(attributes, "copilot_chat.hook.invocation_id");
            // session_events is keyed by (trace_id, span_id) and one host span can
            // carry every hook of a run, so the id must be synthesized or
            // INSERT OR IGNORE would keep only the first hook.
            var key = string.IsNullOrWhiteSpace(invocationId)
                ? index.ToString(CultureInfo.InvariantCulture)
                : invocationId;

            rows ??= new List<OtlpEventRecord>(4);
            rows.Add(new OtlpEventRecord
            {
                TraceId = traceId,
                SpanId = $"{spanId}#hook#{key}",
                ParentSpanId = spanId,
                Operation = "execute_hook",
                Name = hookType is null ? "execute_hook" : $"execute_hook {hookType}",
                HookType = hookType,
                HookDurationMs = durationMs,
                HookDecision =
                    GetString(attributes, "github.copilot.hook.decision"),
                HookToolNames =
                    GetString(attributes, "github.copilot.hook.tool_names"),
                HookResultKind =
                    GetString(attributes, "github.copilot.hook.result_kind") ??
                    GetString(attributes, "copilot_chat.hook_result_kind"),
                SessionId = sessionId,
                ServiceName = serviceName,
                ServiceNamespace = serviceNamespace,
                ScopeName = scopeName,
                Source = source,
                WindowSessionId = windowSessionId,
                StartTimeMs = startMs,
                EndTimeMs = durationMs is null
                    ? null
                    : startMs + (long)Math.Round(durationMs.Value),
                IsError = string.Equals(
                    GetString(attributes, "github.copilot.hook.result_kind"),
                    "error",
                    StringComparison.OrdinalIgnoreCase),
                ReceivedAtMs = receivedAtMs,
            });
        }

        return rows;
    }

    private static readonly HashSet<string> AgentHostServiceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "github-copilot",
            "claude-code",
            "codex-app-server",
            "vscode-agent-host",
        };

    internal static string ResolveSource(
        string? serviceNamespace,
        string? serviceName)
    {
        if (!string.IsNullOrWhiteSpace(serviceNamespace))
        {
            return serviceNamespace.Contains(
                "agent-host",
                StringComparison.OrdinalIgnoreCase)
                ? "agent-host"
                : "copilot-chat";
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return "unknown";
        }

        var normalized = serviceName.Trim();
        if (AgentHostServiceNames.Contains(normalized))
        {
            return "agent-host";
        }

        return string.Equals(
            normalized,
            "copilot-chat",
            StringComparison.OrdinalIgnoreCase)
            ? "copilot-chat"
            : "unknown";
    }

    private static string? GetScopeName(JsonElement scopeSpan)
    {
        if (TryGet(scopeSpan, "scope", out var scope) &&
            TryGet(scope, "name", out var scopeName) &&
            scopeName.ValueKind == JsonValueKind.String)
        {
            return scopeName.GetString();
        }

        if (TryGet(scopeSpan, "instrumentationLibrary", out var library) &&
            TryGet(library, "name", out var libraryName) &&
            libraryName.ValueKind == JsonValueKind.String)
        {
            return libraryName.GetString();
        }

        return null;
    }

    private static string? InferOperation(string name, string? toolName)
    {
        // An approval span carries a tool name but is not a tool execution; check
        // the span name first so it is not folded into execute_tool.
        if (name.StartsWith("permission", StringComparison.OrdinalIgnoreCase))
        {
            return "permission";
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            return "execute_tool";
        }

        foreach (var operation in EventOperations)
        {
            if (name.StartsWith(operation, StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }
        }

        return null;
    }

    private static string? ParseToolName(
        Dictionary<string, string> attributes,
        string name,
        string? operation)
    {
        var toolName =
            GetString(attributes, "gen_ai.tool.name") ??
            GetString(attributes, "copilot_chat.tool.name") ??
            GetString(attributes, "github.copilot.tool.name") ??
            // A permission span names the tool it asks for; the value is the same
            // identifier (hashed for external tools) the execute_tool span uses.
            GetString(attributes, "github.copilot.permission.tool_name");
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            return toolName;
        }

        const string prefix = "execute_tool";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            name.Length > prefix.Length + 1)
        {
            return name[(prefix.Length + 1)..].Trim();
        }

        return string.Equals(operation, "execute_tool", StringComparison.OrdinalIgnoreCase)
            ? "-"
            : null;
    }

    internal static string? ParseToolCallId(
        Dictionary<string, string> attributes) =>
        GetString(attributes, "gen_ai.tool.call.id") ??
        GetString(attributes, "copilot_chat.tool.call.id") ??
        GetString(attributes, "github.copilot.tool.call.id");

    internal static string? ParseSessionId(Dictionary<string, string> attributes)
    {
        var candidates = new[]
        {
            GetString(attributes, "copilot_chat.parent_chat_session_id"),
            GetString(attributes, "copilot_chat.session_id"),
            GetString(attributes, "gen_ai.conversation.id"),
            GetString(attributes, "copilot_chat.chat_session_id"),
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !candidate.TrimStart().StartsWith(
                    "call_",
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static bool IsErrorSpan(JsonElement span)
    {
        if (!TryGet(span, "status", out var status) ||
            status.ValueKind != JsonValueKind.Object ||
            !TryGet(status, "code", out var code))
        {
            return false;
        }

        if (code.ValueKind == JsonValueKind.Number &&
            code.TryGetInt32(out var numeric))
        {
            return numeric == 2;
        }

        return code.ValueKind == JsonValueKind.String &&
            int.TryParse(
                code.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out numeric) &&
            numeric == 2;
    }

    internal static long? ParseTtftMs(Dictionary<string, string> attributes)
    {
        var explicitMs =
            ParseDouble(attributes, "copilot_chat.time_to_first_token_ms") ??
            ParseDouble(attributes, "copilot_chat.ttft_ms");
        if (explicitMs is > 0)
        {
            return SanitizeDurationMs(explicitMs.Value);
        }

        var copilot = ParseDouble(attributes, "copilot_chat.time_to_first_token");
        if (copilot is > 0)
        {
            // Copilot reports milliseconds; very small values are likely seconds.
            return SanitizeDurationMs(copilot.Value < 10
                ? copilot.Value * 1_000
                : copilot.Value);
        }

        // Documented TTFT beats the generic first-chunk fallback; the chunk value
        // is often the response completion time, so only use it when nothing else
        // exists and let the >=90% discard rule in the caller drop it.
        var explicitTtft =
            ParseDouble(attributes, "gen_ai.server.time_to_first_token") ??
            ParseDouble(attributes, "gen_ai.response.time_to_first_token");
        if (explicitTtft is > 0)
        {
            return SanitizeDurationMs(explicitTtft.Value * 1_000);
        }

        var chunk = ParseDouble(attributes, "gen_ai.response.time_to_first_chunk");
        return chunk is > 0
            ? SanitizeDurationMs(chunk.Value * 1_000)
            : null;
    }

    internal static bool HasExplicitTtft(
        Dictionary<string, string> attributes) =>
        ParseDouble(attributes, "copilot_chat.time_to_first_token_ms") is not null ||
        ParseDouble(attributes, "copilot_chat.ttft_ms") is not null ||
        ParseDouble(attributes, "copilot_chat.time_to_first_token") is not null ||
        ParseDouble(attributes, "gen_ai.server.time_to_first_token") is not null ||
        ParseDouble(attributes, "gen_ai.response.time_to_first_token") is not null;

    internal static long? SanitizeDurationMs(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value >= 3_600_000)
        {
            return null;
        }

        return (long)Math.Round(value);
    }

    internal static double? ParseDouble(
        Dictionary<string, string> attributes,
        string key)
    {
        if (!attributes.TryGetValue(key, out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) &&
            double.IsFinite(value)
            ? value
            : null;
    }

    internal static Dictionary<string, string> ParseAttributes(
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

    internal static string? AttributeValueToString(JsonElement value)
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

        if (TryGet(value, "arrayValue", out var arrayValue) &&
            arrayValue.ValueKind == JsonValueKind.Object &&
            TryGet(arrayValue, "values", out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in values.EnumerateArray())
            {
                var text = AttributeValueToString(item);
                if (text is not null)
                {
                    parts.Add(text);
                }
            }

            return string.Join(",", parts);
        }

        return null;
    }

    internal static long ParseUnixNanoMilliseconds(
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

    internal static bool TryGet(
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

    internal static string? GetString(
        Dictionary<string, string> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    internal static long? GetLong(
        Dictionary<string, string> attributes,
        string key)
    {
        if (!attributes.TryGetValue(key, out var text))
        {
            return null;
        }

        if (long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) &&
            double.IsFinite(number)
            ? (long)Math.Round(number)
            : null;
    }

    private static readonly Dictionary<string, string> EmptyAttributes =
        new(StringComparer.Ordinal);
}
