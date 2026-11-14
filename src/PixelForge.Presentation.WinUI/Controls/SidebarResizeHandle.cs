using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace PixelForge.Presentation.Controls;

/// <summary>
/// WinUI 3 exposes the cursor through the protected UIElement.ProtectedCursor
/// property, so a small reflection helper is used for the sidebar splitter.
/// </summary>
public static class SidebarResizeCursor
{
    private static bool _applied;
    private static PropertyInfo? _property;

    public static void Apply(UIElement element)
    {
        try
        {
            _property ??= typeof(UIElement).GetProperty("ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _property?.SetValue(element, InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
            _applied = true;
        }
        catch
        {
            // The cursor is cosmetic; resizing still works without it.
        }
    }

    public static bool IsApplied => _applied;
}
