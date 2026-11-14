using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

public sealed class OcrModelManager : IOcrModelManager
{
    private readonly IAppPaths _paths;
    private readonly ILlamaCppRuntimeManager _llamaRuntime;
    private readonly ILogger<OcrModelManager> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly object _initializationLock = new();
    private readonly List<InstalledOcrModel> _installed = [];
    private Task? _initializationTask;
    private bool _initialized;

    public OcrModelManager(IAppPaths paths, ILlamaCppRuntimeManager llamaRuntime,
        ILogger<OcrModelManager> logger)
    {
        _paths = paths;
        _llamaRuntime = llamaRuntime;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PixelForge/1.1 (+local desktop OCR)");
    }

    public string ModelDirectory => Path.Combine(_paths.RootDirectory, "models", "ocr");
    public IReadOnlyList<OcrModelDescriptor> AvailableModels => OcrModelCatalog.All;

    public IReadOnlyList<InstalledOcrModel> InstalledModels
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
            return _initializationTask ??= InitializeCoreAsync();
        }
    }

    private Task InitializeCoreAsync()
    {
        try
        {
            var models = new List<InstalledOcrModel>();
            foreach (var descriptor in OcrModelCatalog.All)
            {
                var directory = Path.Combine(ModelDirectory, descriptor.Id);
                if (descriptor.Backend == OcrBackendKind.LlamaCpp)
                {
                    if (descriptor.MainModel is null || descriptor.MmprojModel is null) continue;
                    var main = Path.Combine(directory, descriptor.MainModel.FileName);
                    var mmproj = Path.Combine(directory, descriptor.MmprojModel.FileName);
                    if (!File.Exists(main) || !File.Exists(mmproj)) continue;
                    var valid = new FileInfo(main).Length == descriptor.MainModel.SizeBytes &&
                                new FileInfo(mmproj).Length == descriptor.MmprojModel.SizeBytes;
                    models.Add(new InstalledOcrModel
                    {
                        Descriptor = descriptor,
                        DirectoryPath = directory,
                        ActualBytes = new FileInfo(main).Length + new FileInfo(mmproj).Length,
                        InstalledAt = Directory.GetCreationTimeUtc(directory),
                        IsValid = valid,
                        ValidationError = valid ? null : "The installed GGUF files do not match the expected sizes. Download the model again."
                    });
                    continue;
                }

                var detector = Path.Combine(directory, descriptor.DetectorFileName);
                var recognizer = Path.Combine(directory, descriptor.RecognizerFileName);
                var dictionary = Path.Combine(directory, descriptor.DictionaryFileName);
                if (!File.Exists(detector) || !File.Exists(recognizer) || !File.Exists(dictionary)) continue;

                var ppValid = new FileInfo(detector).Length == descriptor.Detector.SizeBytes &&
                              new FileInfo(recognizer).Length == descriptor.Recognizer.SizeBytes;
                models.Add(new InstalledOcrModel
                {
                    Descriptor = descriptor,
                    DirectoryPath = directory,
                    ActualBytes = new FileInfo(detector).Length + new FileInfo(recognizer).Length + new FileInfo(dictionary).Length,
                    InstalledAt = Directory.GetCreationTimeUtc(directory),
                    IsValid = ppValid,
                    ValidationError = ppValid ? null : "The installed OCR files do not match the expected sizes. Download the model again."
                });
            }

            lock (_lock)
            {
                _installed.Clear();
                _installed.AddRange(models);
            }

            _initialized = true;
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        finally
        {
            lock (_initializationLock) _initializationTask = null;
        }
    }

    public InstalledOcrModel? GetInstalled(string modelId)
    {
        lock (_lock)
            return _installed.FirstOrDefault(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<OcrModelDownloadResult> DownloadAsync(string modelId,
        IProgress<OcrModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var descriptor = OcrModelCatalog.Find(modelId);
        if (descriptor is null) return new OcrModelDownloadResult { Success = false, Error = "Unknown OCR model." };
        var existing = GetInstalled(modelId);
        if (existing is { IsValid: true })
            return new OcrModelDownloadResult { Success = true, Model = existing };
        if (descriptor.Backend == OcrBackendKind.LlamaCpp)
            return await DownloadLlamaCppAsync(descriptor, progress, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        var tempFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(ModelDirectory);
            var directory = Path.Combine(ModelDirectory, descriptor.Id);
            var staging = Path.Combine(ModelDirectory, $".{descriptor.Id}.download");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var total = descriptor.TotalSizeBytes;
            long completed = 0;
            foreach (var file in new[] { descriptor.Detector, descriptor.Recognizer })
            {
                var temp = Path.Combine(staging, file.FileName);
                tempFiles.Add(temp);
                var fileProgress = new Progress<long>(received => progress?.Report(new OcrModelDownloadProgress
                {
                    ModelId = descriptor.Id,
                    Stage = $"Downloading {file.FileName}",
                    BytesReceived = completed + received,
                    TotalBytes = total
                }));
                var result = await DownloadFileAsync(file, temp, progress is null ? null : fileProgress, cancellationToken);
                if (!result.Success)
                    return new OcrModelDownloadResult { Success = false, Error = result.Error, Details = result.Details };
                completed += file.SizeBytes;
                progress?.Report(new OcrModelDownloadProgress
                {
                    ModelId = descriptor.Id,
                    Stage = $"Downloaded {file.FileName}",
                    BytesReceived = completed,
                    TotalBytes = total
                });
                await VerifyFileAsync(temp, file, cancellationToken);
            }

            var dictionarySource = FindDictionarySource(descriptor.DictionaryFileName);
            if (dictionarySource is null)
                return new OcrModelDownloadResult
                {
                    Success = false,
                    Error = "The OCR dictionary resource is missing from the PixelForge installation.",
                    Details = descriptor.DictionaryFileName
                };
            File.Copy(dictionarySource, Path.Combine(staging, descriptor.DictionaryFileName), overwrite: true);

            progress?.Report(new OcrModelDownloadProgress
            {
                ModelId = descriptor.Id,
                Stage = "Validating ONNX models",
                BytesReceived = total,
                TotalBytes = total
            });
            ValidateOnnxFiles(staging, descriptor);

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.Move(staging, directory);
            tempFiles.Clear();

            var installed = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = new FileInfo(Path.Combine(directory, descriptor.DetectorFileName)).Length +
                              new FileInfo(Path.Combine(directory, descriptor.RecognizerFileName)).Length +
                              new FileInfo(Path.Combine(directory, descriptor.DictionaryFileName)).Length,
                InstalledAt = DateTimeOffset.Now,
                IsValid = true
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }

            ModelsChanged?.Invoke(this, EventArgs.Empty);
            progress?.Report(new OcrModelDownloadProgress
            {
                ModelId = descriptor.Id,
                Stage = "Installed",
                BytesReceived = total,
                TotalBytes = total
            });
            _logger.LogInformation("Installed OCR model {Model} ({Bytes:N0} bytes)", descriptor.Id, installed.ActualBytes);
            return new OcrModelDownloadResult { Success = true, Model = installed };
        }
        catch (OperationCanceledException)
        {
            return new OcrModelDownloadResult { Success = false, Error = "The OCR model download was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR model download failed for {Model}", modelId);
            return new OcrModelDownloadResult
            {
                Success = false,
                Error = "The OCR model download failed. Check your connection and try again.",
                Details = ex.Message
            };
        }
        finally
        {
            foreach (var temp in tempFiles)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            try
            {
                var staging = Path.Combine(ModelDirectory, $".{descriptor.Id}.download");
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch { }
            _gate.Release();
        }
    }

    public async Task<InstalledOcrModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var descriptor = OcrModelCatalog.Find(modelId);
        if (descriptor is null) return null;
        var directory = Path.Combine(ModelDirectory, descriptor.Id);
        if (!Directory.Exists(directory)) return null;
        if (descriptor.Backend == OcrBackendKind.LlamaCpp)
            return await ValidateLlamaCppAsync(descriptor, directory, cancellationToken);

        try
        {
            await VerifyFileAsync(Path.Combine(directory, descriptor.DetectorFileName), descriptor.Detector, cancellationToken);
            await VerifyFileAsync(Path.Combine(directory, descriptor.RecognizerFileName), descriptor.Recognizer, cancellationToken);
            ValidateOnnxFiles(directory, descriptor);
            var model = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = new FileInfo(Path.Combine(directory, descriptor.DetectorFileName)).Length +
                              new FileInfo(Path.Combine(directory, descriptor.RecognizerFileName)).Length +
                              new FileInfo(Path.Combine(directory, descriptor.DictionaryFileName)).Length,
                InstalledAt = Directory.GetCreationTimeUtc(directory),
                IsValid = true
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
                _installed.Add(model);
            }

            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR model validation failed for {Model}", modelId);
            var invalid = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = 0,
                InstalledAt = Directory.GetCreationTimeUtc(directory),
                IsValid = false,
                ValidationError = ex.Message
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
                _installed.Add(invalid);
            }

            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return invalid;
        }
    }

    public async Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = OcrModelCatalog.Find(modelId);
        if (descriptor is null) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(ModelDirectory, descriptor.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            lock (_lock) _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, modelId, StringComparison.OrdinalIgnoreCase));
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InstalledOcrModel?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var model = GetInstalled(modelId);
        if (model is not { IsValid: true }) return null;
        if (model.Descriptor.Backend == OcrBackendKind.LlamaCpp)
            return File.Exists(model.MainModelPath) && File.Exists(model.MmprojPath) ? model : null;
        return File.Exists(model.DetectorPath) && File.Exists(model.RecognizerPath) && File.Exists(model.DictionaryPath)
            ? model
            : null;
    }

    private async Task<OcrModelDownloadResult> DownloadLlamaCppAsync(OcrModelDescriptor descriptor,
        IProgress<OcrModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (descriptor.MainModel is null || descriptor.MmprojModel is null)
            return new OcrModelDownloadResult { Success = false, Error = "The GGUF model file manifest is incomplete." };

        await _gate.WaitAsync(cancellationToken);
        var tempFiles = new List<string>();
        try
        {
            var runtimeId = descriptor.RuntimeId ?? "llama-cpp-b12110-vulkan";
            if (_llamaRuntime.GetInstalled(runtimeId) is null)
            {
                progress?.Report(new OcrModelDownloadProgress
                {
                    ModelId = descriptor.Id,
                    Stage = "Installing llama.cpp runtime",
                    BytesReceived = 0,
                    TotalBytes = 1
                });
                var runtimeProgress = progress is null
                    ? null
                    : new Progress<LlamaCppDownloadProgress>(update => progress.Report(new OcrModelDownloadProgress
                    {
                        ModelId = descriptor.Id,
                        Stage = "Installing llama.cpp runtime · " + update.Stage,
                        BytesReceived = update.BytesReceived,
                        TotalBytes = update.TotalBytes
                    }));
                var runtimeResult = await _llamaRuntime.DownloadAsync(runtimeId, runtimeProgress, cancellationToken);
                if (!runtimeResult.Success)
                    return new OcrModelDownloadResult
                    {
                        Success = false,
                        Error = "The llama.cpp runtime could not be installed.",
                        Details = runtimeResult.Error + "\n" + runtimeResult.Details
                    };
            }

            Directory.CreateDirectory(ModelDirectory);
            var directory = Path.Combine(ModelDirectory, descriptor.Id);
            var staging = Path.Combine(ModelDirectory, $".{descriptor.Id}.download");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var total = descriptor.TotalSizeBytes;
            long completed = 0;
            foreach (var file in new[] { descriptor.MainModel, descriptor.MmprojModel })
            {
                var temp = Path.Combine(staging, file.FileName);
                tempFiles.Add(temp);
                var fileProgress = new Progress<long>(received => progress?.Report(new OcrModelDownloadProgress
                {
                    ModelId = descriptor.Id,
                    Stage = $"Downloading {file.FileName}",
                    BytesReceived = completed + received,
                    TotalBytes = total
                }));
                var result = await DownloadFileAsync(file, temp, progress is null ? null : fileProgress, cancellationToken);
                if (!result.Success)
                    return new OcrModelDownloadResult { Success = false, Error = result.Error, Details = result.Details };
                completed += file.SizeBytes;
                await VerifyFileAsync(temp, file, cancellationToken);
            }

            EnsureGguf(staging, descriptor.MainModel.FileName);
            EnsureGguf(staging, descriptor.MmprojModel.FileName);

            progress?.Report(new OcrModelDownloadProgress
            {
                ModelId = descriptor.Id,
                Stage = "Installing",
                BytesReceived = total,
                TotalBytes = total
            });
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.Move(staging, directory);
            tempFiles.Clear();

            var installed = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = new FileInfo(Path.Combine(directory, descriptor.MainModel.FileName)).Length +
                              new FileInfo(Path.Combine(directory, descriptor.MmprojModel.FileName)).Length,
                InstalledAt = DateTimeOffset.Now,
                IsValid = true
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(installed);
            }
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return new OcrModelDownloadResult { Success = true, Model = installed };
        }
        catch (OperationCanceledException)
        {
            return new OcrModelDownloadResult { Success = false, Error = "The OCR model download was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advanced OCR model download failed for {Model}", descriptor.Id);
            return new OcrModelDownloadResult
            {
                Success = false,
                Error = "The advanced OCR model download failed. Check your connection and try again.",
                Details = ex.Message
            };
        }
        finally
        {
            foreach (var temp in tempFiles)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            try
            {
                var staging = Path.Combine(ModelDirectory, $".{descriptor.Id}.download");
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch { }
            _gate.Release();
        }
    }

    private async Task<InstalledOcrModel?> ValidateLlamaCppAsync(OcrModelDescriptor descriptor, string directory,
        CancellationToken cancellationToken)
    {
        if (descriptor.MainModel is null || descriptor.MmprojModel is null)
            return null;
        if (_llamaRuntime.GetInstalled(descriptor.RuntimeId ?? "llama-cpp-b12110-vulkan") is null)
        {
            var missingRuntime = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = 0,
                InstalledAt = Directory.GetCreationTimeUtc(directory),
                IsValid = false,
                ValidationError = "The llama.cpp runtime is not installed. Install the OCR model again to prepare it."
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(missingRuntime);
            }
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return missingRuntime;
        }
        try
        {
            var main = Path.Combine(directory, descriptor.MainModel.FileName);
            var mmproj = Path.Combine(directory, descriptor.MmprojModel.FileName);
            await VerifyFileAsync(main, descriptor.MainModel, cancellationToken);
            await VerifyFileAsync(mmproj, descriptor.MmprojModel, cancellationToken);
            EnsureGguf(directory, descriptor.MainModel.FileName);
            EnsureGguf(directory, descriptor.MmprojModel.FileName);
            var model = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = new FileInfo(main).Length + new FileInfo(mmproj).Length,
                InstalledAt = Directory.GetCreationTimeUtc(directory),
                IsValid = true
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(model);
            }
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advanced OCR model validation failed for {Model}", descriptor.Id);
            var invalid = new InstalledOcrModel
            {
                Descriptor = descriptor,
                DirectoryPath = directory,
                ActualBytes = 0,
                InstalledAt = Directory.GetCreationTimeUtc(directory),
                IsValid = false,
                ValidationError = ex.Message
            };
            lock (_lock)
            {
                _installed.RemoveAll(m => string.Equals(m.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
                _installed.Add(invalid);
            }
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return invalid;
        }
    }

    private static void EnsureGguf(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) != 4 || magic[0] != (byte)'G' || magic[1] != (byte)'G' ||
            magic[2] != (byte)'U' || magic[3] != (byte)'F')
            throw new InvalidDataException($"The file {fileName} is not a GGUF model.");
    }

    private async Task<(bool Success, string? Error, string? Details)> DownloadFileAsync(OcrModelFile file,
        string destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, $"The OCR model server returned {(int)response.StatusCode} {response.ReasonPhrase}.", file.DownloadUrl);

        var expected = response.Content.Headers.ContentLength ?? file.SizeBytes;
        if (expected > 0 && expected != file.SizeBytes)
            return (false, "The OCR model server returned an unexpected size.", file.DownloadUrl);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, useAsync: true);
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
                progress?.Report(received);
            }
        }

        progress?.Report(received);
        if (received != file.SizeBytes)
            return (false, "The OCR model download was incomplete.",
                $"Expected {file.SizeBytes:N0} bytes, received {received:N0}.");
        return (true, null, null);
    }

    private static async Task VerifyFileAsync(string path, OcrModelFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The OCR model file is missing.", path);
        if (info.Length != file.SizeBytes)
            throw new InvalidDataException($"The OCR model file has an unexpected size: {info.Length:N0} bytes.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            useAsync: true);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
        if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The OCR model file failed its SHA-256 integrity check.");
    }

    private static void ValidateOnnxFiles(string directory, OcrModelDescriptor descriptor)
    {
        // ONNX Runtime is linked by the OCR engine. The engine performs the full
        // graph check when it opens the models; here we guard against truncated
        // files before exposing the installation as valid.
        foreach (var file in new[] { descriptor.DetectorFileName, descriptor.RecognizerFileName })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"The OCR model file {file} is missing.");
        }
    }

    private static string? FindDictionarySource(string fileName)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ocr", fileName),
            Path.Combine(AppContext.BaseDirectory, "Assets", "OCR", fileName)
        };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "src", "PixelForge.Engines.OCR", "Resources", fileName));
            candidates.Add(Path.Combine(current.FullName, "third_party", "ocr", fileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
