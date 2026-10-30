using System.Globalization;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Tests;

public sealed class PricingTests
{
    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Fact]
    public void PeakWindowsAndLunchBreak()
    {
        Assert.True(BeijingCalendar.IsPeak(Utc("2026-10-27T01:00:00Z")));  // 09:00
        Assert.True(BeijingCalendar.IsPeak(Utc("2026-10-27T03:59:00Z")));  // 11:59
        Assert.False(BeijingCalendar.IsPeak(Utc("2026-10-27T04:00:00Z"))); // 12:00
        Assert.True(BeijingCalendar.IsPeak(Utc("2026-10-27T06:00:00Z")));  // 14:00
        Assert.True(BeijingCalendar.IsPeak(Utc("2026-10-27T09:59:00Z")));  // 17:59
        Assert.False(BeijingCalendar.IsPeak(Utc("2026-10-27T10:00:00Z"))); // 18:00
    }

    [Fact]
    public void WeekendsAndHolidaysAreIdle()
    {
        Assert.False(BeijingCalendar.IsPeak(Utc("2026-10-31T02:00:00Z"))); // Saturday
        Assert.False(BeijingCalendar.IsPeak(Utc("2026-10-01T02:00:00Z"))); // National Day
    }

    [Fact]
    public void ConfiguredPriceOverridesAndNormalizesProviderPrefix()
    {
        var model = "pricing-test-model";
        var peak = Utc("2026-10-27T02:00:00Z");
        var idle = Utc("2026-10-27T04:00:00Z");
        var price = new ModelPrice(
            new PriceTier(0.02m, 1m, 4m),
            new PriceTier(0.04m, 2m, 8m));

        ModelPricing.SetOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>(model, price),
        });
        try
        {
            Assert.Equal(0.02m, ModelPricing.ComputeCostCny(
                $"provider/{model}", 1_000_000, 1_000_000, 0, idle));
            Assert.Equal(0.04m, ModelPricing.ComputeCostCny(
                model, 1_000_000, 1_000_000, 0, peak));
            Assert.Equal(2m, ModelPricing.ComputeCostCny(
                model, 1_000_000, 0, 0, peak));
            Assert.Equal("peak", ModelPricing.GetPricingPeriod(model, peak));
            Assert.Equal("idle", ModelPricing.GetPricingPeriod(model, idle));
            Assert.Null(ModelPricing.ComputeCostCny(
                "unconfigured-model", 1_000_000, 0, 0, peak));
            Assert.Equal(
                "unknown",
                ModelPricing.GetPricingPeriod("unconfigured-model", peak));
        }
        finally
        {
            ModelPricing.SetOverrides(Array.Empty<KeyValuePair<string, ModelPrice>>());
        }
    }

    [Fact]
    public void CachedInputIsClamped()
    {
        var model = "pricing-clamp-model";
        var idle = Utc("2026-10-27T04:00:00Z");
        ModelPricing.SetOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>(
                model,
                new ModelPrice(
                    new PriceTier(0.02m, 1m, 4m),
                    new PriceTier(0.04m, 2m, 8m))),
        });
        try
        {
            Assert.Equal(0.004m, ModelPricing.ComputeCostCny(
                model, 100_000, 200_000, 0, idle));
        }
        finally
        {
            ModelPricing.SetOverrides(Array.Empty<KeyValuePair<string, ModelPrice>>());
        }
    }

    [Fact]
    public void CacheWriteRateFallsBackToUncachedInput()
    {
        var idle = Utc("2026-10-27T04:00:00Z");
        var fallback = new ModelPrice(
            new PriceTier(1m, 10m, 50m),
            new PriceTier(1m, 10m, 50m),
            PeakAware: false);
        var explicitRate = new ModelPrice(
            new PriceTier(1m, 10m, 50m, 12.5m),
            new PriceTier(1m, 10m, 50m, 12.5m),
            PeakAware: false);

        var fallbackDetail = ModelPricing.EvaluateDetailed(
            fallback, 1_000_000, 0, 0, 0, idle, cacheCreationTokens: 1_000_000);
        Assert.Equal(10m, fallbackDetail.CacheCost);
        Assert.Equal(0m, fallbackDetail.UncachedCost);
        Assert.Equal(10m, fallbackDetail.CostCny);

        var explicitDetail = ModelPricing.EvaluateDetailed(
            explicitRate, 1_000_000, 0, 0, 0, idle, cacheCreationTokens: 1_000_000);
        Assert.Equal(12.5m, explicitDetail.CacheCost);
        Assert.Equal(12.5m, explicitDetail.CostCny);
    }

    [Fact]
    public void LongContextThresholdSwitchesEntireRequest()
    {
        var price = new ModelPrice(
            new PriceTier(1m, 10m, 50m),
            new PriceTier(2m, 20m, 100m),
            PeakAware: true,
            new LongContextPrice(
                272_000,
                new PriceTier(2m, 20m, 75m),
                new PriceTier(4m, 40m, 150m)));
        var idle = Utc("2026-10-27T04:00:00Z");
        var peak = Utc("2026-10-27T02:00:00Z");

        Assert.Equal(2.072m, ModelPricing.EvaluateDetailed(
            price, 272_000, 72_000, 0, 0, idle).CostCny);

        var longIdle = ModelPricing.EvaluateDetailed(
            price, 272_001, 72_000, 0, 0, idle);
        Assert.Equal(4.14402m, longIdle.CostCny);
        Assert.Equal(0.144m, longIdle.CacheCost);
        Assert.Equal(4.00002m, longIdle.UncachedCost);

        Assert.Equal(8.28804m, ModelPricing.EvaluateDetailed(
            price, 272_001, 72_000, 0, 0, peak).CostCny);
        Assert.Equal(75m, ModelPricing.EvaluateDetailed(
            price, 272_001, 0, 1_000_000, 0, idle).OutputCost);
    }
}
