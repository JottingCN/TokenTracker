using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TokenTracker.Core.Pricing;
using TokenTracker.Core.Storage;
using TokenTracker_App.Services;

namespace TokenTracker_App;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static SpanStore Store { get; private set; } = null!;
    public static OtlpReceiverHost Receiver { get; private set; } = null!;
    public static string? StartupError { get; private set; }

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        AppLog.Write("App ctor");
        UnhandledException += (_, eventArgs) =>
        {
            AppLog.Write($"application unhandled: {eventArgs.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            AppLog.Write($"domain unhandled: {eventArgs.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Write($"task unobserved: {eventArgs.Exception}");
        };
        InitializeComponent();
        AppLog.Write("App initialized");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Write("OnLaunched start");
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TokenTracker");
            AppLog.Write($"opening database in {directory}");
            Store = new SpanStore(Path.Combine(directory, "tokentracker.db"));
            Store.DeleteOlderThan(
                DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds());
            try
            {
                ModelPricing.SetOverrides(
                    Store.GetModelPriceOverrides().Select(entry =>
                        new KeyValuePair<string, ModelPrice>(entry.Model, entry.Price)));
            }
            catch (Exception exception)
            {
                AppLog.Write($"price overrides failed: {exception.Message}");
            }

            try
            {
                var holidays = new HolidayService(Store);
                var year = BeijingCalendar.ToBeijing(DateTimeOffset.UtcNow).Year;
                _ = holidays.EnsureYearsAsync(new[] { year, year + 1 });
            }
            catch (Exception exception)
            {
                AppLog.Write($"holiday load failed: {exception.Message}");
            }

            Receiver = new OtlpReceiverHost(Store);
        }
        catch (Exception exception)
        {
            StartupError = exception.Message;
            AppLog.Write($"startup failed: {exception}");
        }

        try
        {
            Window = new MainWindow();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Window.Closed += async (_, _) =>
            {
                if (Receiver is not null)
                {
                    await Receiver.StopAsync();
                }
            };
            Window.Activate();
            AppLog.Write("window activated");
        }
        catch (Exception exception)
        {
            AppLog.Write($"window creation failed: {exception}");
            throw;
        }

        _ = StartReceiverAsync();
    }

    private static async Task StartReceiverAsync()
    {
        if (Receiver is null)
        {
            return;
        }

        try
        {
            AppLog.Write("starting OTLP receiver");
            await Receiver.StartAsync();
            AppLog.Write(
                $"OTLP receiver running={Receiver.IsRunning} error={Receiver.LastError}");
        }
        catch (Exception exception)
        {
            AppLog.Write($"receiver start failed: {exception}");
        }
    }
}
