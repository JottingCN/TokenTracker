using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenTracker.Core;
using TokenTracker.Core.Models;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;
using TokenTracker_App.Services;

namespace TokenTracker_App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly SpanStore? _store;
    private readonly OtlpReceiverHost? _receiver;
    private readonly SessionMetadataProvider _metadata = new();
    private int _rangeIndex = 2;
    private bool _includeUtility;
    private string _modelsSignature = string.Empty;
    private string _sessionsSignature = string.Empty;
    private string _callsSignature = string.Empty;
    private long _lastFromMs = -1;
    private long _lastToMs = -1;

    public DashboardViewModel(SpanStore? store, OtlpReceiverHost? receiver)
    {
        _store = store;
        _receiver = receiver;
        CallsText = "0";
        InputText = "0";
        CacheText = "0";
        HitRateText = "-";
        OutputText = "0";
        CostText = "¥0.000000";
        SessionsText = "0";
        UnpricedText = "0";
        ReceiverStatus = "等待接收器";
        ReceivedText = "-";
        LastUpdatedText = string.Empty;
    }

    [ObservableProperty]
    public partial string CallsText { get; set; }

    [ObservableProperty]
    public partial string InputText { get; set; }

    [ObservableProperty]
    public partial string CacheText { get; set; }

    [ObservableProperty]
    public partial string HitRateText { get; set; }

    [ObservableProperty]
    public partial string OutputText { get; set; }

    [ObservableProperty]
    public partial string CostText { get; set; }

    [ObservableProperty]
    public partial string SessionsText { get; set; }

    [ObservableProperty]
    public partial string UnpricedText { get; set; }

    [ObservableProperty]
    public partial string ReceiverStatus { get; set; }

    [ObservableProperty]
    public partial string ReceivedText { get; set; }

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; }

    public ObservableCollection<object> Sessions { get; } = new();
    public ObservableCollection<object> Calls { get; } = new();
    public ObservableCollection<object> Models { get; } = new();
    public int DataVersion { get; private set; }

    public void SetRange(int index) => _rangeIndex = index;

    public void SetIncludeUtility(bool value) => _includeUtility = value;

    public void Refresh()
    {
        var store = _store;
        if (store is null)
        {
            ReceiverStatus = App.StartupError ?? "数据库未初始化";
            return;
        }

        var (fromMs, toMs) = RangeTime.Resolve(_rangeIndex, DateTimeOffset.UtcNow);
        try
        {
            var summary = store.GetSummary(fromMs, _includeUtility, toMs);
            var sessionRows = store.GetSessions(fromMs, toMs: toMs);
            var enrichedSessions = new List<SessionRow>(sessionRows.Count);
            foreach (var row in sessionRows)
            {
                var metadata = _metadata.Get(row.SessionId);
                enrichedSessions.Add(row with { Title = metadata?.Title });
            }

            var callRows = store.GetCalls(fromMs, _includeUtility, toMs: toMs);
            var enrichedCalls = new List<CallRow>(callRows.Count);
            foreach (var row in callRows)
            {
                var metadata = row.SessionId is null ? null : _metadata.Get(row.SessionId);
                enrichedCalls.Add(row with { Title = metadata?.Title });
            }

            var models = store.GetModels(fromMs, toMs);
            var rangeChanged = fromMs != _lastFromMs || toMs != _lastToMs;
            var changed = false;

            var modelsSignature = string.Join(
                "|",
                models.Select(row =>
                    $"{row.Model}|{row.Calls}|{row.InputTokens}|{row.CacheReadTokens}|{row.CostCny:0.####}|{row.UnpricedCalls}"));
            var sessionsSignature = string.Join(
                "|",
                enrichedSessions.Select(row =>
                    $"{row.SessionId}|{row.Title}|{row.Model}|{row.Calls}|{row.InputTokens}|{row.CacheReadTokens}|{row.CostCny:0.####}|{row.LastSeenMs}"));
            var callsSignature = string.Join(
                "|",
                enrichedCalls.Select(row =>
                    $"{row.StartTimeMs}|{row.Model}|{row.SessionId}|{row.Title}|{row.InputTokens}|{row.CacheReadTokens}|{row.OutputTokens}|{row.CostCny:0.####}|{row.PricingPeriod}"));

            if (rangeChanged || modelsSignature != _modelsSignature)
            {
                Replace(Models, models);
                _modelsSignature = modelsSignature;
                changed = true;
            }

            if (rangeChanged || sessionsSignature != _sessionsSignature)
            {
                Replace(Sessions, enrichedSessions);
                _sessionsSignature = sessionsSignature;
                changed = true;
            }

            if (rangeChanged || callsSignature != _callsSignature)
            {
                Replace(Calls, enrichedCalls);
                _callsSignature = callsSignature;
                changed = true;
            }

            _lastFromMs = fromMs;
            _lastToMs = toMs;

            if (changed)
            {
                DataVersion++;
            }

            CallsText = DisplayFormat.Number(summary.Calls);
            InputText = DisplayFormat.Number(summary.InputTokens);
            CacheText = DisplayFormat.Number(summary.CacheReadTokens);
            HitRateText = DisplayFormat.Rate(summary.HitRate);
            OutputText = DisplayFormat.Number(summary.OutputTokens);
            CostText = DisplayFormat.Cost(summary.CostCny, summary.UnpricedCalls);
            SessionsText = DisplayFormat.Number(summary.Sessions);
            UnpricedText = DisplayFormat.Number(summary.UnpricedCalls);
        }
        catch (Exception exception)
        {
            ReceiverStatus = $"查询失败: {exception.Message}";
            return;
        }

        ReceiverStatus = GetReceiverStatus();
        ReceivedText = _receiver is null
            ? "-"
            : $"{_receiver.ReceivedBatches} 批 / {_receiver.ReceivedSpans} span";
        LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
    }

    private string GetReceiverStatus()
    {
        if (App.StartupError is not null)
        {
            return $"启动失败: {App.StartupError}";
        }

        if (_receiver is null)
        {
            return "接收器未初始化";
        }

        return _receiver.IsRunning
            ? $"监听 {_receiver.Endpoint}"
            : $"接收器未运行: {_receiver.LastError ?? "unknown"}";
    }

    private static void Replace<T>(
        ObservableCollection<object> target,
        IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item!);
        }
    }
}
