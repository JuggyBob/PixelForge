using Microsoft.UI.Xaml.Media;
using PixelForge.Presentation.Localization;
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
        SeverityText = LocalizationService.T(notification.Severity.ToString());
        TimeText = FormatRelative(notification.CreatedAt);
        AccessibleName = LocalizationService.F("{0}: {1}. {2}. {3}",
            SeverityText, Title, Message, TimeText);
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
        if (delta.TotalSeconds < 45) return LocalizationService.T("just now");
        if (delta.TotalMinutes < 60) return LocalizationService.P("Relative.MinutesAgo", Math.Max(1, (int)delta.TotalMinutes));
        if (delta.TotalHours < 24) return LocalizationService.P("Relative.HoursAgo", Math.Max(1, (int)delta.TotalHours));
        if (delta.TotalDays < 7) return LocalizationService.P("Relative.DaysAgo", Math.Max(1, (int)delta.TotalDays));
        return LocalizationService.FormatDateTime(time, "g");
    }
}
