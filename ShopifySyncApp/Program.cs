using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PcaData;
using ShopifySyncApp.Services;
using ShopifySyncApp.ViewModels;
using ShopifySyncApp.Views;
using SyncData;
using SyncHistory;
using SyncJob;

namespace ShopifySyncApp;

internal static class Program
{
    private static IServiceProvider? _services;

    public static IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Services not initialized.");

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _services = BuildServices();

        var syncService = _services.GetRequiredService<SyncService>();
        var historyWriter = _services.GetRequiredService<HistoryWriter>();
        syncService.SyncCompleted += async (_, result) =>
        {
            if (result.FatalError == "Sync already in progress.") return;
            await SyncResultHistoryMapper.WriteAsync(historyWriter, result);
        };

        var config = _services.GetRequiredService<IConfiguration>();
        if (config.GetValue<bool>("App:AutoStartOnLaunch"))
        {
            var interval = config.GetValue<int>("App:PollIntervalMinutes", 5);
            syncService.StartScheduler(TimeSpan.FromMinutes(interval));
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .AfterSetup(_ =>
            {
                Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    Console.Error.WriteLine($"[UI-THREAD] {e.Exception}");
                    e.Handled = true;
                    ShowCrashDialog(e.Exception);
                };
            });

    private static IServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);

        services.AddPcAmericaDb(config);
        services.AddSyncDb(config);
        services.AddSyncJob(config);

        services.AddSingleton<HistoryWriter>();
        services.AddSingleton<HistoryReader>();

        services.AddSingleton(_ => new SettingsService(AppContext.BaseDirectory));

        services.AddTransient<SyncStatusViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Console.Error.WriteLine($"[UNHANDLED] {ex}");
            ShowCrashDialog(ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Console.Error.WriteLine($"[UNOBSERVED] {e.Exception}");
        ShowCrashDialog(e.Exception);
    }

    private static void ShowCrashDialog(Exception ex)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ShowCrashDialogCore(ex);
        else
            Dispatcher.UIThread.Post(() => ShowCrashDialogCore(ex));
    }

    private static void ShowCrashDialogCore(Exception ex)
    {
        var window = new CrashWindow(ex);
        window.Closed += (_, _) => Environment.Exit(1);
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null)
            window.ShowDialog(owner);
        else
            window.Show();
    }
}
