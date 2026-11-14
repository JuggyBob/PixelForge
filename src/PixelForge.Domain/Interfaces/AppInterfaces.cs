using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public sealed class JobValidationException : Exception
{
    public ValidationResult Validation { get; }
    public JobValidationException(ValidationResult validation)
        : base(validation.Summary)
    {
        Validation = validation;
    }
}

public sealed class JobChangedEventArgs : EventArgs
{
    public required ProcessingJob Job { get; init; }
    public JobChangeKind Kind { get; init; }
}

public enum JobChangeKind
{
    Added = 0,
    Updated = 1,
    Removed = 2,
    Cleared = 3
}

public interface IJobService
{
    IReadOnlyList<ProcessingJob> Jobs { get; }
    bool IsStarted { get; }
    event EventHandler<JobChangedEventArgs>? JobChanged;
    event EventHandler? QueueChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateAsync(JobRequest request, CancellationToken cancellationToken = default);
    Task<ProcessingJob> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default);
    ProcessingJob? GetJob(Guid jobId);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CancelItemAsync(Guid jobId, Guid itemId, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task RemovePendingAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task RemoveItemAsync(Guid jobId, Guid itemId, CancellationToken cancellationToken = default);
    Task ClearCompletedAsync(CancellationToken cancellationToken = default);
    Task ClearFailedAsync(CancellationToken cancellationToken = default);
    Task ClearTerminalAsync(CancellationToken cancellationToken = default);
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
    Task RequestOpenOutputAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface IModelManager
{
    string ModelDirectory { get; }
    IReadOnlyList<ModelDescriptor> AvailableModels { get; }
    IReadOnlyList<InstalledModel> InstalledModels { get; }
    event EventHandler? ModelsChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    InstalledModel? GetInstalled(string modelId);
    ModelDescriptor? GetDescriptor(string modelId);
    Task<ModelDownloadResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ModelDownloadResult> InstallFromFileAsync(string filePath, string? modelId = null,
        CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default);
    Task<InstalledModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default);
    Task<string?> ResolveModelPathAsync(string modelId, CancellationToken cancellationToken = default);
    Task SetModelDirectoryAsync(string directory, CancellationToken cancellationToken = default);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
    Task<ProcessResult> RunStreamingAsync(ProcessRequest request,
        Action<string>? onStdOut, Action<string>? onStdErr, CancellationToken cancellationToken = default);
    bool IsProcessRunning(int processId);
    void KillProcessTree(int processId);
}

public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public int MaxCapturedLines { get; init; } = 400;
    public bool CaptureOutput { get; init; } = true;
    public string? StandardInput { get; init; }
}

public sealed record ProcessResult
{
    public required int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public required TimeSpan Duration { get; init; }
    public bool WasCancelled { get; init; }
    public bool TimedOut { get; init; }
    public int ProcessId { get; init; }
    public IReadOnlyList<string> StandardOutputLines { get; init; } = [];
    public IReadOnlyList<string> StandardErrorLines { get; init; } = [];
    public bool Succeeded => ExitCode == 0 && !WasCancelled && !TimedOut;
}

public interface IThumbnailService
{
    Task<string?> GetThumbnailPathAsync(string mediaPath, MediaCategory category, int maxSize = 160,
        CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}

public interface IDiagnosticsService
{
    Task<SystemDiagnostics> BuildAsync(CancellationToken cancellationToken = default);
    Task<string> BuildReportAsync(CancellationToken cancellationToken = default);
    Task<string> BuildReportAsync(SystemDiagnostics diagnostics, CancellationToken cancellationToken = default);
}

public interface IOcrModelManager
{
    string ModelDirectory { get; }
    IReadOnlyList<OcrModelDescriptor> AvailableModels { get; }
    IReadOnlyList<InstalledOcrModel> InstalledModels { get; }
    event EventHandler? ModelsChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    InstalledOcrModel? GetInstalled(string modelId);
    Task<OcrModelDownloadResult> DownloadAsync(string modelId,
        IProgress<OcrModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<InstalledOcrModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default);
    Task<InstalledOcrModel?> ResolveAsync(string modelId, CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    void Initialize(string logDirectory, string minimumLevel);
    void LogInformation(string message, params object?[] args);
    void LogWarning(string message, params object?[] args);
    void LogError(Exception? exception, string message, params object?[] args);
    void Shutdown();
    string LogDirectory { get; }
}
