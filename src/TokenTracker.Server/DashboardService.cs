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

    public DashboardPayload GetDashboard(int range, bool includeUtility)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetDashboard(fromMs, toMs, includeUtility);
    }

    public DashboardPayload GetDashboard(long fromMs, long toMs, bool includeUtility)
    {
        var lastReceivedAtMs = _store.GetLastReceivedAtMs();
        var updatedAt = lastReceivedAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastReceivedAtMs)
                .ToLocalTime()
                .ToString("HH:mm:ss")
            : "-";

        return new DashboardPayload(
            _store.GetSummary(fromMs, includeUtility: true, toMs),
            _store.GetModels(fromMs, toMs, includeUtility: true).ToList(),
            _store.GetSessions(fromMs, toMs: toMs).Select(WithTitle).ToList(),
            _store.GetCalls(fromMs, includeUtility: true, toMs: toMs).Select(WithTitle).ToList(),
            _counters.Snapshot(_endpoint, running: true),
            updatedAt,
            _settings.RetentionDays);
    }

    public ModelPayload GetModel(string model, int range)
    {
        var (fromMs, toMs) = RangeTime.Resolve(range, DateTimeOffset.UtcNow);
        return GetModel(model, fromMs, toMs);
    }

    public ModelPayload GetModel(string model, long fromMs, long toMs)
    {
        return new ModelPayload(
            _store.GetModelSummary(model, fromMs, toMs, includeUtility: true),
            _store.GetSessionsForModel(model, fromMs, toMs: toMs).Select(WithTitle).ToList(),
            _store.GetCallsForModel(model, fromMs, toMs).Select(WithTitle).ToList(),
            model);
    }

    public SessionPayload GetSession(string sessionId)
    {
        var calls = _store.GetCallsForSession(sessionId).ToList();
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
        };

        return new SessionPayload(
            summary,
            calls,
            sessionId,
            _metadata.Get(sessionId)?.Title);
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
            overrides[ModelPricing.NormalizeModel(entry.Model)] = entry.Model;
        }

        var spanModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _store.GetDistinctModels())
        {
            spanModels.TryAdd(ModelPricing.NormalizeModel(model), model);
        }

        var defaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ModelPricing.GetDefaultEntries())
        {
            defaultModels[ModelPricing.NormalizeModel(entry.Model)] = entry.Model;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(overrides.Keys);
        keys.UnionWith(spanModels.Keys);
        keys.UnionWith(defaultModels.Keys);

        var rows = new List<ModelPriceRowPayload>();
        foreach (var key in keys)
        {
            var display = defaultModels.TryGetValue(key, out var canonical)
                ? canonical
                : key;
            rows.Add(ToPriceRow(display, customized: overrides.ContainsKey(key)));
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

        var model = request.Model.Trim();
        var idle = new PriceTier(
            (decimal)request.CachedInput,
            (decimal)request.UncachedInput,
            (decimal)request.Output);
        var peak = request.PeakAware
            ? new PriceTier(
                (decimal)(request.PeakCachedInput ?? request.CachedInput),
                (decimal)(request.PeakUncachedInput ?? request.UncachedInput),
                (decimal)(request.PeakOutput ?? request.Output))
            : idle;

        LongContextPrice? longContext = null;
        if (request.LongContextThreshold is > 0 and <= 10_000_000)
        {
            var threshold = request.LongContextThreshold.Value;
            var longCached = request.LongCachedInput ?? request.CachedInput;
            var longUncached = request.LongUncachedInput ?? request.UncachedInput;
            var longOutput = request.LongOutput ?? request.Output;
            if (!IsValidPrice(longCached) ||
                !IsValidPrice(longUncached) ||
                !IsValidPrice(longOutput))
            {
                return null;
            }

            if (request.PeakAware &&
                ((request.LongPeakCachedInput is { } peakCached && !IsValidPrice(peakCached)) ||
                 (request.LongPeakUncachedInput is { } peakUncached && !IsValidPrice(peakUncached)) ||
                 (request.LongPeakOutput is { } peakOutput && !IsValidPrice(peakOutput))))
            {
                return null;
            }

            var longIdle = new PriceTier(
                (decimal)longCached,
                (decimal)longUncached,
                (decimal)longOutput);
            var longPeak = request.PeakAware
                ? new PriceTier(
                    (decimal)(request.LongPeakCachedInput ?? longCached),
                    (decimal)(request.LongPeakUncachedInput ?? longUncached),
                    (decimal)(request.LongPeakOutput ?? longOutput))
                : longIdle;

            longContext = new LongContextPrice(threshold, longIdle, longPeak);
        }

        _store.UpsertModelPrice(
            model,
            new ModelPrice(idle, peak, request.PeakAware, longContext),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ApplyOverrides();
        RecomputeCostsForModel(model);

        return ToPriceRow(model, customized: true);
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

    private static ModelPriceRowPayload ToPriceRow(string model, bool customized)
    {
        var price = ModelPricing.GetPrice(model);
        var hasDefault = ModelPricing.GetBuiltInPrice(model) is not null;
        var idle = price?.Idle ?? default;
        var peak = price?.Peak ?? default;
        var peakAware = price?.PeakAware ?? false;
        var longContext = price?.LongContext;

        return new ModelPriceRowPayload(
            model,
            customized,
            hasDefault,
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
            longContext is null || !peakAware ? null : (double)longContext.Value.Peak.Output);
    }

    private static bool IsValidPrice(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1_000_000;

    private SessionRow WithTitle(SessionRow row)
    {
        var metadata = _metadata.Get(row.SessionId);
        return row with
        {
            Title = metadata?.Title,
            WorkspacePath = metadata?.WorkspacePath,
        };
    }

    private CallRow WithTitle(CallRow row) =>
        row with
        {
            Title = row.SessionId is null ? null : _metadata.Get(row.SessionId)?.Title,
        };
}
