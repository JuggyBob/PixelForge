using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PixelForge.Domain;
using PixelForge.Presentation.Views;

namespace PixelForge.Presentation.Services;

public interface INavigationService
{
    Frame? Frame { get; }
    string CurrentRoute { get; }
    void Initialize(Frame frame);
    void Navigate(string route, object? parameter = null);
    bool GoBack();
    event EventHandler<string>? Navigated;
}

public sealed class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Home"] = typeof(HomePage),
        ["Converter"] = typeof(ConverterPage),
        ["Tools"] = typeof(ToolsPage),
        ["Watch"] = typeof(WatchFoldersPage),
        ["Upscaler"] = typeof(UpscalerPage),
        ["Ocr"] = typeof(OcrPage),
        ["VideoUpscaler"] = typeof(VideoUpscalerPage),
        ["Queue"] = typeof(QueuePage),
        ["History"] = typeof(HistoryPage),
        ["Settings"] = typeof(SettingsPage),
        ["Models"] = typeof(ModelsPage),
        ["Compare"] = typeof(ComparePage)
    };

    public Frame? Frame { get; private set; }
    public string CurrentRoute { get; private set; } = "Home";
    public event EventHandler<string>? Navigated;

    private string? _pendingRoute;
    private Stopwatch? _pendingNavigation;
    private const int SlowNavigationThresholdMs = 40;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

    public void Initialize(Frame frame)
    {
        Frame = frame;
        frame.Navigated += (_, e) =>
        {
            CurrentRoute = _routes.FirstOrDefault(r => r.Value == e.SourcePageType).Key ?? "Home";
            var route = _pendingRoute ?? CurrentRoute;
            var navigation = _pendingNavigation;
            var elapsed = navigation?.Elapsed.TotalMilliseconds;
            _pendingRoute = null;
            _pendingNavigation = null;
            _logger.LogInformation("Navigation {Route}: frame ready in {Elapsed:0.0} ms", route, elapsed ?? 0);
            Navigated?.Invoke(this, CurrentRoute);

            if (e.Content is FrameworkElement page)
            {
                void OnLoaded(object? sender, RoutedEventArgs args)
                {
                    page.Loaded -= OnLoaded;
                    var total = navigation?.Elapsed.TotalMilliseconds ?? elapsed.GetValueOrDefault();
                    _logger.LogInformation("Navigation {Route}: page loaded in {Elapsed:0.0} ms", route, total);
                    if (total >= SlowNavigationThresholdMs)
                        _logger.LogWarning("Slow navigation {Route}: {Elapsed:0.0} ms", route, total);
                }

                page.Loaded += OnLoaded;
            }
        };
    }

    public void Navigate(string route, object? parameter = null)
    {
        if (Frame is null || !_routes.TryGetValue(route, out var type)) return;
        _pendingRoute = route;
        _pendingNavigation = Stopwatch.StartNew();
        Frame.Navigate(type, parameter, new EntranceNavigationTransitionInfo());
    }

    public bool GoBack()
    {
        if (Frame?.CanGoBack != true) return false;
        Frame.GoBack();
        return true;
    }
}
