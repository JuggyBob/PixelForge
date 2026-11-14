using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XamlApplication = Microsoft.UI.Xaml.Application;
using Microsoft.Win32;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PixelForge.Presentation.Services;

public interface IThemeService
{
    ThemeMode CurrentTheme { get; }
    ElementTheme EffectiveTheme { get; }
    string AccentHex { get; }
    IReadOnlyList<string> AvailableFonts { get; }
    FontFamily ResolveFontFamily(string familyName);
    void Attach(Window window, FrameworkElement root);
    void Apply(AppSettings settings);
    void ApplyTo(FrameworkElement root);
    event EventHandler? ThemeApplied;
}

public sealed class ThemeService : IThemeService
{
    private const string DefaultFont = "Segoe UI Variable Text";
    private const string DefaultMonoFont = "JetBrains Mono";
    private const string JetBrainsFontUri = "ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono";
    private readonly IAppLogger _logger;
    private readonly UISettings _uiSettings = new();
    private readonly List<(Window Window, FrameworkElement Root)> _targets = [];
    private readonly ConditionalWeakTable<DependencyObject, BaseTypography> _baseTypography = new();
    private readonly List<string> _fonts;
    private AppSettings? _settings;
    private string _lastVisualSignature = "";

    public ThemeService(IAppLogger logger)
    {
        _logger = logger;
        _fonts = EnumerateFonts();
    }

    public ThemeMode CurrentTheme => _settings?.General.Theme ?? ThemeMode.System;
    public ElementTheme EffectiveTheme { get; private set; } = ElementTheme.Default;
    public string AccentHex => _settings?.General.AccentColorHex ?? "#D2693A";
    public IReadOnlyList<string> AvailableFonts => _fonts;
    public event EventHandler? ThemeApplied;

    public void Attach(Window window, FrameworkElement root)
    {
        if (_targets.All(t => t.Window != window)) _targets.Add((window, root));
        if (_settings is not null)
        {
            _lastVisualSignature = "";
            Apply(_settings);
        }
        else
        {
            ApplyTypography(root, DefaultFont, DefaultMonoFont, 1.0);
        }
    }

    public void Apply(AppSettings settings)
    {
        var applyStarted = Stopwatch.GetTimestamp();
        var signature = $"{settings.General.Theme}|{settings.General.AccentColorHex}|{settings.General.FontFamily}|{settings.General.MonoFontFamily}|{settings.General.FontScale:0.###}";
        var visualChanged = !string.Equals(signature, _lastVisualSignature, StringComparison.Ordinal);
        _settings = settings;
        if (!visualChanged && _targets.Count > 0) return;
        _lastVisualSignature = signature;
        var theme = ResolveTheme(settings.General.Theme);
        EffectiveTheme = theme;
        var font = ResolveFontFamily(settings.General.FontFamily);
        var mono = ResolveFontFamily(settings.General.MonoFontFamily);
        var scale = Math.Clamp(settings.General.FontScale, 0.85, 1.35);
        var palette = AccentPalette.Create(settings.General.AccentColorHex, theme == ElementTheme.Dark);
        _logger.LogInformation("Applying appearance: theme={Theme}; font={Font}; scale={Scale:0.###}; targets={Targets}",
            settings.General.Theme, settings.General.FontFamily, scale, _targets.Count);

        XamlApplication.Current.Resources["AppFontFamily"] = font;
        XamlApplication.Current.Resources["AppMonoFontFamily"] = mono;
        XamlApplication.Current.Resources["ContentControlThemeFontFamily"] = font;
        XamlApplication.Current.Resources["AppBodyFontSize"] = 14.0;
        XamlApplication.Current.Resources["AppBodyLineHeight"] = 20.0;
        XamlApplication.Current.Resources["AppCaptionFontSize"] = 12.0;
        XamlApplication.Current.Resources["AppScale"] = scale;
        XamlApplication.Current.Resources["ControlContentThemeFontSize"] = 14.0;

        SetBrush("AccentPrimaryBrush", palette.Primary);
        SetBrush("AccentButtonBrush", palette.Button);
        SetBrush("AccentButtonHoverBrush", palette.ButtonHover);
        SetBrush("AccentButtonPressedBrush", palette.ButtonPressed);
        SetBrush("AccentButtonBorderBrush", palette.ButtonBorder);
        SetBrush("AccentButtonForegroundBrush", palette.ButtonForeground);
        SetBrush("AccentTextBrush", palette.Text);
        SetBrush("AccentSubtleBrush", WithAlpha(palette.Primary, 0x26));
        SetBrush("AccentSubtleBorderBrush", WithAlpha(palette.Primary, 0x78));
        SetBrush("AccentGlowBrush", WithAlpha(palette.Primary, 0x34));
        SetColor("SystemAccentColor", palette.Primary);
        SetColor("SystemAccentColorLight1", Lighten(palette.Primary, 0.18));
        SetColor("SystemAccentColorLight2", Lighten(palette.Primary, 0.32));
        SetColor("SystemAccentColorLight3", Lighten(palette.Primary, 0.46));
        SetColor("SystemAccentColorDark1", Darken(palette.Primary, 0.16));
        SetColor("SystemAccentColorDark2", Darken(palette.Primary, 0.30));
        SetColor("SystemAccentColorDark3", Darken(palette.Primary, 0.44));

        foreach (var (window, root) in _targets.ToList())
        {
            try
            {
                var previous = root.RequestedTheme;
                root.RequestedTheme = theme;
                if (previous == theme) root.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = theme;
                ApplyBackdrop(window, settings.General.Theme, root);
                ApplyCaptionColors(window, theme);
                ApplyTypography(root, settings.General.FontFamily, settings.General.MonoFontFamily, scale);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Theme application failed: {Message}", ex.Message);
            }
        }

        ThemeApplied?.Invoke(this, EventArgs.Empty);
        var elapsed = Stopwatch.GetElapsedTime(applyStarted).TotalMilliseconds;
        if (elapsed >= 10)
            _logger.LogInformation($"Theme apply completed in {elapsed:0.0} ms");
    }

    public void ApplyTo(FrameworkElement root)
    {
        if (_settings is null) return;
        var started = Stopwatch.GetTimestamp();
        ApplyTypography(root, _settings.General.FontFamily, _settings.General.MonoFontFamily,
            Math.Clamp(_settings.General.FontScale, 0.85, 1.35));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed >= 4)
            _logger.LogInformation("Typography {Root}: {Elapsed:0.0} ms", root.GetType().Name, elapsed);
    }

    public FontFamily ResolveFontFamily(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) familyName = DefaultFont;
        if (familyName.Equals("JetBrains Mono", StringComparison.OrdinalIgnoreCase))
            return new FontFamily(JetBrainsFontUri);
        if (familyName.Equals("Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
            return new FontFamily("Segoe UI Variable Text, Segoe UI");
        return _fonts.Contains(familyName, StringComparer.OrdinalIgnoreCase)
            ? new FontFamily(familyName)
            : new FontFamily("Segoe UI Variable Text, Segoe UI");
    }

    private ElementTheme ResolveTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light
    };

    private bool IsSystemDark()
    {
        try
        {
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            return RelativeLuminance(background) < 0.5;
        }
        catch
        {
            return XamlApplication.Current.RequestedTheme == ApplicationTheme.Dark;
        }
    }

    private void ApplyBackdrop(Window window, ThemeMode mode, FrameworkElement root)
    {
        try
        {
            if (mode == ThemeMode.System && MicaController.IsSupported())
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                var dark = IsSystemDark();
                SetRootBackground(root, new SolidColorBrush(dark ? Color.FromArgb(0xCC, 0x0E, 0x13, 0x18) : Color.FromArgb(0xDD, 0xF6, 0xF7, 0xF9)));
            }
            else
            {
                window.SystemBackdrop = null;
                SetRootBackground(root, new SolidColorBrush(EffectiveTheme == ElementTheme.Dark
                    ? Color.FromArgb(0xFF, 0x0E, 0x13, 0x18)
                    : Color.FromArgb(0xFF, 0xF6, 0xF7, 0xF9)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Backdrop could not be applied: {Message}", ex.Message);
            SetRootBackground(root, new SolidColorBrush(EffectiveTheme == ElementTheme.Dark
                ? Color.FromArgb(0xFF, 0x0E, 0x13, 0x18)
                : Color.FromArgb(0xFF, 0xF6, 0xF7, 0xF9)));
        }
    }


    private static void SetRootBackground(FrameworkElement root, Brush brush)
    {
        switch (root)
        {
            case Page page:
                page.Background = brush;
                break;
            case Panel panel:
                panel.Background = brush;
                break;
            case Control control:
                control.Background = brush;
                break;
        }
    }
    private void ApplyCaptionColors(Window window, ElementTheme theme)
    {
        try
        {
            var dark = theme == ElementTheme.Dark;
            var foreground = dark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
            var hover = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
            var titleBar = window.AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x88, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        }
        catch
        {
            // Title bar customization is best-effort.
        }
    }

    private void ApplyTypography(FrameworkElement root, string fontName, string monoName, double scale)
    {
        // The default typography is already provided by the app resources and
        // implicit styles. Walking every element on each navigation only causes
        // redundant property changes and layout invalidation.
        if (scale is >= 0.999 and <= 1.001 &&
            string.Equals(fontName, DefaultFont, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(monoName, DefaultMonoFont, StringComparison.OrdinalIgnoreCase))
            return;

        var font = ResolveFontFamily(fontName);
        var mono = ResolveFontFamily(monoName);
        ApplyTypographyRecursive(root, font, mono, scale);
    }

    private void ApplyTypographyRecursive(DependencyObject node, FontFamily font, FontFamily mono, double scale)
    {
        try
        {
            switch (node)
            {
                case FontIcon:
                case IconElement:
                    break;
                case TextBlock textBlock:
                {
                    var state = GetState(textBlock);
                    if (state.IsIcon) break;
                    var useMono = string.Equals(textBlock.Tag as string, "Mono", StringComparison.OrdinalIgnoreCase);
                    var target = useMono ? mono : font;
                    if (!string.Equals(textBlock.FontFamily.Source, target.Source, StringComparison.OrdinalIgnoreCase))
                        textBlock.FontFamily = target;
                    var size = Math.Clamp(state.FontSize * scale, 9, 72);
                    if (Math.Abs(textBlock.FontSize - size) > 0.01)
                        textBlock.FontSize = size;
                    break;
                }
                case RichTextBlock rich:
                {
                    var state = GetState(rich);
                    if (!string.Equals(rich.FontFamily.Source, font.Source, StringComparison.OrdinalIgnoreCase))
                        rich.FontFamily = font;
                    var size = Math.Clamp(state.FontSize * scale, 9, 72);
                    if (Math.Abs(rich.FontSize - size) > 0.01)
                        rich.FontSize = size;
                    break;
                }
                case Control control:
                {
                    var state = GetState(control);
                    if (state.IsIcon) break;
                    var useMono = string.Equals(control.Tag as string, "Mono", StringComparison.OrdinalIgnoreCase);
                    var target = useMono ? mono : font;
                    if (!string.Equals(control.FontFamily.Source, target.Source, StringComparison.OrdinalIgnoreCase))
                        control.FontFamily = target;
                    // Font size is applied to the text elements themselves. Scaling the
                    // control too would compound with the inherited size inside templates.
                    break;
                }
            }
        }
        catch
        {
            // Typography should never break layout.
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            ApplyTypographyRecursive(VisualTreeHelper.GetChild(node, i), font, mono, scale);
        }
    }

    private BaseTypography GetState(DependencyObject node)
    {
        if (_baseTypography.TryGetValue(node, out var state)) return state;
        var fontFamily = node switch
        {
            TextBlock text => text.FontFamily,
            RichTextBlock rich => rich.FontFamily,
            Control control => control.FontFamily,
            _ => null
        };
        var size = node switch
        {
            TextBlock text => text.FontSize,
            RichTextBlock rich => rich.FontSize,
            Control control => control.FontSize,
            _ => 14.0
        };
        var source = fontFamily?.Source ?? "";
        state = new BaseTypography(size <= 0 ? 14 : size,
            source.Contains("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Symbol", StringComparison.OrdinalIgnoreCase));
        _baseTypography.Add(node, state);
        return state;
    }

    private static void SetBrush(string key, Color color) =>
        XamlApplication.Current.Resources[key] = new SolidColorBrush(color);

    private static void SetColor(string key, Color color) =>
        XamlApplication.Current.Resources[key] = color;

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Darken(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(color.A,
            (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
            (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
            (byte)Math.Clamp(color.B * (1 - amount), 0, 255));
    }

    private static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    private static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(a.A,
            (byte)Math.Round(a.R + (b.R - a.R) * amount),
            (byte)Math.Round(a.G + (b.G - a.G) * amount),
            (byte)Math.Round(a.B + (b.B - a.B) * amount));
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var l1 = RelativeLuminance(a);
        var l2 = RelativeLuminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static List<string> EnumerateFonts()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Segoe UI Variable Text",
            "Segoe UI",
            "Segoe UI Variable Display",
            "Calibri",
            "Arial",
            "Cascadia Code",
            "Cascadia Mono",
            "Consolas",
            "JetBrains Mono",
            "Verdana",
            "Georgia",
            "Trebuchet MS",
            "Tahoma"
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var name = valueName;
                    var index = name.IndexOf(" (", StringComparison.Ordinal);
                    if (index > 0) name = name[..index];
                    name = name.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && name.Length < 80) names.Add(name);
                }
            }
            catch
            {
                // Font enumeration is best-effort.
            }
        }

        return names.OrderBy(n => n.Equals("Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase) ? 0 :
                n.Equals("JetBrains Mono", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private sealed record BaseTypography(double FontSize, bool IsIcon);
}

internal sealed record AccentPalette(Color Primary, Color Button, Color ButtonHover, Color ButtonPressed,
    Color ButtonBorder, Color ButtonForeground, Color Text)
{
    public static AccentPalette Create(string hex, bool dark)
    {
        var primary = ParseColor(hex) ?? Color.FromArgb(0xFF, 0xD2, 0x69, 0x3A);
        var background = dark ? Color.FromArgb(0xFF, 0x0E, 0x13, 0x18) : Color.FromArgb(0xFF, 0xF6, 0xF7, 0xF9);
        var white = Colors.White;
        var black = Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0B);

        Color button;
        Color fore;
        if (Contrast(primary, white) >= 4.5)
        {
            button = primary;
            fore = white;
        }
        else
        {
            var darkened = Darken(primary, 0.30);
            if (Contrast(darkened, white) >= 4.5)
            {
                button = darkened;
                fore = white;
            }
            else
            {
                button = primary;
                fore = Contrast(primary, black) >= Contrast(primary, white) ? black : white;
            }
        }

        var text = primary;
        var guard = 0;
        while (Contrast(text, background) < 4.5 && guard++ < 30)
            text = dark ? Lighten(text, 0.06) : Darken(text, 0.06);

        return new AccentPalette(
            primary,
            button,
            Lighten(button, 0.10),
            Darken(button, 0.12),
            WithAlpha(button, 0xC0),
            fore,
            text);
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var value = hex.Trim().TrimStart('#');
        if (value.Length is not (6 or 8)) return null;
        try
        {
            var offset = value.Length == 8 ? 0 : 0;
            var a = value.Length == 8 ? Convert.ToByte(value[..2], 16) : (byte)255;
            var r = Convert.ToByte(value.Substring(offset + (value.Length == 8 ? 2 : 0), 2), 16);
            var g = Convert.ToByte(value.Substring(offset + (value.Length == 8 ? 4 : 2), 2), 16);
            var b = Convert.ToByte(value.Substring(offset + (value.Length == 8 ? 6 : 4), 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return null;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color Darken(Color color, double amount) => Color.FromArgb(color.A,
        (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.B * (1 - amount), 0, 255));
    private static Color Lighten(Color color, double amount) => Color.FromArgb(color.A,
        (byte)Math.Round(color.R + (255 - color.R) * amount),
        (byte)Math.Round(color.G + (255 - color.G) * amount),
        (byte)Math.Round(color.B + (255 - color.B) * amount));
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
    private static double Contrast(Color a, Color b)
    {
        var l1 = RelativeLuminance(a);
        var l2 = RelativeLuminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

public sealed class MotionService
{
    private readonly UISettings _settings = new();
    public bool IsEnabled
    {
        get
        {
            try { return _settings.AnimationsEnabled; }
            catch { return true; }
        }
    }

    public void Apply(FrameworkElement root)
    {
        if (IsEnabled) return;
        try
        {
            if (root is Panel panel) panel.Transitions.Clear();
            if (root is Control control) control.Transitions.Clear();
            root.Transitions.Clear();
        }
        catch
        {
            // Ignore.
        }
    }
}
