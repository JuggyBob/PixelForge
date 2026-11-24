using Microsoft.UI.Xaml;

namespace PixelForge.Presentation.Localization;

/// <summary>
/// Keeps weak references to navigation pages so a runtime language change can
/// refresh cached pages too. This avoids forcing a page reload and preserves
/// staged files, queue state and scroll position.
/// </summary>
public static class LocalizationPageRegistry
{
    private static readonly List<WeakReference<FrameworkElement>> Pages = [];
    private static readonly object Gate = new();

    public static void Register(FrameworkElement page)
    {
        lock (Gate)
        {
            Pages.RemoveAll(reference => !reference.TryGetTarget(out _));
            Pages.Add(new WeakReference<FrameworkElement>(page));
        }
    }

    public static void RefreshAll()
    {
        List<FrameworkElement> pages;
        lock (Gate)
        {
            Pages.RemoveAll(reference => !reference.TryGetTarget(out _));
            pages = Pages
                .Select(reference => reference.TryGetTarget(out var page) ? page : null)
                .Where(page => page is not null)
                .Cast<FrameworkElement>()
                .ToList();
        }

        foreach (var page in pages)
        {
            // Every cached page refreshes its view model in place. Never reset
            // the page DataContext: that detaches TwoWay ComboBox bindings and
            // clears the selected text (the blank-dropdown regression).
            if (page is ILocalizationAwarePage aware)
                aware.RefreshLocalization();
        }
    }
}
