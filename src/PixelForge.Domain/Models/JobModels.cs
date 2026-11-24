namespace PixelForge.Domain.Models;

public sealed class ProcessingJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required OperationKind Operation { get; init; }
    public required string Title { get; init; }
    public JobState State { get; set; } = JobState.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required OperationSettings Settings { get; set; }
    public string? EngineId { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string? Error { get; set; }
    public string? ErrorDetails { get; set; }
    public string? SourcePath { get; init; }
    public bool PreserveRelativePaths { get; init; }
    public Guid? PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public int PipelineStep { get; init; }
    public int PipelineStepCount { get; init; }
    public List<JobItem> Items { get; init; } = [];
    public string? BatchSummary { get; set; }

    public int TotalItems => Items.Count;
    public int CompletedItems => Items.Count(i => i.State == JobItemState.Completed);
    public int FailedItems => Items.Count(i => i.State == JobItemState.Failed);
    public int CancelledItems => Items.Count(i => i.State == JobItemState.Cancelled);
    public int SkippedItems => Items.Count(i => i.State == JobItemState.Skipped);
    public int FinishedItems => Items.Count(i => i.State is JobItemState.Completed or JobItemState.Failed or JobItemState.Cancelled or JobItemState.Skipped);
    public long TotalInputBytes => Items.Sum(i => i.InputBytes);
    public long TotalOutputBytes => Items.Sum(i => i.OutputBytes ?? 0);
    public double Progress => Items.Count == 0 ? 0 : Items.Average(i => i.State switch
    {
        JobItemState.Completed or JobItemState.Skipped or JobItemState.Failed or JobItemState.Cancelled => 1.0,
        _ => Math.Clamp(i.Progress, 0, 1)
    });
    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Interrupted;
    public bool CanCancel => State is JobState.Pending or JobState.Preparing or JobState.Running;
    public bool CanRetry => State is JobState.Failed or JobState.Cancelled or JobState.Interrupted;
    public bool CanRemove => State is JobState.Pending;
    public string ProgressText => $"{FinishedItems} of {TotalItems} · {Progress:P0}";
    public string StateText => State switch
    {
        JobState.Completed when FailedItems > 0 => $"Completed with {FailedItems} failure{(FailedItems == 1 ? "" : "s")}",
        JobState.Completed when CancelledItems > 0 => $"Completed with {CancelledItems} cancelled",
        JobState.Cancelled => "Cancelled",
        JobState.Interrupted => "Interrupted",
        _ => State.ToString()
    };
    public string OperationText => Operation switch
    {
        OperationKind.ImageConvert => "Image conversion",
        OperationKind.AudioConvert => "Audio conversion",
        OperationKind.VideoConvert => "Video conversion",
        OperationKind.UpscaleTraditional => "Image upscaling",
        OperationKind.UpscaleAi => "AI upscaling",
        OperationKind.VideoUpscale => "Video upscaling",
        OperationKind.DocumentConvert => "Document conversion",
        OperationKind.EbookConvert => "Ebook conversion",
        OperationKind.ArchiveConvert => "Archive conversion",
        OperationKind.AudioProcess => "Audio processing",
        OperationKind.ImageProcess => "Image processing",
        OperationKind.VideoProcess => "Video processing",
        OperationKind.OcrDocument => "OCR document",
        _ => "Processing"
    };
}

public sealed class JobItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid JobId { get; init; }
    public required string InputPath { get; init; }
    public required string FileName { get; init; }
    public string? RelativeDirectory { get; init; }
    public string? PreferredOutputName { get; init; }
    public string? OutputPath { get; set; }
    public string? TempPath { get; set; }
    public JobItemState State { get; set; } = JobItemState.Pending;
    public double Progress { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? ErrorDetails { get; set; }
    public string? ErrorCategory { get; set; }
    public string? EngineLog { get; set; }
    public string? RequestedProvider { get; set; }
    public string? ActualProvider { get; set; }
    public string? FallbackReason { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? ModelId { get; set; }
    public string? SkipReason { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public long InputBytes { get; init; }
    public long? OutputBytes { get; set; }
    public int Attempt { get; set; }
    public MediaInfo? InputInfo { get; init; }
    public MediaInfo? OutputInfo { get; set; }
    public string? OutputFormatId { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public List<string> AdditionalOutputPaths { get; set; } = [];
    public string? VerificationSummary { get; set; }
    public bool IsBatchChild { get; init; }
    public int SortOrder { get; init; }

    public string StateText => State switch
    {
        JobItemState.Completed => "Done",
        JobItemState.Failed => "Failed",
        JobItemState.Cancelled => "Cancelled",
        JobItemState.Skipped => "Skipped",
        JobItemState.Running => Message ?? "Processing",
        _ => "Waiting"
    };
}

public sealed record JobInput
{
    public required string Path { get; init; }
    public MediaInfo? Info { get; init; }
    public string? RelativeDirectory { get; init; }
    public string? PreferredOutputName { get; init; }
}

public sealed record JobRequest
{
    public required OperationKind Operation { get; init; }
    public required string Title { get; init; }
    public required OperationSettings Settings { get; init; }
    public required IReadOnlyList<JobInput> Inputs { get; init; }
    public string? EngineId { get; init; }
    public string? SourcePath { get; init; }
    public bool PreserveRelativePaths { get; init; }
    public string? BatchSummary { get; init; }
    public Guid? PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public int PipelineStep { get; init; }
    public int PipelineStepCount { get; init; }
}

public sealed record HistoryEntry
{
    public long Id { get; init; }
    public Guid JobId { get; init; }
    public Guid ItemId { get; init; }
    public OperationKind Operation { get; init; }
    public required string Title { get; init; }
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? InputFileName { get; init; }
    public string? OutputFileName { get; init; }
    public long InputBytes { get; init; }
    public long? OutputBytes { get; init; }
    public string? FormatId { get; init; }
    public string? SettingsJson { get; init; }
    public double DurationMs { get; init; }
    public JobItemState Status { get; init; }
    public string? Error { get; init; }
    public string? ErrorDetails { get; init; }
    public string? ErrorCategory { get; init; }
    public string? EngineLog { get; init; }
    public string? WarningsJson { get; init; }
    public string? RequestedProvider { get; init; }
    public string? ActualProvider { get; init; }
    public string? FallbackReason { get; init; }
    public string? RuntimeVersion { get; init; }
    public string? ModelId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? VerificationSummary { get; init; }
    public Guid? PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public int PipelineStep { get; init; }
    public int PipelineStepCount { get; init; }
}
