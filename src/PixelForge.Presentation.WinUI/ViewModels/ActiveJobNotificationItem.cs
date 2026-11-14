using CommunityToolkit.Mvvm.ComponentModel;
using PixelForge.Domain;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class ActiveJobNotificationItem : ObservableObject
{
    public ActiveJobNotificationItem(ProcessingJob job)
    {
        JobId = job.Id;
        Update(job);
    }

    public Guid JobId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _glyph = "\uE8AB";

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
        ProgressText = job.ProgressText;
        Detail = job.BatchSummary ?? job.Error ?? job.OperationText;
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
}
