using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

public sealed class ModelManager : IModelManager
{
    private static readonly JsonSerializerOptions IndexJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IModelValidator _validator;
    private readonly INcnnRuntimeManager _ncnn;
    private readonly ILogger<ModelManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly object _lock = new();
    private readonly object _initializationLock = new();
    private readonly List<InstalledModel> _installed = [];
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private Task? _initializationTask;
    private bool _initialized;
    private string _modelDirectory = "";

    public ModelManager(ISettingsService settings, IAppPaths paths, IModelValidator validator,
        INcnnRuntimeManager ncnn, ILogger<ModelManager> logger)
    {
        _settings = settings;
        _paths = paths;
        _validator = validator;
        _ncnn = ncnn;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PixelForge/1.0 (+local desktop app)");
    }

    public string ModelDirectory => _modelDirectory;
    public IReadOnlyList<ModelDescriptor> AvailableModels => ModelCatalog.All;

    public IReadOnlyList<InstalledModel> InstalledModels
    {
        get
        {
            lock (_lock) return _installed.OrderBy(m => m.Descriptor.SortOrder).ToList();
        }
    }

    public event EventHandler? ModelsChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        lock (_initializationLock)
        {
            if (_initialized) return Task.CompletedTask;
            return _initializationTask ??= InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (_lock)
            {
                _modelDirectory = string.IsNullOrWhiteSpace(_settings.Settings.Upscaler.ModelDirectory)
                    ? _paths.DefaultModelDirectory
                    : _settings.Settings.Upscaler.ModelDirectory;
            }

            await _ncnn.InitializeAsync(cancellationToken);
            await ReloadAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            lock (_initializationLock) _initializationTask = null;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default) => await ReloadAsync(cancellationToken);

    public InstalledModel? GetInstalled(string modelId)
    {
        lock (_lock)
            return _installed.FirstOrDefault(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    public ModelDescriptor? GetDescriptor(string modelId) => ModelCatalog.Find(modelId);

    public async Task<ModelDownloadResult> DownloadAsync(string modelId,
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var descriptor = ModelCatalog.Find(modelId);
        if (descriptor is null)
            return new ModelDownloadResult { Success = false, Error = "Unknown model." };
        if (!descriptor.IsDownloadable)
            return new ModelDownloadResult { Success = false, Error = "This model does not have an automatic download source. Use 'Add model file'." };

        if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
            return await DownloadNcnnAsync(descriptor, progress, cancellationToken);

        await _downloadGate.WaitAsync(cancellationToken);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_modelDirectory);
            var finalPath = Path.Combine(_modelDirectory, descriptor.FileName);
            temp = finalPath + ".download";
            if (File.Exists(temp)) File.Delete(temp);

            _logger.LogInformation("Downloading model {Model} from {Url}", descriptor.Id, descriptor.DownloadUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.DownloadUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = $"The model server returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    Details = descriptor.DownloadUrl
                };
            }

            var total = response.Content.Headers.ContentLength ?? descriptor.FileSizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var lastReport = 0L;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read <= 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    var now = Environment.TickCount64;
                    if (now - lastReport > 120)
                    {
                        lastReport = now;
                        progress?.Report(new ModelDownloadProgress
                        {
                            ModelId = descriptor.Id,
                            BytesReceived = received,
                            TotalBytes = total,
                            Stage = "Downloading"
                        });
                    }
                }

                progress?.Report(new ModelDownloadProgress
                {
                    ModelId = descriptor.Id,
                    BytesReceived = received,
                    TotalBytes = total,
                    Stage = "Downloading"
                });
            }

            progress?.Report(new ModelDownloadProgress
            {
                ModelId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Verifying"
            });

            var length = new FileInfo(temp).Length;
            if (descriptor.FileSizeBytes > 0 && length != descriptor.FileSizeBytes)
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = "The downloaded model has an unexpected file size. It was not installed.",
                    Details = $"Expected {descriptor.FileSizeBytes:N0} bytes, received {length:N0}."
                };
            }

            var hash = await ComputeSha256Async(temp, cancellationToken);
            if (!string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = "The downloaded model failed its SHA-256 integrity check. It was not installed.",
                    Details = $"Expected {descriptor.Sha256}\nActual   {hash}"
                };
            }

            progress?.Report(new ModelDownloadProgress
            {
                ModelId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Validating"
            });

            var validation = await _validator.ValidateAsync(temp, descriptor, cancellationToken);
            if (!validation.IsValid)
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = $"The downloaded model could not be validated: {validation.Error}",
                    Details = validation.Details
                };
            }

            progress?.Report(new ModelDownloadProgress
            {
                ModelId = descriptor.Id,
                BytesReceived = descriptor.FileSizeBytes,
                TotalBytes = descriptor.FileSizeBytes,
                Stage = "Installing"
            });

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(temp, finalPath);
            temp = null;

            var installed = new InstalledModel
            {
                Descriptor = descriptor,
                FilePath = finalPath,
                ActualSizeBytes = length,
                ActualSha256 = hash,
                InstalledAt = DateTimeOffset.Now,
                IsValid = true,
                VerifiedScaleFactor = validation.ScaleFactor ?? descriptor.ScaleFactor,
                VerifiedInputShape = validation.InputShape,
                VerifiedOutputShape = validation.OutputShape
            };

            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }

            await SaveIndexAsync(cancellationToken);
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Installed model {Model} ({Bytes:N0} bytes)", descriptor.Id, length);
            return new ModelDownloadResult { Success = true, Model = installed };
        }
        catch (OperationCanceledException)
        {
            return new ModelDownloadResult { Success = false, Error = "The model download was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model download failed for {Model}", modelId);
            return new ModelDownloadResult
            {
                Success = false,
                Error = "The model download failed. Check your connection and try again.",
                Details = ex.Message
            };
        }
        finally
        {
            if (temp is not null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            _downloadGate.Release();
        }
    }

    public async Task<ModelDownloadResult> InstallFromFileAsync(string filePath, string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return new ModelDownloadResult { Success = false, Error = "The selected model file does not exist." };

        var descriptor = modelId is null ? ModelCatalog.FindByFileName(filePath) : ModelCatalog.Find(modelId);
        if (descriptor is null)
        {
            return new ModelDownloadResult
            {
                Success = false,
                Error = "This file does not match a known model. Choose the model it represents, or download it from the model catalogue."
            };
        }

        if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
        {
            return new ModelDownloadResult
            {
                Success = false,
                Error = "NCNN runtime packages are installed from the built-in download source. Use Download in the model library."
            };
        }

        await _downloadGate.WaitAsync(cancellationToken);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_modelDirectory);
            temp = Path.Combine(_modelDirectory, descriptor.FileName + ".import");
            if (File.Exists(temp)) File.Delete(temp);
            File.Copy(filePath, temp, overwrite: true);

            var length = new FileInfo(temp).Length;
            if (descriptor.FileSizeBytes > 0 && length != descriptor.FileSizeBytes)
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = "The selected file does not match the expected size for this model.",
                    Details = $"Expected {descriptor.FileSizeBytes:N0} bytes, found {length:N0}."
                };
            }

            var hash = await ComputeSha256Async(temp, cancellationToken);
            if (!string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    Error = "The selected file failed the SHA-256 integrity check for this model.",
                    Details = $"Expected {descriptor.Sha256}\nActual   {hash}"
                };
            }

            var validation = await _validator.ValidateAsync(temp, descriptor, cancellationToken);
            if (!validation.IsValid)
                return new ModelDownloadResult { Success = false, Error = $"The model could not be loaded: {validation.Error}", Details = validation.Details };

            var finalPath = Path.Combine(_modelDirectory, descriptor.FileName);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(temp, finalPath);
            temp = null;

            var installed = new InstalledModel
            {
                Descriptor = descriptor,
                FilePath = finalPath,
                ActualSizeBytes = length,
                ActualSha256 = hash,
                InstalledAt = DateTimeOffset.Now,
                IsValid = true,
                VerifiedScaleFactor = validation.ScaleFactor ?? descriptor.ScaleFactor,
                VerifiedInputShape = validation.InputShape,
                VerifiedOutputShape = validation.OutputShape
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }

            await SaveIndexAsync(cancellationToken);
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return new ModelDownloadResult { Success = true, Model = installed };
        }
        catch (OperationCanceledException)
        {
            return new ModelDownloadResult { Success = false, Error = "The model import was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model import failed for {Path}", filePath);
            return new ModelDownloadResult { Success = false, Error = "The model file could not be installed.", Details = ex.Message };
        }
        finally
        {
            if (temp is not null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            _downloadGate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = ModelCatalog.Find(modelId);
        if (descriptor is null) return false;
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan && descriptor.RuntimeId is { } runtimeId)
        {
            var removed = await _ncnn.RemoveAsync(runtimeId, cancellationToken);
            if (removed) await ReloadAsync(cancellationToken);
            return removed;
        }

        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_modelDirectory, descriptor.FileName);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete model file {Path}", path);
                    return false;
                }
            }

            lock (_lock)
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
            await SaveIndexAsync(cancellationToken);
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    public async Task<InstalledModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = ModelCatalog.Find(modelId);
        if (descriptor is null) return null;
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan && descriptor.RuntimeId is { } runtimeId)
        {
            var runtime = await _ncnn.ValidateAsync(runtimeId, cancellationToken);
            await ReloadAsync(cancellationToken);
            if (runtime is null || !runtime.IsValid) return null;
            return BuildNcnnInstalled(descriptor, runtime);
        }

        var path = Path.Combine(_modelDirectory, descriptor.FileName);
        if (!File.Exists(path)) return null;

        var length = new FileInfo(path).Length;
        var hash = await ComputeSha256Async(path, cancellationToken);
        var validation = await _validator.ValidateAsync(path, descriptor, cancellationToken);
        var installed = new InstalledModel
        {
            Descriptor = descriptor,
            FilePath = path,
            ActualSizeBytes = length,
            ActualSha256 = hash,
            InstalledAt = GetInstalled(modelId)?.InstalledAt ?? DateTimeOffset.Now,
            IsValid = validation.IsValid &&
                      string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) &&
                      (descriptor.FileSizeBytes <= 0 || length == descriptor.FileSizeBytes),
            ValidationError = validation.IsValid
                ? null
                : validation.Error,
            VerifiedScaleFactor = validation.ScaleFactor,
            VerifiedInputShape = validation.InputShape,
            VerifiedOutputShape = validation.OutputShape
        };

        lock (_lock)
        {
            _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
            _installed.Add(installed);
        }

        await SaveIndexAsync(cancellationToken);
        ModelsChanged?.Invoke(this, EventArgs.Empty);
        return installed;
    }

    public Task<string?> ResolveModelPathAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var installed = GetInstalled(modelId);
        if (installed is { IsValid: true } && File.Exists(installed.FilePath))
            return Task.FromResult<string?>(installed.FilePath);
        return Task.FromResult<string?>(null);
    }

    public async Task SetModelDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Choose a model folder.", nameof(directory));
        var full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        await _settings.UpdateAsync(s => s.Upscaler.ModelDirectory = full, cancellationToken);
        lock (_lock) _modelDirectory = full;
        await ReloadAsync(cancellationToken);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_modelDirectory);
            var index = await LoadIndexAsync(cancellationToken);
            var installed = new List<InstalledModel>();
            foreach (var descriptor in ModelCatalog.All)
            {
                if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
                {
                    if (descriptor.RuntimeId is not { } ncnnRuntimeId) continue;
                    var runtime = _ncnn.GetInstalled(ncnnRuntimeId);
                    if (runtime is null) continue;
                    var modelDirectory = Path.Combine(runtime.InstallDirectory,
                        (descriptor.ModelDirectoryName ?? "").Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(modelDirectory)) continue;
                    installed.Add(BuildNcnnInstalled(descriptor, runtime));
                    continue;
                }

                var file = Path.Combine(_modelDirectory, descriptor.FileName);
                if (!File.Exists(file)) continue;
                var length = new FileInfo(file).Length;
                var prior = index.FirstOrDefault(m =>
                    string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                var sizeMatches = descriptor.FileSizeBytes <= 0 || length == descriptor.FileSizeBytes;
                installed.Add(new InstalledModel
                {
                    Descriptor = descriptor,
                    FilePath = file,
                    ActualSizeBytes = length,
                    ActualSha256 = prior?.ActualSha256 ?? "",
                    InstalledAt = prior?.InstalledAt ?? DateTimeOffset.Now,
                    IsValid = sizeMatches && (prior?.IsValid ?? true),
                    ValidationError = sizeMatches ? prior?.ValidationError : "The installed file size does not match this model. Install it again.",
                    VerifiedScaleFactor = prior?.VerifiedScaleFactor,
                    VerifiedInputShape = prior?.VerifiedInputShape,
                    VerifiedOutputShape = prior?.VerifiedOutputShape
                });
            }

            var indexChanged = installed.Count != index.Count ||
                               installed.Any(m => index.All(i =>
                                   !string.Equals(i.Descriptor.Id, m.Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
                                   i.ActualSizeBytes != m.ActualSizeBytes ||
                                   i.IsValid != m.IsValid));
            lock (_lock)
            {
                _installed.Clear();
                _installed.AddRange(installed);
            }

            if (indexChanged) await SaveIndexAsync(cancellationToken);
            ModelsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<List<InstalledModel>> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_modelDirectory, "installed-models.json");
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var list = JsonSerializer.Deserialize<List<InstalledModel>>(json, IndexJson) ?? [];
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the installed model index");
            return [];
        }
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_modelDirectory, "installed-models.json");
            var temp = path + ".tmp";
            List<InstalledModel> snapshot;
            lock (_lock) snapshot = _installed.ToList();
            var json = JsonSerializer.Serialize(snapshot, IndexJson);
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the installed model index");
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<ModelDownloadResult> DownloadNcnnAsync(ModelDescriptor descriptor,
        IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (descriptor.RuntimeId is not { } runtimeId)
            return new ModelDownloadResult { Success = false, Error = "The NCNN runtime identifier is missing." };

        var runtimeProgress = progress is null
            ? null
            : new Progress<NcnnRuntimeDownloadProgress>(update => progress.Report(new ModelDownloadProgress
            {
                ModelId = descriptor.Id,
                BytesReceived = update.BytesReceived,
                TotalBytes = update.TotalBytes,
                Stage = update.Stage
            }));

        var result = await _ncnn.DownloadAsync(runtimeId, runtimeProgress, cancellationToken);
        if (!result.Success)
        {
            return new ModelDownloadResult
            {
                Success = false,
                Error = result.Error ?? "The NCNN runtime could not be installed.",
                Details = result.Details
            };
        }

        await ReloadAsync(cancellationToken);
        var installed = GetInstalled(descriptor.Id);
        return installed is null
            ? new ModelDownloadResult { Success = false, Error = "The NCNN runtime was installed but its model files were not found." }
            : new ModelDownloadResult { Success = true, Model = installed };
    }

    private static InstalledModel BuildNcnnInstalled(ModelDescriptor descriptor, InstalledNcnnRuntime runtime) =>
        new()
        {
            Descriptor = descriptor,
            FilePath = runtime.ExecutablePath,
            ActualSizeBytes = runtime.SizeBytes,
            ActualSha256 = runtime.ActualSha256,
            InstalledAt = runtime.InstalledAt,
            IsValid = runtime.IsValid,
            ValidationError = runtime.ValidationError,
            VerifiedScaleFactor = descriptor.ScaleFactor,
            VerifiedInputShape = "NCNN/Vulkan package",
            VerifiedOutputShape = "NCNN/Vulkan package"
        };

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
