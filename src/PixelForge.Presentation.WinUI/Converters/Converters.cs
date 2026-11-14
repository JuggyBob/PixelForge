using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using PixelForge.Domain;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null && value is not string s || (value is string str && !string.IsNullOrWhiteSpace(str));
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            long bytes => FormatBytes(bytes),
            int bytes => FormatBytes(bytes),
            _ => "—"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var seconds = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };
        if (seconds <= 0) return "—";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class OperationGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is OperationKind operation
            ? operation switch
            {
                OperationKind.ImageConvert => "\uE91B",
                OperationKind.AudioConvert => "\uE8D6",
                OperationKind.VideoConvert => "\uE714",
                OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
                OperationKind.VideoUpscale => "\uE8B2",
                OperationKind.DocumentConvert or OperationKind.EbookConvert or OperationKind.OcrDocument => "\uE8A5",
                OperationKind.ArchiveConvert => "\uE7B8",
                OperationKind.AudioProcess => "\uE8D6",
                OperationKind.ImageProcess => "\uE91B",
                OperationKind.VideoProcess => "\uE714",
                _ => "\uE8AB"
            }
            : "\uE8AB";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class OperationTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is OperationKind operation
            ? operation switch
            {
                OperationKind.ImageConvert => "Image conversion",
                OperationKind.AudioConvert => "Audio conversion",
                OperationKind.VideoConvert => "Video conversion",
                OperationKind.UpscaleAi => "AI upscale",
                OperationKind.UpscaleTraditional => "Upscale",
                OperationKind.VideoUpscale => "Video upscaling",
                OperationKind.DocumentConvert => "Document conversion",
                OperationKind.EbookConvert => "Ebook conversion",
                OperationKind.ArchiveConvert => "Archive conversion",
                OperationKind.AudioProcess => "Audio processing",
                OperationKind.ImageProcess => "Image processing",
                OperationKind.VideoProcess => "Video processing",
                OperationKind.OcrDocument => "OCR document",
                _ => "Processing"
            }
            : "Processing";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset time) return "";
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return time.LocalDateTime.ToString("MMM d");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d ? $"{d * 100:0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is string s && !string.IsNullOrWhiteSpace(s);
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex) return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        var text = hex.Trim().TrimStart('#');
        try
        {
            if (text.Length == 6)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF,
                    System.Convert.ToByte(text[..2], 16),
                    System.Convert.ToByte(text.Substring(2, 2), 16),
                    System.Convert.ToByte(text.Substring(4, 2), 16)));
            }
            if (text.Length == 8)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                    System.Convert.ToByte(text[..2], 16),
                    System.Convert.ToByte(text.Substring(2, 2), 16),
                    System.Convert.ToByte(text.Substring(4, 2), 16),
                    System.Convert.ToByte(text.Substring(6, 2), 16)));
            }
        }
        catch
        {
            // Fall through to the neutral colour.
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}
