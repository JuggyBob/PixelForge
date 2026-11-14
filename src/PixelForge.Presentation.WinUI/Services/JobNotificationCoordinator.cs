using System.Collections.Concurrent;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.Services;

public sealed class JobNotificationCoordinator
{
    private readonly IJobService _jobs;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<Guid, JobState> _lastStates = new();
    private readonly object _startLock = new();
    private bool _started;

    public JobNotificationCoordinator(IJobService jobs, ISettingsService settings,
        INotificationService notifications, IAppLogger logger)
    {
        _jobs = jobs;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
    }

    public void Start()
    {
        lock (_startLock)
        {
            if (_started) return;
            _started = true;
            _jobs.JobChanged += OnJobChanged;
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        try
        {
            if (!_settings.Settings.General.ShowNotifications) return;
            var job = e.Job;
            if (e.Kind == JobChangeKind.Removed)
            {
                _lastStates.TryRemove(job.Id, out _);
                return;
            }

            var previous = _lastStates.GetOrAdd(job.Id, JobState.Pending);
            _lastStates[job.Id] = job.State;
            if (!job.IsTerminal || previous == job.State) return;

            switch (job.State)
            {
                case JobState.Completed when job.FailedItems > 0:
                    _notifications.Notify("Batch finished with errors",
                        $"{job.Title} · {job.BatchSummary ?? $"{job.FailedItems} failed"}", NotificationSeverity.Warning);
                    break;
                case JobState.Completed:
                    _notifications.Notify("Processing complete",
                        $"{job.Title} · {job.CompletedItems} item{(job.CompletedItems == 1 ? "" : "s")} verified.",
                        NotificationSeverity.Success);
                    break;
                case JobState.Failed:
                    _notifications.Notify("Processing failed",
                        $"{job.Title} · {job.Error ?? "Open the queue for details."}", NotificationSeverity.Error,
                        autoDismiss: false);
                    break;
                case JobState.Cancelled:
                    _notifications.Notify("Job cancelled", job.Title, NotificationSeverity.Warning);
                    break;
                case JobState.Interrupted:
                    _notifications.Notify("Job interrupted", $"{job.Title} — retry it from the queue.",
                        NotificationSeverity.Warning);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Job notification failed: {Message}", ex.Message);
        }
    }
}
