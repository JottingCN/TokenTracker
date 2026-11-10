using TokenTracker.Core;
using TokenTracker.Core.Models;
using TokenTracker.Core.Pricing;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;

namespace TokenTracker.Server;

public sealed class ReceiverCounters
{
    private long _batches;
    private long _spans;
    private long _metricBatches;
    private long _metricPoints;
    private long _metricRejected;
    private string? _lastError;
    private readonly object _metricNameLock = new();
    private readonly Dictionary<string, long> _filteredMetricNames =
        new(StringComparer.Ordinal);
    private const int MaxFilteredMetricNames = 8;

    public void Record(int spans)
    {
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _spans, spans);
    }

    // Metrics are tracked separately: an empty metric_points table is either
    // "nothing arrived" or "everything was filtered by the outcome allowlist",
    // and the two need different fixes.
    public void RecordMetrics(
        int parsedPoints,
        int storedPoints,
        IReadOnlyCollection<string>? seenNames = null)
    {
        Interlocked.Increment(ref _metricBatches);
        Interlocked.Add(ref _metricPoints, storedPoints);
        if (parsedPoints != 0)
        {
            return;
        }

        Interlocked.Increment(ref _metricRejected);
        if (seenNames is not { Count: > 0 })
        {
            return;
        }

        lock (_metricNameLock)
        {
            foreach (var name in seenNames)
            {
                _filteredMetricNames.TryGetValue(name, out var count);
                _filteredMetricNames[name] = count + 1;
            }

            while (_filteredMetricNames.Count > MaxFilteredMetricNames)
            {
                var smallest = _filteredMetricNames
                    .OrderBy(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .First()
                    .Key;
                _filteredMetricNames.Remove(smallest);
            }
        }
    }

    public void RecordError(string message) => _lastError = message;

    // "gen_ai.client.operation.duration ×12, copilot_chat.session.count ×3": the
    // names Copilot actually exports when none of them match the allowlist.
    private string? FilteredMetricText()
    {
        lock (_metricNameLock)
        {
            return _filteredMetricNames.Count == 0
                ? null
                : string.Join(
                    ", ",
                    _filteredMetricNames
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key} ×{entry.Value}"));
        }
    }

    public ReceiverInfo Snapshot(string endpoint, bool running) =>
        new(
            endpoint,
            running,
            Interlocked.Read(ref _batches),
            Interlocked.Read(ref _spans),
            _lastError,
            Interlocked.Read(ref _metricBatches),
            Interlocked.Read(ref _metricPoints),
            Interlocked.Read(ref _metricRejected),
            FilteredMetricText());
}

public sealed class DashboardService
{
    private readonly SpanStore _store;
    private readonly SessionMetadataProvider _metadata;
    private readonly ReceiverCounters _counters;
    private readonly ServerSettings _settings;
    private readonly string _endpoint;
    private readonly object _outcomeCacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, OutcomeSummary Value)>
        _outcomeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan OutcomeCacheTtl = TimeSpan.FromMinutes(2);
    private const int MaxOutcomeCacheEntries = 32;
    private long _outcomeVersion;
    private readonly SessionWorkspaceCache _workspaces;

    /// <summary>
    /// Pass-through of <see cref="SpanStore.DataVersion"/> so a WebSocket connection
    /// can skip rebuilding a snapshot that no write has invalidated.
    /// </summary>
    public long DataVersion => _store.DataVersion;

    public DashboardService(
        SpanStore store,
        SessionMetadataProvider metadata,
        ReceiverCounters counters,
        ServerSettings settings,
        string endpoint)
    {
        _store = store;
        _metadata = metadata;
        _counters = counters;
        _settings = settings;
        _endpoint = endpoint;
        _workspaces = new SessionWorkspaceCache(store);
    }

    public DashboardPayload GetDashboard(
        int range,
        bool includeUtility,
        string? source = null)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetDashboard(fromMs, toMs, includeUtility, source);
    }

    public DashboardPayload GetDashboard(
        long fromMs,
        long toMs,
        bool includeUtility,
        string? source = null)
    {
        source = NormalizeSource(source);
        EnsureCombined(source);
        var lastReceivedAtMs = _store.GetLastReceivedAtMs();
        var updatedAt = lastReceivedAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastReceivedAtMs)
                .ToLocalTime()
                .ToString("HH:mm:ss")
            : "-";
        var metadataCache = new Dictionary<string, SessionMetadata?>(
            StringComparer.OrdinalIgnoreCase);
        var ahp = _metadata.GetAhpStatus();
        var sessions = _store
            .GetSessions(fromMs, limit: 500, toMs: toMs, source: source)
            .ToDictionary(row => row.SessionId, StringComparer.OrdinalIgnoreCase);
        foreach (var aggregate in _store.GetSessionEventAggregates(
                     fromMs,
                     toMs,
                     source == "combined" ? null : source))
        {
            if (sessions.TryGetValue(aggregate.SessionId, out var existing))
            {
                sessions[aggregate.SessionId] = existing with
                {
                    Events = aggregate.Events,
                    FirstSeenMs = existing.FirstSeenMs > 0
                        ? Math.Min(existing.FirstSeenMs, aggregate.FirstSeenMs)
                        : aggregate.FirstSeenMs,
                    LastSeenMs = Math.Max(existing.LastSeenMs, aggregate.LastSeenMs),
                };
            }
            else
            {
                sessions[aggregate.SessionId] = new SessionRow
                {
                    SessionId = aggregate.SessionId,
                    Model = "-",
                    Events = aggregate.Events,
                    FirstSeenMs = aggregate.FirstSeenMs,
                    LastSeenMs = aggregate.LastSeenMs,
                };
            }
        }

        var sessionRows = sessions.Values
            .OrderByDescending(row => row.LastSeenMs)
            .Take(50)
            .Select(row => WithTitle(row, metadataCache))
            .ToList();

        _workspaces.Flush();

        return new DashboardPayload(
            _store.GetSummary(fromMs, includeUtility: true, toMs, source),
            _store.GetModels(fromMs, toMs, includeUtility: true, source).ToList(),
            sessionRows,
            _counters.Snapshot(_endpoint, running: true),
            updatedAt,
            _settings.RetentionDays,
            _store.GetSignalHealth(
                null,
                source == "combined" ? null : source,
                fromMs,
                toMs),
            GetOutcomes(
                fromMs,
                toMs,
                source == "combined" ? null : source),
            new AhpStatusPayload(
                ahp.State,
                ahp.Connected,
                ahp.Sessions,
                ahp.Items.Select(item => new AhpSessionPayload(
                    item.Resource,
                    item.Provider,
                    item.Title,
                    item.Status,
                    item.Activity,
                    item.WorkspacePath,
                    item.ModifiedAt,
                    item.Chats)).ToList()));
    }

    public ModelPayload GetModel(
        string model,
        int range,
        string? source = null)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetModel(model, fromMs, toMs, source);
    }

    public ModelPayload GetModel(
        string model,
        long fromMs,
        long toMs,
        string? source = null)
    {
        source = NormalizeSource(source);
        EnsureCombined(source);
        var metadataCache = new Dictionary<string, SessionMetadata?>(
            StringComparer.OrdinalIgnoreCase);
        _workspaces.Flush();

        return new ModelPayload(
            _store.GetModelSummary(
                model,
                fromMs,
                toMs,
                includeUtility: true,
                source: source),
            _store.GetSessionsForModel(model, fromMs, toMs: toMs, source: source)
                .Select(row => WithTitle(row, metadataCache)).ToList(),
            model);
    }

    public CallsPayload GetCalls(
        int range,
        bool includeUtility,
        string? model,
        string? source,
        string? query,
        string? kind,
        bool? priced,
        string? period,
        bool? hasError,
        string? agent,
        int offset = 0,
        int limit = 50)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetCalls(
            fromMs,
            toMs,
            includeUtility,
            model,
            source,
            query,
            kind,
            priced,
            period,
            hasError,
            agent,
            offset,
            limit);
    }

    public CallsPayload GetCalls(
        long fromMs,
        long toMs,
        bool includeUtility,
        string? model,
        string? source,
        string? query,
        string? kind,
        bool? priced,
        string? period,
        bool? hasError,
        string? agent,
        int offset = 0,
        int limit = 50)
    {
        source = NormalizeSource(source);
        EnsureCombined(source);
        var pageSize = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        var pageOffset = Math.Max(0, offset);
        var kindFilter = string.IsNullOrWhiteSpace(kind) ||
                         string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : kind.Trim().ToLowerInvariant();
        var baseQuery = new CallQuery
        {
            FromMs = fromMs,
            ToMs = toMs,
            IncludeUtility = includeUtility || kindFilter is "main" or "utility",
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Source = source,
            Kind = kindFilter,
            Priced = priced,
            Period = string.IsNullOrWhiteSpace(period) ? null : period.Trim(),
            HasError = hasError,
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
        };

        var metadataCache = new Dictionary<string, SessionMetadata?>(
            StringComparer.OrdinalIgnoreCase);
        List<CallRow> calls;
        long total;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            var titleSessionIds = _store
                .GetSessionIds(baseQuery.FromMs, baseQuery.ToMs, baseQuery.Source)
                .Where(sessionId => Contains(
                    GetCachedMetadata(sessionId, metadataCache)?.Title,
                    term))
                .ToList();
            var matchQuery = new CallQuery
            {
                FromMs = baseQuery.FromMs,
                ToMs = baseQuery.ToMs,
                IncludeUtility = baseQuery.IncludeUtility,
                Model = baseQuery.Model,
                Source = baseQuery.Source,
                Kind = baseQuery.Kind,
                Priced = baseQuery.Priced,
                Period = baseQuery.Period,
                HasError = baseQuery.HasError,
                Agent = baseQuery.Agent,
                Query = term,
                SessionIds = titleSessionIds.Count > 0
                    ? titleSessionIds
                    : null,
            };
            total = _store.CountCalls(matchQuery);
            calls = _store
                .GetCalls(new CallQuery
                {
                    FromMs = matchQuery.FromMs,
                    ToMs = matchQuery.ToMs,
                    IncludeUtility = matchQuery.IncludeUtility,
                    Model = matchQuery.Model,
                    Source = matchQuery.Source,
                    Kind = matchQuery.Kind,
                    Priced = matchQuery.Priced,
                    Period = matchQuery.Period,
                    HasError = matchQuery.HasError,
                    Agent = matchQuery.Agent,
                    Query = matchQuery.Query,
                    SessionIds = matchQuery.SessionIds,
                    Offset = pageOffset,
                    Limit = pageSize,
                })
                .Select(row => WithTitle(row, metadataCache))
                .ToList();
        }
        else
        {
            total = _store.CountCalls(baseQuery);
            calls = _store
                .GetCalls(new CallQuery
                {
                    FromMs = baseQuery.FromMs,
                    ToMs = baseQuery.ToMs,
                    IncludeUtility = baseQuery.IncludeUtility,
                    Model = baseQuery.Model,
                    Source = baseQuery.Source,
                    Kind = baseQuery.Kind,
                    Priced = baseQuery.Priced,
                    Period = baseQuery.Period,
                    HasError = baseQuery.HasError,
                    Agent = baseQuery.Agent,
                    Offset = pageOffset,
                    Limit = pageSize,
                })
                .Select(row => WithTitle(row, metadataCache))
                .ToList();
        }

        _workspaces.Flush();

        return new CallsPayload(calls, total, pageOffset, pageSize);
    }

    public SessionPayload GetSession(
        string sessionId,
        int range,
        string? source = null,
        bool includeCalls = true)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetSession(sessionId, source, fromMs, toMs, includeCalls);
    }

    public SessionPayload GetSession(
        string sessionId,
        string? source = null,
        long fromMs = 0,
        long toMs = long.MaxValue,
        bool includeCalls = true)
    {
        source = NormalizeSource(source);
        EnsureCombined(source);
        var detailSource = source == "combined" ? null : source;
        // The session page must honour the selected range like the dashboard
        // does; filtering inside SQL keeps out-of-range rows from being loaded.
        // Only the call list payload needs full rows; aggregates read a small
        // projection instead of materializing thousands of CallRow objects on
        // every snapshot.
        var calls = includeCalls
            ? _store.GetCallsForSession(sessionId, fromMs, toMs, source: source)
            : (IReadOnlyList<CallRow>)Array.Empty<CallRow>();
        // Both paths converge on the projection so totals, model rows and turns
        // share one arithmetic implementation.
        var samples = includeCalls
            ? calls.Select(ToSample).ToList()
            : _store.GetSessionCallSamples(sessionId, fromMs, toMs, source);
        var totals = new SummaryAccumulator();
        var byModel = new Dictionary<string, SummaryAccumulator>(
            StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            // Utility calls stay in the timeline but are not user-facing usage:
            // the dashboard session rows already total `is_main = 1`, so the
            // session page must exclude them too or the two totals disagree.
            if (!sample.IsMain)
            {
                continue;
            }

            totals.Add(sample);
            if (!byModel.TryGetValue(sample.Model, out var accumulator))
            {
                accumulator = new SummaryAccumulator();
                byModel[sample.Model] = accumulator;
            }

            accumulator.Add(sample);
        }

        var summary = totals.Build();
        var modelSummaries = byModel
            .Select(entry => new SessionModelPayload(entry.Key, entry.Value.Build()))
            .OrderByDescending(row => row.Summary.CostCny)
            .ThenByDescending(row => row.Summary.InputTokens)
            .ThenBy(row => row.Model, StringComparer.Ordinal)
            .ToList();

        var events = _store
            .GetSessionEvents(sessionId, detailSource, fromMs, toMs)
            .ToList();
        var chatEvents = includeCalls
            ? BuildChatEvents(calls)
            : BuildChatEvents(samples);
        events.InsertRange(0, chatEvents);

        var metadata = _metadata.Get(sessionId);
        var tools = _store
            .GetSessionTools(sessionId, detailSource, fromMs, toMs)
            .ToList();
        var agentRuns = _store
            .GetAgentRuns(sessionId, detailSource)
            .Where(row => row.StartTimeMs >= fromMs && row.StartTimeMs < toMs)
            .ToList();
        _workspaces.Flush();

        return new SessionPayload(
            summary,
            modelSummaries,
            // The call list dominates the payload (hundreds of KB); only the
            // client that aggregates per-model details asks for it.
            includeCalls ? calls.ToList() : new List<CallRow>(),
            events,
            tools,
            agentRuns,
            _store
                .GetHookSummaries(sessionId, detailSource, fromMs, toMs)
                .ToList(),
            _store.GetSignalHealth(sessionId, detailSource, fromMs, toMs),
            _store.GetOutcomeSummary(
                fromMs,
                toMs,
                detailSource,
                sessionId),
            sessionId,
            metadata?.Title,
            metadata?.WorkspacePath);
    }

    private static CallSample ToSample(CallRow call) => new(
        call.Model,
        call.Source,
        call.InputTokens,
        call.CacheReadTokens,
        call.OutputTokens,
        call.ReasoningTokens,
        call.CostCny,
        call.CacheCostCny,
        call.UncachedCostCny,
        call.OutputCostCny,
        call.ReasoningCostCny,
        call.TtftMs,
        call.DurationMs,
        call.ErrorType,
        call.StartTimeMs,
        call.IsMain);

    private static List<SessionEventRow> BuildChatEvents(
        IReadOnlyList<CallRow> calls) =>
        BuildChatEvents(
            calls.Select(call => (call.Source, call.DurationMs, call.ErrorType, call.StartTimeMs))
                .ToList());

    private static List<SessionEventRow> BuildChatEvents(
        IReadOnlyList<CallSample> calls) =>
        BuildChatEvents(
            calls.Select(call => (call.Source, call.DurationMs, call.ErrorType, call.StartTimeMs))
                .ToList());

    private static List<SessionEventRow> BuildChatEvents(
        IReadOnlyList<(string? Source, long? DurationMs, string? ErrorType, long StartTimeMs)> calls)
    {
        var rows = new List<SessionEventRow>();
        foreach (var group in calls.GroupBy(
                     call => call.Source ?? "copilot-chat",
                     StringComparer.OrdinalIgnoreCase))
        {
            long count = 0;
            long durationCount = 0;
            double durationSum = 0;
            long errorCount = 0;
            var lastSeen = 0L;
            foreach (var call in group)
            {
                count++;
                if (call.DurationMs is > 0)
                {
                    durationCount++;
                    durationSum += call.DurationMs.Value;
                }

                if (call.ErrorType is not null)
                {
                    errorCount++;
                }

                if (call.StartTimeMs > lastSeen)
                {
                    lastSeen = call.StartTimeMs;
                }
            }

            rows.Add(new SessionEventRow
            {
                Operation = "chat",
                Source = group.Key,
                Count = count,
                AvgDurationMs = durationCount > 0
                    ? durationSum / durationCount
                    : null,
                ErrorCount = errorCount,
                LastSeenMs = lastSeen,
            });
        }

        return rows
            .OrderByDescending(row => row.Count)
            .ToList();
    }

    private static SummaryDto BuildSummary(IReadOnlyCollection<CallRow> calls)
    {
        var accumulator = new SummaryAccumulator();
        foreach (var call in calls)
        {
            accumulator.Add(call);
        }

        return accumulator.Build();
    }

    // One pass replaces the previous mix of LINQ sums and four intermediate
    // ToList() calls; the dashboard rebuilds these for every model on every
    // snapshot, so the allocations add up.
    private sealed class SummaryAccumulator
    {
        private long _calls;
        private long _inputTokens;
        private long _cacheReadTokens;
        private long _outputTokens;
        private long _reasoningTokens;
        private long _unpricedCalls;
        private double _costCny;
        private double _cacheCostCny;
        private double _uncachedCostCny;
        private double _outputCostCny;
        private double _reasoningCostCny;
        private long _ttftSamples;
        private double _ttftSum;
        private long _minTtftMs = long.MaxValue;
        private long _maxTtftMs = long.MinValue;
        private double _minTokensPerSecond = double.MaxValue;
        private double _maxTokensPerSecond = double.MinValue;
        private long _generationOutputTokens;
        private double _generationMs;

        public void Add(CallRow call) => Add(new CallSample(
            call.Model,
            call.Source,
            call.InputTokens,
            call.CacheReadTokens,
            call.OutputTokens,
            call.ReasoningTokens,
            call.CostCny,
            call.CacheCostCny,
            call.UncachedCostCny,
            call.OutputCostCny,
            call.ReasoningCostCny,
            call.TtftMs,
            call.DurationMs,
            call.ErrorType,
            call.StartTimeMs));

        public void Add(CallSample call)
        {
            _calls++;
            _inputTokens += call.InputTokens;
            _cacheReadTokens += call.CacheReadTokens;
            _outputTokens += call.OutputTokens;
            _reasoningTokens += call.ReasoningTokens;
            _costCny += call.CostCny ?? 0;
            _cacheCostCny += call.CacheCostCny;
            _uncachedCostCny += call.UncachedCostCny;
            _outputCostCny += call.OutputCostCny;
            _reasoningCostCny += call.ReasoningCostCny;
            if (call.CostCny is null)
            {
                _unpricedCalls++;
            }

            var ttft = call.TtftMs is > 0 ? call.TtftMs : (long?)null;
            if (ttft is long ttftMs)
            {
                _ttftSamples++;
                _ttftSum += ttftMs;
                var ok = call.ErrorType is null;
                if (ok && ttftMs < _minTtftMs)
                {
                    _minTtftMs = ttftMs;
                }

                if (ok && ttftMs > _maxTtftMs)
                {
                    _maxTtftMs = ttftMs;
                }
            }

            if (call.DurationMs is > 0 &&
                call.OutputTokens >= 5 &&
                ttft is long generationTtft)
            {
                var generationMs = call.DurationMs.Value - generationTtft;
                if (generationMs >= 50)
                {
                    _generationOutputTokens += call.OutputTokens;
                    _generationMs += generationMs;
                    var speed = call.OutputTokens * 1_000d / generationMs;
                    if (call.ErrorType is null && speed > 0)
                    {
                        if (speed < _minTokensPerSecond)
                        {
                            _minTokensPerSecond = speed;
                        }

                        if (speed > _maxTokensPerSecond)
                        {
                            _maxTokensPerSecond = speed;
                        }
                    }
                }
            }
        }

        public SummaryDto Build() => new()
        {
            Calls = _calls,
            InputTokens = _inputTokens,
            CacheReadTokens = _cacheReadTokens,
            OutputTokens = _outputTokens,
            ReasoningTokens = _reasoningTokens,
            Sessions = _calls > 0 ? 1 : 0,
            CostCny = _costCny,
            CacheCostCny = _cacheCostCny,
            UncachedCostCny = _uncachedCostCny,
            OutputCostCny = _outputCostCny,
            ReasoningCostCny = _reasoningCostCny,
            UnpricedCalls = _unpricedCalls,
            AvgTtftMs = _ttftSamples > 0 ? _ttftSum / _ttftSamples : null,
            TtftSamples = _ttftSamples,
            TokensPerSecond = _generationMs > 0
                ? _generationOutputTokens * 1_000d / _generationMs
                : null,
            MinTtftMs = _minTtftMs == long.MaxValue ? null : _minTtftMs,
            MaxTtftMs = _maxTtftMs == long.MinValue ? null : _maxTtftMs,
            MinTokensPerSecond = _minTokensPerSecond == double.MaxValue
                ? null
                : _minTokensPerSecond,
            MaxTokensPerSecond = _maxTokensPerSecond == double.MinValue
                ? null
                : _maxTokensPerSecond,
        };
    }

    public const int TraceBudget = 400;

    public TracePayload GetTrace(
        string sessionId,
        string? source = null,
        string? cursor = null,
        int budget = TraceBudget,
        long fromMs = 0,
        long toMs = long.MaxValue)
    {
        var normalized = NormalizeSource(source);
        var page = _store.GetTracePage(
            sessionId,
            normalized == "combined" ? null : normalized,
            cursor,
            budget,
            fromMs,
            toMs);
        return new TracePayload(
            page.Nodes.ToList(),
            page.HasMore,
            page.NextCursor,
            page.Total);
    }

    public void ApplyOverrides() =>
        ModelPricing.SetOverrides(
            _store.GetModelPriceOverrides().Select(entry =>
                new KeyValuePair<string, ModelPrice>(entry.Model, entry.Price)));

    public List<ModelSettingsRowPayload> GetModels()
    {
        var profiles = new Dictionary<string, StoredModelProfile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _store.GetModelProfiles())
        {
            profiles[profile.Model] = profile;
        }

        var spanModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _store.GetDistinctModels())
        {
            spanModels.TryAdd(model, model);
        }

        var callCounts = _store.GetModelCallCounts();

        // Prices are keyed by the normalized model id while profiles use the exact
        // id, so the merged list collapses both onto one row per display model.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(spanModels.Keys);
        keys.UnionWith(profiles.Keys);
        foreach (var price in _store.GetModelPriceOverrides())
        {
            keys.Add(price.Model);
        }

        // A model id may appear as `provider/model` in profiles and as the parser
        // normalized leaf in spans; both project onto one row per normalized id.
        var displayByNormalized = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var model in spanModels.Keys)
        {
            displayByNormalized.TryAdd(ModelPricing.NormalizeModel(model), model);
        }

        var profileByNormalized = new Dictionary<string, StoredModelProfile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Values)
        {
            profileByNormalized.TryAdd(
                ModelPricing.NormalizeModel(profile.Model),
                profile);
        }

        var rows = new List<ModelSettingsRowPayload>(keys.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var normalized = ModelPricing.NormalizeModel(key);
            if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            var display = displayByNormalized.TryGetValue(normalized, out var canonical)
                ? canonical
                : key;
            var profile = profiles.GetValueOrDefault(display) ??
                profileByNormalized.GetValueOrDefault(normalized);
            var calls = callCounts.TryGetValue(display, out var count) ? count : 0;
            rows.Add(ToSettingsRow(
                display,
                priced: ModelPricing.GetPrice(display) is not null,
                profile,
                calls));
        }

        return rows
            .OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ModelSettingsRowPayload? SaveModel(ModelSettingsUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return null;
        }

        var model = request.Model.Trim();
        if (model.Length > 200)
        {
            return null;
        }

        var provider = NormalizeOptional(request.Provider, 64);
        if (!TryNormalizeIconKind(request.IconKind, out var iconKind))
        {
            return null;
        }

        var iconLetter = NormalizeOptional(request.IconLetter, 8);
        var iconColor = NormalizeOptional(request.IconColor, 9);
        if (iconColor is not null && !IsHexColor(iconColor))
        {
            return null;
        }

        var iconImage = NormalizeOptional(request.IconImage, MaxIconImageChars);
        if (iconImage is not null && !IsSupportedIcon(iconImage))
        {
            return null;
        }

        // An uploaded image without data (or a letter without a character) would
        // render as an empty badge, so reject the combination instead of saving it.
        if (iconKind == "image" && iconImage is null)
        {
            return null;
        }

        if (iconKind == "letter" && iconLetter is null)
        {
            return null;
        }

        var priceModel = ModelPricing.NormalizeModel(model);
        if (string.IsNullOrWhiteSpace(priceModel))
        {
            return null;
        }

        var hasProfile = provider is not null ||
            iconKind is not null ||
            iconLetter is not null ||
            iconColor is not null ||
            iconImage is not null;

        if (request.Priced)
        {
            if (!TryBuildPrice(request, out var price) || price is null)
            {
                return null;
            }

            _store.UpsertModelPrice(
                priceModel,
                price.Value,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyOverrides();
            RecomputeCostsForModel(priceModel);
        }
        else
        {
            // Turning pricing off keeps the identity but drops the override.
            _store.DeleteModelPrice(priceModel);
            ApplyOverrides();
            RecomputeCostsForModel(priceModel);
        }

        // Replace a stale profile whose id differs only by case or provider
        // prefix, so the unique model id never shows up twice in the list.
        foreach (var existing in _store.GetModelProfiles())
        {
            if (!string.Equals(existing.Model, model, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    ModelPricing.NormalizeModel(existing.Model),
                    priceModel,
                    StringComparison.OrdinalIgnoreCase))
            {
                _store.DeleteModelProfile(existing.Model);
            }
        }

        if (hasProfile)
        {
            _store.UpsertModelProfile(new StoredModelProfile(
                model,
                provider,
                iconKind,
                iconLetter,
                iconColor,
                iconImage,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        else
        {
            _store.DeleteModelProfile(model);
        }

        // Reuse the list projection so the response always matches a reload.
        return GetModels().FirstOrDefault(row => string.Equals(
            ModelPricing.NormalizeModel(row.Model),
            priceModel,
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryBuildPrice(
        ModelSettingsUpdateRequest request,
        out ModelPrice? price)
    {
        price = null;
        if (!IsValidPrice(request.CachedInput) ||
            !IsValidPrice(request.UncachedInput) ||
            !IsValidPrice(request.Output))
        {
            return false;
        }

        if (request.PeakAware &&
            (!IsValidPrice(request.PeakCachedInput ?? 0) ||
             !IsValidPrice(request.PeakUncachedInput ?? 0) ||
             !IsValidPrice(request.PeakOutput ?? 0)))
        {
            return false;
        }

        var cacheWrite = request.CacheWrite ?? request.UncachedInput;
        var peakCacheWrite = request.PeakCacheWrite ??
            request.CacheWrite ??
            request.PeakUncachedInput ??
            request.UncachedInput;
        if (!IsValidPrice(cacheWrite) ||
            (request.PeakAware && !IsValidPrice(peakCacheWrite)))
        {
            return false;
        }

        var idle = new PriceTier(
            (decimal)request.CachedInput,
            (decimal)request.UncachedInput,
            (decimal)request.Output,
            (decimal)cacheWrite);
        var peak = request.PeakAware
            ? new PriceTier(
                (decimal)(request.PeakCachedInput ?? request.CachedInput),
                (decimal)(request.PeakUncachedInput ?? request.UncachedInput),
                (decimal)(request.PeakOutput ?? request.Output),
                (decimal)peakCacheWrite)
            : idle;

        LongContextPrice? longContext = null;
        if (request.LongContextThreshold is > 0 and <= 10_000_000)
        {
            var threshold = request.LongContextThreshold.Value;
            var longCached = request.LongCachedInput ?? request.CachedInput;
            var longUncached = request.LongUncachedInput ?? request.UncachedInput;
            var longOutput = request.LongOutput ?? request.Output;
            var longCacheWrite = request.LongCacheWrite ?? longUncached;
            if (!IsValidPrice(longCached) ||
                !IsValidPrice(longUncached) ||
                !IsValidPrice(longOutput) ||
                !IsValidPrice(longCacheWrite))
            {
                return false;
            }

            if (request.PeakAware &&
                ((request.LongPeakCachedInput is { } peakCached && !IsValidPrice(peakCached)) ||
                 (request.LongPeakUncachedInput is { } peakUncached && !IsValidPrice(peakUncached)) ||
                 (request.LongPeakOutput is { } peakOutput && !IsValidPrice(peakOutput)) ||
                 (request.LongPeakCacheWrite is { } peakWrite && !IsValidPrice(peakWrite))))
            {
                return false;
            }

            var longIdle = new PriceTier(
                (decimal)longCached,
                (decimal)longUncached,
                (decimal)longOutput,
                (decimal)longCacheWrite);
            var longPeak = request.PeakAware
                ? new PriceTier(
                    (decimal)(request.LongPeakCachedInput ?? longCached),
                    (decimal)(request.LongPeakUncachedInput ?? longUncached),
                    (decimal)(request.LongPeakOutput ?? longOutput),
                    (decimal)(request.LongPeakCacheWrite ?? longCacheWrite))
                : longIdle;

            longContext = new LongContextPrice(threshold, longIdle, longPeak);
        }

        price = new ModelPrice(idle, peak, request.PeakAware, longContext);
        return true;
    }

    public bool DeleteModel(string model)
    {
        // Prices are stored under the normalized id, profiles under the exact id.
        var normalized = ModelPricing.NormalizeModel(model);
        var removedPrice = _store.DeleteModelPrice(model) |
            _store.DeleteModelPrice(normalized);
        var removedProfile = _store.DeleteModelProfile(model);
        if (removedPrice)
        {
            ApplyOverrides();
            RecomputeCostsForModel(model);
        }

        return removedPrice || removedProfile;
    }

    private void RecomputeCostsForModel(string model)
    {
        var key = ModelPricing.NormalizeModel(model);
        foreach (var candidate in _store.GetDistinctModels())
        {
            if (string.Equals(
                    ModelPricing.NormalizeModel(candidate),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                _store.RecomputeCosts(candidate);
            }
        }
    }

    private const int MaxIconImageChars = 300_000;

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? null : trimmed;
    }

    private static bool TryNormalizeIconKind(string? value, out string? kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                kind = null;
                return true;
            case "letter":
                kind = "letter";
                return true;
            case "image":
                kind = "image";
                return true;
            default:
                kind = null;
                return false;
        }
    }

    private static bool IsHexColor(string value) =>
        value.Length is 4 or 7 or 9 &&
        value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);

    private static bool IsSupportedIcon(string value) =>
        value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase);

    private static ModelSettingsRowPayload ToSettingsRow(
        string model,
        bool priced,
        StoredModelProfile? profile,
        long calls)
    {
        var price = ModelPricing.GetPrice(model);
        var idle = price?.Idle ?? default;
        var peak = price?.Peak ?? default;
        var peakAware = price?.PeakAware ?? false;
        var longContext = price?.LongContext;

        return new ModelSettingsRowPayload(
            model,
            priced,
            profile is not null,
            calls,
            profile?.Provider,
            profile?.IconKind,
            profile?.IconLetter,
            profile?.IconColor,
            profile?.IconImage,
            peakAware,
            (double)idle.CachedInput,
            (double)idle.UncachedInput,
            (double)idle.Output,
            peakAware ? (double)peak.CachedInput : null,
            peakAware ? (double)peak.UncachedInput : null,
            peakAware ? (double)peak.Output : null,
            longContext?.ThresholdTokens,
            longContext is null ? null : (double)longContext.Value.Idle.CachedInput,
            longContext is null ? null : (double)longContext.Value.Idle.UncachedInput,
            longContext is null ? null : (double)longContext.Value.Idle.Output,
            longContext is null || !peakAware ? null : (double)longContext.Value.Peak.CachedInput,
            longContext is null || !peakAware ? null : (double)longContext.Value.Peak.UncachedInput,
            longContext is null || !peakAware ? null : (double)longContext.Value.Peak.Output,
            (double)idle.EffectiveCacheWrite,
            peakAware ? (double)peak.EffectiveCacheWrite : null,
            longContext is null ? null : (double)longContext.Value.Idle.EffectiveCacheWrite,
            longContext is null || !peakAware
                ? null
                : (double)longContext.Value.Peak.EffectiveCacheWrite);
    }

    private static bool IsValidPrice(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1_000_000;

    private OutcomeSummary GetOutcomes(
        long fromMs,
        long toMs,
        string? source)
    {
        var bucketMs = 5 * 60 * 1000L;
        var fromBucket = fromMs - fromMs % bucketMs;
        var toBucket = toMs == long.MaxValue
            ? long.MaxValue
            : toMs - toMs % bucketMs;
        var key =
            $"{source ?? "all"}|{fromBucket}|{toBucket}|{Interlocked.Read(ref _outcomeVersion)}";
        lock (_outcomeCacheLock)
        {
            if (_outcomeCache.TryGetValue(key, out var cached) &&
                DateTimeOffset.UtcNow - cached.At < OutcomeCacheTtl)
            {
                return cached.Value;
            }
        }

        var value = _store.GetOutcomeSummary(fromMs, toMs, source);
        lock (_outcomeCacheLock)
        {
            if (_outcomeCache.Count >= MaxOutcomeCacheEntries)
            {
                // Dropping the oldest entry keeps the other ranges warm; clearing
                // the whole dictionary made every open page recompute at once.
                var oldestKey = (string?)null;
                var oldestAt = DateTimeOffset.MaxValue;
                foreach (var entry in _outcomeCache)
                {
                    if (entry.Value.At < oldestAt)
                    {
                        oldestAt = entry.Value.At;
                        oldestKey = entry.Key;
                    }
                }

                if (oldestKey is not null)
                {
                    _outcomeCache.Remove(oldestKey);
                }
            }

            _outcomeCache[key] = (DateTimeOffset.UtcNow, value);
        }

        return value;
    }

    /// <summary>
    /// metric_points 变化后结果类指标会变，缓存必须失效，否则新指标最长 2 分钟不可见。
    /// </summary>
    public void InvalidateOutcomes() => Interlocked.Increment(ref _outcomeVersion);

    private static string? NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "combined";
        }

        var normalized = source.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" or "combined" => "combined",
            "extension-host" => "copilot-chat",
            _ => normalized,
        };
    }

    private void EnsureCombined(string? source)
    {
        if (string.Equals(source, "combined", StringComparison.OrdinalIgnoreCase))
        {
            _store.EnsureCombinedSpans();
        }
    }

    private static bool Contains(string? value, string term) =>
        value is not null &&
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private SessionMetadata? GetCachedMetadata(
        string? sessionId,
        Dictionary<string, SessionMetadata?> cache)
    {
        if (sessionId is null)
        {
            return null;
        }

        if (!cache.TryGetValue(sessionId, out var metadata))
        {
            metadata = _metadata.Get(sessionId);
            var workspacePath = _workspaces.Resolve(sessionId, metadata?.WorkspacePath);
            if (!string.IsNullOrWhiteSpace(workspacePath) &&
                metadata?.WorkspacePath != workspacePath)
            {
                metadata = metadata is null
                    ? new SessionMetadata(sessionId, null, false, workspacePath)
                    : metadata with { WorkspacePath = workspacePath };
            }

            cache[sessionId] = metadata;
        }

        return metadata;
    }

    private SessionRow WithTitle(
        SessionRow row,
        Dictionary<string, SessionMetadata?> cache)
    {
        var metadata = GetCachedMetadata(row.SessionId, cache);
        return row with
        {
            Title = metadata?.Title,
            WorkspacePath = metadata?.WorkspacePath,
        };
    }

    private CallRow WithTitle(
        CallRow row,
        Dictionary<string, SessionMetadata?> cache) =>
        row with
        {
            Title = GetCachedMetadata(row.SessionId, cache)?.Title,
        };
}
