namespace TokenTracker.Core.Models;

public sealed class SummaryDto
{
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long Sessions { get; init; }
    public double CostCny { get; init; }
    public double CacheCostCny { get; init; }
    public double UncachedCostCny { get; init; }
    public double OutputCostCny { get; init; }
    public double ReasoningCostCny { get; init; }
    public long UnpricedCalls { get; init; }

    public double? HitRate =>
        InputTokens > 0 ? (double)CacheReadTokens / InputTokens : null;

    public string CacheCostText => DisplayFormat.Price(CacheCostCny);
    public string UncachedCostText => DisplayFormat.Price(UncachedCostCny);
    public string OutputCostText => DisplayFormat.Price(OutputCostCny);
    public string ReasoningCostText => DisplayFormat.Price(ReasoningCostCny);
    public string ReasoningTokensText => DisplayFormat.Number(ReasoningTokens);
}

public sealed partial record SessionRow
{
    public required string SessionId { get; init; }
    public required string Model { get; init; }
    public string? Title { get; init; }
    public string? WorkspacePath { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public double? HitRate { get; init; }
    public double CostCny { get; init; }
    public double CacheCostCny { get; init; }
    public double UncachedCostCny { get; init; }
    public double OutputCostCny { get; init; }
    public double ReasoningCostCny { get; init; }
    public long UnpricedCalls { get; init; }
    public long LastSeenMs { get; init; }

    public string SessionShort => DisplayFormat.SessionShort(SessionId);
    public string SessionDisplay => DisplayFormat.SessionDisplay(Title, SessionId);
    public string CallsText => DisplayFormat.Number(Calls);
    public string InputText => DisplayFormat.Number(InputTokens);
    public string CacheText => DisplayFormat.Number(CacheReadTokens);
    public string OutputText => DisplayFormat.Number(OutputTokens);
    public string ReasoningText => DisplayFormat.Number(ReasoningTokens);
    public string HitRateText => DisplayFormat.Rate(HitRate);
    public string CostText => DisplayFormat.Cost(CostCny, UnpricedCalls);
    public string CacheCostText => DisplayFormat.Price(CacheCostCny);
    public string UncachedCostText => DisplayFormat.Price(UncachedCostCny);
    public string OutputCostText => DisplayFormat.Price(OutputCostCny);
    public string ReasoningCostText => DisplayFormat.Price(ReasoningCostCny);
    public string LastSeenText => DisplayFormat.LocalTime(LastSeenMs);
}

public sealed partial record CallRow
{
    public long StartTimeMs { get; init; }
    public required string Model { get; init; }
    public string? SessionId { get; init; }
    public string? Title { get; init; }
    public bool IsMain { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public double? HitRate { get; init; }
    public double? CostCny { get; init; }
    public double CacheCostCny { get; init; }
    public double UncachedCostCny { get; init; }
    public double OutputCostCny { get; init; }
    public double ReasoningCostCny { get; init; }
    public string? PricingPeriod { get; init; }

    public string TimeText => DisplayFormat.LocalTime(StartTimeMs);
    public string SessionShort => DisplayFormat.SessionShort(SessionId);
    public string SessionDisplay => DisplayFormat.SessionDisplay(Title, SessionId);
    public string InputText => DisplayFormat.Number(InputTokens);
    public string CacheText => DisplayFormat.Number(CacheReadTokens);
    public string OutputText => DisplayFormat.Number(OutputTokens);
    public string HitRateText => DisplayFormat.Rate(HitRate);
    public string CostText => CostCny is null ? "-" : DisplayFormat.Cost(CostCny.Value);
    public string CacheCostText => DisplayFormat.Price(CacheCostCny);
    public string UncachedCostText => DisplayFormat.Price(UncachedCostCny);
    public string OutputCostText => DisplayFormat.Price(OutputCostCny);
    public string ReasoningCostText => DisplayFormat.Price(ReasoningCostCny);
    public string PeriodText => DisplayFormat.Period(PricingPeriod);
    public string KindText => IsMain ? "main" : "util";
}

public sealed partial class ModelRow
{
    public required string Model { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long OutputTokens { get; init; }
    public double? HitRate { get; init; }
    public double CostCny { get; init; }
    public long UnpricedCalls { get; init; }

    public string CallsText => DisplayFormat.Number(Calls);
    public string TokensText =>
        $"{DisplayFormat.Number(InputTokens)} / {DisplayFormat.Number(CacheReadTokens)}";
    public string HitRateText => DisplayFormat.Rate(HitRate);
    public string CostText => DisplayFormat.Cost(CostCny, UnpricedCalls);
    public double HitRatePercent => (HitRate ?? 0) * 100;
}
