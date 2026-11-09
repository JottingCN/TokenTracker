namespace TokenTracker.Core.Models;

public sealed class OtlpEventRecord
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Operation { get; init; }
    public string? Name { get; init; }
    public string? ToolName { get; init; }
    public string? ToolType { get; init; }
    public string? EditType { get; init; }
    public string? SkillName { get; init; }
    public string? McpServerHash { get; init; }
    public string? McpToolName { get; init; }
    public string? HookDecision { get; init; }
    public double? HookDurationMs { get; init; }
    public string? HookToolNames { get; init; }
    public string? HookType { get; init; }
    public string? HookResultKind { get; init; }
    public string? PermissionKind { get; init; }
    public string? PermissionResult { get; init; }
    public string? CallId { get; init; }
    public string? SessionId { get; init; }
    public string? ServiceName { get; init; }
    public string? ServiceNamespace { get; init; }
    public string? ScopeName { get; init; }
    public string? Source { get; init; }
    public string? WindowSessionId { get; init; }
    public string? ErrorType { get; init; }
    public string? AgentName { get; init; }
    public string? AgentType { get; init; }
    public string? ProviderName { get; init; }
    public long StartTimeMs { get; init; }
    public long? EndTimeMs { get; init; }
    public bool IsError { get; init; }
    public long ReceivedAtMs { get; init; }
}

public sealed record OtlpParseResult(
    IReadOnlyList<OtlpSpanRecord> Spans,
    IReadOnlyList<OtlpEventRecord> Events,
    IReadOnlyList<AgentRunRecord> AgentRuns);
