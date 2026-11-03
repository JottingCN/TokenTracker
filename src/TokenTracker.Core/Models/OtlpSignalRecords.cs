namespace TokenTracker.Core.Models;

public sealed class AgentRunRecord
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public bool IsRoot { get; init; }
    public string? SessionId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentType { get; init; }
    public string? ProviderName { get; init; }
    public string? RequestModel { get; init; }
    public string? ResponseModel { get; init; }
    public string? ServiceName { get; init; }
    public string? ServiceNamespace { get; init; }
    public string? Source { get; init; }
    public string? GitRepository { get; init; }
    public string? GitBranch { get; init; }
    public string? GitCommit { get; init; }
    public string? GithubOrg { get; init; }
    public long? TurnCount { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long StartTimeMs { get; init; }
    public long? EndTimeMs { get; init; }
    public bool IsError { get; init; }
    public string? ErrorType { get; init; }
    public long ReceivedAtMs { get; init; }
}

public sealed class OtlpMetricPointRecord
{
    public required string Source { get; init; }
    public required string SeriesKey { get; init; }
    public required string MetricName { get; init; }
    public required string MetricKind { get; init; }
    public string? Temporality { get; init; }
    public string? ServiceName { get; init; }
    public string? ScopeName { get; init; }
    public string? Attributes { get; init; }
    public string? WindowSessionId { get; init; }
    public string? DirectSessionId { get; init; }
    public string? ToolCallId { get; init; }
    public long StartTimeMs { get; init; }
    public long EndTimeMs { get; init; }
    public double? Value { get; init; }
    public long? Count { get; init; }
    public double? Sum { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? BucketCounts { get; init; }
    public string? ExplicitBounds { get; init; }
    public long ReceivedAtMs { get; init; }
}
