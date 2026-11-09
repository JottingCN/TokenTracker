using System.Globalization;
using System.Text.Json;
using TokenTracker.Core.Otlp;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Tests;

[Collection(PricingCollection.Name)]
public sealed class PricingTests
{
    [Theory]
    [InlineData("gpt-4o-mini-2024-07-18", "gpt-4o-mini-2024-07-18")]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o-2024-08-06")]
    [InlineData("claude-3-5-sonnet-20241022", "claude-3-5-sonnet-20241022")]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4-20250514")]
    [InlineData("gemini-2.5-pro-preview-05-06", "gemini-2.5-pro-preview-05-06")]
    [InlineData("gpt-4o-latest", "gpt-4o-latest")]
    [InlineData("DeepSeek/deepseek-flash", "deepseek-flash")]
    [InlineData("openai/gpt-4o-mini-2024-07-18", "gpt-4o-mini-2024-07-18")]
    [InlineData("openrouter/deepseek/deepseek-v4.1-flash-1017", "deepseek/deepseek-v4.1-flash-1017")]
    [InlineData("GPT-4O", "gpt-4o")]
    [InlineData("gpt-4o-mini (recommended)", "gpt-4o-mini")]
    public void NormalizesModelNames(string input, string expected)
    {
        Assert.Equal(expected, ModelPricing.NormalizeModel(input));
    }

    [Fact]
    public void NormalizationDependsOnSource()
    {
        Assert.Equal(
            "deepseek-flash",
            ModelPricing.NormalizeModel("deepseek-flash", "copilot-chat"));
        Assert.Equal(
            "anthropic/claude-sonnet-4-20250514",
            ModelPricing.NormalizeModel(
                "anthropic/claude-sonnet-4-20250514",
                "copilot-chat"));
        Assert.Equal(
            "deepseek-flash",
            ModelPricing.NormalizeModel("DeepSeek/deepseek-flash", "agent-host"));
        Assert.Equal(
            "deepseek/deepseek-v4.1-flash-1017",
            ModelPricing.NormalizeModel(
                "openrouter/deepseek/deepseek-v4.1-flash-1017",
                "agent-host"));
    }

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

    [Fact]
    public void PeakFollowsCallEndTimeAcrossBoundary()
    {
        var model = "boundary-test-model";
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
            // 2026-11-04 is a Wednesday; lunch break is 12:00-14:00 Beijing.
            var straddleStart = Utc("2026-11-04T05:59:55Z"); // 13:59:55
            var straddleEnd = Utc("2026-11-04T06:00:08Z");   // 14:00:08
            var idleStart = Utc("2026-11-04T05:50:00Z");     // 13:50:00
            var idleEnd = Utc("2026-11-04T05:52:00Z");       // 13:52:00
            var json = $$"""
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "copilot-chat" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "trace-boundary",
                          "spanId": "span-straddle",
                          "name": "chat",
                          "startTimeUnixNano": "{{straddleStart.ToUnixTimeMilliseconds() * 1_000_000L}}",
                          "endTimeUnixNano": "{{straddleEnd.ToUnixTimeMilliseconds() * 1_000_000L}}",
                          "attributes": [
                            { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                            { "key": "gen_ai.request.model", "value": { "stringValue": "{{model}}" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "10" } }
                          ]
                        },
                        {
                          "traceId": "trace-boundary",
                          "spanId": "span-idle",
                          "name": "chat",
                          "startTimeUnixNano": "{{idleStart.ToUnixTimeMilliseconds() * 1_000_000L}}",
                          "endTimeUnixNano": "{{idleEnd.ToUnixTimeMilliseconds() * 1_000_000L}}",
                          "attributes": [
                            { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                            { "key": "gen_ai.request.model", "value": { "stringValue": "{{model}}" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "10" } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

            using var document = JsonDocument.Parse(json);
            var spans = OtlpTraceParser.ParseTraces(
                document,
                straddleStart.ToUnixTimeMilliseconds());

            Assert.Equal(2, spans.Count);
            var straddle = Assert.Single(spans, span => span.SpanId == "span-straddle");
            var idle = Assert.Single(spans, span => span.SpanId == "span-idle");
            // The request settles at 14:00:08, inside the afternoon peak window.
            Assert.Equal("peak", straddle.PricingPeriod);
            Assert.Equal("idle", idle.PricingPeriod);
        }
        finally
        {
            ModelPricing.SetOverrides(Array.Empty<KeyValuePair<string, ModelPrice>>());
        }
    }
}
