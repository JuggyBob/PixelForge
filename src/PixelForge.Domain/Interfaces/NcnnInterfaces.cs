using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface INcnnRuntimeManager
{
    string RuntimeDirectory { get; }
    IReadOnlyList<NcnnRuntimeDescriptor> AvailableRuntimes { get; }
    IReadOnlyList<InstalledNcnnRuntime> InstalledRuntimes { get; }
    event EventHandler? RuntimesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    InstalledNcnnRuntime? GetInstalled(string runtimeId);
    NcnnRuntimePaths? Resolve(string runtimeId);
    Task<NcnnRuntimeDownloadResult> DownloadAsync(string runtimeId,
        IProgress<NcnnRuntimeDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<InstalledNcnnRuntime?> ValidateAsync(string runtimeId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string runtimeId, CancellationToken cancellationToken = default);
}
