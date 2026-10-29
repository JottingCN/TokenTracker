using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenTracker.Core.Models;
using TokenTracker_App.Ui;
using TokenTracker_App.ViewModels;

namespace TokenTracker_App.Pages;

public sealed partial class SessionDetailPage : Page
{
    private static readonly GridLength[] CallWidths =
    {
        new(100),
        new(2, GridUnitType.Star),
        new(80),
        new(80),
        new(80),
        new(64),
        new(110),
        new(60),
    };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private int _tableVersion = -1;

    public SessionDetailViewModel ViewModel { get; }

    public SessionDetailPage(string sessionId)
    {
        ViewModel = new SessionDetailViewModel(sessionId);
        ViewModel.Refresh();
        InitializeComponent();
        _tableVersion = ViewModel.DataVersion;
        RenderCalls();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAndAttach();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => RefreshAndAttach();
        _timer.Start();
    }

    private void RefreshAndAttach()
    {
        ViewModel.Refresh();
        if (ViewModel.DataVersion == _tableVersion)
        {
            return;
        }

        _tableVersion = ViewModel.DataVersion;
        RenderCalls();
    }

    private void RenderCalls()
    {
        CallsPanel.Children.Clear();
        foreach (var item in ViewModel.Calls)
        {
            if (item is CallRow row)
            {
                CallsPanel.Children.Add(
                    TableRowFactory.CreateCallRow(CallWidths, row, includeSession: false));
            }
        }

        if (CallsPanel.Children.Count == 0)
        {
            CallsPanel.Children.Add(TableRowFactory.CreateEmptyRow());
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshAndAttach();
}
