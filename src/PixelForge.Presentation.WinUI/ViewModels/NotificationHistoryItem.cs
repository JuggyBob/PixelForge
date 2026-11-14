using Microsoft.UI.Xaml.Media;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed class NotificationHistoryItem
{
    public NotificationHistoryItem(AppNotification notification, Brush accentBrush)
    {
        NotificationId = notification.Id;
        Title = notification.Title;
        Message = notification.Message;
        AccentBrush = accentBrush;
        Glyph = notification.Severity switch
        {
            NotificationSeverity.Success => "\uE73E",
            NotificationSeverity.Warning => "\uE7BA",
            NotificationSeverity.Error => "\uE783",
            _ => "\uE946"
        };
        SeverityText = notification.Severity.ToString();
        TimeText = FormatRelative(notification.CreatedAt);
        AccessibleName = $"{SeverityText}: {Title}. {Message}. {TimeText}";
    }

    public Guid NotificationId { get; }
    public string Title { get; }
    public string Message { get; }
    public string SeverityText { get; }
    public string Glyph { get; }
    public string TimeText { get; }
    public Brush AccentBrush { get; }
    public string AccessibleName { get; }

    private static string FormatRelative(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalSeconds < 45) return "just now";
        if (delta.TotalMinutes < 60) return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        if (delta.TotalHours < 24) return $"{Math.Max(1, (int)delta.TotalHours)} h ago";
        if (delta.TotalDays < 7) return $"{Math.Max(1, (int)delta.TotalDays)} d ago";
        return time.LocalDateTime.ToString("MMM d, HH:mm");
    }
}
