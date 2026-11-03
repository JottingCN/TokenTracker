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
    private string? _lastError;

    public void Record(int spans)
    {
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _spans, spans);
    }

    public void RecordError(string message) => _lastError = message;

    public ReceiverInfo Snapshot(string endpoint, bool running) =>
        new(
            endpoint,
            running,
            Interlocked.Read(ref _batches),
            Interlocked.Read(ref _spans),
            _lastError);
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

        return new DashboardPayload(
            _store.GetSummary(fromMs, includeUtility: true, toMs, source),
            _store.GetModels(fromMs, toMs, includeUtility: true, source).ToList(),
            sessionRows,
            _counters.Snapshot(_endpoint, running: true),
            updatedAt,
            _settings.RetentionDays,
            _store.GetSignalHealth(
                null,
                source == "combined" ? null : source),
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

        return new CallsPayload(calls, total, pageOffset, pageSize);
    }

    public SessionPayload GetSession(string sessionId, string? source = null)
    {
        source = NormalizeSource(source);
        EnsureCombined(source);
        var detailSource = source == "combined" ? null : source;
        var calls = _store.GetCallsForSession(sessionId, source: source).ToList();
        var ttftCalls = calls.Where(call => call.TtftMs is > 0).ToList();
        var generationCalls = calls
            .Where(call => call.DurationMs is > 0 &&
                           call.OutputTokens >= 5 &&
                           call.TtftMs is long ttft &&
                           call.DurationMs - ttft >= 50)
            .ToList();
        var generationMs = generationCalls.Sum(
            call => call.DurationMs!.Value - call.TtftMs!.Value);
        var summary = new SummaryDto
        {
            Calls = calls.Count,
            InputTokens = calls.Sum(call => call.InputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            OutputTokens = calls.Sum(call => call.OutputTokens),
            ReasoningTokens = calls.Sum(call => call.ReasoningTokens),
            Sessions = calls.Count > 0 ? 1 : 0,
            CostCny = calls.Sum(call => call.CostCny ?? 0),
            CacheCostCny = calls.Sum(call => call.CacheCostCny),
            UncachedCostCny = calls.Sum(call => call.UncachedCostCny),
            OutputCostCny = calls.Sum(call => call.OutputCostCny),
            ReasoningCostCny = calls.Sum(call => call.ReasoningCostCny),
            UnpricedCalls = calls.Count(call => call.CostCny is null),
            AvgTtftMs = ttftCalls.Count > 0
                ? ttftCalls.Average(call => call.TtftMs!.Value)
                : null,
            TtftSamples = ttftCalls.Count,
            TokensPerSecond = generationMs > 0
                ? generationCalls.Sum(call => call.OutputTokens) * 1_000d /
                  generationMs
                : null,
        };

        var events = _store.GetSessionEvents(sessionId, detailSource).ToList();
        var chatEvents = calls
            .GroupBy(
                call => call.Source ?? "copilot-chat",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var durations = group
                    .Where(call => call.DurationMs is > 0)
                    .Select(call => call.DurationMs!.Value)
                    .ToList();
                return new SessionEventRow
                {
                    Operation = "chat",
                    Source = group.Key,
                    Count = group.LongCount(),
                    AvgDurationMs = durations.Count > 0
                        ? durations.Average()
                        : null,
                    ErrorCount = 0,
                    LastSeenMs = group.Max(call => call.StartTimeMs),
                };
            })
            .OrderByDescending(row => row.Count)
            .ToList();
        events.InsertRange(0, chatEvents);

        var metadata = _metadata.Get(sessionId);
        var tools = _store.GetSessionTools(sessionId, detailSource).ToList();
        return new SessionPayload(
            summary,
            calls,
            events,
            tools,
            _store.GetAgentRuns(sessionId, detailSource).ToList(),
            _store.GetHookSummaries(sessionId, detailSource).ToList(),
            _store.GetSignalHealth(sessionId, detailSource),
            _store.GetOutcomeSummary(
                0,
                long.MaxValue,
                detailSource,
                sessionId),
            sessionId,
            metadata?.Title,
            metadata?.WorkspacePath);
    }

    public TracePayload GetTrace(string sessionId, string? source = null)
    {
        var normalized = NormalizeSource(source);
        return new TracePayload(
            _store.GetTraceNodes(
                sessionId,
                normalized == "combined" ? null : normalized).ToList());
    }

    public void ApplyOverrides() =>
        ModelPricing.SetOverrides(
            _store.GetModelPriceOverrides().Select(entry =>
                new KeyValuePair<string, ModelPrice>(entry.Model, entry.Price)));

    public List<ModelPriceRowPayload> GetPrices()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _store.GetModelPriceOverrides())
        {
            overrides[entry.Model] = entry.Model;
        }

        var spanModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _store.GetDistinctModels())
        {
            spanModels.TryAdd(model, model);
        }

        var callCounts = _store.GetModelCallCounts();

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(overrides.Keys);
        keys.UnionWith(spanModels.Keys);

        var rows = new List<ModelPriceRowPayload>();
        foreach (var key in keys)
        {
            var display = spanModels.TryGetValue(key, out var canonical)
                ? canonical
                : overrides.TryGetValue(key, out var original)
                    ? original
                    : key;
            var calls = callCounts.TryGetValue(display, out var count) ? count : 0;
            rows.Add(ToPriceRow(
                display,
                customized: overrides.ContainsKey(key),
                calls));
        }

        return rows
            .OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ModelPriceRowPayload? SavePrice(PriceUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model) ||
            !IsValidPrice(request.CachedInput) ||
            !IsValidPrice(request.UncachedInput) ||
            !IsValidPrice(request.Output))
        {
            return null;
        }

        if (request.PeakAware &&
            (!IsValidPrice(request.PeakCachedInput ?? 0) ||
             !IsValidPrice(request.PeakUncachedInput ?? 0) ||
             !IsValidPrice(request.PeakOutput ?? 0)))
        {
            return null;
        }

        var model = ModelPricing.NormalizeModel(request.Model);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        var cacheWrite = request.CacheWrite ?? request.UncachedInput;
        var peakCacheWrite = request.PeakCacheWrite ??
            request.CacheWrite ??
            request.PeakUncachedInput ??
            request.UncachedInput;
        if (!IsValidPrice(cacheWrite) ||
            (request.PeakAware && !IsValidPrice(peakCacheWrite)))
        {
            return null;
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
                return null;
            }

            if (request.PeakAware &&
                ((request.LongPeakCachedInput is { } peakCached && !IsValidPrice(peakCached)) ||
                 (request.LongPeakUncachedInput is { } peakUncached && !IsValidPrice(peakUncached)) ||
                 (request.LongPeakOutput is { } peakOutput && !IsValidPrice(peakOutput)) ||
                 (request.LongPeakCacheWrite is { } peakWrite && !IsValidPrice(peakWrite))))
            {
                return null;
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

        _store.UpsertModelPrice(
            model,
            new ModelPrice(idle, peak, request.PeakAware, longContext),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ApplyOverrides();
        RecomputeCostsForModel(model);

        var calls = _store.GetModelCallCounts()
            .TryGetValue(model, out var count) ? count : 0;
        return ToPriceRow(model, customized: true, calls);
    }

    public bool DeletePrice(string model)
    {
        var removed = _store.DeleteModelPrice(model);
        ApplyOverrides();
        RecomputeCostsForModel(model);
        return removed;
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

    private static ModelPriceRowPayload ToPriceRow(
        string model,
        bool customized,
        long calls)
    {
        var price = ModelPricing.GetPrice(model);
        var idle = price?.Idle ?? default;
        var peak = price?.Peak ?? default;
        var peakAware = price?.PeakAware ?? false;
        var longContext = price?.LongContext;

        return new ModelPriceRowPayload(
            model,
            customized,
            calls,
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
        const long bucketMs = 5 * 60 * 1000;
        var fromBucket = fromMs - fromMs % bucketMs;
        var toBucket = toMs == long.MaxValue
            ? long.MaxValue
            : toMs - toMs % bucketMs;
        var key = $"{source ?? "all"}|{fromBucket}|{toBucket}";
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
            if (_outcomeCache.Count > 32)
            {
                _outcomeCache.Clear();
            }

            _outcomeCache[key] = (DateTimeOffset.UtcNow, value);
        }

        return value;
    }

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
