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
        Assert.Equal(5, main.ReasoningTokens);
        Assert.Equal(2, main.OutputTokens);
        Assert.Equal("unknown", main.PricingPeriod);
        Assert.Equal(PeakUtc.ToUnixTimeMilliseconds(), main.StartTimeMs);
        Assert.NotNull(main.HitRate);
        Assert.Equal(0.129438202247191, main.HitRate!.Value, 6);
        Assert.Null(main.CostCny);
        Assert.Equal("copilot-chat", main.ServiceName);
        Assert.Equal("copilot-chat", main.Source);

        var utility = Assert.Single(spans, span => !span.IsMain);
        Assert.Null(utility.SessionId);
        Assert.Equal(422, utility.InputTokens);
        Assert.Equal(256, utility.CacheReadTokens);
        Assert.Equal(28, utility.OutputTokens);
    }

    [Fact]
    public void IgnoresGenericFirstChunkCloseToTotalDuration()
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
                      "traceId": "ttft-trace",
                      "spanId": "ttft-span",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{startNanos + 1_000_000_000}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "100" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "20" } },
                        { "key": "gen_ai.response.time_to_first_chunk", "value": { "doubleValue": 0.95 } }
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
            OtlpTraceParser.ParseTraces(
                document,
                PeakUtc.ToUnixTimeMilliseconds()));
        Assert.Null(span.TtftMs);
    }

    [Fact]
    public void UsesParentChatSessionForSubagentSpans()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var endNanos = startNanos + 1_000_000_000;
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
                      "traceId": "trace-sub",
                      "spanId": "span-chat",
                      "name": "chat",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{endNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.response.model", "value": { "stringValue": "gpt-4o-mini-2024-07-18" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "100" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "10" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "call_00_sub" } },
                        { "key": "copilot_chat.chat_session_id", "value": { "stringValue": "call_00_sub" } },
                        { "key": "gen_ai.conversation.id", "value": { "stringValue": "call_00_sub" } },
                        { "key": "copilot_chat.parent_chat_session_id", "value": { "stringValue": "root-session" } }
                      ]
                    },
                    {
                      "traceId": "trace-sub",
                      "spanId": "span-tool",
                      "parentSpanId": "span-sub",
                      "name": "execute_tool",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{endNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "execute_tool" } },
                        { "key": "gen_ai.tool.name", "value": { "stringValue": "read_file" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "call_00_sub" } },
                        { "key": "copilot_chat.parent_chat_session_id", "value": { "stringValue": "root-session" } }
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
        var result = OtlpTraceParser.Parse(
            document,
            PeakUtc.ToUnixTimeMilliseconds());

        Assert.Equal(
            "root-session",
            Assert.Single(result.Spans).SessionId);
        Assert.Equal(
            "root-session",
            Assert.Single(result.Events).SessionId);
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
                        { "key": "gen_ai.response.model", "value": { "stringValue": "DeepSeek/deepseek-flash" } },
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
        Assert.Null(span.CostCny);
        Assert.Equal("github-copilot", span.ServiceName);
        Assert.Equal("agent-host", span.Source);
    }

    [Fact]
    public void UsesNamespaceToSeparateAgentHostFromCopilotChat()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var json = $$"""
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "copilot-chat" } },
                  { "key": "service.namespace", "value": { "stringValue": "vscode.agent-host" } }
                ]
              },
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "traceId": "ns-trace",
                      "spanId": "ns-span",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "10" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
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

        Assert.Equal("copilot-chat", span.ServiceName);
        Assert.Equal("agent-host", span.Source);
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
        Assert.Null(span.CostCny);
    }

    [Fact]
    public void ParsesTtftAndTrackedEvents()
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
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
                      "traceId": "trace-chat",
                      "spanId": "span-chat",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{startNanos + 2_000_000_000}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "100" } },
                        { "key": "copilot_chat.time_to_first_token", "value": { "intValue": "850" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
                      ]
                    },
                    {
                      "traceId": "trace-tool",
                      "spanId": "span-tool",
                      "parentSpanId": "span-agent",
                      "name": "execute_tool read_file",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{startNanos + 500_000_000}}",
                      "status": { "code": 2 },
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "execute_tool" } },
                        { "key": "gen_ai.tool.name", "value": { "stringValue": "read_file" } },
                        { "key": "gen_ai.tool.type", "value": { "stringValue": "function" } },
                        { "key": "gen_ai.tool.call.id", "value": { "stringValue": "call-1" } },
                        { "key": "github.copilot.tool.parameters.edit_type", "value": { "stringValue": "str_replace" } },
                        { "key": "github.copilot.tool.parameters.skill_name", "value": { "stringValue": "review" } },
                        { "key": "github.copilot.tool.parameters.mcp_server_name_hash", "value": { "stringValue": "hash-1" } },
                        { "key": "github.copilot.tool.parameters.mcp_tool_name", "value": { "stringValue": "search" } },
                        { "key": "error.type", "value": { "stringValue": "FileNotFoundError" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
                      ]
                    },
                    {
                      "traceId": "trace-agent",
                      "spanId": "span-agent",
                      "name": "invoke_agent",
                      "startTimeUnixNano": "{{startNanos}}",
                      "endTimeUnixNano": "{{startNanos + 1_000_000_000}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "invoke_agent" } },
                        { "key": "gen_ai.agent.name", "value": { "stringValue": "copilot" } },
                        { "key": "github.copilot.agent.type", "value": { "stringValue": "builtin" } },
                        { "key": "gen_ai.provider.name", "value": { "stringValue": "github" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "gpt-4o" } },
                        { "key": "gen_ai.response.model", "value": { "stringValue": "gpt-4o-2024" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1000" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "100" } },
                        { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "300" } },
                        { "key": "gen_ai.usage.cache_creation.input_tokens", "value": { "intValue": "50" } },
                        { "key": "copilot_chat.turn_count", "value": { "intValue": "3" } },
                        { "key": "github.copilot.git.repository", "value": { "stringValue": "https://github.com/owner/repo.git" } },
                        { "key": "github.copilot.git.branch", "value": { "stringValue": "main" } },
                        { "key": "github.copilot.git.commit_sha", "value": { "stringValue": "abc123" } },
                        { "key": "github.copilot.github.org", "value": { "stringValue": "owner" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
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
        var result = OtlpTraceParser.Parse(document, PeakUtc.ToUnixTimeMilliseconds());

        var span = Assert.Single(result.Spans);
        Assert.Equal(850, span.TtftMs);
        Assert.Equal(2_000, span.EndTimeMs - span.StartTimeMs);
        Assert.Equal("copilot-chat", span.Source);

        Assert.Equal(2, result.Events.Count);
        var tool = Assert.Single(
            result.Events,
            item => item.Operation == "execute_tool");
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal("function", tool.ToolType);
        Assert.Equal("str_replace", tool.EditType);
        Assert.Equal("review", tool.SkillName);
        Assert.Equal("call-1", tool.CallId);
        Assert.Equal("session-1", tool.SessionId);
        Assert.Equal("copilot-chat", tool.Source);
        Assert.Equal("span-agent", tool.ParentSpanId);
        Assert.Equal("hash-1", tool.McpServerHash);
        Assert.Equal("search", tool.McpToolName);
        Assert.Equal("FileNotFoundError", tool.ErrorType);
        Assert.True(tool.IsError);
        Assert.Equal(500, tool.EndTimeMs - tool.StartTimeMs);

        var agent = Assert.Single(
            result.Events,
            item => item.Operation == "invoke_agent");
        Assert.Null(agent.ToolName);
        Assert.Equal("copilot", agent.AgentName);
        Assert.Equal("builtin", agent.AgentType);

        var run = Assert.Single(result.AgentRuns);
        Assert.Equal("copilot", run.AgentName);
        Assert.Equal("builtin", run.AgentType);
        Assert.Equal("github", run.ProviderName);
        Assert.Equal("gpt-4o", run.RequestModel);
        Assert.Equal("gpt-4o-2024", run.ResponseModel);
        Assert.Equal("main", run.GitBranch);
        Assert.Equal("abc123", run.GitCommit);
        Assert.Equal("owner", run.GithubOrg);
        Assert.Equal(3, run.TurnCount);
        Assert.Equal(1_000, run.InputTokens);
        Assert.Equal(100, run.OutputTokens);
        Assert.Equal(300, run.CacheReadTokens);
        Assert.Equal(50, run.CacheCreationTokens);
        Assert.Equal("github.com/owner/repo", run.GitRepository);
    }

    [Theory]
    [InlineData("claude-code", null, "agent-host")]
    [InlineData("codex-app-server", null, "agent-host")]
    [InlineData("vscode-agent-host", null, "agent-host")]
    [InlineData("custom-model", null, "unknown")]
    [InlineData("github-copilot", "vscode.extension-host", "copilot-chat")]
    [InlineData("copilot-chat", null, "copilot-chat")]
    public void ResolvesSourceFromNamespaceAndServiceName(
        string serviceName,
        string? serviceNamespace,
        string expectedSource)
    {
        using var document = JsonDocument.Parse(
            IdentityJson(serviceName, serviceNamespace));
        var span = Assert.Single(
            OtlpTraceParser.ParseTraces(
                document,
                PeakUtc.ToUnixTimeMilliseconds()));

        Assert.Equal(expectedSource, span.Source);
        Assert.Equal(serviceNamespace, span.ServiceNamespace);
    }

    [Fact]
    public void SourceOverrideWinsAndCapturesScopeName()
    {
        using var document = JsonDocument.Parse(
            IdentityJson("github-copilot", "vscode.agent-host", "test-scope"));
        var span = Assert.Single(
            OtlpTraceParser.ParseTraces(
                document,
                PeakUtc.ToUnixTimeMilliseconds(),
                sourceOverride: "copilot-chat"));

        Assert.Equal("copilot-chat", span.Source);
        Assert.Equal("vscode.agent-host", span.ServiceNamespace);
        Assert.Equal("test-scope", span.ScopeName);
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

    private static string IdentityJson(
        string serviceName,
        string? serviceNamespace,
        string? scopeName = null)
    {
        var startNanos = PeakUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var namespaceAttribute = serviceNamespace is null
            ? string.Empty
            : $", {{ \"key\": \"service.namespace\", \"value\": {{ \"stringValue\": \"{serviceNamespace}\" }} }}";
        var scope = scopeName is null
            ? string.Empty
            : $"\"scope\": {{ \"name\": \"{scopeName}\" }},";

        return $$"""
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "{{serviceName}}" } }{{namespaceAttribute}}
                ]
              },
              "scopeSpans": [
                {
                  {{scope}}
                  "spans": [
                    {
                      "traceId": "identity-trace",
                      "spanId": "identity-span",
                      "name": "chat deepseek-flash",
                      "startTimeUnixNano": "{{startNanos}}",
                      "attributes": [
                        { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                        { "key": "gen_ai.request.model", "value": { "stringValue": "deepseek-flash" } },
                        { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "10" } },
                        { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "2" } },
                        { "key": "copilot_chat.session_id", "value": { "stringValue": "session-1" } }
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
                        { "key": "gen_ai.usage.reasoning.output_tokens", "value": { "intValue": "5" } },
                        { "key": "gen_ai.usage.reasoning_tokens", "value": { "intValue": "1" } },
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
