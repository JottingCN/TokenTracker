namespace TokenTracker.Core.Pricing;

public readonly record struct PriceTier(
    decimal CachedInput,
    decimal UncachedInput,
    decimal Output);

public readonly record struct ModelPrice(
    PriceTier Idle,
    PriceTier Peak,
    bool PeakAware = true);

public static class ModelPricing
{
    private static readonly ModelPrice DeepSeekFlash = new(
        new PriceTier(0.02m, 1m, 4m),
        new PriceTier(0.04m, 2m, 8m));

    private static readonly ModelPrice DeepSeekV4Pro = new(
        new PriceTier(0.15m, 4.5m, 13.5m),
        new PriceTier(0.30m, 9m, 27m));

    private static readonly ModelPrice KimiK31Pro = Flat(1.30m, 13m, 68m);
    private static readonly ModelPrice MiMoV26Pro = Flat(0.025m, 3m, 6m);
    private static readonly ModelPrice MiMoV26Flash = Flat(0.02m, 1m, 2m);
    private static readonly ModelPrice MiniMaxM31Flash = Flat(0.10m, 1m, 4m);
    private static readonly ModelPrice Glm53 = Flat(2m, 8m, 28m);
    private static readonly ModelPrice Glm53Flash = Flat(0.23m, 0.8m, 2.8m);
    private static readonly ModelPrice Glm53FlashX = Flat(0.57m, 2m, 7m);

    private static readonly Dictionary<string, ModelPrice> Prices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek-flash"] = DeepSeekFlash,
            ["deepseek-v4-flash"] = DeepSeekFlash,
            ["deepseek-v4-flash-vision-exp"] = DeepSeekFlash,
            ["deepseek-flash-vision-exp"] = DeepSeekFlash,
            ["deepseek-v4-pro"] = DeepSeekV4Pro,

            ["kimi-k3.1-pro"] = KimiK31Pro,

            ["mimo-v2.6-pro"] = MiMoV26Pro,
            ["mimo-v2.6-flash"] = MiMoV26Flash,

            ["minimax-m3.1-flash"] = MiniMaxM31Flash,

            ["glm-5.3"] = Glm53,
            ["glm-5.3-flash"] = Glm53Flash,
            ["glm-5.3-flashx"] = Glm53FlashX,
        };

    private static readonly (string Model, ModelPrice Price)[] DefaultEntries =
    [
        ("deepseek-flash", DeepSeekFlash),
        ("deepseek-v4-pro", DeepSeekV4Pro),
        ("kimi-k3.1-pro", KimiK31Pro),
        ("mimo-v2.6-pro", MiMoV26Pro),
        ("mimo-v2.6-flash", MiMoV26Flash),
        ("minimax-m3.1-flash", MiniMaxM31Flash),
        ("glm-5.3", Glm53),
        ("glm-5.3-flash", Glm53Flash),
        ("glm-5.3-flashx", Glm53FlashX),
    ];

    private static IReadOnlyDictionary<string, ModelPrice> _overrides =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<(string Model, ModelPrice Price)> GetDefaultEntries() =>
        DefaultEntries;

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

    public static ModelPrice? GetBuiltInPrice(string model)
    {
        var normalized = NormalizeModel(model);
        return Prices.TryGetValue(normalized, out var price) ? price : null;
    }

    public static ModelPrice? GetPrice(string model)
    {
        var normalized = NormalizeModel(model);
        if (Volatile.Read(ref _overrides).TryGetValue(normalized, out var custom))
        {
            return custom;
        }

        return Prices.TryGetValue(normalized, out var price) ? price : null;
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
        DateTimeOffset timestamp)
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

        var cached = Math.Max(0, cacheReadTokens);
        var uncached = Math.Max(0, inputTokens - cached);
        var output = Math.Max(0, outputTokens);
        var reasoning = Math.Clamp(reasoningTokens, 0, Math.Max(0, outputTokens));

        var cacheCost = cached / 1_000_000m * tier.CachedInput;
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

    private static ModelPrice Flat(decimal cachedInput, decimal uncachedInput, decimal output) =>
        new(
            new PriceTier(cachedInput, uncachedInput, output),
            new PriceTier(cachedInput, uncachedInput, output),
            PeakAware: false);

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
