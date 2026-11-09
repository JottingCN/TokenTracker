using System.Text;
using System.Text.Json;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Otlp;

public static partial class OtlpTraceParser
{
    private static readonly HashSet<string> ExcludedAttributeKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "gen_ai.input.messages",
            "gen_ai.output.messages",
            "gen_ai.system_instructions",
            "gen_ai.tool.definitions",
            "gen_ai.tool.call.arguments",
            "gen_ai.tool.call.result",
            "copilot_chat.hook_input",
            "copilot_chat.hook_output",
            "copilot_chat.repo.remote_url",
            "github.copilot.tool.parameters.command",
            "github.copilot.tool.parameters.file_path",
            "github.copilot.tool.parameters.mcp_server_name",
            "user.name",
            "process.user.name",
            "host.name",
            "vscode.agent_host.session.title",
            "vscode.agent_host.session.uri",
        };

    private static readonly HashSet<string> ExtraMetadataKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "success",
            "action",
            "rating",
            "type",
            "outcome",
            "partner_agent",
            "language_id",
            "edit_source",
            "edit_surface",
            "duration",
            "duration_ms",
            "turn.index",
            "tool_call_count",
            "accepted",
            "edit_count",
            "edit_line_count",
            "reply_type",
            "is_notebook",
            "has_remaining_edits",
            "lines_added",
            "lines_removed",
            "line_count",
            "survival_rate_four_gram",
            "survival_rate_no_revert",
            "time_delay_ms",
            "did_branch_change",
            "request_id",
            "session_id",
            "conversation_id",
        };

    private static AgentRunRecord BuildAgentRun(
        JsonElement span,
        string traceId,
        string spanId,
        string? parentSpanId,
        string spanName,
        Dictionary<string, string> attributes,
        string? serviceName,
        string? serviceNamespace,
        string source,
        long receivedAtMs)
    {
        var startMs = ParseUnixNanoMilliseconds(
            span,
            "startTimeUnixNano",
            receivedAtMs);
        var endMs = TryGet(span, "endTimeUnixNano", out _)
            ? ParseUnixNanoMilliseconds(span, "endTimeUnixNano", startMs)
            : (long?)null;
        var provider =
            GetString(attributes, "gen_ai.provider.name") ?? serviceName;
        var requestModel = GetString(attributes, "gen_ai.request.model");
        var responseModel = GetString(attributes, "gen_ai.response.model");
        var errorType = ParseErrorType(span, attributes);

        return new AgentRunRecord
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            IsRoot = string.IsNullOrWhiteSpace(parentSpanId),
            SessionId = ParseSessionId(attributes),
            // Agent Host names the run in the span name ("invoke_agent explore")
            // and leaves gen_ai.agent.name unset for most turns, which is why the
            // agent column used to be blank.
            AgentName =
                GetString(attributes, "gen_ai.agent.name") ??
                AgentNameFromSpanName(spanName),
            AgentType = GetString(attributes, "github.copilot.agent.type"),
            ProviderName = provider,
            RequestModel = NormalizeModel(requestModel, source),
            ResponseModel = NormalizeModel(responseModel, source),
            ServiceName = serviceName,
            ServiceNamespace = serviceNamespace,
            Source = source,
            GitRepository = NormalizeGitRepository(
                GetString(attributes, "github.copilot.git.repository") ??
                GetString(attributes, "copilot_chat.repo.remote_url")),
            GitBranch = GetString(
                attributes,
                "github.copilot.git.branch") ??
                GetString(attributes, "copilot_chat.repo.head_branch_name"),
            GitCommit = GetString(
                attributes,
                "github.copilot.git.commit_sha") ??
                GetString(attributes, "copilot_chat.repo.head_commit_hash"),
            GithubOrg = GetString(attributes, "github.copilot.github.org"),
            TurnCount = GetLong(attributes, "copilot_chat.turn_count"),
            InputTokens =
                GetLong(attributes, "gen_ai.usage.input_tokens") ?? 0,
            OutputTokens =
                GetLong(attributes, "gen_ai.usage.output_tokens") ?? 0,
            CacheReadTokens =
                GetLong(attributes, "gen_ai.usage.cache_read.input_tokens") ?? 0,
            CacheCreationTokens =
                GetLong(
                    attributes,
                    "gen_ai.usage.cache_creation.input_tokens") ?? 0,
            ReasoningTokens =
                GetLong(attributes, "gen_ai.usage.reasoning_tokens") ??
                GetLong(attributes, "gen_ai.usage.reasoning.output_tokens") ??
                0,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            IsError = errorType is not null,
            ErrorType = errorType,
            ReceivedAtMs = receivedAtMs,
        };
    }

    private static string? NormalizeModel(string? model, string source) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : ModelPricing.NormalizeModel(model, source);

    /// <summary>
    /// Agent Host puts the run's name in the span name ("invoke_agent explore",
    /// "invoke_agent GitHub Copilot Chat") and does not always set
    /// <c>gen_ai.agent.name</c>, so the name has to come from the label too.
    /// </summary>
    internal static string? AgentNameFromSpanName(string? spanName)
    {
        const string prefix = "invoke_agent";
        if (string.IsNullOrWhiteSpace(spanName) ||
            !spanName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = spanName[prefix.Length..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private static string? ParseErrorType(
        JsonElement span,
        Dictionary<string, string> attributes)
    {
        return ErrorTypeFrom(
            GetString(attributes, "error.type"),
            IsErrorSpan(span));
    }

    internal static string? ErrorTypeFrom(
        string? explicitType,
        bool isError) =>
        !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : isError
                ? "Error"
                : null;

    private static double? ParseHookDurationMs(
        Dictionary<string, string> attributes)
    {
        var seconds = ParseDouble(
            attributes,
            "github.copilot.hook.duration");
        if (seconds is not null)
        {
            return seconds.Value * 1000d;
        }

        return ParseDouble(attributes, "copilot_chat.hook.duration");
    }

    internal static string? SerializeMetadataAttributes(
        Dictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(MaxAttributesLength, 256));
        foreach (var (key, value) in attributes.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            if (!IsMetadataAttribute(key))
            {
                continue;
            }

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

        return builder.Length > 0 ? builder.ToString() : null;
    }

    internal static bool IsMetadataAttribute(string key)
    {
        if (ExcludedAttributeKeys.Contains(key))
        {
            return false;
        }

        if (ExtraMetadataKeys.Contains(key))
        {
            return true;
        }

        return key.StartsWith("gen_ai.", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("copilot_chat.", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("github.copilot.", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("server.", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("error.", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("service.", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeGitRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var at = text.IndexOf('@');
            var colon = text.IndexOf(':', at + 1);
            if (colon > at)
            {
                text = $"{text[(at + 1)..colon]}/{text[(colon + 1)..]}";
            }
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                 (uri.Scheme == Uri.UriSchemeHttp ||
                  uri.Scheme == Uri.UriSchemeHttps))
        {
            text = $"{uri.Host}{uri.AbsolutePath}";
        }

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.Trim('/');
    }
}
