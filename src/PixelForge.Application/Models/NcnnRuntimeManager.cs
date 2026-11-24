using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

/// <summary>
/// Downloads and validates self-contained NCNN/Vulkan packages. Each package
/// is installed into its own directory so unrelated runtimes cannot interfere
/// with each other.
/// </summary>
public sealed class NcnnRuntimeManager : INcnnRuntimeManager
{
    private sealed record IndexEntry(string Id, string Version, string Sha256, DateTimeOffset InstalledAt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<NcnnRuntimeManager> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<InstalledNcnnRuntime> _installed = [];
    private bool _initialized;

    public NcnnRuntimeManager(IAppPaths paths, IProcessRunner processRunner, ILogger<NcnnRuntimeManager> logger)
    {
        _paths = paths;
        _processRunner = processRunner;
        _logger = logger;
        RuntimeDirectory = Path.Combine(paths.RootDirectory, "runtimes", "ncnn");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PixelForge/1.1 (+local desktop app)");
    }

    public string RuntimeDirectory { get; }
    public IReadOnlyList<NcnnRuntimeDescriptor> AvailableRuntimes => NcnnRuntimeCatalog.All;
    public IReadOnlyList<InstalledNcnnRuntime> InstalledRuntimes
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
            var index = await LoadIndexAsync(cancellationToken);
            var installed = new List<InstalledNcnnRuntime>();
            foreach (var descriptor in NcnnRuntimeCatalog.All)
            {
                var runtime = Inspect(descriptor, index.FirstOrDefault(i =>
                    string.Equals(i.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)));
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

    public InstalledNcnnRuntime? GetInstalled(string runtimeId)
    {
        lock (_lock)
            return _installed.FirstOrDefault(r =>
                string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase) && r.IsValid);
    }

    public NcnnRuntimePaths? Resolve(string runtimeId)
    {
        var installed = GetInstalled(runtimeId);
        if (installed is null) return null;
        return new NcnnRuntimePaths
        {
            RuntimeId = installed.Descriptor.Id,
            InstallDirectory = installed.InstallDirectory,
            ExecutablePath = installed.ExecutablePath
        };
    }

    public async Task<NcnnRuntimeDownloadResult> DownloadAsync(string runtimeId,
        IProgress<NcnnRuntimeDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var descriptor = NcnnRuntimeCatalog.Find(runtimeId);
        if (descriptor is null)
            return new NcnnRuntimeDownloadResult { Success = false, Error = "Unknown NCNN runtime." };

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

            _logger.LogInformation("Downloading NCNN runtime {Runtime} from {Url}", descriptor.Id, descriptor.DownloadUrl);
            try
            {
                await ResumableDownloader.DownloadAsync(_httpClient, descriptor.DownloadUrl, tempZip,
                    descriptor.FileSizeBytes,
                    (received, total) => progress?.Report(new NcnnRuntimeDownloadProgress
                    {
                        RuntimeId = descriptor.Id,
                        BytesReceived = received,
                        TotalBytes = total,
                        Stage = "Downloading"
                    }), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new NcnnRuntimeDownloadResult
                {
                    Success = false,
                    Error = ex.Message,
                    Details = descriptor.DownloadUrl
                };
            }

            progress?.Report(new NcnnRuntimeDownloadProgress
            {
                RuntimeId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Verifying"
            });

            var length = new FileInfo(tempZip).Length;
            if (descriptor.FileSizeBytes > 0 && length != descriptor.FileSizeBytes)
            {
                return new NcnnRuntimeDownloadResult
                {
                    Success = false,
                    Error = "The downloaded runtime package has an unexpected size.",
                    Details = $"Expected {descriptor.FileSizeBytes:N0} bytes, received {length:N0}."
                };
            }

            var hash = await ComputeSha256Async(tempZip, cancellationToken);
            if (!string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new NcnnRuntimeDownloadResult
                {
                    Success = false,
                    Error = "The downloaded runtime package failed its SHA-256 integrity check.",
                    Details = $"Expected {descriptor.Sha256}\nActual   {hash}"
                };
            }

            progress?.Report(new NcnnRuntimeDownloadProgress
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
                return new NcnnRuntimeDownloadResult
                {
                    Success = false,
                    Error = "The downloaded runtime package is incomplete or cannot start.",
                    Details = validation.ValidationError
                };
            }

            progress?.Report(new NcnnRuntimeDownloadProgress
            {
                RuntimeId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Installing"
            });

            var finalDirectory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            InstallDirectoryTransactional(tempExtract, finalDirectory);
            tempExtract = null;

            var installed = Inspect(descriptor, new IndexEntry(descriptor.Id, descriptor.Version, hash, DateTimeOffset.Now));
            if (installed is null)
                return new NcnnRuntimeDownloadResult { Success = false, Error = "The runtime was installed but could not be validated." };

            lock (_lock)
            {
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }

            await SaveIndexAsync(cancellationToken);
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Installed NCNN runtime {Runtime} ({Bytes:N0} bytes)", descriptor.Id, length);
            return new NcnnRuntimeDownloadResult { Success = true, Runtime = installed };
        }
        catch (OperationCanceledException)
        {
            preservePartial = true;
            return new NcnnRuntimeDownloadResult { Success = false, Error = "The runtime download was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NCNN runtime download failed for {Runtime}", runtimeId);
            return new NcnnRuntimeDownloadResult
            {
                Success = false,
                Error = "The runtime download failed. Check your connection and try again.",
                Details = ex.Message
            };
        }
        finally
        {
            if (tempZip is not null && !preservePartial)
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
            if (tempExtract is not null && !preservePartial)
            {
                try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
            }
            _gate.Release();
        }
    }

    public async Task<InstalledNcnnRuntime?> ValidateAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var descriptor = NcnnRuntimeCatalog.Find(runtimeId);
        if (descriptor is null) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            if (!Directory.Exists(directory))
            {
                lock (_lock)
                    _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
                await SaveIndexAsync(cancellationToken);
                RuntimesChanged?.Invoke(this, EventArgs.Empty);
                return null;
            }

            var index = await LoadIndexAsync(cancellationToken);
            var entry = index.FirstOrDefault(i => string.Equals(i.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
            var installed = await InspectAndValidateAsync(descriptor, directory, entry, cancellationToken);
            lock (_lock)
            {
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
                if (installed is not null) _installed.Add(installed);
            }
            await SaveIndexAsync(cancellationToken);
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            return installed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var descriptor = NcnnRuntimeCatalog.Find(runtimeId);
        if (descriptor is null) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
            if (Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove NCNN runtime directory {Path}", directory);
                    return false;
                }
            }

            lock (_lock)
                _installed.RemoveAll(r => string.Equals(r.Descriptor.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
            await SaveIndexAsync(cancellationToken);
            RuntimesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private InstalledNcnnRuntime? Inspect(NcnnRuntimeDescriptor descriptor, IndexEntry? entry)
    {
        var directory = Path.Combine(RuntimeDirectory, descriptor.InstallDirectoryName);
        if (!Directory.Exists(directory)) return null;
        var executable = Path.Combine(directory, descriptor.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(executable)) return null;
        var missing = descriptor.RequiredRelativePaths
            .Select(path => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => !File.Exists(path));
        if (missing is not null) return null;

        return new InstalledNcnnRuntime
        {
            Descriptor = descriptor,
            InstallDirectory = directory,
            ExecutablePath = executable,
            SizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length),
            ActualSha256 = entry?.Sha256 ?? descriptor.Sha256,
            InstalledAt = entry?.InstalledAt ?? Directory.GetCreationTime(directory),
            IsValid = true
        };
    }

    private async Task<InstalledNcnnRuntime?> InspectAndValidateAsync(NcnnRuntimeDescriptor descriptor,
        string directory, IndexEntry? entry, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(directory, descriptor.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var missing = descriptor.RequiredRelativePaths
            .Select(path => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => !File.Exists(path));
        if (!File.Exists(executable) || missing is not null)
        {
            return new InstalledNcnnRuntime
            {
                Descriptor = descriptor,
                InstallDirectory = directory,
                ExecutablePath = executable,
                SizeBytes = 0,
                ActualSha256 = entry?.Sha256 ?? descriptor.Sha256,
                InstalledAt = entry?.InstalledAt ?? DateTimeOffset.Now,
                IsValid = false,
                ValidationError = missing is null
                    ? "The runtime executable is missing."
                    : $"A required model file is missing: {missing}"
            };
        }

        var help = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = executable,
            Arguments = ["-h"],
            WorkingDirectory = directory,
            Timeout = TimeSpan.FromSeconds(15),
            MaxCapturedLines = 80
        }, cancellationToken);
        var output = (help.StandardOutput + "\n" + help.StandardError).ToLowerInvariant();
        var starts = output.Contains("usage:") &&
                     (output.Contains("waifu2x") || output.Contains("realcugan"));
        return new InstalledNcnnRuntime
        {
            Descriptor = descriptor,
            InstallDirectory = directory,
            ExecutablePath = executable,
            SizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length),
            ActualSha256 = entry?.Sha256 ?? descriptor.Sha256,
            InstalledAt = entry?.InstalledAt ?? DateTimeOffset.Now,
            IsValid = starts,
            ValidationError = starts ? null : "The runtime executable did not return its expected usage text."
        };
    }

    private async Task<InstalledNcnnRuntime> ValidateDirectoryAsync(NcnnRuntimeDescriptor descriptor,
        string directory, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(directory, descriptor.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var missing = descriptor.RequiredRelativePaths
            .Select(path => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => !File.Exists(path));
        if (!File.Exists(executable) || missing is not null)
        {
            return new InstalledNcnnRuntime
            {
                Descriptor = descriptor,
                InstallDirectory = directory,
                ExecutablePath = executable,
                SizeBytes = 0,
                ActualSha256 = descriptor.Sha256,
                InstalledAt = DateTimeOffset.Now,
                IsValid = false,
                ValidationError = missing is null
                    ? "The runtime executable is missing."
                    : $"A required model file is missing: {missing}"
            };
        }

        var help = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = executable,
            Arguments = ["-h"],
            WorkingDirectory = directory,
            Timeout = TimeSpan.FromSeconds(15),
            MaxCapturedLines = 80
        }, cancellationToken);
        var output = (help.StandardOutput + "\n" + help.StandardError).ToLowerInvariant();
        var starts = output.Contains("usage:") &&
                     (output.Contains("waifu2x") || output.Contains("realcugan"));
        return new InstalledNcnnRuntime
        {
            Descriptor = descriptor,
            InstallDirectory = directory,
            ExecutablePath = executable,
            SizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length),
            ActualSha256 = descriptor.Sha256,
            InstalledAt = DateTimeOffset.Now,
            IsValid = starts,
            ValidationError = starts ? null : "The runtime executable did not return its expected usage text."
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

    private async Task<List<IndexEntry>> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(RuntimeDirectory, "installed-ncnn-runtimes.json");
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<List<IndexEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the installed NCNN runtime index");
            return [];
        }
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(RuntimeDirectory, "installed-ncnn-runtimes.json");
            var temp = path + ".tmp";
            List<IndexEntry> entries;
            lock (_lock)
            {
                entries = _installed
                    .Where(r => r.IsValid)
                    .Select(r => new IndexEntry(r.Descriptor.Id, r.Descriptor.Version, r.ActualSha256, r.InstalledAt))
                    .ToList();
            }
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entries, JsonOptions), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the installed NCNN runtime index");
        }
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
