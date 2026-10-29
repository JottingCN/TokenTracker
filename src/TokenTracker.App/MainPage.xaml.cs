using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenTracker.Core.Models;
using TokenTracker_App.Ui;
using TokenTracker_App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TokenTracker_App;

public sealed partial class MainPage : Page
{
    private static readonly GridLength[] ModelWidths =
    {
        new(2, GridUnitType.Star),
        new(70),
        new(220),
        new(170),
        new(140),
    };

    private static readonly GridLength[] SessionWidths =
    {
        new(200),
        new(1, GridUnitType.Star),
        new(50),
        new(80),
        new(80),
        new(64),
        new(80),
        new(80),
        new(100),
        new(100),
    };

    private static readonly GridLength[] CallWidths =
    {
        new(100),
        new(1.2, GridUnitType.Star),
        new(1.2, GridUnitType.Star),
        new(80),
        new(80),
        new(72),
        new(64),
        new(110),
        new(60),
    };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _isReady;
    private int _tableVersion = -1;

    public DashboardViewModel ViewModel { get; }

    public MainPage()
    {
        AppLog.Write("MainPage ctor");
        ViewModel = new DashboardViewModel(App.Store, App.Receiver);
        ViewModel.Refresh();
        InitializeComponent();
        _tableVersion = ViewModel.DataVersion;
        RenderTables();
        AppLog.Write(
            $"tables initial: v={_tableVersion} models={ViewModel.Models.Count} " +
            $"sessions={ViewModel.Sessions.Count} calls={ViewModel.Calls.Count}");
        _isReady = true;
        AppLog.Write("MainPage initialized");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write("MainPage loaded");
        try
        {
            RefreshAndAttach();
            AppLog.Write("MainPage first refresh done");

            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) =>
            {
                try
                {
                    RefreshAndAttach();
                }
                catch (Exception exception)
                {
                    AppLog.Write($"timer refresh failed: {exception}");
                }
            };
            _timer.Start();
        }
        catch (Exception exception)
        {
            AppLog.Write($"MainPage load failed: {exception}");
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

    private void RefreshAndAttach()
    {
        ViewModel.Refresh();
        if (ViewModel.DataVersion == _tableVersion)
        {
            return;
        }

        _tableVersion = ViewModel.DataVersion;
        RenderTables();
    }

    private void RenderTables()
    {
        ModelsPanel.Children.Clear();
        foreach (var item in ViewModel.Models)
        {
            if (item is ModelRow row)
            {
                ModelsPanel.Children.Add(TableRowFactory.CreateModelRow(ModelWidths, row));
            }
        }

        if (ModelsPanel.Children.Count == 0)
        {
            ModelsPanel.Children.Add(TableRowFactory.CreateEmptyRow());
        }

        SessionsPanel.Children.Clear();
        foreach (var item in ViewModel.Sessions)
        {
            if (item is SessionRow row)
            {
                SessionsPanel.Children.Add(
                    TableRowFactory.CreateSessionRow(SessionWidths, row, SessionLink_Click));
            }
        }

        if (SessionsPanel.Children.Count == 0)
        {
            SessionsPanel.Children.Add(TableRowFactory.CreateEmptyRow());
        }

        CallsPanel.Children.Clear();
        foreach (var item in ViewModel.Calls)
        {
            if (item is CallRow row)
            {
                CallsPanel.Children.Add(TableRowFactory.CreateCallRow(CallWidths, row));
            }
        }

        if (CallsPanel.Children.Count == 0)
        {
            CallsPanel.Children.Add(TableRowFactory.CreateEmptyRow());
        }
    }

    private void SessionLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sessionId } &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            MainWindow.CurrentWindow?.ShowSessionDetail(sessionId);
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

    private void UtilitySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        ViewModel.SetIncludeUtility(UtilitySwitch.IsOn);
        RefreshAndAttach();
    }

    private void CopyConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(
                """
                {
                  "github.copilot.chat.otel.enabled": true,
                  "github.copilot.chat.otel.exporterType": "otlp-http",
                  "github.copilot.chat.otel.otlpEndpoint": "http://127.0.0.1:4318",
                  "github.copilot.chat.otel.protocol": "http/json",
                  "github.copilot.chat.otel.captureContent": false
                }
                """);
            Clipboard.SetContent(data);
        }
        catch
        {
            // Clipboard is best-effort.
        }
    }
}
