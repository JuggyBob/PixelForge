using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public enum QueueFilter
{
    All,
    Active,
    Finished,
    Failed
}

public sealed record QueueFilterOption(QueueFilter Value, string Label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    public override string ToString() => DisplayLabel;
}

public sealed partial class QueueViewModel : ObservableObject, IDisposable
{
    private readonly IJobService _jobs;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _filePicker;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<JobItemViewModel> _allJobs = [];
    private bool _disposed;

    public QueueViewModel(IJobService jobs, INavigationService navigation, IDialogService dialogs,
        IFilePickerService filePicker, INotificationService notifications, IAppLogger logger)
    {
        _jobs = jobs;
        _navigation = navigation;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _jobs.JobChanged += OnJobChanged;
        _jobs.QueueChanged += OnQueueChanged;
        SelectedFilter = FilterOptions[0];
    }

    public IReadOnlyList<QueueFilterOption> FilterOptions { get; } =
    [
        new(QueueFilter.All, "All jobs"),
        new(QueueFilter.Active, "Active"),
        new(QueueFilter.Finished, "Finished"),
        new(QueueFilter.Failed, "Failed")
    ];

    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in FilterOptions) option.RefreshLocalization();
        foreach (var item in _allJobs)
        {
            var job = _jobs.Jobs.FirstOrDefault(j => j.Id == item.Id);
            if (job is not null)
                item.Update(job);
            else
                item.RefreshLocalization();
        }
    }

    [ObservableProperty] private ObservableCollection<JobItemViewModel> _visibleJobs = [];
    [ObservableProperty] private QueueFilterOption _selectedFilter;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private string _emptyMessage = "The queue is empty. Add a conversion or upscaling job to begin.";

    public void Refresh()
    {
        var source = _jobs.Jobs.OrderByDescending(j => j.State is JobState.Pending or JobState.Preparing or JobState.Running)
            .ThenByDescending(j => j.CreatedAt)
            .ToList();
        var sourceIds = source.Select(j => j.Id).ToList();
        var existing = _allJobs.ToDictionary(j => j.Id);
        _allJobs.Clear();
        foreach (var job in source)
        {
            if (existing.TryGetValue(job.Id, out var wrapper))
            {
                wrapper.Update(job);
            }
            else
            {
                wrapper = new JobItemViewModel(job, this);
            }
            _allJobs.Add(wrapper);
        }

        ActiveCount = _allJobs.Count(j => j.IsActive);
        CompletedCount = _allJobs.Count(j => j.IsCompleted);
        FailedCount = _allJobs.Count(j => j.IsFailed);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilter?.Value ?? QueueFilter.All;
        var items = filter switch
        {
            QueueFilter.Active => _allJobs.Where(j => j.IsActive),
            QueueFilter.Finished => _allJobs.Where(j => j.IsTerminal),
            QueueFilter.Failed => _allJobs.Where(j => j.IsFailed || j.IsInterrupted),
            _ => _allJobs
        };
        VisibleJobs = new ObservableCollection<JobItemViewModel>(items);
        HasJobs = VisibleJobs.Count > 0;
        EmptyMessage = filter switch
        {
            QueueFilter.Active => "No jobs are currently processing.",
            QueueFilter.Finished => "No finished jobs yet.",
            QueueFilter.Failed => "No failed jobs. Nice.",
            _ => "The queue is empty. Add a conversion or upscaling job to begin."
        };
    }

    [RelayCommand]
    private void Cancel(JobItemViewModel? job)
    {
        if (job is not null) _ = CancelAsync(job);
    }

    private async Task CancelAsync(JobItemViewModel job)
    {
        try
        {
            await _jobs.CancelAsync(job.Id);
            _notifications.Notify("Cancellation requested", job.Title, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not cancel the job", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private void Retry(JobItemViewModel? job)
    {
        if (job is not null) _ = RetryAsync(job);
    }

    private async Task RetryAsync(JobItemViewModel job)
    {
        try
        {
            await _jobs.RetryAsync(job.Id);
            _notifications.Notify("Job re-queued", job.Title, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not retry the job", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private void Remove(JobItemViewModel? job)
    {
        if (job is not null) _ = RemoveAsync(job);
    }

    private async Task RemoveAsync(JobItemViewModel job)
    {
        try
        {
            await _jobs.RemovePendingAsync(job.Id);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not remove the job", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private void OpenOutput(JobItemViewModel? job)
    {
        if (job is null) return;
        var folder = job.OutputDirectory;
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!Directory.Exists(folder))
        {
            try { Directory.CreateDirectory(folder); } catch { }
        }
        if (!Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the output folder", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear completed jobs?", "Remove completed jobs from the queue? History entries are kept.", "Clear completed", "Cancel"))
            return;
        await _jobs.ClearCompletedAsync();
    }

    [RelayCommand]
    private async Task ClearFailedAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear failed jobs?", "Remove failed jobs from the queue? History entries are kept.", "Clear failed", "Cancel"))
            return;
        await _jobs.ClearFailedAsync();
    }

    [RelayCommand]
    private async Task ClearTerminalAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear finished jobs?", "Remove all completed, failed, cancelled and interrupted jobs from the queue? History is kept.", "Clear finished", "Cancel"))
            return;
        await _jobs.ClearTerminalAsync();
    }

    [RelayCommand]
    private void OpenHistory() => _navigation.Navigate("History");

    public async Task SaveReportTextAsync(string reportText, string suggestedName)
    {
        var path = await _filePicker.PickTextSaveFileAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await File.WriteAllTextAsync(path, reportText);
            _notifications.Notify("Job report saved", Path.GetFileName(path), NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not save the report", ex.Message);
        }
    }

    public Task ViewReportTextAsync(string title, string reportText) =>
        _dialogs.ShowTextAsync(title, reportText);

    public void Compare(JobItemViewModel job, JobChildItemViewModel child)
    {
        if (child.InputPath is null || child.OutputPath is null) return;
        _navigation.Navigate("Compare", new ComparisonRequest(child.InputPath, child.OutputPath, child.FileName));
    }

    public async Task CancelItemAsync(JobChildItemViewModel child)
    {
        if (!await _dialogs.ConfirmAsync("Cancel this item?",
                "Only this file will be cancelled. The remaining files in the batch keep processing.",
                "Cancel item", "Keep processing", destructive: true))
            return;
        try
        {
            await _jobs.CancelItemAsync(child.ParentJobId, child.Id);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not cancel the item", ex.Message, ex.ToString());
        }
    }

    public async Task RemoveItemAsync(JobChildItemViewModel child)
    {
        try
        {
            await _jobs.RemoveItemAsync(child.ParentJobId, child.Id);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not remove the item", ex.Message, ex.ToString());
        }
    }

    public void OpenChildOutput(JobChildItemViewModel child)
    {
        var path = child.OutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _ = _dialogs.ShowErrorAsync("Output not available", "The output file no longer exists.");
            return;
        }
        if (Directory.Exists(path))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { _ = _dialogs.ShowErrorAsync("Could not open the output folder", ex.Message); }
            return;
        }
        if (!File.Exists(path))
        {
            _ = _dialogs.ShowErrorAsync("Output not available", "The output file no longer exists.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the output", ex.Message);
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnJobChanged(sender, e));
            return;
        }

        var wrapper = _allJobs.FirstOrDefault(j => j.Id == e.Job.Id);
        if (wrapper is null)
        {
            Refresh();
            return;
        }

        wrapper.Update(e.Job);
        ActiveCount = _allJobs.Count(j => j.IsActive);
        CompletedCount = _allJobs.Count(j => j.IsCompleted);
        FailedCount = _allJobs.Count(j => j.IsFailed);
        if (SelectedFilter?.Value == QueueFilter.Active && !wrapper.IsActive)
            ApplyFilter();
        else if (SelectedFilter?.Value == QueueFilter.Failed && wrapper.IsFailed)
            ApplyFilter();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnQueueChanged(sender, e));
            return;
        }
        Refresh();
    }

    partial void OnSelectedFilterChanged(QueueFilterOption value) => ApplyFilter();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        _jobs.QueueChanged -= OnQueueChanged;
    }
}

public sealed partial class JobItemViewModel : ObservableObject
{
    private readonly QueueViewModel _owner;
    private readonly List<JobChildItemViewModel> _children = [];

    public JobItemViewModel(ProcessingJob job, QueueViewModel owner)
    {
        _owner = owner;
        Id = job.Id;
        Children = [];
        Update(job);
    }

    public Guid Id { get; }
    public ObservableCollection<JobChildItemViewModel> Children { get; }
    public void RefreshLocalization() => OnPropertyChanged(string.Empty);
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _batchSummary = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isCancelled;
    [ObservableProperty] private bool _isInterrupted;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _iconGlyph = "\uE8AB";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _reportText = "";

    public bool IsTerminal => IsCompleted || IsFailed || IsCancelled || IsInterrupted;
    public bool HasItems => Children.Count > 0;
    public string ItemCountText => LocalizationService.P("Count.Item", Children.Count);

    public void Update(ProcessingJob job)
    {
        Title = job.Title;
        OperationText = job.OperationText;
        StateText = job.StateText;
        Progress = job.Progress * 100;
        ProgressText = LocalizationService.T(job.ProgressText);
        BatchSummary = LocalizationService.LocalizeBatchSummary(job.BatchSummary);
        Error = job.Error ?? "";
        HasError = !string.IsNullOrWhiteSpace(Error) && job.IsTerminal;
        CreatedText = FormatRelative(job.CreatedAt);
        IsActive = job.State is JobState.Pending or JobState.Preparing or JobState.Running;
        IsCompleted = job.State == JobState.Completed;
        IsFailed = job.State == JobState.Failed;
        IsCancelled = job.State == JobState.Cancelled;
        IsInterrupted = job.State == JobState.Interrupted;
        CanCancel = job.CanCancel;
        CanRetry = job.CanRetry;
        CanRemove = job.CanRemove;
        OutputDirectory = job.OutputDirectory;
        IconGlyph = job.Operation switch
        {
            OperationKind.ImageConvert => "\uE91B",
            OperationKind.AudioConvert => "\uE8D6",
            OperationKind.VideoConvert => "\uE714",
            OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
            OperationKind.VideoUpscale => "\uE8B2",
            OperationKind.OcrDocument => "\uE8A5",
            _ => "\uE8AB"
        };

        foreach (var item in job.Items)
        {
            var child = _children.FirstOrDefault(c => c.Id == item.Id);
            if (child is null)
            {
                child = new JobChildItemViewModel(item, this, _owner);
                _children.Add(child);
                Children.Add(child);
            }
            else
            {
                child.Update(item);
            }
        }

        ReportText = BuildJobReport(job);
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(HasItems));
    }

    private string BuildJobReport(ProcessingJob job)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"PixelForge job report");
        builder.AppendLine($"Job ID: {job.Id}");
        builder.AppendLine($"Operation: {job.OperationText}");
        builder.AppendLine($"Title: {job.Title}");
        builder.AppendLine($"State: {job.StateText}");
        builder.AppendLine($"Created: {job.CreatedAt:O}");
        builder.AppendLine($"Started: {job.StartedAt:O}");
        builder.AppendLine($"Completed: {job.CompletedAt:O}");
        builder.AppendLine($"Output directory: {job.OutputDirectory}");
        builder.AppendLine($"Progress: {job.ProgressText}");
        if (!string.IsNullOrWhiteSpace(job.BatchSummary)) builder.AppendLine($"Batch summary: {job.BatchSummary}");
        if (job.CompletedAt is { } completed && job.StartedAt is { } started)
            builder.AppendLine($"Duration: {(completed - started).TotalSeconds:0.0}s");
        if (!string.IsNullOrWhiteSpace(job.Error)) builder.AppendLine($"Error: {job.Error}");
        if (!string.IsNullOrWhiteSpace(job.ErrorDetails)) builder.AppendLine($"Error details: {job.ErrorDetails}");
        builder.AppendLine();
        foreach (var child in Children)
            builder.AppendLine(child.ReportText);
        return builder.ToString().TrimEnd();
    }

    [RelayCommand]
    private void CopyReport()
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(ReportText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
            // Clipboard failures must not break the queue.
        }
    }

    [RelayCommand]
    private void SaveReport() => _ = _owner.SaveReportTextAsync(ReportText, $"pixelforge-job-{Id:N}.txt");

    [RelayCommand]
    private void ViewReport() => _ = _owner.ViewReportTextAsync($"Job report · {Title}", ReportText);

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Cancel() => _owner.CancelCommand.Execute(this);

    [RelayCommand]
    private void Retry() => _owner.RetryCommand.Execute(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveCommand.Execute(this);

    [RelayCommand]
    private void OpenOutput() => _owner.OpenOutputCommand.Execute(this);

    [RelayCommand]
    private void OpenHistory() => _owner.OpenHistoryCommand.Execute(null);

    private static string FormatRelative(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalSeconds < 60) return LocalizationService.T("just now");
        if (delta.TotalMinutes < 60) return LocalizationService.P("Relative.MinutesAgo", Math.Max(1, (int)delta.TotalMinutes));
        if (delta.TotalHours < 24) return LocalizationService.P("Relative.HoursAgo", Math.Max(1, (int)delta.TotalHours));
        return LocalizationService.FormatDateTime(time, "g");
    }
}

public sealed partial class JobChildItemViewModel : ObservableObject
{
    private readonly JobItemViewModel _parent;
    private readonly QueueViewModel _owner;

    public JobChildItemViewModel(JobItem item, JobItemViewModel parent, QueueViewModel owner)
    {
        _parent = parent;
        _owner = owner;
        Id = item.Id;
        FileName = item.FileName;
        InputPath = item.InputPath;
        Update(item);
    }

    private JobItemState _state;
    public Guid Id { get; }
    public void RefreshLocalization() => OnPropertyChanged(string.Empty);
    public Guid ParentJobId => _parent.Id;
    public string FileName { get; }
    public string InputPath { get; }
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string? _outputPathNullable;
    [ObservableProperty] private bool _canOpenOutput;
    [ObservableProperty] private bool _canCompare;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _reportText = "";

    public void Update(JobItem item)
    {
        _state = item.State;
        StateText = item.StateText;
        Progress = item.Progress * 100;
        ProgressText = $"{item.Progress:P0}";
        Error = item.Error ?? "";
        HasError = item.State == JobItemState.Failed && !string.IsNullOrWhiteSpace(Error);
        OutputPath = item.OutputPath ?? "";
        OutputPathNullable = item.OutputPath;
        CanOpenOutput = item.State == JobItemState.Completed && item.OutputPath is not null;
        CanCompare = item.State == JobItemState.Completed && item.OutputPath is not null &&
                     _parent.IconGlyph == "\uE71E";
        Detail = item.State switch
        {
            JobItemState.Completed => item.VerificationSummary ?? "Verified",
            JobItemState.Failed => item.Error ?? "Failed",
            JobItemState.Skipped => item.SkipReason ?? "Skipped",
            JobItemState.Cancelled => item.Message ?? "Cancelled",
            _ => item.Message ?? item.StateText
        };
        ReportText = BuildReport(item);
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRemove));
    }

    private static string BuildReport(JobItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Item: {item.FileName}");
        builder.AppendLine($"Input: {item.InputPath}");
        builder.AppendLine($"Output: {item.OutputPath}");
        builder.AppendLine($"State: {item.StateText}");
        builder.AppendLine($"Attempt: {item.Attempt}");
        builder.AppendLine($"Started: {item.StartedAt:O}");
        builder.AppendLine($"Completed: {item.CompletedAt:O}");
        builder.AppendLine($"Input bytes: {item.InputBytes:N0}");
        builder.AppendLine($"Output bytes: {item.OutputBytes?.ToString("N0") ?? "n/a"}");
        builder.AppendLine($"Duration: {item.Duration?.TotalSeconds:0.0}s");
        if (!string.IsNullOrWhiteSpace(item.Error)) builder.AppendLine($"Error: {item.Error}");
        if (!string.IsNullOrWhiteSpace(item.ErrorCategory)) builder.AppendLine($"Category: {item.ErrorCategory}");
        if (!string.IsNullOrWhiteSpace(item.ErrorDetails)) builder.AppendLine($"Details: {item.ErrorDetails}");
        if (!string.IsNullOrWhiteSpace(item.EngineLog)) builder.AppendLine($"Engine log: {item.EngineLog}");
        if (!string.IsNullOrWhiteSpace(item.VerificationSummary)) builder.AppendLine($"Verification: {item.VerificationSummary}");
        if (!string.IsNullOrWhiteSpace(item.RequestedProvider)) builder.AppendLine($"Requested provider: {item.RequestedProvider}");
        if (!string.IsNullOrWhiteSpace(item.ActualProvider)) builder.AppendLine($"Actual provider: {item.ActualProvider}");
        if (!string.IsNullOrWhiteSpace(item.FallbackReason)) builder.AppendLine($"Fallback: {item.FallbackReason}");
        if (!string.IsNullOrWhiteSpace(item.RuntimeVersion)) builder.AppendLine($"Runtime: {item.RuntimeVersion}");
        if (!string.IsNullOrWhiteSpace(item.ModelId)) builder.AppendLine($"Model: {item.ModelId}");
        if (item.Warnings.Count > 0) builder.AppendLine($"Warnings: {string.Join(" | ", item.Warnings)}");
        return builder.ToString().TrimEnd();
    }

    public bool CanCancel => _state is JobItemState.Pending or JobItemState.Running;
    public bool CanRemove => _state == JobItemState.Pending || (_parent.IsTerminal && _state is not (JobItemState.Pending or JobItemState.Running));

    [RelayCommand]
    private void CopyReport()
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(ReportText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
            // Clipboard failures must not break the queue.
        }
    }

    [RelayCommand]
    private void SaveReport() => _ = _owner.SaveReportTextAsync(ReportText, $"pixelforge-item-{Id:N}.txt");

    [RelayCommand]
    private void ViewReport() => _ = _owner.ViewReportTextAsync($"Item report · {FileName}", ReportText);

    [RelayCommand]
    private void OpenOutput() => _owner.OpenChildOutput(this);

    [RelayCommand]
    private void Compare() => _owner.Compare(_parent, this);

    [RelayCommand]
    private void CancelItem() => _ = _owner.CancelItemAsync(this);

    [RelayCommand]
    private void RemoveItem() => _ = _owner.RemoveItemAsync(this);
}
