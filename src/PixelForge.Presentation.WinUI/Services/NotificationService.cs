using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;

namespace PixelForge.Presentation.Services;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record AppNotification(Guid Id, string Title, string Message, NotificationSeverity Severity,
    DateTimeOffset CreatedAt, bool AutoDismiss = true);

public interface INotificationService
{
    event EventHandler<AppNotification>? NotificationRaised;
    void Notify(string title, string message, NotificationSeverity severity = NotificationSeverity.Info,
        bool autoDismiss = true);
}

public sealed class NotificationService : INotificationService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppLogger _logger;

    public NotificationService(IAppLogger logger)
    {
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public event EventHandler<AppNotification>? NotificationRaised;

    public void Notify(string title, string message, NotificationSeverity severity = NotificationSeverity.Info,
        bool autoDismiss = true)
    {
        var notification = new AppNotification(Guid.NewGuid(), title, message, severity, DateTimeOffset.Now, autoDismiss);
        if (_dispatcher.HasThreadAccess)
            Raise(notification);
        else
            _dispatcher.TryEnqueue(() => Raise(notification));
    }

    private void Raise(AppNotification notification)
    {
        try
        {
            NotificationRaised?.Invoke(this, notification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notification handler failed: {Message}", ex.Message);
        }
    }
}
