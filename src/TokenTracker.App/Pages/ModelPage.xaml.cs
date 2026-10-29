using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenTracker.Core.Models;
using TokenTracker_App.Ui;
using TokenTracker_App.ViewModels;

namespace TokenTracker_App.Pages;

public sealed partial class ModelPage : Page
{
    private static readonly GridLength[] SessionWidths =
    {
        new(2, GridUnitType.Star),
        new(50),
        new(80),
        new(80),
        new(64),
        new(80),
        new(80),
        new(100),
        new(100),
    };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _isReady;
    private int _tableVersion = -1;

    public ModelViewModel ViewModel { get; }

    public ModelPage(string modelId, string displayName)
    {
        ViewModel = new ModelViewModel(modelId, displayName);
        ViewModel.Refresh();
        InitializeComponent();
        _tableVersion = ViewModel.DataVersion;
        RenderSessions();
        AppLog.Write(
            $"model tables initial: v={_tableVersion} sessions={ViewModel.Sessions.Count}");
        _isReady = true;
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
        RenderSessions();
    }

    private void RenderSessions()
    {
        SessionsPanel.Children.Clear();
        foreach (var item in ViewModel.Sessions)
        {
            if (item is SessionRow row)
            {
                SessionsPanel.Children.Add(
                    TableRowFactory.CreateSessionRow(
                        SessionWidths,
                        row,
                        SessionLink_Click,
                        includeModel: false));
            }
        }

        if (SessionsPanel.Children.Count == 0)
        {
            SessionsPanel.Children.Add(TableRowFactory.CreateEmptyRow());
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

    private void SessionLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sessionId } &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            MainWindow.CurrentWindow?.ShowSessionDetail(
                sessionId,
                ViewModel.ModelId,
                ViewModel.DisplayName);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshAndAttach();

    private void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || RangeBox.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SetRange(RangeBox.SelectedIndex);
        RefreshAndAttach();
    }
}
