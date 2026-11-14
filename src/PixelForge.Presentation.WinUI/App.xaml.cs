using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelForge.Composition;
using PixelForge.Domain.Interfaces;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using PixelForge.Presentation.Views;

namespace PixelForge.Presentation;

public partial class App : Microsoft.UI.Xaml.Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;
    private bool _runtimeStarted;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindow { get; internal set; }
    public static bool IsClosing { get; private set; }
    public static Stopwatch StartupClock { get; } = Stopwatch.StartNew();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, "PixelForge.Desktop.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Exit();
            return;
        }

        try
        {
            var services = new ServiceCollection();
            services.AddPixelForge();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<MotionService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IFilePickerService, FilePickerService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IModelPanelService, ModelPanelService>();
            services.AddSingleton<IWaveformService, WaveformService>();
            services.AddSingleton<JobNotificationCoordinator>();
            services.AddSingleton<ShellPage>();
            services.AddSingleton<MainWindow>();
            services.AddTransient<HomeViewModel>();
            services.AddSingleton<ConverterStagingSession>();
            services.AddTransient<ConverterViewModel>();
            services.AddTransient<MediaToolsViewModel>();
            services.AddTransient<WatchFoldersViewModel>();
            services.AddTransient<UpscalerViewModel>();
            services.AddTransient<VideoUpscalerViewModel>();
            services.AddSingleton<OcrViewModel>();
            services.AddTransient<QueueViewModel>();
            services.AddTransient<HistoryViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddSingleton<ModelManagerViewModel>();
            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

            var settings = Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();
            Services.GetRequiredService<IAppLogger>().LogInformation(
                $"Startup: settings ready at {StartupClock.Elapsed.TotalMilliseconds:0.0} ms");
            var theme = Services.GetRequiredService<IThemeService>();
            theme.Apply(settings.Settings);
            settings.SettingsChanged += (_, updated) =>
            {
                theme.Apply(updated);
                Services.GetRequiredService<IAppLogger>().Initialize(
                    Services.GetRequiredService<IAppPaths>().LogsDirectory, updated.Processing.LogLevel);
            };

            MainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow.Activate();
            MainWindow.ApplyInitialPlacement();
            Services.GetRequiredService<IAppLogger>().LogInformation(
                $"Startup: window activated at {StartupClock.Elapsed.TotalMilliseconds:0.0} ms");
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                async () => await InitializeRuntimeAsync());
        }
        catch (Exception ex)
        {
            try
            {
                Services?.GetService<IAppLogger>()?.LogError(ex, "Application startup failed");
            }
            catch
            {
                // Ignore logging failures during startup.
            }

            var errorWindow = new Window
            {
                Title = "PixelForge — startup error",
                Content = new ScrollViewer
                {
                    Padding = new Thickness(32),
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "PixelForge could not start.", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = ex.ToString(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap }
                        }
                    }
                }
            };
            errorWindow.Activate();
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        if (_runtimeStarted) return;
        _runtimeStarted = true;
        var notifications = Services.GetRequiredService<INotificationService>();
        var coordinator = Services.GetRequiredService<JobNotificationCoordinator>();
        coordinator.Start();
        var logger = Services.GetRequiredService<IAppLogger>();
        logger.LogInformation("Startup: runtime initialization began at {Elapsed:0.0} ms",
            StartupClock.Elapsed.TotalMilliseconds);
        try
        {
            var jobService = Services.GetRequiredService<IJobService>();
            await jobService.StartAsync();
            logger.LogInformation("Startup: queue and engines ready at {Elapsed:0.0} ms",
                StartupClock.Elapsed.TotalMilliseconds);
            try
            {
                var watch = Services.GetRequiredService<IWatchFolderService>();
                await watch.InitializeAsync();
                logger.LogInformation("Startup: watch folders ready at {Elapsed:0.0} ms",
                    StartupClock.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Services.GetRequiredService<IAppLogger>().LogError(ex, "Watch folder initialization failed");
                notifications.Notify("Watch folders unavailable",
                    "The processing queue started, but watch-folder monitoring could not be initialized.",
                    NotificationSeverity.Warning);
            }

            notifications.Notify("PixelForge is ready", "Processing engines and the queue are initialized.",
                NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<IAppLogger>().LogError(ex, "Runtime initialization failed");
            notifications.Notify("Engine initialization failed",
                "Open Settings → Diagnostics for details. Some conversion features may be unavailable.",
                NotificationSeverity.Error, autoDismiss: false);
        }
    }

    public static async Task ShutdownAsync()
    {
        if (IsClosing) return;
        IsClosing = true;
        try
        {
            var watch = Services?.GetService<IWatchFolderService>();
            if (watch is not null) await watch.StopAsync();
            var jobs = Services?.GetService<IJobService>();
            if (jobs is not null) await jobs.StopAsync();
        }
        catch
        {
            // Shutdown should never throw.
        }

        try
        {
            if (Services is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (Services is IDisposable disposable) disposable.Dispose();
        }
        catch
        {
            // Ignore disposal errors.
        }

        try
        {
            if (_singleInstanceMutex is not null && _ownsMutex) _singleInstanceMutex.ReleaseMutex();
        }
        catch
        {
            // Ignore.
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services?.GetService<IAppLogger>()?.LogError(e.Exception, "Unhandled UI exception");
        }
        catch
        {
            // Ignore.
        }

        e.Handled = true;
        try
        {
            var dialog = Services?.GetService<IDialogService>();
            if (dialog is not null)
            {
                _ = dialog.ShowErrorAsync("Something went wrong",
                    "PixelForge recovered from an unexpected error. Your files were not modified.",
                    e.Exception?.ToString());
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
