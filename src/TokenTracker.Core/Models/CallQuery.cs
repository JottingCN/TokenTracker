namespace TokenTracker.Core.Models;

public sealed class CallQuery
{
    public long FromMs { get; init; }
    public long ToMs { get; init; } = long.MaxValue;
    public bool IncludeUtility { get; init; } = true;
    public string? Model { get; init; }
    public string? Source { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyCollection<string>? SessionIds { get; init; }
    public string? Query { get; init; }
    public string? Kind { get; init; }
    public bool? Priced { get; init; }
    public string? Period { get; init; }
    public bool? HasError { get; init; }
    public string? Agent { get; init; }
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}
