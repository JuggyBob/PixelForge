using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.Views;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PixelForge.Presentation;

public sealed partial class MainWindow : Window
{
    // The two-column workspaces need the fixed 280 DIP navigation pane, page
    // padding, a 380 DIP content column and the 440 DIP settings sidebar. The
    // Converter's six category tabs also need enough room to keep "Archives"
    // and the output actions fully visible.
    private const int MinimumWindowWidth = 1280;
    private const int MinimumWindowHeight = 720;
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IAppLogger _logger;
    private readonly ShellPage _shell;
    private bool _allowClose;
    private bool _closingStarted;
    private bool _placementApplied;

    public MainWindow(ShellPage shell, ISettingsService settings, IThemeService theme, IAppLogger logger)
    {
        InitializeComponent();
        _shell = shell;
        _settings = settings;
        _theme = theme;
        _logger = logger;

        Title = "PixelForge";
        ExtendsContentIntoTitleBar = true;
        Content = shell;
        SetTitleBar(shell.TitleBarDragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        TrySetIcon();
        ApplyPlacement();
        _theme.Attach(this, shell);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => App.MainWindow = null;
    }

    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialPlacement();
    }

    public void ApplyInitialPlacement()
    {
        if (_placementApplied) return;
        _placementApplied = true;
        ApplyPlacement();
    }

    private void TrySetIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "PixelForge.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Window icon could not be set: {Message}", ex.Message);
        }
    }

    private void ApplyPlacement()
    {
        try
        {
            var placement = _settings.Settings.Window;
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var scale = GetRasterizationScale();
            var width = Math.Clamp((int)Math.Round(placement.Width * scale),
                (int)Math.Round(MinimumWindowWidth * scale), Math.Max(1, work.Width));
            var height = Math.Clamp((int)Math.Round(placement.Height * scale),
                (int)Math.Round(MinimumWindowHeight * scale), Math.Max(1, work.Height));
            var x = placement.Left >= 0 ? Math.Clamp(placement.Left, work.X - 40, work.X + work.Width - width) : work.X + (work.Width - width) / 2;
            var y = placement.Top >= 0 ? Math.Clamp(placement.Top, work.Y - 20, work.Y + work.Height - height) : work.Y + (work.Height - height) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWindowWidth * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(MinimumWindowHeight * scale);
                if (placement.Maximized) presenter.Maximize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Window placement could not be restored: {Message}", ex.Message);
            AppWindow.Resize(new SizeInt32(1440, 900));
        }
    }

    private double GetRasterizationScale()
    {
        try
        {
            if (_shell.XamlRoot is { RasterizationScale: > 0 } xamlRoot)
                return xamlRoot.RasterizationScale;
        }
        catch
        {
            // Fall through to the Win32 DPI.
        }

        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch
        {
            // Fall through to the DIP assumption.
        }

        return 1.0;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closingStarted) return;
        _closingStarted = true;
        SavePlacement();
        _shell.ShowClosingState();
        try
        {
            await App.ShutdownAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void SavePlacement()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            var scale = GetRasterizationScale();
            _settings.Settings.Window.Width = (int)Math.Round(AppWindow.Size.Width / scale);
            _settings.Settings.Window.Height = (int)Math.Round(AppWindow.Size.Height / scale);
            _settings.Settings.Window.Left = AppWindow.Position.X;
            _settings.Settings.Window.Top = AppWindow.Position.Y;
            _settings.Settings.Window.Maximized = presenter?.State == OverlappedPresenterState.Maximized;
            _ = _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Window placement could not be saved: {Message}", ex.Message);
        }
    }
}
