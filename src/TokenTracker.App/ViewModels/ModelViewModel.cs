using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenTracker.Core;
using TokenTracker.Core.Models;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;

namespace TokenTracker_App.ViewModels;

public partial class ModelViewModel : ObservableObject
{
    private readonly SpanStore? _store;
    private readonly SessionMetadataProvider _metadata = new();
    private readonly string _modelId;
    private readonly string _displayName;
    private int _rangeIndex = 2;
    private string _sessionsSignature = string.Empty;
    private long _lastFromMs = -1;
    private long _lastToMs = -1;

    public ModelViewModel(string modelId, string displayName)
    {
        _store = App.Store;
        _modelId = modelId;
        _displayName = displayName;
        Title = displayName;
        ModelIdText = modelId;
        CallsText = "0";
        InputText = "0";
        CacheText = "0";
        HitRateText = "-";
        OutputText = "0";
        CostText = "¥0.000000";
        UnpricedText = "0";
        SessionsText = "0";
        LastUpdatedText = string.Empty;
    }

    public string ModelId => _modelId;
    public string DisplayName => _displayName;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string ModelIdText { get; set; }

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
    public partial string UnpricedText { get; set; }

    [ObservableProperty]
    public partial string SessionsText { get; set; }

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; }

    public ObservableCollection<object> Sessions { get; } = new();
    public int DataVersion { get; private set; }

    public void SetRange(int index) => _rangeIndex = index;

    public void Refresh()
    {
        var store = _store;
        if (store is null)
        {
            Title = "数据库未初始化";
            return;
        }

        var (fromMs, toMs) = RangeTime.Resolve(_rangeIndex, DateTimeOffset.UtcNow);
        try
        {
            var summary = store.GetModelSummary(_modelId, fromMs, toMs);
            var sessionRows = store.GetSessionsForModel(_modelId, fromMs, toMs: toMs);
            var sessions = new List<SessionRow>(sessionRows.Count);
            foreach (var row in sessionRows)
            {
                var metadata = _metadata.Get(row.SessionId);
                sessions.Add(row with { Title = metadata?.Title });
            }

            var rangeChanged = fromMs != _lastFromMs || toMs != _lastToMs;
            var signature = string.Join(
                "|",
                sessions.Select(row =>
                    $"{row.SessionId}|{row.Title}|{row.Calls}|{row.InputTokens}|{row.CacheReadTokens}|{row.CostCny:0.####}|{row.LastSeenMs}"));
            if (rangeChanged || signature != _sessionsSignature)
            {
                Sessions.Clear();
                foreach (var row in sessions)
                {
                    Sessions.Add(row);
                }

                _sessionsSignature = signature;
                DataVersion++;
            }

            _lastFromMs = fromMs;
            _lastToMs = toMs;

            CallsText = DisplayFormat.Number(summary.Calls);
            InputText = DisplayFormat.Number(summary.InputTokens);
            CacheText = DisplayFormat.Number(summary.CacheReadTokens);
            HitRateText = DisplayFormat.Rate(summary.HitRate);
            OutputText = DisplayFormat.Number(summary.OutputTokens);
            CostText = DisplayFormat.Cost(summary.CostCny, summary.UnpricedCalls);
            UnpricedText = DisplayFormat.Number(summary.UnpricedCalls);
            SessionsText = DisplayFormat.Number(summary.Sessions);
            LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            Title = $"查询失败: {exception.Message}";
        }
    }

}
