using Microsoft.UI.Xaml.Data;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Converters;

/// <summary>
/// Translates an already-bound source string when it matches a catalog entry.
/// Technical values, file names and unknown runtime strings pass through.
/// </summary>
public sealed class LocalizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text ? LocalizationService.T(text) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value;
}
