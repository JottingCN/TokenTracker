using System.Globalization;
using System.Text.Json;
using TokenTracker.Core.Otlp;

namespace TokenTracker.Core.Tests;

public sealed class OtlpTraceParserTests
{
    private static readonly DateTimeOffset PeakUtc =
        DateTimeOffset.Parse(
            "2026-10-27T02:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    [Fact]
    public void ParsesMainAndUtilityChatSpans()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var json = BuildJson(startNanos);

        using var document = JsonDocument.Parse(json);
        var spans = OtlpTraceParser.ParseTraces(document, PeakUtc.ToUnixTimeMilliseconds());

        Assert.Equal(2, spans.Count);

        var main = Assert.Single(spans, span => span.IsMain);
        Assert.Equal("trace-main", main.TraceId);
        Assert.Equal("span-main", main.SpanId);
        Assert.Equal("deepseek-flash", main.Model);
        Assert.Equal("session-1", main.SessionId);
        Assert.Equal(8900, main.InputTokens);
        Assert.Equal(1152, main.CacheReadTokens);
        Assert.Equal(2, main.OutputTokens);
        Assert.Equal("peak", main.PricingPeriod);
        Assert.Equal(PeakUtc.ToUnixTimeMilliseconds(), main.StartTimeMs);
        Assert.NotNull(main.HitRate);
        Assert.Equal(0.129438202247191, main.HitRate!.Value, 6);
        Assert.NotNull(main.CostCny);
        Assert.Equal(0.01555808, main.CostCny!.Value, 8);
        Assert.Equal("copilot-chat", main.ServiceName);

        var utility = Assert.Single(spans, span => !span.IsMain);
        Assert.Null(utility.SessionId);
        Assert.Equal(422, utility.InputTokens);
        Assert.Equal(256, utility.CacheReadTokens);
        Assert.Equal(28, utility.OutputTokens);
    }

    [Fact]
    public void ParsesAgentHostResponseModelAndChatSessionId()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var json = $$"""
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "github-copilot" } },
                  { "key": "service.namespace", "value": { "stringValue": "vscode.agent-host" } }
                ]
              },
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "traceId": "agent-trace",
                      "spanId": "agent-span",
                      "name": "chat",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "auto" } },
                        { "key": "gen_ai.response.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "10" } },
                        { "key": "copilot_chat.copilot_usage_nano_aiu", "value": { "intValue": "1250000000" } },
                        { "key": "copilot_chat.chat_session_id", "value": { "stringValue": "agent-chat-1" } }
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
        var span = Assert.Single(
            OtlpTraceParser.ParseTraces(document, PeakUtc.ToUnixTimeMilliseconds()));

        Assert.Equal("deepseek-flash", span.Model);
        Assert.Equal("agent-chat-1", span.SessionId);
        Assert.Equal(1_250_000_000, span.NanoAiu);
        Assert.True(span.IsMain);
        Assert.NotNull(span.CostCny);
        Assert.Equal("github-copilot", span.ServiceName);
    }

    [Fact]
    public void MergesAnthropicCacheTokensIntoTotalInput()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var json = $$"""
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "claude-code" } }
                ]
              },
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "traceId": "claude-trace",
                      "spanId": "claude-span",
                      "name": "chat",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.provider.name", "value": { "stringValue": "anthropic" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                        { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "2000" } },
                        { "key": "gen_ai.usage.cache_creation.input_tokens", "value": { "intValue": "500" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "100" } }
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
        var span = Assert.Single(
            OtlpTraceParser.ParseTraces(document, PeakUtc.ToUnixTimeMilliseconds()));

        Assert.Equal(3500, span.InputTokens);
        Assert.Equal(2000, span.CacheReadTokens);
        Assert.Equal(500, span.CacheCreationTokens);
        Assert.Equal(2000d / 3500d, span.HitRate!.Value, 6);
        Assert.Equal(0.00388, span.CostCny!.Value, 8);
    }

    [Fact]
    public void SkipsNonChatAndIncompleteSpans()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var json = $$"""
        {
          "resourceSpans": [
            {
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "traceId": "t1",
                      "spanId": "s1",
                      "name": "execute_tool",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "execute_tool" } }
                      ]
                    },
                    {
                      "traceId": "t2",
                      "spanId": "s2",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } }
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
        var spans = OtlpTraceParser.ParseTraces(document, 0);

        Assert.Empty(spans);
    }

    private static string BuildJson(long startNanos)
    {
        var endNanos = startNanos + 500_000_000;
        return $$"""
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "copilot-chat" } },
                  { "key": "service.version", "value": { "stringValue": "0.71.0" } },
                  { "key": "session.id", "value": { "stringValue": "telemetry-1" } }
                ]
              },
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "traceId": "trace-main",
                      "spanId": "span-main",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{endNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "8900" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "2" } },
                        { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "1152" } },
                        { "key": "gen_ai.usage.cache_creation.input_tokens", "value": { "intValue": "0" } },
                        { "key": "gen_ai.usage.reasoning_tokens", "value": { "intValue": "0" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
                      ]
                    },
                    {
                      "traceId": "trace-util",
                      "spanId": "span-util",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "422" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "28" } },
                        { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "256" } }
                      ]
                    },
                    {
                      "traceId": "trace-tool",
                      "spanId": "span-tool",
                      "name": "execute_tool",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "execute_tool" } }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
    }
}
