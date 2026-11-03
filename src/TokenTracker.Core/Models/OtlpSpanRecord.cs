namespace TokenTracker.Core.Models;

public sealed class OtlpSpanRecord
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? Name { get; init; }
    public long StartTimeMs { get; init; }
    public long? EndTimeMs { get; init; }
    public string? ServiceName { get; init; }
    public string? ServiceNamespace { get; init; }
    public string? ScopeName { get; init; }
    public string? Source { get; init; }
    public string? WindowSessionId { get; init; }
    public string? Model { get; init; }
    public string? SessionId { get; init; }
    public bool IsMain { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long? TtftMs { get; init; }
    public long? NanoAiu { get; init; }
    public double? HitRate { get; init; }
    public double? CostCny { get; init; }
    public double? CacheCostCny { get; init; }
    public double? UncachedCostCny { get; init; }
    public double? OutputCostCny { get; init; }
    public double? ReasoningCostCny { get; init; }
    public string? PricingPeriod { get; init; }
    public string? ErrorType { get; init; }
    public string? ProviderName { get; init; }
    public string? AgentName { get; init; }
    public string? FinishReasons { get; init; }
    public long ReceivedAtMs { get; init; }
}
