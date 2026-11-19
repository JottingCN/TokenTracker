using System.Globalization;
using Microsoft.AgentHostProtocol;
using TokenTracker.Core.Otlp;

namespace TokenTracker.Core.Session;

public sealed record AhpTurnUsage(
    string ChatResource,
    string TurnId,
    string State,
    long? StartedAtMs,
    long? DurationMs,
    string? Model,
    long? InputTokens,
    long? CacheReadTokens,
    long? OutputTokens,
    bool HasUsage,
    List<string> Tools);

public sealed record AhpSessionUsage(
    long TurnCount,
    bool HistoryComplete,
    List<AhpTurnUsage> Turns);

internal static class AhpSessionUsageBuilder
{
    public const int MaxRetainedTurns = 20_000;

    public static AhpSessionUsage Build(
        IEnumerable<(string ChatResource, ChatState State)> chats)
    {
        var turns = new List<AhpTurnUsage>();
        long turnCount = 0;
        var historyComplete = true;

        foreach (var (chatResource, state) in chats)
        {
            historyComplete &= string.IsNullOrWhiteSpace(state.TurnsNextCursor);
            foreach (var turn in state.Turns)
            {
                turnCount++;
                turns.Add(new AhpTurnUsage(
                    chatResource,
                    turn.Id,
                    turn.State.ToString().ToLowerInvariant(),
                    ParseTimestamp(turn.StartedAt),
                    turn.Duration,
                    turn.Usage?.Model,
                    turn.Usage?.InputTokens,
                    turn.Usage?.CacheReadTokens,
                    turn.Usage?.OutputTokens,
                    turn.Usage is not null,
                    ToolNames(turn.ResponseParts)));
            }

            if (state.ActiveTurn is { } activeTurn)
            {
                turnCount++;
                turns.Add(new AhpTurnUsage(
                    chatResource,
                    activeTurn.Id,
                    "in_progress",
                    ParseTimestamp(activeTurn.StartedAt),
                    null,
                    activeTurn.Usage?.Model,
                    activeTurn.Usage?.InputTokens,
                    activeTurn.Usage?.CacheReadTokens,
                    activeTurn.Usage?.OutputTokens,
                    activeTurn.Usage is not null,
                    ToolNames(activeTurn.ResponseParts)));
            }
        }

        if (turns.Count > MaxRetainedTurns)
        {
            turns = turns
                .OrderByDescending(turn => turn.StartedAtMs)
                .Take(MaxRetainedTurns)
                .OrderBy(turn => turn.StartedAtMs)
                .ToList();
            historyComplete = false;
        }

        return new AhpSessionUsage(turnCount, historyComplete, turns);
    }

    private static List<string> ToolNames(IEnumerable<ResponsePart> parts)
    {
        var names = new List<string>();
        foreach (var part in parts)
        {
            if (part.Value is not ToolCallResponsePart toolPart)
            {
                continue;
            }

            var (toolName, displayName) = toolPart.ToolCall.Value switch
            {
                ToolCallStreamingState call => (call.ToolName, call.DisplayName),
                ToolCallPendingConfirmationState call => (call.ToolName, call.DisplayName),
                ToolCallRunningState call => (call.ToolName, call.DisplayName),
                ToolCallAuthRequiredState call => (call.ToolName, call.DisplayName),
                ToolCallPendingResultConfirmationState call => (call.ToolName, call.DisplayName),
                ToolCallCompletedState call => (call.ToolName, call.DisplayName),
                ToolCallCancelledState call => (call.ToolName, call.DisplayName),
                _ => (null, null),
            };
            if (toolName is null)
            {
                continue;
            }

            ToolNameResolver.Register(toolName, displayName);
            names.Add(ToolNameResolver.Display(toolName));
        }

        return names;
    }

    private static long? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp.ToUnixTimeMilliseconds()
            : null;
}
