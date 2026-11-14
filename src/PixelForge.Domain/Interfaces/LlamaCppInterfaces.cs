using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface ILlamaCppRuntimeManager
{
    string RuntimeDirectory { get; }
    IReadOnlyList<LlamaCppRuntimeDescriptor> AvailableRuntimes { get; }
    IReadOnlyList<InstalledLlamaCppRuntime> InstalledRuntimes { get; }
    event EventHandler? RuntimesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    InstalledLlamaCppRuntime? GetInstalled(string runtimeId);
    Task<LlamaCppDownloadResult> DownloadAsync(string runtimeId,
        IProgress<LlamaCppDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<InstalledLlamaCppRuntime?> ValidateAsync(string runtimeId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string runtimeId, CancellationToken cancellationToken = default);
}

public interface ILlamaCppOcrService
{
    bool IsRuntimeAvailable { get; }
    Task<LlamaOcrResult> RecognizeAsync(InstalledOcrModel model, IReadOnlyList<string> pagePaths,
        string prompt, bool useGpu, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default);
}
