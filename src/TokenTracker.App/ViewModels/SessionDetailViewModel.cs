using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenTracker.Core;
using TokenTracker.Core.Models;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;

namespace TokenTracker_App.ViewModels;

public partial class SessionDetailViewModel : ObservableObject
{
    private readonly SpanStore? _store;
    private readonly SessionMetadataProvider _metadata = new();
    private readonly string _sessionId;
    private string _callsSignature = string.Empty;

    public SessionDetailViewModel(string sessionId)
    {
        _store = App.Store;
        _sessionId = sessionId;
        Title = DisplayFormat.SessionDisplay(null, sessionId);
        SessionIdText = sessionId;
        CallsText = "0";
        InputText = "0";
        CacheText = "0";
        HitRateText = "-";
        OutputText = "0";
        CostText = "¥0.000000";
        UnpricedText = "0";
        DetailCountText = "0";
        LastUpdatedText = string.Empty;
    }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string SessionIdText { get; set; }

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
    public partial string DetailCountText { get; set; }

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; }

    public ObservableCollection<object> Calls { get; } = new();
    public int DataVersion { get; private set; }

    public void Refresh()
    {
        var store = _store;
        if (store is null)
        {
            Title = "数据库未初始化";
            return;
        }

        try
        {
            var calls = store.GetCallsForSession(_sessionId);
            var signature = string.Join(
                "|",
                calls.Select(row =>
                    $"{row.StartTimeMs}|{row.Model}|{row.InputTokens}|{row.CacheReadTokens}|{row.OutputTokens}|{row.CostCny:0.####}|{row.PricingPeriod}"));
            if (signature != _callsSignature)
            {
                Calls.Clear();
                foreach (var row in calls)
                {
                    Calls.Add(row);
                }

                _callsSignature = signature;
                DataVersion++;
            }

            var input = calls.Sum(row => row.InputTokens);
            var cache = calls.Sum(row => row.CacheReadTokens);
            var output = calls.Sum(row => row.OutputTokens);
            var cost = calls.Sum(row => row.CostCny ?? 0);
            var unpriced = calls.Count(row => row.CostCny is null);
            var hitRate = input > 0 ? (double)cache / input : (double?)null;

            CallsText = DisplayFormat.Number(calls.Count);
            InputText = DisplayFormat.Number(input);
            CacheText = DisplayFormat.Number(cache);
            HitRateText = DisplayFormat.Rate(hitRate);
            OutputText = DisplayFormat.Number(output);
            CostText = DisplayFormat.Cost(cost, unpriced);
            UnpricedText = DisplayFormat.Number(unpriced);
            DetailCountText = DisplayFormat.Number(calls.Count);

            var metadata = _metadata.Get(_sessionId);
            Title = DisplayFormat.SessionDisplay(metadata?.Title, _sessionId);
            LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            Title = $"查询失败: {exception.Message}";
        }
    }
}
