using Microsoft.Extensions.Logging;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

public enum LlamaCppRuntimeUpgradeOutcome
{
    NotNeeded,
    AlreadyCurrent,
    Updated,
    Failed,
    Cancelled
}

public sealed record LlamaCppRuntimeUpgradeResult
{
    public required LlamaCppRuntimeUpgradeOutcome Outcome { get; init; }
    public InstalledLlamaCppRuntime? Runtime { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Detects an older llama.cpp runtime left in the user profile by a previous
/// PixelForge build and replaces it with the runtime required by this build.
/// The migration downloads the current verified package instead of reusing an
/// old binary, then removes the superseded directory.
/// </summary>
public sealed class LlamaCppRuntimeUpgradeService
{
    private readonly ILlamaCppRuntimeManager _runtime;
    private readonly ILogger<LlamaCppRuntimeUpgradeService> _logger;

    public LlamaCppRuntimeUpgradeService(ILlamaCppRuntimeManager runtime,
        ILogger<LlamaCppRuntimeUpgradeService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public string RuntimeId => LlamaCppRuntimeCatalog.Vulkan.Id;

    public async Task<LlamaCppRuntimeUpgradeResult> UpgradeAsync(
        IProgress<LlamaCppDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _runtime.InitializeAsync(cancellationToken);
        if (_runtime.GetInstalled(RuntimeId) is { IsValid: true } installed)
        {
            DeleteLegacyRuntimes();
            return new LlamaCppRuntimeUpgradeResult
            {
                Outcome = LlamaCppRuntimeUpgradeOutcome.AlreadyCurrent,
                Runtime = installed
            };
        }

        var legacy = FindLegacyRuntimeDirectory();
        if (legacy is null)
            return new LlamaCppRuntimeUpgradeResult { Outcome = LlamaCppRuntimeUpgradeOutcome.NotNeeded };

        _logger.LogInformation("Upgrading llama.cpp runtime from {Legacy} to {Current}",
            Path.GetFileName(legacy), RuntimeId);
        var result = await _runtime.DownloadAsync(RuntimeId, progress, cancellationToken);
        if (!result.Success)
        {
            if (cancellationToken.IsCancellationRequested)
                return new LlamaCppRuntimeUpgradeResult
                {
                    Outcome = LlamaCppRuntimeUpgradeOutcome.Cancelled,
                    Error = "The runtime upgrade was cancelled."
                };

            _logger.LogWarning("llama.cpp runtime upgrade failed: {Error}", result.Error);
            return new LlamaCppRuntimeUpgradeResult
            {
                Outcome = LlamaCppRuntimeUpgradeOutcome.Failed,
                Error = result.Error ?? "The llama.cpp runtime could not be updated.",
                Details = result.Details
            };
        }

        DeleteDirectory(legacy);
        return new LlamaCppRuntimeUpgradeResult
        {
            Outcome = LlamaCppRuntimeUpgradeOutcome.Updated,
            Runtime = result.Runtime
        };
    }

    public string? FindLegacyRuntimeDirectory()
    {
        var root = _runtime.RuntimeDirectory;
        if (!Directory.Exists(root)) return null;

        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(IsManagedLegacyRuntime)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not inspect legacy llama.cpp runtimes");
            return null;
        }
    }

    private void DeleteLegacyRuntimes()
    {
        var root = _runtime.RuntimeDirectory;
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!IsManagedLegacyRuntime(directory)) continue;
                DeleteDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove a superseded llama.cpp runtime");
        }
    }

    /// <summary>
    /// A legacy runtime must live directly under the managed runtime root, use
    /// the PixelForge runtime naming scheme, contain the complete executable
    /// payload and must not be a reparse point. This prevents the automatic
    /// upgrade sweep from deleting unrelated user folders that merely contain a
    /// file named llama-server.exe.
    /// </summary>
    private bool IsManagedLegacyRuntime(string directory)
    {
        try
        {
            var root = Path.GetFullPath(_runtime.RuntimeDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            var name = Path.GetFileName(full);
            if (string.Equals(name, LlamaCppRuntimeCatalog.Vulkan.InstallDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (!name.StartsWith("llama-cpp-", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".extract", StringComparison.OrdinalIgnoreCase))
                return false;

            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;

            return File.Exists(Path.Combine(full, "llama-server.exe")) &&
                   File.Exists(Path.Combine(full, "ggml-base.dll")) &&
                   (File.Exists(Path.Combine(full, "ggml-vulkan.dll")) ||
                    File.Exists(Path.Combine(full, "llama-server-impl.dll")));
        }
        catch
        {
            return false;
        }
    }

    private void DeleteDirectory(string directory)
    {
        try
        {
            if (IsManagedLegacyRuntime(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove superseded llama.cpp runtime {Directory}", directory);
        }
    }
}
