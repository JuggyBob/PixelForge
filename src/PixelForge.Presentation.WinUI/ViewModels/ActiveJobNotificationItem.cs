using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class ActiveJobNotificationItem : ObservableObject
{
    private readonly Func<Guid, Task> _cancelJob;
    private bool _cancelRequested;

    public ActiveJobNotificationItem(ProcessingJob job, Func<Guid, Task> cancelJob)
    {
        JobId = job.Id;
        _cancelJob = cancelJob;
        Update(job);
    }

    public Guid JobId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _glyph = "\uE8AB";
    [ObservableProperty] private bool _canCancel;

    public void Update(ProcessingJob job)
    {
        Title = job.Title;
        StateText = job.State switch
        {
            JobState.Pending => "Queued",
            JobState.Preparing => "Preparing",
            JobState.Running => "Running",
            _ => job.StateText
        };
        Progress = job.Progress * 100;
        ProgressText = LocalizationService.T(job.ProgressText);
        Detail = LocalizationService.LocalizeBatchSummary(job.BatchSummary) is { Length: > 0 } summary
            ? summary
            : job.Error ?? LocalizationService.T(job.OperationText);
        CanCancel = !_cancelRequested && job.State is JobState.Pending or JobState.Preparing or JobState.Running;
        if (job.IsTerminal) _cancelRequested = false;
        Glyph = job.Operation switch
        {
            OperationKind.ImageConvert => "\uE91B",
            OperationKind.AudioConvert => "\uE8D6",
            OperationKind.VideoConvert => "\uE714",
            OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
            OperationKind.VideoUpscale => "\uE8B2",
            OperationKind.OcrDocument => "\uE8A5",
            _ => "\uE8AB"
        };
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!CanCancel || _cancelRequested) return;
        _cancelRequested = true;
        CanCancel = false;
        try
        {
            await _cancelJob(JobId);
        }
        catch
        {
            _cancelRequested = false;
            CanCancel = true;
        }
    }
}
