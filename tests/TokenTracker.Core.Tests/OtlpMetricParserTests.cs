using System.Text.Json;
using TokenTracker.Core.Otlp;

namespace TokenTracker.Core.Tests;

public sealed class OtlpMetricParserTests
{
    // Shape of a Copilot Chat batch: GenAI metrics ship next to the outcome
    // counters, and only the outcome ones are stored.
    private const string Payload = """
        {
          "resourceMetrics": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "copilot-chat" } }
                ]
              },
              "scopeMetrics": [
                {
                  "scope": { "name": "copilot-chat", "version": "0.73.0" },
                  "metrics": [
                    {
                      "name": "gen_ai.client.operation.duration",
                      "histogram": {
                        "aggregationTemporality": 2,
                        "dataPoints": [
                          {
                            "attributes": [
                              { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } }
                            ],
                            "startTimeUnixNano": "1794285971949000000",
                            "timeUnixNano": "1794285977950000000",
                            "count": "1",
                            "sum": 354
                          }
                        ]
                      }
                    },
                    {
                      "name": "copilot_chat.tool.call.count",
                      "sum": {
                        "aggregationTemporality": 2,
                        "isMonotonic": true,
                        "dataPoints": [
                          {
                            "attributes": [
                              { "key": "gen_ai.tool.name", "value": { "stringValue": "read_file" } },
                              { "key": "success", "value": { "stringValue": "true" } }
                            ],
                            "startTimeUnixNano": "1794285971949000000",
                            "timeUnixNano": "1794285977950000000",
                            "asInt": "3"
                          }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void StoresOnlyOutcomeMetricsAndReportsTheFilteredNames()
    {
        using var document = JsonDocument.Parse(Payload);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var points = OtlpMetricParser.Parse(document, 1_000, null, seen);

        var point = Assert.Single(points);
        Assert.Equal("copilot_chat.tool.call.count", point.MetricName);
        Assert.Equal("copilot-chat", point.Source);
        Assert.Equal("read_file", AttributeValue(point.Attributes, "gen_ai.tool.name"));
        // The GenAI metric is dropped but still reported: an empty metric table is
        // otherwise indistinguishable from "nothing was exported at all".
        Assert.Contains("gen_ai.client.operation.duration", seen);
        Assert.Contains("copilot_chat.tool.call.count", seen);
        Assert.Equal(2, seen.Count);
    }

    private static string? AttributeValue(string? serialized, string key)
    {
        if (string.IsNullOrEmpty(serialized))
        {
            return null;
        }

        // Attributes serialize as newline-separated `key=value` pairs.
        foreach (var line in serialized.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 &&
                string.Equals(
                    line[..separator],
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..];
            }
        }

        return null;
    }
}
