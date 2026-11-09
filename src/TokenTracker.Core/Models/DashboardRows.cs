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
    public double? AvgTtftMs { get; init; }
    public long TtftSamples { get; init; }
    public double? TokensPerSecond { get; init; }

    // Fastest/slowest over successful calls only (error_type IS NULL); failed
    // requests often lack TTFT or end early and would skew the extremes.
    public double? MinTtftMs { get; init; }
    public double? MaxTtftMs { get; init; }
    public double? MinTokensPerSecond { get; init; }
    public double? MaxTokensPerSecond { get; init; }

    public double? HitRate =>
        InputTokens > 0 ? (double)CacheReadTokens / InputTokens : null;

    public string CacheCostText => DisplayFormat.Price(CacheCostCny);
    public string UncachedCostText => DisplayFormat.Price(UncachedCostCny);
    public string OutputCostText => DisplayFormat.Price(OutputCostCny);
    public string ReasoningCostText => DisplayFormat.Price(ReasoningCostCny);
    public string ReasoningTokensText => DisplayFormat.Number(ReasoningTokens);
    public string AvgTtftText => DisplayFormat.Duration(AvgTtftMs);
    public string MinTtftText => DisplayFormat.Duration(MinTtftMs);
    public string MaxTtftText => DisplayFormat.Duration(MaxTtftMs);
    public string TokensPerSecondText => DisplayFormat.Speed(TokensPerSecond);
    public string MinTokensPerSecondText =>
        DisplayFormat.Speed(MinTokensPerSecond);
    public string MaxTokensPerSecondText =>
        DisplayFormat.Speed(MaxTokensPerSecond);
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
    public long FirstSeenMs { get; init; }
    public long Events { get; init; }
    public double? AvgDurationMs { get; init; }
    public double? AvgTtftMs { get; init; }
    public long TtftSamples { get; init; }
    public double? TokensPerSecond { get; init; }

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
    public string TimeRangeText => FirstSeenMs > 0 && FirstSeenMs < LastSeenMs
        ? $"{DisplayFormat.LocalTime(FirstSeenMs)} ~ {DisplayFormat.LocalTime(LastSeenMs)}"
        : DisplayFormat.LocalTime(LastSeenMs);
    public string AvgDurationText => DisplayFormat.Duration(AvgDurationMs);
    public string AvgTtftText => DisplayFormat.Duration(AvgTtftMs);
    public string TokensPerSecondText => DisplayFormat.Speed(TokensPerSecond);
}

/// <summary>
/// Minimal projection of one call, used when only aggregates are needed: the
/// session page must not materialize thousands of <see cref="CallRow"/> objects
/// just to sum them, but the arithmetic still lives in one place.
/// </summary>
public readonly record struct CallSample(
    string Model,
    string? Source,
    long InputTokens,
    long CacheReadTokens,
    long OutputTokens,
    long ReasoningTokens,
    double? CostCny,
    double CacheCostCny,
    double UncachedCostCny,
    double OutputCostCny,
    double ReasoningCostCny,
    long? TtftMs,
    long? DurationMs,
    string? ErrorType,
    long StartTimeMs);

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
    public long? NanoAiu { get; init; }
    public string? Source { get; init; }
    public long? DurationMs { get; init; }
    public long? TtftMs { get; init; }
    public string? TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? ErrorType { get; init; }
    public string? AgentName { get; init; }
    public string? SubagentName { get; init; }
    public string? FinishReasons { get; init; }
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
    public string? AiuText =>
        NanoAiu is null ? null : DisplayFormat.Aic(NanoAiu.Value);
    public string CacheCostText => DisplayFormat.Price(CacheCostCny);
    public string UncachedCostText => DisplayFormat.Price(UncachedCostCny);
    public string OutputCostText => DisplayFormat.Price(OutputCostCny);
    public string ReasoningCostText => DisplayFormat.Price(ReasoningCostCny);
    public string PeriodText => CostCny is null
        ? "-"
        : DisplayFormat.Period(PricingPeriod);
    public string KindText => IsMain ? "main" : "util";
    public string SourceText => DisplayFormat.Source(Source);
    public string TtftText => DisplayFormat.Duration(TtftMs);
    public string DurationText => DisplayFormat.Duration(DurationMs);
    public string ErrorText => string.IsNullOrWhiteSpace(ErrorType)
        ? "-"
        : ErrorType;
    public string AgentText => string.IsNullOrWhiteSpace(AgentName)
        ? "-"
        : AgentName;
    public string FinishReasonsText => string.IsNullOrWhiteSpace(FinishReasons)
        ? "-"
        : FinishReasons;
    public double? TokensPerSecond
    {
        get
        {
            if (DurationMs is not > 0 || OutputTokens < 5)
            {
                return null;
            }

            if (TtftMs is long ttft)
            {
                var generationMs = DurationMs.Value - ttft;
                return generationMs >= 50
                    ? OutputTokens * 1_000d / generationMs
                    : null;
            }

            return null;
        }
    }
    public string TokensPerSecondText => DisplayFormat.Speed(TokensPerSecond);
}

public sealed partial class ModelRow
{
    public required string Model { get; init; }
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
    public double? AvgTtftMs { get; init; }
    public long TtftSamples { get; init; }
    public double? TokensPerSecond { get; init; }

    public string CallsText => DisplayFormat.Number(Calls);
    public string TokensText =>
        $"{DisplayFormat.Number(InputTokens)} / {DisplayFormat.Number(CacheReadTokens)}";
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
    public double HitRatePercent => (HitRate ?? 0) * 100;
    public string AvgTtftText => DisplayFormat.Duration(AvgTtftMs);
    public string TokensPerSecondText => DisplayFormat.Speed(TokensPerSecond);
}

public sealed record SessionEventAggregate(
    string SessionId,
    long Events,
    long FirstSeenMs,
    long LastSeenMs);

public sealed class SessionEventRow
{
    public required string Operation { get; init; }
    public string? Source { get; init; }
    public long Count { get; init; }
    public double? AvgDurationMs { get; init; }
    public long ErrorCount { get; init; }
    public long LastSeenMs { get; init; }

    public string OperationText => DisplayFormat.Operation(Operation);
    public string SourceText => DisplayFormat.Source(Source);
    public string CountText => DisplayFormat.Number(Count);
    public string AvgDurationText => DisplayFormat.Duration(AvgDurationMs);
    public string LastSeenText => DisplayFormat.LocalTime(LastSeenMs);
}

public sealed class ToolCallRow
{
    public required string Tool { get; init; }
    public string? Source { get; init; }
    public long Calls { get; init; }
    public double? AvgDurationMs { get; init; }
    public double? MaxDurationMs { get; init; }
    public long ErrorCount { get; init; }
    public long LastSeenMs { get; init; }
    public List<string> ToolTypes { get; init; } = new();
    public List<string> EditTypes { get; init; } = new();
    public List<string> SkillNames { get; init; } = new();
    public List<string> McpServerHashes { get; init; } = new();
    public List<string> McpToolNames { get; init; } = new();
    public List<string> ErrorTypes { get; init; } = new();
    public List<string> AgentNames { get; init; } = new();

    public string SourceText => DisplayFormat.Source(Source);
    public string CallsText => DisplayFormat.Number(Calls);
    public string ToolDisplayText => Otlp.ToolNameResolver.Display(Tool);
    public string AvgDurationText => DisplayFormat.Duration(AvgDurationMs);
    public string MaxDurationText => DisplayFormat.Duration(MaxDurationMs);
    public string LastSeenText => DisplayFormat.LocalTime(LastSeenMs);
    public string ToolTypeText => ToolTypes.Count > 0
        ? string.Join(", ", ToolTypes)
        : "-";
    public string EditTypeText => EditTypes.Count > 0
        ? string.Join(", ", EditTypes)
        : "-";
    public string SkillNameText => SkillNames.Count > 0
        ? string.Join(", ", SkillNames)
        : "-";
    public string McpServerHashText => McpServerHashes.Count > 0
        ? string.Join(", ", McpServerHashes)
        : "-";
    public string McpToolNameText => McpToolNames.Count > 0
        ? string.Join(", ", McpToolNames)
        : "-";
    public string ErrorTypeText => ErrorTypes.Count > 0
        ? string.Join(", ", ErrorTypes)
        : "-";
    public string AgentNameText => AgentNames.Count > 0
        ? string.Join(", ", AgentNames)
        : "-";
}

public sealed class AgentRunRow
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public bool IsRoot { get; init; }
    public string? SessionId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentType { get; init; }
    public string? ProviderName { get; init; }
    public string? RequestModel { get; init; }
    public string? ResponseModel { get; init; }
    public string? Source { get; init; }
    public string? GitRepository { get; init; }
    public string? GitBranch { get; init; }
    public string? GitCommit { get; init; }
    public string? GithubOrg { get; init; }
    public long? TurnCount { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long StartTimeMs { get; init; }
    public long? EndTimeMs { get; init; }
    public bool IsError { get; init; }
    public string? ErrorType { get; init; }

    public string AgentText => string.IsNullOrWhiteSpace(AgentName)
        ? "-"
        : AgentName;
    public string AgentTypeText => string.IsNullOrWhiteSpace(AgentType)
        ? "-"
        : AgentType;
    public string ProviderText => string.IsNullOrWhiteSpace(ProviderName)
        ? "-"
        : ProviderName;
    public string ModelText => ResponseModel ?? RequestModel ?? "-";
    public string SourceText => DisplayFormat.Source(Source);
    public string RepoText => string.IsNullOrWhiteSpace(GitRepository)
        ? "-"
        : GitRepository;
    public string BranchText => string.IsNullOrWhiteSpace(GitBranch)
        ? "-"
        : GitBranch;
    public string TurnText => TurnCount is null
        ? "-"
        : DisplayFormat.Number(TurnCount.Value);
    public string InputText => DisplayFormat.Number(InputTokens);
    public string CacheText => DisplayFormat.Number(CacheReadTokens);
    public string OutputText => DisplayFormat.Number(OutputTokens);
    public string TimeText => DisplayFormat.LocalTime(StartTimeMs);
    public string DurationText => DisplayFormat.Duration(
        EndTimeMs is null ? null : EndTimeMs - StartTimeMs);
    public string ErrorText => string.IsNullOrWhiteSpace(ErrorType)
        ? "-"
        : ErrorType;
}

/// <summary>
/// One tool execution as seen by the turn builder: the start decides which turn
/// owns it, the end decides how long the turn was still working.
/// </summary>
public readonly record struct ToolRunSample(long StartTimeMs, long EndTimeMs);

/// <summary>
/// One Agent Host turn. A turn starts at a root <c>invoke_agent</c> span (one per
/// user message) and owns every call and tool run until the next turn starts.
/// </summary>
public sealed class SessionTurnRow
{
    public int Index { get; init; }
    public long StartTimeMs { get; init; }
    public long? EndTimeMs { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public double CostCny { get; init; }
    public long ToolCalls { get; init; }

    /// <summary>
    /// True for the trailing bucket that has activity but no root invoke_agent yet:
    /// that span only arrives when the turn ends, so without this flag a running
    /// turn would be reported as the previous one.
    /// </summary>
    public bool InProgress { get; init; }

    public string IndexText => DisplayFormat.Number(Index);
    public string StartText => DisplayFormat.LocalTime(StartTimeMs);
    public string DurationText => DisplayFormat.Duration(
        EndTimeMs is null ? null : EndTimeMs - StartTimeMs);
    public string CallsText => DisplayFormat.Number(Calls);
    public string InputText => DisplayFormat.Number(InputTokens);
    public string CacheText => DisplayFormat.Number(CacheReadTokens);
    public string OutputText => DisplayFormat.Number(OutputTokens);
    public string ReasoningText => DisplayFormat.Number(ReasoningTokens);
    public string ToolCallsText => DisplayFormat.Number(ToolCalls);
}

public sealed class HookSummaryRow
{
    public string? HookType { get; init; }
    public string? Decision { get; init; }
    public string? ResultKind { get; init; }
    public string? Source { get; init; }
    public long Count { get; init; }
    public double? AvgDurationMs { get; init; }
    public long ErrorCount { get; init; }
    public List<string> ToolNames { get; init; } = new();
    public long LastSeenMs { get; init; }

    public string HookTypeText => string.IsNullOrWhiteSpace(HookType)
        ? "-"
        : HookType;
    public string DecisionText => string.IsNullOrWhiteSpace(Decision)
        ? "-"
        : Decision;
    public string ResultKindText => string.IsNullOrWhiteSpace(ResultKind)
        ? "-"
        : ResultKind;
    public string SourceText => DisplayFormat.Source(Source);
    public string CountText => DisplayFormat.Number(Count);
    public string AvgDurationText => DisplayFormat.Duration(AvgDurationMs);
    public string ToolNamesText => ToolNames.Count > 0
        ? string.Join(", ", ToolNames)
        : "-";
    public string LastSeenText => DisplayFormat.LocalTime(LastSeenMs);
}

/// <summary>
/// One slice of the FIFO node stream. <see cref="NextCursor"/> walks towards newer
/// rows; context rows at the start repeat ancestors from earlier slices.
/// </summary>
public sealed record TracePage(
    IReadOnlyList<TraceNodeRow> Nodes,
    bool HasMore,
    string? NextCursor,
    int Total);

public sealed record TraceNodeRow
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Kind { get; init; }
    public required string Operation { get; init; }
    public string? Name { get; init; }
    public string? Tool { get; init; }
    /// <summary>
    /// Normalized model of a chat node. The OTel span name keeps the provider
    /// prefix from Agent Host (<c>chat DeepSeek/deepseek-flash</c>) and repeats the
    /// operation, so the label is built from this instead.
    /// </summary>
    public string? Model { get; init; }
    public string? Source { get; init; }
    public long StartTimeMs { get; init; }
    public long? DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public bool IsError { get; init; }
    public string? ErrorType { get; init; }
    public int Depth { get; init; }

    /// <summary>
    /// True when this row only repeats an ancestor that lives in an earlier block.
    /// Context rows carry no counts and must not be summed.
    /// </summary>
    public bool IsContext { get; init; }

    /// <summary>
    /// One-line label for the tree. The tree renders the operation as its own tag,
    /// so a chat row shows only the model instead of repeating "chat" from the span
    /// name, and an approval row keeps the kind/result/tool text it was built with.
    /// </summary>
    public string Label => Kind == "chat"
        ? Model ?? Name ?? Operation
        : Kind == "permission"
            ? Name ?? Operation
            : string.IsNullOrWhiteSpace(Tool)
                ? Name ?? Operation
                : $"{Operation} {Otlp.ToolNameResolver.Display(Tool)}";
    public string SourceText => DisplayFormat.Source(Source);
    public string DurationText => DisplayFormat.Duration(DurationMs);
}

public sealed class SignalHealthRow
{
    public long AgentRuns { get; init; }
    public long HookEvents { get; init; }
    public long MetricPoints { get; init; }
    public long LastSignalMs { get; init; }

    public string LastSignalText => LastSignalMs > 0
        ? DisplayFormat.LocalTime(LastSignalMs)
        : "-";
}

public sealed class OutcomeSummary
{
    public long EditAccepted { get; init; }
    public long EditRejected { get; init; }
    public long ChatEditAccepted { get; init; }
    public long ChatEditRejected { get; init; }
    public long ChatEditSaved { get; init; }
    public long LinesAdded { get; init; }
    public long LinesRemoved { get; init; }
    public double? SurvivalFourGram { get; init; }
    public double? SurvivalNoRevert { get; init; }
    public long UserCopy { get; init; }
    public long UserInsert { get; init; }
    public long UserApply { get; init; }
    public long UserFollowup { get; init; }
    public long FeedbackPositive { get; init; }
    public long FeedbackNegative { get; init; }
    public long AgentEditSuccess { get; init; }
    public long AgentEditError { get; init; }
    public long SummarizationApplied { get; init; }
    public long SummarizationFailed { get; init; }
    public long PullRequests { get; init; }
    public long CloudSessions { get; init; }
    // Cloud agent operations (recordCloudOperation / incrementCloudError).
    public long CloudOperations { get; init; }
    public long CloudErrors { get; init; }
    public double? CloudOperationAvgMs { get; init; }
    public long ToolMetricCalls { get; init; }
    public double? ToolMetricAvgMs { get; init; }

    public double? EditAcceptRate
    {
        get
        {
            var total = EditAccepted + EditRejected;
            return total > 0 ? (double)EditAccepted / total : null;
        }
    }

    public bool HasData =>
        EditAccepted + EditRejected + ChatEditAccepted + ChatEditRejected +
        ChatEditSaved + LinesAdded + LinesRemoved + UserCopy + UserInsert +
        UserApply + UserFollowup + FeedbackPositive + FeedbackNegative +
        AgentEditSuccess + AgentEditError + SummarizationApplied +
        SummarizationFailed + PullRequests + CloudSessions +
        CloudOperations + CloudErrors +
        ToolMetricCalls > 0 ||
        SurvivalFourGram is not null ||
        SurvivalNoRevert is not null;

    public string EditAcceptRateText => EditAcceptRate is null
        ? "-"
        : $"{EditAcceptRate.Value:P1}";
    public string SurvivalFourGramText => SurvivalFourGram is null
        ? "-"
        : $"{SurvivalFourGram.Value:P1}";
    public string SurvivalNoRevertText => SurvivalNoRevert is null
        ? "-"
        : $"{SurvivalNoRevert.Value:P1}";
    public string ToolMetricAvgText => ToolMetricAvgMs is null
        ? "-"
        : DisplayFormat.Duration(ToolMetricAvgMs);
}
