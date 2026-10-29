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
    public void FlashAndProPeakIdleRates()
    {
        var peak = Utc("2026-10-27T02:00:00Z");
        var idle = Utc("2026-10-27T04:00:00Z");

        Assert.Equal(0.02m, ModelPricing.ComputeCostCny(
            "deepseek-flash", 1_000_000, 1_000_000, 0, idle));
        Assert.Equal(0.04m, ModelPricing.ComputeCostCny(
            "deepseek-flash", 1_000_000, 1_000_000, 0, peak));
        Assert.Equal(2m, ModelPricing.ComputeCostCny(
            "deepseek-flash", 1_000_000, 0, 0, peak));
        Assert.Equal(27m, ModelPricing.ComputeCostCny(
            "deepseek-v4-pro", 0, 0, 1_000_000, peak));
    }

    [Fact]
    public void ProviderPrefixedAndLegacyModels()
    {
        var peak = Utc("2026-10-27T02:00:00Z");

        Assert.Equal(0.04m, ModelPricing.ComputeCostCny(
            "DeepSeek/deepseek-flash", 1_000_000, 1_000_000, 0, peak));
        Assert.Equal(0.04m, ModelPricing.ComputeCostCny(
            "customendpoint/DeepSeek/deepseek-v4-flash", 1_000_000, 1_000_000, 0, peak));
        Assert.Null(ModelPricing.ComputeCostCny(
            "gpt-5.5", 1_000_000, 0, 0, peak));
    }

    [Fact]
    public void CachedInputIsClamped()
    {
        var idle = Utc("2026-10-27T04:00:00Z");

        Assert.Equal(0.004m, ModelPricing.ComputeCostCny(
            "deepseek-flash", 100_000, 200_000, 0, idle));
    }

    [Fact]
    public void FlatProviderRates()
    {
        var peak = Utc("2026-10-27T02:00:00Z");
        var idle = Utc("2026-10-27T04:00:00Z");

        Assert.Equal(13m, ModelPricing.ComputeCostCny(
            "kimi-k3.1-pro", 1_000_000, 0, 0, peak));
        Assert.Equal(1.30m, ModelPricing.ComputeCostCny(
            "moonshotai/kimi-k3.1-pro", 1_000_000, 1_000_000, 0, idle));
        Assert.Equal(68m, ModelPricing.ComputeCostCny(
            "kimi-k3.1-pro", 0, 0, 1_000_000, peak));

        Assert.Equal(3m, ModelPricing.ComputeCostCny(
            "mimo-v2.6-pro（实时）", 1_000_000, 0, 0, idle));
        Assert.Equal(0.02m, ModelPricing.ComputeCostCny(
            "mimo-v2.6-flash", 1_000_000, 1_000_000, 0, idle));

        Assert.Equal(0.10m, ModelPricing.ComputeCostCny(
            "MiniMax-M3.1-Flash", 1_000_000, 1_000_000, 0, peak));

        Assert.Equal(8m, ModelPricing.ComputeCostCny(
            "glm-5.3", 1_000_000, 0, 0, peak));
        Assert.Equal(2.8m, ModelPricing.ComputeCostCny(
            "GLM-5.3-Flash", 0, 0, 1_000_000, idle));
        Assert.Equal(0.57m, ModelPricing.ComputeCostCny(
            "glm-5.3-flashx", 1_000_000, 1_000_000, 0, peak));

        Assert.Equal("flat", ModelPricing.GetPricingPeriod("glm-5.3", peak));
        Assert.Equal("peak", ModelPricing.GetPricingPeriod("deepseek-flash", peak));
        Assert.Equal("idle", ModelPricing.GetPricingPeriod("deepseek-flash", idle));
    }
}
