using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

public sealed class LlamaCppRuntimeManager : ILlamaCppRuntimeManager
{
    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<LlamaCppRuntimeManager> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<InstalledLlamaCppRuntime> _installed = [];
    private bool _initialized;

    public LlamaCppRuntimeManager(IAppPaths paths, IProcessRunner processRunner,
        ILogger<LlamaCppRuntimeManager> logger)
    {
        _paths = paths;
        _processRunner = processRunner;
        _logger = logger;
        RuntimeDirectory = Path.Combine(paths.RootDirectory, "runtimes", "llama.cpp");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PixelForge/1.1 (+local desktop OCR)");
    }

    public string RuntimeDirectory { get; }
    public IReadOnlyList<LlamaCppRuntimeDescriptor> AvailableRuntimes => LlamaCppRuntimeCatalog.All;
    public IReadOnlyList<InstalledLlamaCppRuntime> InstalledRuntimes
    {
        get
        {
            lock (_lock) return _installed.ToList();
        }
    }

    public event EventHandler? RuntimesChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(RuntimeDirectory);
            var installed = new List<InstalledLlamaCppRuntime>();
            foreach (var descriptor in LlamaCppRuntimeCatalog.All)
            {
                var runtime = Inspect(descriptor);
                if (runtime is not null) installed.Add(runtime);
            }
            lock (_lock)
            {
                _installed.Clear();
                _installed.AddRange(installed);
            }
            _initialized = true;
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public InstalledLlamaCppRuntime? GetInstalled(string runtimeId)
    {
        lock (_lock)
            return _installed.FirstOrDefault(r =>
                string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase) && r.IsValid);
    }

    public async Task<LlamaCppDownloadResult> DownloadAsync(string runtimeId,
        IProgress<LlamaCppDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var descriptor = LlamaCppRuntimeCatalog.Find(runtimeId);
        if (descriptor is null)
            return new LlamaCppDownloadResult { Success = false, Error = "Unknown llama.cpp runtime." };

        await _gate.WaitAsync(cancellationToken);
        string? tempZip = null;
        string? tempExtract = null;
        var preservePartial = false;
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            tempZip = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName + ".download.zip");
            tempExtract = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName + ".extract");
            if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);

            try
            {
                await ResumableDownloader.DownloadAsync(_httpClient, descriptor.DownloadUrl, tempZip,
                    descriptor.FileSizeBytes,
                    (received, total) => progress?.Report(new LlamaCppDownloadProgress
                    {
                        RuntimeId = descriptor.Id,
                        BytesReceived = received,
                        TotalBytes = total,
                        Stage = "Downloading"
                    }), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new LlamaCppDownloadResult
                {
                    Success = false,
                    Error = ex.Message,
                    Details = descriptor.DownloadUrl
                };
            }

            progress?.Report(new LlamaCppDownloadProgress
            {
                RuntimeId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Verifying"
            });
            var length = new FileInfo(tempZip).Length;
            if (length != descriptor.FileSizeBytes)
            {
                return new LlamaCppDownloadResult
                {
                    Success = false,
                    Error = "The llama.cpp package has an unexpected size.",
                    Details = $"Expected {descriptor.FileSizeBytes:N0} bytes, received {length:N0}."
                };
            }
            var hash = await ComputeSha256Async(tempZip, cancellationToken);
            if (!string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new LlamaCppDownloadResult
                {
                    Success = false,
                    Error = "The llama.cpp package failed its SHA-256 integrity check.",
                    Details = $"Expected {descriptor.Sha256}\nActual   {hash}"
                };
            }

            progress?.Report(new LlamaCppDownloadProgress
            {
                RuntimeId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Extracting"
            });
            Directory.CreateDirectory(tempExtract);
            await Task.Run(() => SafeExtractZip(tempZip, tempExtract), cancellationToken);
            var validation = await ValidateDirectoryAsync(descriptor, tempExtract, cancellationToken);
            if (!validation.IsValid)
            {
                return new LlamaCppDownloadResult
                {
                    Success = false,
                    Error = "The llama.cpp package is incomplete or cannot start.",
                    Details = validation.ValidationError
                };
            }

            var finalDirectory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            InstallDirectoryTransactional(tempExtract, finalDirectory);
            tempExtract = null;

            var installed = Inspect(descriptor, hash);
            if (installed is null)
                return new LlamaCppDownloadResult { Success = false, Error = "The llama.cpp runtime could not be validated after installation." };

            lock (_lock)
            {
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            return new LlamaCppDownloadResult { Success = true, Runtime = installed };
        }
        catch (OperationCanceledException)
        {
            preservePartial = true;
            return new LlamaCppDownloadResult { Success = false, Error = "The llama.cpp runtime download was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "llama.cpp runtime download failed for {Runtime}", runtimeId);
            return new LlamaCppDownloadResult
            {
                Success = false,
                Error = "The llama.cpp runtime download failed.",
                Details = ex.Message
            };
        }
        finally
        {
            if (!preservePartial)
            {
                try { if (tempZip is not null && File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (tempExtract is not null && Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
            }
            _gate.Release();
        }
    }

    public async Task<InstalledLlamaCppRuntime?> ValidateAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var descriptor = LlamaCppRuntimeCatalog.Find(runtimeId);
        if (descriptor is null) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            if (!Directory.Exists(directory))
            {
                lock (_lock)
                    _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
                RuntimesChanged?.Invoke(this, EventArgs.Empty);
                return null;
            }

            var validation = await ValidateDirectoryAsync(descriptor, directory, cancellationToken);
            lock (_lock)
            {
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
                if (validation.IsValid) _installed.Add(validation);
            }
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            return validation.IsValid ? validation : validation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var descriptor = LlamaCppRuntimeCatalog.Find(runtimeId);
        if (descriptor is null) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            lock (_lock)
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove llama.cpp runtime {Runtime}", runtimeId);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private InstalledLlamaCppRuntime? Inspect(LlamaCppRuntimeDescriptor descriptor, string? sha256 = null)
    {
        var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
        if (!Directory.Exists(directory)) return null;
        var executable = Path.Combine(directory, descriptor.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(executable)) return null;
        if (descriptor.RequiredRelativePaths.Any(path =>
                !File.Exists(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)))))
            return null;
        return new InstalledLlamaCppRuntime
        {
            Descriptor = descriptor,
            InstallDirectory = directory,
            ExecutablePath = executable,
            SizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length),
            ActualSha256 = sha256 ?? descriptor.Sha256,
            InstalledAt = Directory.GetCreationTime(directory),
            IsValid = true
        };
    }

    private async Task<InstalledLlamaCppRuntime> ValidateDirectoryAsync(LlamaCppRuntimeDescriptor descriptor,
        string directory, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(directory, descriptor.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var missing = descriptor.RequiredRelativePaths
            .Select(path => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => !File.Exists(path));
        if (!File.Exists(executable) || missing is not null)
        {
            return new InstalledLlamaCppRuntime
            {
                Descriptor = descriptor,
                InstallDirectory = directory,
                ExecutablePath = executable,
                SizeBytes = 0,
                ActualSha256 = descriptor.Sha256,
                InstalledAt = DateTimeOffset.Now,
                IsValid = false,
                ValidationError = missing is null ? "llama-server.exe is missing." : $"A required file is missing: {missing}"
            };
        }

        var version = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = executable,
            Arguments = ["--version"],
            WorkingDirectory = directory,
            Timeout = TimeSpan.FromSeconds(20),
            MaxCapturedLines = 80
        }, cancellationToken);
        var output = (version.StandardOutput + "\n" + version.StandardError).ToLowerInvariant();
        var starts = version.ExitCode == 0 && output.Contains("version");
        return new InstalledLlamaCppRuntime
        {
            Descriptor = descriptor,
            InstallDirectory = directory,
            ExecutablePath = executable,
            SizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length),
            ActualSha256 = descriptor.Sha256,
            InstalledAt = Directory.GetCreationTime(directory),
            IsValid = starts,
            ValidationError = starts ? null : "llama-server.exe did not return version information."
        };
    }

    private static void SafeExtractZip(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The runtime archive contains an unsafe path.");
        }
        archive.ExtractToDirectory(destination, overwriteFiles: true);
    }

    private static void InstallDirectoryTransactional(string staging, string destination)
    {
        var backup = destination + ".backup";
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        try
        {
            Directory.Move(staging, destination);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
        catch
        {
            try
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
            }
            catch
            {
                // Keep the original error; a leftover backup remains recoverable.
            }

            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
