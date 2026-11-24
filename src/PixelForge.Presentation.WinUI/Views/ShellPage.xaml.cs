using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using XamlApplication = Microsoft.UI.Xaml.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Presentation.Controls;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace PixelForge.Presentation.Views;

public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigation;
    private readonly IThemeService _theme;
    private readonly MotionService _motion;
    private readonly IJobService _jobs;
    private readonly ICapabilityRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IModelPanelService _modelPanel;
    private readonly IDownloadActivityService _downloads;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<NotificationHistoryItem> _notificationHistory = [];
    private readonly ObservableCollection<ActiveJobNotificationItem> _activeNotifications = [];
    private readonly List<AppNotification> _notificationRecords = [];
    private int _unreadNotifications;
    private bool _activeNotificationUpdateQueued;
    private bool _syncingSelection;
    private bool _initialized;
    private bool _eventsSubscribed;
    private string[]? _pendingShellActivation;
    private Storyboard? _compactPulse;
    private Storyboard? _indicatorStoryboard;
    private Storyboard? _statusCardStoryboard;
    private bool? _statusCardCompact;
    private bool? _paneTransitionTarget;
    private double _indicatorTargetY;
    private double _sidebarWidth = 280;
    private bool _resizingPane;
    private bool _indicatorUpdateQueued;
    private bool _indicatorAnimateRequested;
    private readonly List<OnboardingStep> _onboardingSteps = [];
    private int _onboardingIndex;
    private bool _onboardingActive;
    private Storyboard? _onboardingStoryboard;
    private const double MinSidebarWidth = 240;
    private const double MaxSidebarWidth = 400;
    private const double OnboardingCardWidth = 400;

    public ShellPage(INavigationService navigation, IThemeService theme, MotionService motion,
        IJobService jobs, ICapabilityRegistry registry, ISettingsService settings,
        INotificationService notifications, IModelPanelService modelPanel,
        IDownloadActivityService downloads, IAppLogger logger)
    {
        InitializeComponent();
        NotificationHistoryList.ItemsSource = _notificationHistory;
        ActiveNotificationList.ItemsSource = _activeNotifications;
        DownloadActivityList.ItemsSource = downloads.Items;
        UpdateNotificationHistoryState();
        _navigation = navigation;
        _theme = theme;
        _motion = motion;
        _jobs = jobs;
        _registry = registry;
        _settings = settings;
        _notifications = notifications;
        _modelPanel = modelPanel;
        _downloads = downloads;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _onboardingSteps.AddRange([
            new OnboardingStep(HomeNav, OnboardingAnchor.Right, "Welcome to PixelForge",
                "Your local media studio. Convert, restore, upscale, and OCR files without uploading anything."),
            new OnboardingStep(ConverterNav, OnboardingAnchor.Right, "Convert almost anything",
                "Stage images, audio, video, documents, e-books, and archives. Choose a target format, destination, and collision policy."),
            new OnboardingStep(ToolsNav, OnboardingAnchor.Right, "Focused media tools",
                "Trim, compress, normalise, remove backgrounds, edit metadata, and fix video without leaving the app."),
            new OnboardingStep(OcrNav, OnboardingAnchor.Right, "Turn scans into documents",
                "Order pages, review recognised text, and export a single DOCX with an optional PDF companion."),
            new OnboardingStep(UpscalerNav, OnboardingAnchor.Right, "Restore and upscale images",
                "Use traditional resampling or AI models, with provider and compatibility details shown before you queue."),
            new OnboardingStep(VideoUpscalerNav, OnboardingAnchor.Right, "Sharpen video frame by frame",
                "Combine AI super-resolution with RIFE interpolation and temporal stability while preserving audio."),
            new OnboardingStep(WatchNav, OnboardingAnchor.Right, "Let folders run themselves",
                "Watch a folder and queue new files automatically with the same validated settings as the main tools."),
            new OnboardingStep(QueueNav, OnboardingAnchor.Right, "One verified queue",
                "Every job runs locally through the queue. History keeps the settings, providers, verification, and reports."),
            new OnboardingStep(NotificationCenterButton, OnboardingAnchor.Below, "Stay in the loop",
                "The bell collects job results and download activity. Live progress and Pause/Resume controls stay here."),
            new OnboardingStep(SettingsNav, OnboardingAnchor.Right, "Make it yours",
                "Set defaults, model folders, OCR and upscaling behaviour, appearance, diagnostics, and update checks.")
        ]);
        ShellRoot.SizeChanged += (_, _) => { if (_onboardingActive) PositionOnboardingStep(); };

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += OnNavigated;
        SubscribeShellEvents();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void SubscribeShellEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;
        _jobs.JobChanged += OnJobChanged;
        _jobs.QueueChanged += OnQueueChanged;
        _registry.CapabilitiesChanged += OnCapabilitiesChanged;
        _theme.ThemeApplied += OnThemeApplied;
        _notifications.NotificationRaised += OnNotification;
        _downloads.Changed += OnDownloadsChanged;
        _modelPanel.OpenRequested += OnModelPanelRequested;
        _modelPanel.CloseRequested += OnModelPanelCloseRequested;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Nav.PaneOpening += OnPaneOpening;
        Nav.PaneClosing += OnPaneClosing;
        Nav.PaneOpened += OnPaneStateChanged;
        Nav.PaneClosed += OnPaneStateChanged;
        Nav.DisplayModeChanged += OnDisplayModeChanged;
        Nav.SizeChanged += OnNavSizeChanged;
    }

    public Grid TitleBarDragRegion => TitleBarDragArea;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeShellEvents();
        if (_initialized) return;
        _initialized = true;
        AddKeyboardAccelerators();
        _sidebarWidth = Math.Clamp(_settings.Settings.Window.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        Nav.OpenPaneLength = _sidebarWidth;
        Controls.SidebarResizeCursor.Apply(PaneResizeHandle);
        UpdatePaneResizeHandle();
        var last = _settings.Settings.General.RememberLastPage ? _settings.Settings.General.LastPage : "Home";
        if (last is not ("Home" or "Converter" or "Pipelines" or "Tools" or "Upscaler" or "Ocr" or "VideoUpscaler" or "Watch" or "Queue" or "History" or "Settings"))
            last = "Home";
        NavigateAndSelect(last);
        UpdateStatusCardLayout(animate: false);
        UpdateStatus();
        UpdateActiveNotifications();
        QueueIndicatorUpdate(animate: false);
        _logger.LogInformation($"Startup: shell ready at {App.StartupClock.Elapsed.TotalMilliseconds:0.0} ms");
        var pending = _pendingShellActivation;
        _pendingShellActivation = null;
        if (pending is not null) _ = HandleShellActivationAsync(pending);
        if (!_settings.Settings.General.OnboardingCompleted) _ = StartOnboardingAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _jobs.JobChanged -= OnJobChanged;
        _jobs.QueueChanged -= OnQueueChanged;
        _registry.CapabilitiesChanged -= OnCapabilitiesChanged;
        _theme.ThemeApplied -= OnThemeApplied;
        _notifications.NotificationRaised -= OnNotification;
        _downloads.Changed -= OnDownloadsChanged;
        _modelPanel.OpenRequested -= OnModelPanelRequested;
        _modelPanel.CloseRequested -= OnModelPanelCloseRequested;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        Nav.PaneOpening -= OnPaneOpening;
        Nav.PaneClosing -= OnPaneClosing;
        Nav.PaneOpened -= OnPaneStateChanged;
        Nav.PaneClosed -= OnPaneStateChanged;
        Nav.DisplayModeChanged -= OnDisplayModeChanged;
        Nav.SizeChanged -= OnNavSizeChanged;
        _eventsSubscribed = false;
    }

    private void OnPaneOpening(NavigationView sender, object args)
    {
        _paneTransitionTarget = false;
        UpdateStatusCardLayout(animate: true, compactOverride: false);
    }

    private void OnPaneClosing(NavigationView sender, object args)
    {
        _paneTransitionTarget = true;
        UpdateStatusCardLayout(animate: true, compactOverride: true);
    }

    private void OnPaneStateChanged(NavigationView sender, object args)
    {
        _paneTransitionTarget = null;
        UpdateStatusCardLayout();
        UpdatePaneResizeHandle();
        QueueIndicatorUpdate(animate: false);
    }

    private void OnDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        UpdateStatusCardLayout(compactOverride: _paneTransitionTarget);
        UpdatePaneResizeHandle();
        QueueIndicatorUpdate(animate: false);
    }

    private void OnNavSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePaneResizeHandle();
        QueueIndicatorUpdate(animate: false);
    }

    private void UpdateStatusCardLayout(bool animate = true, bool? compactOverride = null)
    {
        try
        {
            var compact = compactOverride ?? !Nav.IsPaneOpen;
            var targetWidth = compact ? 40.0 : Math.Max(120.0, Nav.OpenPaneLength - 16.0);
            FrameworkElement incoming = compact ? CompactStatus : ExpandedStatus;
            FrameworkElement outgoing = compact ? ExpandedStatus : CompactStatus;
            if (_statusCardCompact == compact &&
                incoming.Visibility == Visibility.Visible &&
                Math.Abs(QueueStatusCard.Width - targetWidth) < 0.5)
                return;
            _statusCardCompact = compact;

            if (!animate || !_motion.IsEnabled)
            {
                _statusCardStoryboard?.Stop();
                _statusCardStoryboard = null;
                ExpandedStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                CompactStatus.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                ExpandedStatus.Opacity = compact ? 0 : 1;
                CompactStatus.Opacity = compact ? 1 : 0;
                QueueStatusCard.Padding = compact ? new Thickness(2, 5, 2, 5) : new Thickness(10, 7, 10, 7);
                QueueStatusCard.Margin = compact ? new Thickness(4, 4, 4, 8) : new Thickness(8, 6, 8, 10);
                QueueStatusCard.Width = targetWidth;
                return;
            }

            _statusCardStoryboard?.Stop();
            var startWidth = double.IsNaN(QueueStatusCard.Width) || QueueStatusCard.Width <= 0
                ? Math.Max(40, QueueStatusCard.ActualWidth)
                : QueueStatusCard.Width;
            QueueStatusCard.Width = startWidth;
            QueueStatusCard.Padding = compact ? new Thickness(2, 5, 2, 5) : new Thickness(10, 7, 10, 7);
            QueueStatusCard.Margin = compact ? new Thickness(4, 4, 4, 8) : new Thickness(8, 6, 8, 10);
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0;

            var storyboard = new Storyboard();
            var width = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(220),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(width, QueueStatusCard);
            Storyboard.SetTargetProperty(width, "Width");
            storyboard.Children.Add(width);

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, incoming);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_statusCardStoryboard, storyboard)) return;
                _statusCardStoryboard = null;
                QueueStatusCard.Width = targetWidth;
                incoming.Opacity = 1;
                outgoing.Visibility = Visibility.Collapsed;
            };

            _statusCardStoryboard = storyboard;
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Status card layout update failed: {Message}", ex.Message);
        }
    }

    private void UpdatePaneResizeHandle()
    {
        try
        {
            var visible = Nav.IsPaneOpen && Nav.DisplayMode != NavigationViewDisplayMode.Minimal;
            PaneResizeHandle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;
            var edge = Math.Max(MinSidebarWidth, Nav.OpenPaneLength);
            PaneResizeHandle.Margin = new Thickness(edge - 4, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sidebar resize handle update failed: {Message}", ex.Message);
        }
    }

    private void QueueIndicatorUpdate(bool animate)
    {
        _indicatorAnimateRequested |= animate;
        if (_indicatorUpdateQueued) return;
        _indicatorUpdateQueued = true;
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _indicatorUpdateQueued = false;
            var shouldAnimate = _indicatorAnimateRequested;
            _indicatorAnimateRequested = false;
            UpdateActiveNavIndicator(shouldAnimate);
        });
    }

    private void UpdateActiveNavIndicator(bool animate, int retry = 0)
    {
        try
        {
            if (Nav.SelectedItem is null ||
                (Nav.DisplayMode == NavigationViewDisplayMode.Minimal && !Nav.IsPaneOpen))
            {
                ActiveNavIndicator.Visibility = Visibility.Collapsed;
                return;
            }

            var container = Nav.ContainerFromMenuItem(Nav.SelectedItem) as NavigationViewItem;
            if (container is null)
            {
                // Never force a full layout pass from inside navigation. The
                // container is usually realized on the next UI turn.
                if (retry < 3)
                {
                    _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => UpdateActiveNavIndicator(animate, retry + 1));
                }
                else
                {
                    ActiveNavIndicator.Visibility = Visibility.Collapsed;
                }
                return;
            }
            if (container.ActualHeight <= 0)
            {
                ActiveNavIndicator.Visibility = Visibility.Collapsed;
                return;
            }

            var bounds = container.TransformToVisual(Nav).TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var targetY = bounds.Y + Math.Max(0, (bounds.Height - ActiveNavIndicator.Height) / 2);
            var targetX = Math.Max(0, bounds.X + 1);

            Canvas.SetLeft(ActiveNavIndicator, targetX);
            ActiveNavIndicator.Visibility = Visibility.Visible;
            if (!animate || !_motion.IsEnabled)
            {
                _indicatorStoryboard?.Stop();
                _indicatorStoryboard = null;
                ActiveNavIndicatorTransform.Y = targetY;
                _indicatorTargetY = targetY;
                return;
            }

            var current = ActiveNavIndicatorTransform.Y;
            if (Math.Abs(current - targetY) < 0.5)
            {
                ActiveNavIndicatorTransform.Y = targetY;
                _indicatorTargetY = targetY;
                return;
            }

            var distance = Math.Abs(targetY - current);
            var duration = TimeSpan.FromMilliseconds(Math.Clamp(180 + distance * 0.30, 180, 460));
            var animation = new DoubleAnimation
            {
                From = current,
                To = targetY,
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            Storyboard.SetTarget(animation, ActiveNavIndicatorTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
            _indicatorStoryboard = storyboard;
            _indicatorTargetY = targetY;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Navigation indicator update failed: {Message}", ex.Message);
        }
    }

    private void OnPaneResizeStarted(object sender, DragStartedEventArgs e)
    {
        _resizingPane = true;
    }

    private void OnPaneResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (!Nav.IsPaneOpen) return;
        _resizingPane = true;
        var maxForWindow = Math.Clamp(ShellRoot.ActualWidth * 0.45, MinSidebarWidth, MaxSidebarWidth);
        _sidebarWidth = Math.Clamp(_sidebarWidth + e.HorizontalChange, MinSidebarWidth,
            Math.Min(MaxSidebarWidth, maxForWindow));
        Nav.OpenPaneLength = _sidebarWidth;
        _statusCardStoryboard?.Stop();
        _statusCardStoryboard = null;
        if (Nav.IsPaneOpen)
        {
            _statusCardCompact = false;
            QueueStatusCard.Width = Math.Max(120.0, _sidebarWidth - 16.0);
            ExpandedStatus.Visibility = Visibility.Visible;
            ExpandedStatus.Opacity = 1;
            CompactStatus.Visibility = Visibility.Collapsed;
            CompactStatus.Opacity = 0;
        }
        UpdatePaneResizeHandle();
        UpdateActiveNavIndicator(animate: false);
    }

    private async void OnPaneResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizingPane) return;
        _resizingPane = false;
        await SaveSidebarWidthAsync();
    }

    private async Task SaveSidebarWidthAsync()
    {
        try
        {
            await _settings.UpdateAsync(settings =>
                settings.Window.SidebarWidth = _sidebarWidth);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save sidebar width: {Message}", ex.Message);
        }
    }

    private void AddKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.Number1, "Home");
        AddAccelerator(VirtualKey.Number2, "Converter");
        AddAccelerator(VirtualKey.P, "Pipelines", VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddAccelerator(VirtualKey.Number3, "Upscaler");
        AddAccelerator(VirtualKey.Number4, "Queue");
        AddAccelerator(VirtualKey.Number5, "History");
        AddAccelerator(VirtualKey.Number6, "Settings");
        AddAccelerator(VirtualKey.Number7, "Tools");
        AddAccelerator(VirtualKey.Number8, "Watch");
        AddAccelerator(VirtualKey.Number9, "VideoUpscaler");
        AddAccelerator(VirtualKey.O, "Ocr", VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
    }

    private void AddAccelerator(VirtualKey key, string route,
        VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += (_, args) =>
        {
            NavigateAndSelect(route);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void NavigateAndSelect(string route)
    {
        _navigation.Navigate(route);
        var item = route switch
        {
            "Home" => HomeNav,
            "Converter" => ConverterNav,
            "Pipelines" => PipelinesNav,
            "Tools" => ToolsNav,
            "Watch" => WatchNav,
            "Upscaler" => UpscalerNav,
            "Ocr" => OcrNav,
            "VideoUpscaler" => VideoUpscalerNav,
            "Queue" => QueueNav,
            "History" => HistoryNav,
            "Settings" => SettingsNav,
            _ => null
        };
        if (item is not null)
        {
            _syncingSelection = true;
            Nav.SelectedItem = item;
            _syncingSelection = false;
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.SelectedItemContainer?.Tag is string route)
        {
            _navigation.Navigate(route);
            QueueIndicatorUpdate(animate: true);
        }
    }

    private async Task<T?> WaitForPageAsync<T>(string route) where T : Page
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (ContentFrame.Content is T page) return page;
            await Task.Delay(100);
            if (ContentFrame.Content is null && attempt == 5) NavigateAndSelect(route);
        }
        return ContentFrame.Content as T;
    }

    public async Task HandleShellActivationAsync(string[] args)
    {
        if (!IsLoaded)
        {
            _pendingShellActivation = args;
            return;
        }
        var offset = args.Length > 0 && string.Equals(args[0], "--shell", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (args.Length <= offset + 1) return;
        var verb = args[offset].TrimStart('-').ToLowerInvariant();
        var paths = args.Skip(offset + 1)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Select(Path.GetFullPath)
            .ToList();
        if (paths.Count == 0) return;

        App.MainWindow?.Activate();
        switch (verb)
        {
            case "convert-images":
            case "convert-audio":
            case "convert-video":
            case "convert-documents":
            case "convert-ebooks":
            case "convert-archives":
            {
                var category = verb switch
                {
                    "convert-audio" => MediaCategory.Audio,
                    "convert-video" => MediaCategory.Video,
                    "convert-documents" => MediaCategory.Document,
                    "convert-ebooks" => MediaCategory.Ebook,
                    "convert-archives" => MediaCategory.Archive,
                    _ => MediaCategory.Image
                };
                NavigateAndSelect("Converter");
                var converter = await WaitForPageAsync<ConverterPage>("Converter");
                if (converter is not null)
                    await converter.ViewModel.AddFilesAsync(paths, category);
                break;
            }
            case "scan-images-folder":
            case "convert-images-folder":
            {
                NavigateAndSelect("Converter");
                var converter = await WaitForPageAsync<ConverterPage>("Converter");
                if (converter is not null && Directory.Exists(paths[0]))
                    await converter.ViewModel.AddFolderAsCategoryAsync(paths[0], MediaCategory.Image);
                break;
            }
            case "upscale-image":
            {
                NavigateAndSelect("Upscaler");
                var upscaler = await WaitForPageAsync<UpscalerPage>("Upscaler");
                if (upscaler is not null)
                    await upscaler.ViewModel.AddImagesAsync(paths);
                break;
            }
            case "upscale-video":
            {
                NavigateAndSelect("VideoUpscaler");
                var video = await WaitForPageAsync<VideoUpscalerPage>("VideoUpscaler");
                if (video is not null)
                    await video.ViewModel.AddVideosAsync(paths);
                break;
            }
            case "ocr":
            {
                NavigateAndSelect("Ocr");
                var ocr = await WaitForPageAsync<OcrPage>("Ocr");
                if (ocr is not null)
                    await ocr.ViewModel.AddPathsAsync(paths, allowDuplicates: false);
                break;
            }
        }
    }

    private void OnNavigated(object? sender, string route)
    {
        _settings.Settings.General.LastPage = route;
        var item = route switch
        {
            "Home" => HomeNav,
            "Converter" => ConverterNav,
            "Pipelines" => PipelinesNav,
            "Tools" => ToolsNav,
            "Watch" => WatchNav,
            "Upscaler" => UpscalerNav,
            "Ocr" => OcrNav,
            "VideoUpscaler" => VideoUpscalerNav,
            "Queue" => QueueNav,
            "History" => HistoryNav,
            "Settings" => SettingsNav,
            _ => null
        };
        if (item is not null)
        {
            _syncingSelection = true;
            Nav.SelectedItem = item;
            _syncingSelection = false;
        }

        if (ContentFrame.Content is FrameworkElement page)
        {
            _theme.ApplyTo(page);
            _motion.Apply(page);
        }

        QueueIndicatorUpdate(animate: true);
    }

    private void OnStatusCardClick(object sender, RoutedEventArgs e)
    {
        NavigateAndSelect("Queue");
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        QueueStatusUpdate();
        ScheduleActiveNotificationUpdate();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        QueueStatusUpdate();
        ScheduleActiveNotificationUpdate();
    }
    private void OnCapabilitiesChanged(object? sender, EventArgs e) => QueueStatusUpdate();
    private void OnThemeApplied(object? sender, EventArgs e)
    {
        QueueStatusUpdate();
        RebuildNotificationHistory();
        if (ContentFrame.Content is FrameworkElement page) _theme.ApplyTo(page);
    }

    private void QueueStatusUpdate()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(UpdateStatus);
            return;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        try
        {
            var jobs = _jobs.Jobs;
            var active = jobs.Where(j => j.State is JobState.Pending or JobState.Preparing or JobState.Running).ToList();
            var badge = QueueNav.InfoBadge as InfoBadge;
            if (badge is null)
            {
                badge = new InfoBadge();
                QueueNav.InfoBadge = badge;
            }

            badge.Value = active.Count;
            badge.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (active.Count > 0)
            {
                var progress = active.Average(j => j.Progress) * 100;
                var running = active.Count(j => j.State == JobState.Running);
                ApplyStatus(
                    LocalizationService.P("Status.JobsActive", active.Count),
                    running > 0
                        ? LocalizationService.P("Status.ProcessingLocally", (int)Math.Round(progress))
                        : LocalizationService.T("Preparing files."),
                    "AccentPrimaryBrush",
                    true,
                    progress,
                    running == 0,
                    LocalizationService.P("Status.JobsCompact", active.Count));
                return;
            }

            if (!_registry.IsInitialized)
            {
                ApplyStatus("Starting engines…", "Checking local capabilities", "WarningBrush",
                    false, 0, false, "Start");
                return;
            }

            var unavailable = _registry.Engines.Where(e => !e.IsAvailable).ToList();
            if (unavailable.Count > 0)
            {
                ApplyStatus("Engine attention needed",
                    LocalizationService.F("{0} unavailable", unavailable[0].DisplayName), "DangerBrush",
                    false, 0, false, "Issue");
                return;
            }

            ApplyStatus("Ready", "All engines available locally", "SuccessBrush", false, 0, false, "Ready");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Status update failed: {Message}", ex.Message);
        }
    }

    private void ApplyStatus(string title, string detail, string brushKey, bool active,
        double progress, bool indeterminate, string compactLabel)
    {
        var brush = GetThemeBrush(brushKey);
        var localizedTitle = LocalizationService.T(title);
        var localizedDetail = LocalizationService.T(detail);
        var localizedCompact = LocalizationService.T(compactLabel);
        StatusTitle.Text = localizedTitle;
        StatusDetail.Text = localizedDetail;
        StatusDot.Fill = brush;
        StatusDotCompact.Fill = brush;
        StatusProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusProgress.IsIndeterminate = indeterminate;
        StatusProgress.Value = progress;
        StatusDotCompact.Visibility = Visibility.Visible;
        CompactStatusLabel.Text = localizedCompact;
        CompactStatusLabel.Foreground = brush;
        SetCompactPulse(active);
        ToolTipService.SetToolTip(QueueStatusCard,
            LocalizationService.F("{0} — {1}", localizedTitle, localizedDetail));
        AutomationProperties.SetName(QueueStatusCard,
            LocalizationService.F("{0}. {1}", localizedTitle, localizedDetail));
    }

    private void SetCompactPulse(bool active)
    {
        if (active && _motion.IsEnabled)
        {
            if (_compactPulse is not null) return;
            var animation = new DoubleAnimation
            {
                From = 1,
                To = 0.35,
                Duration = new Duration(TimeSpan.FromMilliseconds(650)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, StatusDotCompact);
            Storyboard.SetTargetProperty(animation, "Opacity");
            _compactPulse = new Storyboard();
            _compactPulse.Children.Add(animation);
            _compactPulse.Begin();
            return;
        }

        _compactPulse?.Stop();
        _compactPulse = null;
        StatusDotCompact.Opacity = 1;
    }

    private Brush GetThemeBrush(string key)
    {
        try
        {
            if (XamlApplication.Current.Resources.TryGetValue(key, out var direct) && direct is Brush directBrush)
                return directBrush;

            var themeKey = _theme.EffectiveTheme == ElementTheme.Dark ? "Dark" : "Light";
            if (XamlApplication.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary) &&
                dictionary is ResourceDictionary resourceDictionary &&
                resourceDictionary.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch
        {
            // Ignore resource lookup failures.
        }

        return new SolidColorBrush(Colors.Gray);
    }

    private void OnNotification(object? sender, AppNotification notification)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnNotification(sender, notification));
            return;
        }

        var severity = notification.Severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
        var infoBar = new InfoBar
        {
            Title = notification.Title,
            Message = notification.Message,
            Severity = severity,
            IsOpen = true,
            IsClosable = true,
            Width = 380,
            Tag = notification
        };
        if (_motion.IsEnabled)
        {
            infoBar.Transitions.Add(new EntranceThemeTransition { IsStaggeringEnabled = false });
        }

        void Remove()
        {
            if (NotificationStack.Children.Contains(infoBar)) NotificationStack.Children.Remove(infoBar);
        }

        infoBar.Closed += (_, _) => Remove();
        NotificationStack.Children.Insert(0, infoBar);
        while (NotificationStack.Children.Count > 3)
            NotificationStack.Children.RemoveAt(NotificationStack.Children.Count - 1);

        if (notification.AutoDismiss)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                infoBar.IsOpen = false;
                Remove();
            };
            timer.Start();
        }

        TrackNotification(notification);
    }

    private void TrackNotification(AppNotification notification)
    {
        _notificationRecords.Insert(0, notification);
        while (_notificationRecords.Count > 30)
            _notificationRecords.RemoveAt(_notificationRecords.Count - 1);

        RebuildNotificationHistory();
        if (NotificationCenterFlyout.IsOpen)
            _unreadNotifications = 0;
        else
            _unreadNotifications++;
        UpdateNotificationBadge();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnLanguageChanged(sender, e));
            return;
        }

        LocalizationPageRegistry.RefreshAll();
        RebuildNotificationHistory();
        RefreshActiveNotificationBars();
        UpdateActiveNotifications();
        if (_onboardingActive) ShowOnboardingStep();
        UpdateStatus();
    }

    private void RefreshActiveNotificationBars()
    {
        foreach (var child in NotificationStack.Children)
        {
            if (child is InfoBar { Tag: AppNotification notification } bar)
            {
                bar.Title = notification.Title;
                bar.Message = notification.Message;
            }
        }
    }

    private void RebuildNotificationHistory()
    {
        _notificationHistory.Clear();
        foreach (var notification in _notificationRecords)
        {
            _notificationHistory.Add(new NotificationHistoryItem(notification,
                GetThemeBrush(notification.Severity switch
                {
                    NotificationSeverity.Success => "SuccessBrush",
                    NotificationSeverity.Warning => "WarningBrush",
                    NotificationSeverity.Error => "DangerBrush",
                    _ => "AccentPrimaryBrush"
                })));
        }

        UpdateNotificationHistoryState();
    }

    private void UpdateNotificationHistoryState()
    {
        var hasHistory = _notificationHistory.Count > 0;
        NotificationHistoryList.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        NotificationEmptyState.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnModelPanelRequested(object? sender, ModelPanelFilter filter)
    {
        try
        {
            if (ModelPanelHost.Content is not ModelManagerView view)
            {
                view = new ModelManagerView();
                ModelPanelHost.Content = view;
            }

            await view.ViewModel.InitializeAsync();
            view.ViewModel.SetPanelMode(filter, isPanelMode: true);
            ModelPanelSplitView.IsPaneOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the AI model panel");
            _notifications.Notify("Could not open AI Models",
                "The model library could not be initialized. Open Settings → Diagnostics for details.",
                NotificationSeverity.Error);
        }
    }

    private void OnModelPanelCloseRequested(object? sender, EventArgs e) =>
        ModelPanelSplitView.IsPaneOpen = false;

    private void ScheduleActiveNotificationUpdate()
    {
        if (_activeNotificationUpdateQueued) return;
        _activeNotificationUpdateQueued = true;
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _activeNotificationUpdateQueued = false;
            UpdateActiveNotifications();
        });
    }

    private void UpdateActiveNotifications()
    {
        try
        {
            var activeJobs = _jobs.Jobs
                .Where(j => j.State is JobState.Pending or JobState.Preparing or JobState.Running)
                .OrderBy(j => j.CreatedAt)
                .ToList();
            var activeIds = activeJobs.Select(j => j.Id).ToHashSet();

            for (var i = _activeNotifications.Count - 1; i >= 0; i--)
            {
                if (!activeIds.Contains(_activeNotifications[i].JobId))
                    _activeNotifications.RemoveAt(i);
            }

            foreach (var job in activeJobs)
            {
                var item = _activeNotifications.FirstOrDefault(n => n.JobId == job.Id);
                if (item is null)
                    _activeNotifications.Add(new ActiveJobNotificationItem(job, id => _jobs.CancelAsync(id)));
                else
                    item.Update(job);
            }

            ActiveNotificationsSection.Visibility =
                _activeNotifications.Count > 0 || _downloads.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadActivitySection.Visibility =
                _downloads.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Active notification update failed: {Message}", ex.Message);
        }
    }

    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess) UpdateActiveNotifications();
        else _dispatcher.TryEnqueue(UpdateActiveNotifications);
    }

    private void UpdateNotificationBadge()
    {
        if (_unreadNotifications <= 0)
        {
            NotificationBadge.Value = 0;
            NotificationBadge.Visibility = Visibility.Collapsed;
            return;
        }

        NotificationBadge.Value = Math.Min(_unreadNotifications, 99);
        NotificationBadge.Visibility = Visibility.Visible;
    }

    private void OnNotificationFlyoutOpened(object? sender, object e)
    {
        _unreadNotifications = 0;
        UpdateNotificationBadge();
        RebuildNotificationHistory();
        UpdateActiveNotifications();
    }

    private void OnClearNotificationsClick(object sender, RoutedEventArgs e)
    {
        _notificationRecords.Clear();
        _notificationHistory.Clear();
        _unreadNotifications = 0;
        UpdateNotificationHistoryState();
        UpdateNotificationBadge();
    }

    private void OnRemoveNotificationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotificationHistoryItem item }) return;
        _notificationRecords.RemoveAll(n => n.Id == item.NotificationId);
        RebuildNotificationHistory();
        UpdateNotificationBadge();
    }

    private async Task StartOnboardingAsync()
    {
        try
        {
            await Task.Delay(700);
            if (!IsLoaded || _onboardingActive || _settings.Settings.General.OnboardingCompleted) return;
            if (ContentFrame.Content is null) await Task.Delay(350);
            if (!IsLoaded || _onboardingActive) return;

            _onboardingActive = true;
            _onboardingIndex = 0;
            NotificationStack.Visibility = Visibility.Collapsed;
            OnboardingOverlay.Visibility = Visibility.Visible;
            ShowOnboardingStep();
            OnboardingNext.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Onboarding could not start: {Message}", ex.Message);
        }
    }

    private void ShowOnboardingStep()
    {
        if (!_onboardingActive || _onboardingSteps.Count == 0) return;
        _onboardingIndex = Math.Clamp(_onboardingIndex, 0, _onboardingSteps.Count - 1);
        var step = _onboardingSteps[_onboardingIndex];
        OnboardingStepLabel.Text = LocalizationService.F("{0} of {1}",
            _onboardingIndex + 1, _onboardingSteps.Count);
        OnboardingTitle.Text = LocalizationService.T(step.Title);
        OnboardingBody.Text = LocalizationService.T(step.Body);
        OnboardingBack.IsEnabled = _onboardingIndex > 0;
        OnboardingNext.Content = LocalizationService.T(
            _onboardingIndex == _onboardingSteps.Count - 1 ? "Finish" : "Next");
        OnboardingCard.Measure(new Size(OnboardingCardWidth, double.PositiveInfinity));
        PositionOnboardingStep();
        AnimateOnboardingStep();
    }

    private void PositionOnboardingStep()
    {
        if (!_onboardingActive || _onboardingSteps.Count == 0) return;
        var step = _onboardingSteps[_onboardingIndex];
        var target = step.Target;
        var rootWidth = Math.Max(ShellRoot.ActualWidth, 640);
        var rootHeight = Math.Max(ShellRoot.ActualHeight, 480);
        var cardHeight = Math.Max(170, OnboardingCard.DesiredSize.Height);
        var targetPoint = target.TransformToVisual(ShellRoot).TransformPoint(new Point(0, 0));
        var targetWidth = Math.Max(target.ActualWidth, 180);
        var targetHeight = Math.Max(target.ActualHeight, 36);

        double x;
        double y;
        switch (step.Anchor)
        {
            case OnboardingAnchor.Below:
                x = targetPoint.X + targetWidth / 2 - OnboardingCardWidth / 2;
                y = targetPoint.Y + targetHeight + 24;
                break;
            case OnboardingAnchor.Left:
                x = targetPoint.X - OnboardingCardWidth - 24;
                y = targetPoint.Y + targetHeight / 2 - cardHeight / 2;
                break;
            case OnboardingAnchor.Above:
                x = targetPoint.X + targetWidth / 2 - OnboardingCardWidth / 2;
                y = targetPoint.Y - cardHeight - 24;
                break;
            default:
                x = targetPoint.X + targetWidth + 24;
                y = targetPoint.Y + targetHeight / 2 - cardHeight / 2;
                break;
        }

        x = Math.Clamp(x, 16, Math.Max(16, rootWidth - OnboardingCardWidth - 16));
        y = Math.Clamp(y, 56, Math.Max(56, rootHeight - cardHeight - 16));
        OnboardingCard.Margin = new Thickness(x, y, 0, 0);
        OnboardingCardTransform.CenterX = OnboardingCardWidth / 2;
        OnboardingCardTransform.CenterY = cardHeight / 2;

        OnboardingSpotlight.Margin = new Thickness(targetPoint.X - 7, targetPoint.Y - 7, 0, 0);
        OnboardingSpotlight.Width = targetWidth + 14;
        OnboardingSpotlight.Height = targetHeight + 14;
        OnboardingSpotlight.Visibility = Visibility.Visible;

        double arrowX;
        double arrowY;
        double arrowAngle;
        switch (step.Anchor)
        {
            case OnboardingAnchor.Below:
                arrowX = targetPoint.X + targetWidth / 2 - 10;
                arrowY = targetPoint.Y + targetHeight + 7;
                arrowAngle = 270;
                break;
            case OnboardingAnchor.Left:
                arrowX = targetPoint.X - 27;
                arrowY = targetPoint.Y + targetHeight / 2 - 10;
                arrowAngle = 0;
                break;
            case OnboardingAnchor.Above:
                arrowX = targetPoint.X + targetWidth / 2 - 10;
                arrowY = targetPoint.Y - 27;
                arrowAngle = 90;
                break;
            default:
                arrowX = targetPoint.X + targetWidth + 7;
                arrowY = targetPoint.Y + targetHeight / 2 - 10;
                arrowAngle = 180;
                break;
        }

        OnboardingArrow.Margin = new Thickness(arrowX, arrowY, 0, 0);
        OnboardingArrowRotation.Angle = arrowAngle;
        OnboardingArrow.Visibility = Visibility.Visible;
    }

    private void AnimateOnboardingStep()
    {
        _onboardingStoryboard?.Stop();
        _onboardingStoryboard = null;
        OnboardingCardTransform.ScaleX = 1;
        OnboardingCardTransform.ScaleY = 1;
        OnboardingCard.Opacity = 1;
        OnboardingSpotlight.Opacity = 1;

        if (!_motion.IsEnabled)
        {
            OnboardingArrow.Opacity = 1;
            return;
        }

        OnboardingCard.Opacity = 0;
        OnboardingCardTransform.ScaleX = 0.92;
        OnboardingCardTransform.ScaleY = 0.92;
        OnboardingArrow.Opacity = 0;
        OnboardingSpotlight.Opacity = 0.45;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var opacity = new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = ease
        };
        var scaleX = new DoubleAnimation
        {
            From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(240), EasingFunction = pop
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(240), EasingFunction = pop
        };
        var arrowOpacity = new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease
        };
        var spotlightOpacity = new DoubleAnimation
        {
            From = 0.45, To = 1, Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        };

        Storyboard.SetTarget(opacity, OnboardingCard);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(scaleX, OnboardingCardTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, OnboardingCardTransform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        Storyboard.SetTarget(arrowOpacity, OnboardingArrow);
        Storyboard.SetTargetProperty(arrowOpacity, "Opacity");
        Storyboard.SetTarget(spotlightOpacity, OnboardingSpotlight);
        Storyboard.SetTargetProperty(spotlightOpacity, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(arrowOpacity);
        storyboard.Children.Add(spotlightOpacity);
        storyboard.Begin();
        _onboardingStoryboard = storyboard;
    }

    private void OnOnboardingNextClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingIndex >= _onboardingSteps.Count - 1)
        {
            _ = CompleteOnboardingAsync();
            return;
        }

        _onboardingIndex++;
        ShowOnboardingStep();
    }

    private void OnOnboardingBackClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingIndex <= 0) return;
        _onboardingIndex--;
        ShowOnboardingStep();
    }

    private void OnOnboardingSkipClick(object sender, RoutedEventArgs e) => _ = CompleteOnboardingAsync();

    private void OnOnboardingKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_onboardingActive) return;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _ = CompleteOnboardingAsync();
        }
        else if (e.Key is VirtualKey.Right or VirtualKey.Enter)
        {
            e.Handled = true;
            OnOnboardingNextClick(sender, new RoutedEventArgs());
        }
        else if (e.Key == VirtualKey.Left && _onboardingIndex > 0)
        {
            e.Handled = true;
            OnOnboardingBackClick(sender, new RoutedEventArgs());
        }
    }

    private async Task CompleteOnboardingAsync()
    {
        if (!_onboardingActive) return;
        _onboardingActive = false;
        _onboardingStoryboard?.Stop();
        _onboardingStoryboard = null;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        OnboardingSpotlight.Visibility = Visibility.Collapsed;
        OnboardingArrow.Visibility = Visibility.Collapsed;
        NotificationStack.Visibility = Visibility.Visible;
        try
        {
            await _settings.UpdateAsync(settings => settings.General.OnboardingCompleted = true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save onboarding state: {Message}", ex.Message);
        }

        ContentFrame.Focus(FocusState.Programmatic);
    }

    private enum OnboardingAnchor
    {
        Right,
        Below,
        Left,
        Above
    }

    private sealed record OnboardingStep(FrameworkElement Target, OnboardingAnchor Anchor,
        string Title, string Body);

    public void ShowClosingState()
    {
        try
        {
            ClosingOverlay.Visibility = Visibility.Visible;
            ClosingOverlay.IsHitTestVisible = true;
        }
        catch
        {
            // Ignore.
        }
    }
}
