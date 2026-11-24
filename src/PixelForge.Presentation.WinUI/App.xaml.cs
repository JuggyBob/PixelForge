using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelForge.Application.Models;
using PixelForge.Composition;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
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
        var commandArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string userScope;
        try
        {
            userScope = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                        ?? Environment.UserName;
        }
        catch
        {
            userScope = Environment.UserName;
        }

        _singleInstanceMutex = new Mutex(true,
            @"Local\PixelForge.Desktop.SingleInstance." + userScope.Replace('\\', '_'), out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            await ShellActivationService.SendToRunningInstanceAsync(commandArgs);
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
            services.AddSingleton<IDownloadActivityService, DownloadActivityService>();
            services.AddSingleton<IModelPanelService, ModelPanelService>();
            services.AddSingleton<IWaveformService, WaveformService>();
            services.AddSingleton<JobNotificationCoordinator>();
            services.AddSingleton<ShellPage>();
            services.AddSingleton<MainWindow>();
            services.AddTransient<HomeViewModel>();
            services.AddSingleton<ConverterStagingSession>();
            services.AddTransient<ConverterViewModel>();
            services.AddTransient<PipelinesViewModel>();
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
            var logger = Services.GetRequiredService<IAppLogger>();
            logger.LogInformation(
                $"Startup: settings ready at {StartupClock.Elapsed.TotalMilliseconds:0.0} ms");

            // Resolve the language before constructing any window. A stored value
            // is validated; an empty value means this is the first launch.
            var storedLanguage = settings.Settings.General.Language;
            var language = string.IsNullOrWhiteSpace(storedLanguage)
                ? LocalizationService.DetectSystemLanguage()
                : LocalizationService.NormalizeLocale(storedLanguage);
            LocalizationService.Initialize(language);
            if (!string.Equals(storedLanguage, language, StringComparison.Ordinal))
            {
                await settings.UpdateAsync(s => s.General.Language = language);
                logger.LogInformation("Language initialized as {Language}", language);
            }

            var theme = Services.GetRequiredService<IThemeService>();
            theme.Apply(settings.Settings);
            settings.SettingsChanged += (_, updated) =>
            {
                var dispatcher = MainWindow?.DispatcherQueue;
                if (dispatcher is null || dispatcher.HasThreadAccess)
                {
                    ApplySettings(updated);
                }
                else
                {
                    dispatcher.TryEnqueue(() => ApplySettings(updated));
                }
            };

            void ApplySettings(AppSettings updated)
            {
                theme.Apply(updated);
                Services.GetRequiredService<IAppLogger>().Initialize(
                    Services.GetRequiredService<IAppPaths>().LogsDirectory, updated.Processing.LogLevel);
            }

            MainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow.Activate();
            MainWindow.ApplyInitialPlacement();
            Services.GetRequiredService<IAppLogger>().LogInformation(
                $"Startup: window activated at {StartupClock.Elapsed.TotalMilliseconds:0.0} ms");
            _ = Task.Run(LocalizationService.WarmupPatterns);
            ShellActivationService.StartServer(forwarded =>
                MainWindow?.DispatcherQueue.TryEnqueue(async () => await HandleShellActivationAsync(forwarded)));
            if (commandArgs.Length > 0)
                _ = HandleShellActivationAsync(commandArgs);
            if (settings.Settings.Updates.CheckOnStartup)
                QueueStartupUpdateCheck();
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
                Title = LocalizationService.T("PixelForge - startup error"),
                Content = new ScrollViewer
                {
                    Padding = new Thickness(32),
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = LocalizationService.T("PixelForge could not start."), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = ex.ToString(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap }
                        }
                    }
                }
            };
            errorWindow.Activate();
        }
    }

    private static void QueueStartupUpdateCheck()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var updates = Services?.GetService<IUpdateService>();
                var notifications = Services?.GetService<INotificationService>();
                var settings = Services?.GetService<ISettingsService>();
                if (updates is null || notifications is null || settings is null) return;
                var result = await updates.CheckAsync(includePreReleases: false);
                if (result.Outcome != UpdateCheckOutcome.UpdateAvailable || result.Release is null) return;
                if (string.Equals(settings.Settings.Updates.LastNotifiedVersion,
                        result.Release.Version, StringComparison.OrdinalIgnoreCase)) return;
                await settings.UpdateAsync(s => s.Updates.LastNotifiedVersion = result.Release.Version);
                MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    notifications.Notify("PixelForge update available",
                        $"Version {result.Release.Version} is available. Open Settings > About to review it.",
                        NotificationSeverity.Info));
            }
            catch
            {
                // The opt-in startup check must never disturb application startup.
            }
        });
    }

    private static async Task HandleShellActivationAsync(string[] args)
    {
        try
        {
            var shell = Services?.GetService<ShellPage>();
            if (shell is not null) await shell.HandleShellActivationAsync(args);
        }
        catch (Exception ex)
        {
            try { Services?.GetService<IAppLogger>()?.LogError(ex, "Shell activation failed"); } catch { }
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
            QueueLegacyLlamaRuntimeUpgrade(notifications, logger);
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<IAppLogger>().LogError(ex, "Runtime initialization failed");
            notifications.Notify("Engine initialization failed",
                "Open Settings → Diagnostics for details. Some conversion features may be unavailable.",
                NotificationSeverity.Error, autoDismiss: false);
        }
    }

    private static void QueueLegacyLlamaRuntimeUpgrade(INotificationService notifications, IAppLogger logger)
    {
        try
        {
            var upgrade = Services.GetRequiredService<LlamaCppRuntimeUpgradeService>();
            if (upgrade.FindLegacyRuntimeDirectory() is null) return;

            notifications.Notify("Updating OCR runtime",
                "A newer llama.cpp Vulkan runtime is required. PixelForge is installing it in the background.",
                NotificationSeverity.Info, autoDismiss: true);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await upgrade.UpgradeAsync();
                    switch (result.Outcome)
                    {
                        case LlamaCppRuntimeUpgradeOutcome.Updated:
                        case LlamaCppRuntimeUpgradeOutcome.AlreadyCurrent:
                            notifications.Notify("OCR runtime updated",
                                "Advanced OCR is ready on the current llama.cpp Vulkan runtime.",
                                NotificationSeverity.Success);
                            break;
                        case LlamaCppRuntimeUpgradeOutcome.Failed:
                            notifications.Notify("OCR runtime update failed",
                                result.Error ?? "Open AI Models to retry the llama.cpp runtime download.",
                                NotificationSeverity.Warning, autoDismiss: false);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "llama.cpp runtime upgrade failed");
                    notifications.Notify("OCR runtime update failed",
                        "Open AI Models to retry the llama.cpp runtime download.",
                        NotificationSeverity.Warning, autoDismiss: false);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Could not start the llama.cpp runtime upgrade: {ex.Message}");
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
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
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
                    "PixelForge recovered from an unexpected error. A running job may have been interrupted; check the Queue and History for details.",
                    e.Exception?.ToString());
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
