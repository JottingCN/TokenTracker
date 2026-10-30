namespace TokenTracker.Core.Pricing;

public readonly record struct PriceTier(
    decimal CachedInput,
    decimal UncachedInput,
    decimal Output,
    decimal? CacheWrite = null)
{
    public decimal EffectiveCacheWrite => CacheWrite ?? UncachedInput;
}

public readonly record struct LongContextPrice(
    long ThresholdTokens,
    PriceTier Idle,
    PriceTier Peak);

public readonly record struct ModelPrice(
    PriceTier Idle,
    PriceTier Peak,
    bool PeakAware = true,
    LongContextPrice? LongContext = null);

public static class ModelPricing
{
    private static IReadOnlyDictionary<string, ModelPrice> _overrides =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    public static void SetOverrides(
        IEnumerable<KeyValuePair<string, ModelPrice>> overrides)
    {
        var map = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, price) in overrides)
        {
            map[NormalizeModel(model)] = price;
        }

        Volatile.Write(ref _overrides, map);
    }

    public static string NormalizeModel(string model)
    {
        var slash = model.LastIndexOf('/');
        var leaf = slash >= 0 ? model[(slash + 1)..] : model;
        return StripAnnotation(leaf.Trim()).ToLowerInvariant();
    }

    public static ModelPrice? GetPrice(string model)
    {
        var normalized = NormalizeModel(model);
        return Volatile.Read(ref _overrides).TryGetValue(normalized, out var custom)
            ? custom
            : null;
    }

    public static decimal? ComputeCostCny(
        string model,
        long inputTokens,
        long cacheReadTokens,
        long outputTokens,
        DateTimeOffset timestamp) =>
        Evaluate(
            GetPrice(model),
            inputTokens,
            cacheReadTokens,
            outputTokens,
            timestamp).CostCny;

    public static string GetPricingPeriod(string model, DateTimeOffset timestamp)
    {
        var price = GetPrice(model);
        if (price is null)
        {
            return "unknown";
        }

        if (price is { PeakAware: false })
        {
            return "flat";
        }

        return BeijingCalendar.IsPeak(timestamp) ? "peak" : "idle";
    }

    public static (decimal? CostCny, string Period) Evaluate(
        ModelPrice? price,
        long inputTokens,
        long cacheReadTokens,
        long outputTokens,
        DateTimeOffset timestamp)
    {
        var detail = EvaluateDetailed(
            price,
            inputTokens,
            cacheReadTokens,
            outputTokens,
            0,
            timestamp);
        return (detail.CostCny, detail.Period);
    }

    public static (
        decimal? CostCny,
        string Period,
        decimal? CacheCost,
        decimal? UncachedCost,
        decimal? OutputCost,
        decimal? ReasoningCost) EvaluateDetailed(
        ModelPrice? price,
        long inputTokens,
        long cacheReadTokens,
        long outputTokens,
        long reasoningTokens,
        DateTimeOffset timestamp,
        long cacheCreationTokens = 0)
    {
        if (price is null)
        {
            return (null, "unknown", null, null, null, null);
        }

        var period = price is { PeakAware: false }
            ? "flat"
            : BeijingCalendar.IsPeak(timestamp) ? "peak" : "idle";

        var tier = price.Value.PeakAware && period == "peak"
            ? price.Value.Peak
            : price.Value.Idle;

        if (price.Value.LongContext is { } longContext &&
            inputTokens > longContext.ThresholdTokens)
        {
            tier = price.Value.PeakAware && period == "peak"
                ? longContext.Peak
                : longContext.Idle;
        }

        var cached = Math.Max(0, cacheReadTokens);
        var cacheWrite = Math.Clamp(
            cacheCreationTokens,
            0,
            Math.Max(0, inputTokens - cached));
        var uncached = Math.Max(0, inputTokens - cached - cacheWrite);
        var output = Math.Max(0, outputTokens);
        var reasoning = Math.Clamp(reasoningTokens, 0, Math.Max(0, outputTokens));

        var cacheCost = cached / 1_000_000m * tier.CachedInput +
            cacheWrite / 1_000_000m * tier.EffectiveCacheWrite;
        var uncachedCost = uncached / 1_000_000m * tier.UncachedInput;
        var outputCost = output / 1_000_000m * tier.Output;
        var reasoningCost = reasoning / 1_000_000m * tier.Output;

        return (
            cacheCost + uncachedCost + outputCost,
            period,
            cacheCost,
            uncachedCost,
            outputCost,
            reasoningCost);
    }

    private static string StripAnnotation(string value)
    {
        var close = value.EndsWith('）') ? '）' : value.EndsWith(')') ? ')' : '\0';
        if (close == '\0')
        {
            return value;
        }

        var open = close == '）' ? value.LastIndexOf('（') : value.LastIndexOf('(');
        return open > 0 ? value[..open].Trim() : value;
    }
}
