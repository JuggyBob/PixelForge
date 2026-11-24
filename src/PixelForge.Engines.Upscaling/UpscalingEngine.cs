using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.Upscaling;

public sealed class UpscalingEngine : IProcessingEngine, IModelValidator, IFrameUpscaler, IDisposable
{
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<UpscalingEngine> _logger;
    private readonly RifeInterpolator _rife;
    private readonly INcnnRuntimeManager _ncnn;
    private readonly IProcessRunner _processRunner;
    private readonly ConcurrentDictionary<SessionKey, SessionHolder> _sessions = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly Timer _sessionCleanup;
    private readonly string? _aiRuntimeError;
    private readonly bool _cpuAvailable;
    private readonly bool _directMlAvailable;
    private readonly string _onnxRuntimeVersion;
    private static bool _magickInitialized;
    private static readonly object MagickInitLock = new();

    public UpscalingEngine(IAppPaths paths, ISettingsService settings, ILogger<UpscalingEngine> logger,
        RifeInterpolator rife, INcnnRuntimeManager ncnn, IProcessRunner processRunner)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _rife = rife;
        _ncnn = ncnn;
        _processRunner = processRunner;
        EnsureMagickInitialized();

        _onnxRuntimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            _cpuAvailable = providers.Contains("CPUExecutionProvider", StringComparer.OrdinalIgnoreCase);
            _directMlAvailable = providers.Contains("DmlExecutionProvider", StringComparer.OrdinalIgnoreCase);
            if (!_cpuAvailable && !_directMlAvailable)
                _aiRuntimeError = "ONNX Runtime reported no execution providers.";
        }
        catch (Exception ex)
        {
            _aiRuntimeError = ex.Message;
            _logger.LogError(ex, "ONNX Runtime could not be initialized");
        }

        _sessionCleanup = new Timer(_ => CleanupIdleSessions(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public string Id => EngineIds.Upscaling;
    public string DisplayName => "Upscaling engine";
    public MediaCategory[] Categories => [MediaCategory.Image];

    private static void EnsureMagickInitialized()
    {
        if (_magickInitialized) return;
        lock (MagickInitLock)
        {
            if (_magickInitialized) return;
            ResourceLimits.Memory = 1024UL * 1024 * 1024;
            ResourceLimits.Disk = 8192UL * 1024 * 1024;
            ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 8);
            ResourceLimits.Width = 32768;
            ResourceLimits.Height = 32768;
            _magickInitialized = true;
        }
    }

    public async Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureMagickInitialized();
            await _ncnn.InitializeAsync(cancellationToken);
            var formats = new List<MediaFormat>();
            foreach (var definition in FormatCatalog.ImageFormats)
            {
                var info = MagickNET.SupportedFormats.FirstOrDefault(f =>
                    string.Equals(f.Format.ToString(), definition.TechnicalId, StringComparison.OrdinalIgnoreCase));
                var canRead = info?.SupportsReading ?? false;
                var canWrite = (info?.SupportsWriting ?? false) &&
                               definition.Id is not ("svg" or "heic" or "psd");
                formats.Add(definition.ToMediaFormat(Id, canRead, canWrite));
            }

            var providers = new List<ExecutionProviderInfo>
            {
                new()
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = _cpuAvailable,
                    DeviceName = Environment.MachineName,
                    Details = _cpuAvailable ? $"ONNX Runtime {_onnxRuntimeVersion}" : _aiRuntimeError
                },
                new()
                {
                    Kind = ExecutionProviderKind.DirectML,
                    DisplayName = "DirectML (GPU)",
                    IsAvailable = _directMlAvailable,
                    DeviceName = _directMlAvailable ? "DirectML adapter 0" : null,
                    Details = _directMlAvailable
                        ? "AMD, NVIDIA or Intel GPU through DirectML"
                        : _aiRuntimeError ?? "DirectML is not available in this build or driver."
                },
                new()
                {
                    Kind = ExecutionProviderKind.Vulkan,
                    DisplayName = "Vulkan (NCNN)",
                    IsAvailable = _ncnn.InstalledRuntimes.Any(r => r.IsValid),
                    DeviceName = _ncnn.InstalledRuntimes.FirstOrDefault(r => r.IsValid)?.Descriptor.Name,
                    Details = _ncnn.InstalledRuntimes.Any(r => r.IsValid)
                        ? "Waifu2x and Real-CUGAN through the installed NCNN/Vulkan runtime; CPU fallback is available."
                        : "Install a Waifu2x or Real-CUGAN runtime to enable Vulkan inference."
                }
            };

            return new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                Version = $"ImageMagick {MagickNET.ImageMagickVersion} · ONNX Runtime {_onnxRuntimeVersion}",
                IsAvailable = true,
                Formats = formats,
                ExecutionProviders = providers,
                Algorithms =
                [
                    new() { Id = "nearest", DisplayName = "Nearest Neighbor", Description = "Fastest; preserves hard pixel edges and is ideal for pixel art." },
                    new() { Id = "bilinear", DisplayName = "Bilinear", Description = "Smooth and fast; can soften fine detail." },
                    new() { Id = "bicubic", DisplayName = "Bicubic", Description = "Balanced smoothing and sharpness for photographs." },
                    new() { Id = "lanczos", DisplayName = "Lanczos", Description = "High-quality sharpening with minimal ringing; a strong general default." },
                    new() { Id = "mitchell", DisplayName = "Mitchell-Netravali", Description = "Soft, natural results with reduced halos and ringing." }
                ]
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upscaling engine probe failed");
            return new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = ex.Message
            };
        }
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var collection = new MagickImageCollection();
                collection.Ping(path);
                if (collection.Count == 0) return null;
                var first = collection[0];
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var formatId = extension switch
                {
                    ".ico" => "ico",
                    ".psd" or ".psb" => "psd",
                    _ => UpscaleFormatMap.ToId(first.Format)
                };
                if (formatId is null || !FormatCatalog.ImageFormats.Any(f => string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase)))
                {
                    return (MediaInfo?)new MediaInfo
                    {
                        FilePath = Path.GetFullPath(path),
                        FileName = Path.GetFileName(path),
                        Extension = extension,
                        FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                        Category = MediaCategory.Image,
                        Error = $"The file is not a supported image format ({first.Format}).",
                        EngineId = Id
                    };
                }

                var definition = FormatCatalog.FindById(formatId);
                var primary = collection.Count > 1 && formatId is "ico" or "psd"
                    ? collection.OrderByDescending(f => (long)f.Width * f.Height).First()
                    : first;
                var orientation = primary.Orientation;
                var width = (int)primary.Width;
                var height = (int)primary.Height;
                if (orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom)
                    (width, height) = (height, width);
                var isAnimatedFormat = definition?.SupportsAnimation == true && formatId is not ("ico" or "psd");
                var isMultipageFormat = definition?.SupportsMultipage == true;
                return (MediaInfo?)new MediaInfo
                {
                    FilePath = Path.GetFullPath(path),
                    FileName = Path.GetFileName(path),
                    Extension = extension,
                    FileSizeBytes = new FileInfo(path).Length,
                    Category = MediaCategory.Image,
                    FormatId = formatId,
                    FormatName = definition?.DisplayName ?? first.Format.ToString(),
                    Width = width,
                    Height = height,
                    HasAlpha = collection.Any(f => f.HasAlpha),
                    IsAnimated = isAnimatedFormat && collection.Count > 1,
                    IsMultipage = isMultipageFormat && collection.Count > 1,
                    IsVector = definition?.IsVector ?? first.Format == MagickFormat.Svg,
                    FrameCount = collection.Count,
                    RotationDegrees = orientation switch
                    {
                        Orientation.RightTop => 90,
                        Orientation.BottomRight => 180,
                        Orientation.LeftBottom => 270,
                        _ => null
                    },
                    ColorSpace = primary.ColorSpace.ToString(),
                    EngineId = Id,
                    Tags = new Dictionary<string, string>
                    {
                        ["HasIccProfile"] = primary.HasProfile("icc") ? "true" : "false",
                        ["HasExif"] = primary.GetExifProfile() is not null ? "true" : "false"
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect image {Path}", path);
                return (MediaInfo?)new MediaInfo
                {
                    FilePath = Path.GetFullPath(path),
                    FileName = Path.GetFileName(path),
                    Extension = Path.GetExtension(path).ToLowerInvariant(),
                    FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                    Category = MediaCategory.Image,
                    Error = ex.Message.Split('\n')[0],
                    EngineId = Id
                };
            }
        }, cancellationToken);
    }

    public Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Settings is not UpscaleSettings settings)
            return Task.FromResult(ValidationResult.Failure("upscale.settings", "The upscaling settings are invalid."));
        if (request.Operation is not (OperationKind.UpscaleAi or OperationKind.UpscaleTraditional))
            return Task.FromResult(ValidationResult.Failure("upscale.operation", "The upscaling engine only supports upscaling operations."));

        var format = UpscaleFormatMap.ToMagick(request.OutputFormatId);
        if (format is null)
            return Task.FromResult(ValidationResult.Failure("upscale.format", "The selected output image format is not available."));
        var validationIssues = new List<ValidationIssue>();

        if (settings.Method == UpscaleMethod.Ai)
        {
            var descriptor = ModelCatalog.Find(settings.ModelId);
            if (descriptor is null)
                return Task.FromResult(ValidationResult.Failure("upscale.model", "Choose an AI model."));
            if (descriptor.Task is not (ModelTask.ImageUpscale or ModelTask.ImageRestore))
                return Task.FromResult(ValidationResult.Failure("upscale.model.task",
                    "This model is for frame interpolation, not image reconstruction."));

            if (descriptor.CpuOnly && settings.ExecutionProvider != ExecutionProviderKind.Cpu)
                validationIssues.Add(ValidationIssue.Warning("upscale.provider.cpuonly",
                    "This model is CPU-only after a verified DirectML crash; the CPU path will be used."));
            if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
            {
                if (descriptor.RuntimeId is not { } runtimeId || _ncnn.GetInstalled(runtimeId) is null)
                    return Task.FromResult(ValidationResult.Failure("upscale.model.runtime",
                        "The selected NCNN runtime is not installed."));
                var modelDirectory = Path.Combine(_ncnn.GetInstalled(runtimeId)!.InstallDirectory,
                    (descriptor.ModelDirectoryName ?? "").Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(modelDirectory))
                    return Task.FromResult(ValidationResult.Failure("upscale.model.missing",
                        "The selected NCNN model directory is missing."));
                if (descriptor.SupportedScales.Count > 0 && !descriptor.SupportedScales.Contains(settings.ScaleFactor) &&
                    settings.ScaleFactor != 3)
                    return Task.FromResult(ValidationResult.Failure("upscale.scale",
                        $"This model supports {string.Join(", ", descriptor.SupportedScales)}× output."));
                if (settings.NoiseLevel is { } noise && descriptor.SupportedNoiseLevels.Count > 0 &&
                    !descriptor.SupportedNoiseLevels.Contains(noise))
                    return Task.FromResult(ValidationResult.Failure("upscale.noise",
                        $"Noise level {noise} is not available for this model."));

                if (settings.ExecutionProvider == ExecutionProviderKind.DirectML)
                    validationIssues.Add(ValidationIssue.Warning("upscale.provider.ncnn",
                        "NCNN uses Vulkan instead of DirectML; the GPU will still be used."));
                return Task.FromResult(ValidationResult.Success(validationIssues.ToArray()));
            }

            if (!_cpuAvailable && !_directMlAvailable)
                return Task.FromResult(ValidationResult.Failure("upscale.runtime", _aiRuntimeError ?? "The AI runtime is unavailable."));
            var modelPath = ResolveModelPath(descriptor);
            if (modelPath is null)
                return Task.FromResult(ValidationResult.Failure("upscale.model.missing", "The selected AI model is not installed."));
            if (settings.ExecutionProvider == ExecutionProviderKind.DirectML && !_directMlAvailable)
                return Task.FromResult(ValidationResult.Failure("upscale.provider", "DirectML is not available."));
            if (settings.ExecutionProvider == ExecutionProviderKind.Vulkan)
                validationIssues.Add(ValidationIssue.Warning("upscale.provider.vulkan",
                    "Vulkan is used by NCNN models; this ONNX model will use DirectML/CPU instead."));
        }

        if (request.InputInfo is { IsReadable: false })
            return Task.FromResult(ValidationResult.Failure("upscale.input", request.InputInfo.Error ?? "The input image cannot be read."));

        return Task.FromResult(ValidationResult.Success(validationIssues.ToArray()));
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (request.Settings is not UpscaleSettings settings)
            return ProcessingResult.Fail("The upscaling settings are invalid.");
        try
        {
            return settings.Method == UpscaleMethod.Ai
                ? await ProcessAiAsync(request, settings, progress, cancellationToken)
                : await ProcessTraditionalAsync(request, settings, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upscaling failed for {Input}", request.InputPath);
            return ProcessingResult.Fail(FriendlyError(ex), ex.ToString());
        }
    }

    private async Task<ProcessingResult> ProcessTraditionalAsync(ProcessingRequest request, UpscaleSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await Task.Run<ProcessingResult>(() =>
        {
            progress.Report(ProcessingProgress.Percent(0.05, "Reading image…"));
            using var collection = new MagickImageCollection();
            collection.Ping(request.InputPath);
            if (collection.Count > 1)
                return ProcessingResult.Fail("Animated or multi-page images are not supported by the upscaler.");
            collection.Clear();
            using var image = new MagickImage(request.InputPath);
            image.AutoOrient();

            var (targetWidth, targetHeight) = CalculateTargetDimensions((int)image.Width, (int)image.Height, settings);
            if (targetWidth <= 0 || targetHeight <= 0)
                return ProcessingResult.Fail("The target dimensions are invalid.");
            if ((long)targetWidth * targetHeight > 120_000_000)
                return ProcessingResult.Fail("The target image exceeds the safe output limit.");

            progress.Report(ProcessingProgress.Percent(0.35, "Resampling…"));
            cancellationToken.ThrowIfCancellationRequested();
            image.Resize((uint)targetWidth, (uint)targetHeight, MapFilter(settings.Algorithm));
            ApplyMetadataPolicy(image, settings);
            var format = FormatCatalog.FindById(request.OutputFormatId);
            if (format is null) return ProcessingResult.Fail("The output format is unknown.");
            if (!format.SupportsAlpha || !settings.PreserveAlpha)
            {
                image.BackgroundColor = new MagickColor(settings.BackgroundColor);
                image.Alpha(AlphaAction.Remove);
            }

            ApplyOutputSettings(image, format, settings);
            progress.Report(ProcessingProgress.Percent(0.75, "Encoding image…"));
            cancellationToken.ThrowIfCancellationRequested();
            var magickFormat = UpscaleFormatMap.ToMagick(request.OutputFormatId)
                ?? throw new InvalidOperationException("The output format cannot be encoded.");
            image.Write(request.TempOutputPath, magickFormat);
            stopwatch.Stop();
            var bytes = new FileInfo(request.TempOutputPath).Length;
            if (bytes == 0) return ProcessingResult.Fail("The image encoder produced no output.");
            return ProcessingResult.Ok(request.TempOutputPath, null, bytes, stopwatch.Elapsed,
                warnings: image.Depth > 8 ? ["The output was reduced to 8 bits per channel."] : [])
                with { RequestedProvider = "CPU", ActualProvider = "CPU" };
        }, cancellationToken);

        if (!result.Success) return result;
        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        progress.Report(ProcessingProgress.Percent(1, "Done"));
        return result with { OutputInfo = verification.ActualInfo, Verification = verification };
    }

    private async Task<ProcessingResult> ProcessAiAsync(ProcessingRequest request, UpscaleSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var descriptor = ModelCatalog.Find(settings.ModelId);
        if (descriptor is null) return ProcessingResult.Fail("The selected AI model is unknown.");
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
            return await ProcessNcnnAsync(request, settings, descriptor, progress, cancellationToken);

        if (!_cpuAvailable && !_directMlAvailable)
            return ProcessingResult.Fail(_aiRuntimeError ?? "The AI runtime is unavailable. Traditional upscaling is still available.");

        var modelPath = ResolveModelPath(descriptor);
        if (modelPath is null)
            return ProcessingResult.Fail("The selected AI model is not installed. Open Model Management to install it.");

        var stopwatch = Stopwatch.StartNew();
        progress.Report(ProcessingProgress.Percent(0.01, "Reading image…"));
        using var source = new MagickImage(request.InputPath);
        source.AutoOrient();
        if (source.Width * source.Height > 40_000_000)
            return ProcessingResult.Fail("The source image is too large for AI upscaling. Resize it or use traditional upscaling first.");
        var hadHighDepth = source.Depth > 8;
        if (hadHighDepth)
        {
            // Real-ESRGAN operates on 8-bit RGB; keep the source for metadata but reduce the inference copy.
            source.Depth = 8;
        }
        source.ColorSpace = ColorSpace.sRGB;
        var width = (int)source.Width;
        var height = (int)source.Height;
        var (targetWidth, targetHeight) = CalculateTargetDimensions(width, height, settings);
        if ((long)targetWidth * targetHeight > 120_000_000)
            return ProcessingResult.Fail("The output image would exceed the 120 megapixel safety limit.");

        source.Depth = 8;
        var sourceBgra = source.ToByteArray(MagickFormat.Bgra);
        if (sourceBgra.Length != width * height * 4)
            return ProcessingResult.Fail("The image could not be prepared for inference.");
        var hasAlpha = source.HasAlpha;
        var alphaIsMeaningful = hasAlpha && !source.IsOpaque;
        var sourceAlpha = alphaIsMeaningful ? ExtractAlpha(sourceBgra, width, height) : null;
        var warnings = new List<string>();
        if (hadHighDepth)
            warnings.Add("The AI model processes 8-bit RGB, so the source precision was reduced to 8 bits per channel.");
        if (alphaIsMeaningful)
            warnings.Add("The alpha channel is preserved, but AI models process RGB only; colour bleed near transparent edges is possible.");
        if (descriptor.FixedInputWidth > 0)
            return await ProcessFixedTileAsync(request, settings, descriptor, source, sourceBgra, sourceAlpha,
                width, height, targetWidth, targetHeight, hasAlpha, hadHighDepth, warnings, progress, cancellationToken);

        var attempts = GetProviderAttempts(settings.ExecutionProvider);
        var errors = new List<string>();
        AiTiledResult? aiResult = null;
        ExecutionProviderKind actualProvider = ExecutionProviderKind.Cpu;
        var tileSize = 0;

        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var provider in attempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tileSize = GetTileSize(provider, settings);
                var overlap = GetOverlap(provider, settings, tileSize);
                try
                {
                    progress.Report(ProcessingProgress.Percent(0.04, $"Loading model · {ProviderName(provider)}…"));
                    var holder = await GetSessionAsync(modelPath, provider, cancellationToken);
                    holder.BeginUse();
                    try
                    {
                        aiResult = await Task.Run(() => RunTiledInference(holder, sourceBgra, width, height, descriptor.ScaleFactor,
                            tileSize, overlap, progress, cancellationToken), cancellationToken);
                        actualProvider = provider;
                        break;
                    }
                    finally
                    {
                        holder.EndUse();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI inference with {Provider} failed; attempting fallback if available", provider);
                    errors.Add($"{ProviderName(provider)}: {ex.Message}");
                    RemoveSession(modelPath, provider);
                    if (provider != ExecutionProviderKind.Cpu)
                    {
                        warnings.Add($"{ProviderName(provider)} failed: {ex.Message}. The image was processed on the CPU instead.");
                        progress.Report(ProcessingProgress.Percent(0.04, "GPU inference failed; falling back to CPU…"));
                    }
                }
            }
        }
        finally
        {
            _inferenceGate.Release();
        }

        if (aiResult is null)
            return ProcessingResult.Fail("AI inference failed on every available execution provider. " +
                                         string.Join(" | ", errors), string.Join(Environment.NewLine, errors));

        progress.Report(ProcessingProgress.Percent(0.92, "Encoding image…"));
        if (hasAlpha && sourceAlpha is not null)
        {
            var alpha = ResizeAlpha(sourceAlpha, width, height, aiResult.Width, aiResult.Height);
            for (var i = 0; i < alpha.Length; i++) aiResult.Bgra[i * 4 + 3] = alpha[i];
        }

        using var output = CreateOutputImage(aiResult, hasAlpha);
        if (aiResult.Width != targetWidth || aiResult.Height != targetHeight)
        {
            output.Resize((uint)targetWidth, (uint)targetHeight, FilterType.Lanczos);
        }

        var format = FormatCatalog.FindById(request.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output format is unknown.");
        CopyProfiles(source, output, settings);
        if (!format.SupportsAlpha || !settings.PreserveAlpha)
        {
            output.BackgroundColor = new MagickColor(settings.BackgroundColor);
            output.Alpha(AlphaAction.Remove);
        }

        ApplyOutputSettings(output, format, settings);
        var magickFormat = UpscaleFormatMap.ToMagick(request.OutputFormatId)
            ?? throw new InvalidOperationException("The output format cannot be encoded.");
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(request.TempOutputPath, magickFormat);
        stopwatch.Stop();
        var outputBytes = new FileInfo(request.TempOutputPath).Length;
        if (outputBytes == 0) return ProcessingResult.Fail("The image encoder produced no output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        warnings.Add($"AI inference executed on {ProviderName(actualProvider)} with a {tileSize}px tile.");
        var finalVerification = verification with
        {
            Summary = $"{verification.Summary} · {ProviderName(actualProvider)}",
            Issues = verification.Issues.Concat(warnings.Select(w => ValidationIssue.Info("upscale.info", w))).ToList()
        };
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo, outputBytes, stopwatch.Elapsed,
            finalVerification, warnings)
            with
            {
                RequestedProvider = settings.ExecutionProvider.ToString(),
                ActualProvider = ProviderName(actualProvider),
                FallbackReason = settings.ExecutionProvider != ExecutionProviderKind.Auto &&
                                 actualProvider != settings.ExecutionProvider
                    ? $"Requested {settings.ExecutionProvider}; inference ran on {ProviderName(actualProvider)}."
                    : null,
                ModelId = settings.ModelId,
                RuntimeVersion = "ONNX Runtime"
            };
    }

    private async Task<ProcessingResult> ProcessFixedTileAsync(ProcessingRequest request, UpscaleSettings settings,
        ModelDescriptor descriptor, MagickImage source, byte[] sourceBgra, byte[]? sourceAlpha,
        int width, int height, int targetWidth, int targetHeight, bool hasAlpha, bool hadHighDepth,
        List<string> warnings, IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var modelPath = ResolveModelPath(descriptor);
        if (modelPath is null) return ProcessingResult.Fail("The selected AI model is not installed.");

        var stopwatch = Stopwatch.StartNew();
        var attempts = descriptor.CpuOnly
            ? new[] { ExecutionProviderKind.Cpu }
            : GetProviderAttempts(settings.ExecutionProvider);
        if (descriptor.CpuOnly && settings.ExecutionProvider != ExecutionProviderKind.Cpu)
            warnings.Add("This model is verified on CPU only. DirectML inference was disabled after a native ONNX Runtime crash; the job ran on the CPU.");
        var errors = new List<string>();
        AiTiledResult? result = null;
        var actualProvider = ExecutionProviderKind.Cpu;
        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var provider in attempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress.Report(ProcessingProgress.Percent(0.04, $"Loading {descriptor.Name} · {ProviderName(provider)}…"));
                    var holder = await GetSessionAsync(modelPath, provider, cancellationToken);
                    holder.BeginUse();
                    try
                    {
                        result = await Task.Run(() => RunFixedTileInference(holder, sourceBgra, width, height,
                            descriptor, progress, cancellationToken), cancellationToken);
                        actualProvider = provider;
                        break;
                    }
                    finally
                    {
                        holder.EndUse();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fixed-tile inference failed with {Provider}", provider);
                    errors.Add($"{ProviderName(provider)}: {ex.Message}");
                    RemoveSession(modelPath, provider);
                    if (provider != ExecutionProviderKind.Cpu)
                        warnings.Add($"{ProviderName(provider)} failed: {ex.Message}. The image was processed on the CPU instead.");
                }
            }
        }
        finally
        {
            _inferenceGate.Release();
        }

        if (result is null)
            return ProcessingResult.Fail($"{descriptor.Name} failed on every available execution provider. " +
                                         string.Join(" | ", errors), string.Join(Environment.NewLine, errors));

        progress.Report(ProcessingProgress.Percent(0.92, "Encoding image…"));
        if (hasAlpha && sourceAlpha is not null)
        {
            var alpha = ResizeAlpha(sourceAlpha, width, height, result.Width, result.Height);
            for (var i = 0; i < alpha.Length; i++) result.Bgra[i * 4 + 3] = alpha[i];
        }

        using var output = CreateOutputImage(result, hasAlpha);
        if (result.Width != targetWidth || result.Height != targetHeight)
            output.Resize((uint)targetWidth, (uint)targetHeight, FilterType.Lanczos);

        var format = FormatCatalog.FindById(request.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output format is unknown.");
        CopyProfiles(source, output, settings);
        if (!format.SupportsAlpha || !settings.PreserveAlpha)
        {
            output.BackgroundColor = new MagickColor(settings.BackgroundColor);
            output.Alpha(AlphaAction.Remove);
        }

        ApplyOutputSettings(output, format, settings);
        var magickFormat = UpscaleFormatMap.ToMagick(request.OutputFormatId)
            ?? throw new InvalidOperationException("The output format cannot be encoded.");
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(request.TempOutputPath, magickFormat);
        stopwatch.Stop();
        var outputBytes = new FileInfo(request.TempOutputPath).Length;
        if (outputBytes == 0) return ProcessingResult.Fail("The image encoder produced no output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        if (hadHighDepth)
            warnings.Add("SwinIR processes 8-bit RGB, so the source precision was reduced to 8 bits per channel.");
        warnings.Add($"{descriptor.Name} executed on {ProviderName(actualProvider)} with fixed " +
                     $"{descriptor.FixedInputWidth}×{descriptor.FixedInputHeight} tiles.");
        var finalVerification = verification with
        {
            Summary = $"{verification.Summary} · {ProviderName(actualProvider)}",
            Issues = verification.Issues.Concat(warnings.Select(w => ValidationIssue.Info("upscale.fixedtile", w))).ToList()
        };
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo, outputBytes, stopwatch.Elapsed,
            finalVerification, warnings)
            with
            {
                RequestedProvider = settings.ExecutionProvider.ToString(),
                ActualProvider = ProviderName(actualProvider),
                FallbackReason = settings.ExecutionProvider != ExecutionProviderKind.Auto &&
                                 actualProvider != settings.ExecutionProvider
                    ? $"Requested {settings.ExecutionProvider}; inference ran on {ProviderName(actualProvider)}."
                    : null,
                ModelId = settings.ModelId,
                RuntimeVersion = "ONNX Runtime"
            };
    }

    private static AiTiledResult RunFixedTileInference(SessionHolder holder, byte[] sourceBgra,
        int width, int height, ModelDescriptor descriptor, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken)
    {
        var tile = descriptor.FixedInputWidth;
        var overlap = Math.Clamp(descriptor.FixedTileOverlap, 0, Math.Max(0, tile / 2 - 1));
        var core = Math.Max(1, tile - overlap * 2);
        var scale = descriptor.ScaleFactor;
        var outputWidth = checked(width * scale);
        var outputHeight = checked(height * scale);
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        var tilesX = (width + core - 1) / core;
        var tilesY = (height + core - 1) / core;
        var total = tilesX * tilesY;
        var completed = 0;

        for (var coreY = 0; coreY < height; coreY += core)
        {
            for (var coreX = 0; coreX < width; coreX += core)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var coreW = Math.Min(core, width - coreX);
                var coreH = Math.Min(core, height - coreY);
                var input = new float[3 * tile * tile];
                for (var y = 0; y < tile; y++)
                {
                    var sourceY = Math.Clamp(coreY - overlap + y, 0, height - 1);
                    for (var x = 0; x < tile; x++)
                    {
                        var sourceX = Math.Clamp(coreX - overlap + x, 0, width - 1);
                        var sourceIndex = (sourceY * width + sourceX) * 4;
                        var targetIndex = y * tile + x;
                        var plane = tile * tile;
                        input[targetIndex] = sourceBgra[sourceIndex + 2] / 255f;
                        input[plane + targetIndex] = sourceBgra[sourceIndex + 1] / 255f;
                        input[plane * 2 + targetIndex] = sourceBgra[sourceIndex] / 255f;
                    }
                }

                var tensor = new DenseTensor<float>(input, [1, 3, tile, tile]);
                using var results = holder.Session.Run(
                    [NamedOnnxValue.CreateFromTensor(holder.InputName, tensor)], [holder.OutputName]);
                var outputTensor = results.First().AsTensor<float>();
                var dims = outputTensor.Dimensions.ToArray();
                if (dims.Length != 4 || dims[2] < tile * scale || dims[3] < tile * scale)
                    throw new InvalidOperationException(
                        $"The fixed-tile model returned an unexpected shape: {string.Join("×", dims)}.");

                var offsetX = overlap * scale;
                var offsetY = overlap * scale;
                for (var y = 0; y < coreH * scale; y++)
                {
                    var destinationY = coreY * scale + y;
                    for (var x = 0; x < coreW * scale; x++)
                    {
                        var destinationX = coreX * scale + x;
                        var destination = (destinationY * outputWidth + destinationX) * 4;
                        output[destination] = (byte)Math.Clamp(outputTensor[0, 2, offsetY + y, offsetX + x] * 255f, 0, 255);
                        output[destination + 1] = (byte)Math.Clamp(outputTensor[0, 1, offsetY + y, offsetX + x] * 255f, 0, 255);
                        output[destination + 2] = (byte)Math.Clamp(outputTensor[0, 0, offsetY + y, offsetX + x] * 255f, 0, 255);
                        output[destination + 3] = 255;
                    }
                }

                completed++;
                progress.Report(ProcessingProgress.Percent(0.1 + 0.75 * completed / Math.Max(1, total),
                    $"Fixed-tile inference {completed} of {total}…"));
            }
        }

        return new AiTiledResult(output, outputWidth, outputHeight);
    }

    private async Task<ProcessingResult> ProcessNcnnAsync(ProcessingRequest request, UpscaleSettings settings,
        ModelDescriptor descriptor, IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        if (descriptor.RuntimeId is not { } runtimeId)
            return ProcessingResult.Fail("The NCNN runtime identifier is missing.");
        var runtime = _ncnn.GetInstalled(runtimeId);
        if (runtime is null)
            return ProcessingResult.Fail("The selected NCNN runtime is not installed. Open AI Models to install it.");
        var modelDirectory = Path.Combine(runtime.InstallDirectory,
            (descriptor.ModelDirectoryName ?? "").Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(modelDirectory))
            return ProcessingResult.Fail("The selected NCNN model directory is missing. Reinstall the model runtime.");

        var stopwatch = Stopwatch.StartNew();
        var tempInput = Path.Combine(_paths.TempDirectory, $"ncnn-{Guid.NewGuid():N}-input.png");
        var tempOutput = Path.Combine(_paths.TempDirectory, $"ncnn-{Guid.NewGuid():N}-output.png");
        try
        {
            progress.Report(ProcessingProgress.Percent(0.01, "Reading image…"));
            using var source = new MagickImage(request.InputPath);
            source.AutoOrient();
            if (source.Width * source.Height > 40_000_000)
                return ProcessingResult.Fail("The source image is too large for AI upscaling. Resize it or use traditional upscaling first.");
            var hadHighDepth = source.Depth > 8;
            source.Depth = 8;
            source.ColorSpace = ColorSpace.sRGB;
            var width = (int)source.Width;
            var height = (int)source.Height;
            var (targetWidth, targetHeight) = CalculateTargetDimensions(width, height, settings);
            if ((long)targetWidth * targetHeight > 120_000_000)
                return ProcessingResult.Fail("The output image would exceed the 120 megapixel safety limit.");

            var hasAlpha = source.HasAlpha && !source.IsOpaque;
            using (var input = new MagickImage(source))
            {
                input.Write(tempInput, hasAlpha ? MagickFormat.Png32 : MagickFormat.Png24);
            }

            var requestedScale = settings.ScaleFactor <= 0 ? descriptor.ScaleFactor : settings.ScaleFactor;
            var cliScale = descriptor.SupportedScales.Count == 0 || descriptor.SupportedScales.Contains(requestedScale)
                ? requestedScale
                : descriptor.ScaleFactor;
            var noise = settings.NoiseLevel ?? descriptor.DefaultNoiseLevel;
            if (descriptor.SupportedNoiseLevels.Count > 0 && !descriptor.SupportedNoiseLevels.Contains(noise))
                noise = descriptor.DefaultNoiseLevel;
            var useCpu = settings.ExecutionProvider == ExecutionProviderKind.Cpu;
            var allowCpuFallback = settings.ExecutionProvider != ExecutionProviderKind.Vulkan;
            var tile = Math.Clamp(settings.TileSize, 0, 2048);
            progress.Report(ProcessingProgress.Percent(0.08, $"Starting {descriptor.Name}…"));

            var run = await RunNcnnProcessAsync(runtime, modelDirectory, tempInput, tempOutput,
                cliScale, noise, useCpu ? -1 : 0, tile, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Result.WasCancelled) throw new OperationCanceledException(cancellationToken);
            var warnings = new List<string>();
            if (!run.Result.Succeeded && !useCpu && allowCpuFallback)
            {
                warnings.Add("GPU inference failed; the NCNN runtime was retried on the CPU.");
                progress.Report(ProcessingProgress.Percent(0.18, "GPU unavailable; retrying on CPU…"));
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                run = await RunNcnnProcessAsync(runtime, modelDirectory, tempInput, tempOutput,
                    cliScale, noise, -1, tile, progress, cancellationToken);
                useCpu = true;
            }

            if (!run.Result.Succeeded || !File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
            {
                var details = string.Join("\n", run.Result.StandardErrorLines.TakeLast(12));
                return ProcessingResult.Fail($"{descriptor.Name} did not produce an output image.", details);
            }

            progress.Report(ProcessingProgress.Percent(0.86, "Encoding image…"));
            using var ncnnOutput = new MagickImage(tempOutput);
            var expectedWidth = checked(width * cliScale);
            var expectedHeight = checked(height * cliScale);
            if (ncnnOutput.Width != expectedWidth || ncnnOutput.Height != expectedHeight)
            {
                return ProcessingResult.Fail(
                    $"{descriptor.Name} returned {ncnnOutput.Width}×{ncnnOutput.Height}; expected {expectedWidth}×{expectedHeight}.");
            }

            var aiResult = new AiTiledResult(ncnnOutput.ToByteArray(MagickFormat.Bgra),
                (int)ncnnOutput.Width, (int)ncnnOutput.Height);
            if (hasAlpha)
            {
                var sourceAlpha = ExtractAlpha(source.ToByteArray(MagickFormat.Bgra), width, height);
                var alpha = ResizeAlpha(sourceAlpha, width, height, aiResult.Width, aiResult.Height);
                for (var i = 0; i < alpha.Length; i++) aiResult.Bgra[i * 4 + 3] = alpha[i];
            }

            using var output = CreateOutputImage(aiResult, hasAlpha);
            if (output.Width != targetWidth || output.Height != targetHeight)
                output.Resize((uint)targetWidth, (uint)targetHeight, FilterType.Lanczos);

            var format = FormatCatalog.FindById(request.OutputFormatId);
            if (format is null) return ProcessingResult.Fail("The output format is unknown.");
            CopyProfiles(source, output, settings);
            if (!format.SupportsAlpha || !settings.PreserveAlpha)
            {
                output.BackgroundColor = new MagickColor(settings.BackgroundColor);
                output.Alpha(AlphaAction.Remove);
            }

            ApplyOutputSettings(output, format, settings);
            var magickFormat = UpscaleFormatMap.ToMagick(request.OutputFormatId)
                ?? throw new InvalidOperationException("The output format cannot be encoded.");
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(request.TempOutputPath, magickFormat);
            stopwatch.Stop();
            var outputBytes = new FileInfo(request.TempOutputPath).Length;
            if (outputBytes == 0) return ProcessingResult.Fail("The image encoder produced no output.");

            var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
            if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
            if (hadHighDepth)
                warnings.Add("The NCNN model processes 8-bit RGB, so the source precision was reduced to 8 bits per channel.");
            if (settings.ExecutionProvider == ExecutionProviderKind.DirectML)
                warnings.Add("NCNN uses Vulkan rather than DirectML; the selected GPU path was used through Vulkan.");
            if (requestedScale > 0 && cliScale != requestedScale)
                warnings.Add($"The model ran at its native {cliScale}× scale and the result was resized to {requestedScale}×.");
            var providerName = useCpu ? "CPU" : run.Device;
            warnings.Add($"{descriptor.Name} executed through NCNN/Vulkan on {providerName}.");
            var finalVerification = verification with
            {
                Summary = $"{verification.Summary} · NCNN/{providerName}",
                Issues = verification.Issues.Concat(warnings.Select(w => ValidationIssue.Info("upscale.ncnn", w))).ToList()
            };
            return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo, outputBytes, stopwatch.Elapsed,
                finalVerification, warnings)
                with
                {
                    RequestedProvider = settings.ExecutionProvider.ToString(),
                    ActualProvider = "NCNN/" + providerName,
                    FallbackReason = settings.ExecutionProvider == ExecutionProviderKind.DirectML
                        ? "NCNN does not support DirectML; the GPU path ran through Vulkan."
                        : null,
                    ModelId = settings.ModelId,
                    RuntimeVersion = runtime.Descriptor.Version
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NCNN upscaling failed for {Input}", request.InputPath);
            return ProcessingResult.Fail(FriendlyError(ex), ex.ToString());
        }
        finally
        {
            try { if (File.Exists(tempInput)) File.Delete(tempInput); } catch { }
            try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
        }
    }

    private async Task<(ProcessResult Result, string Device)> RunNcnnProcessAsync(
        InstalledNcnnRuntime runtime, string modelDirectory, string inputPath, string outputPath,
        int scale, int noise, int gpuId, int tile, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-i", inputPath,
            "-o", outputPath,
            "-n", noise.ToString(CultureInfo.InvariantCulture),
            "-s", scale.ToString(CultureInfo.InvariantCulture),
            "-m", modelDirectory,
            "-g", gpuId.ToString(CultureInfo.InvariantCulture),
            "-f", "png",
            "-v"
        };
        if (tile >= 32)
        {
            args.Add("-t");
            args.Add(tile.ToString(CultureInfo.InvariantCulture));
        }

        string? device = null;
        var result = await _processRunner.RunStreamingAsync(new ProcessRequest
        {
            FileName = runtime.ExecutablePath,
            Arguments = args,
            WorkingDirectory = runtime.InstallDirectory,
            Timeout = TimeSpan.FromHours(1),
            MaxCapturedLines = 300
        }, null, line =>
        {
            if (device is null)
            {
                var match = Regex.Match(line, @"^\[(\d+)\s+([^\]]+)\]");
                if (match.Success) device = match.Groups[2].Value.Trim();
            }
            var percent = Regex.Match(line, @"(\d{1,3})\s*%");
            if (percent.Success && int.TryParse(percent.Groups[1].Value, out var value))
                progress.Report(ProcessingProgress.Percent(0.18 + Math.Clamp(value, 0, 100) / 100.0 * 0.65,
                    "NCNN processing…"));
        }, cancellationToken);

        return (result, device ?? (gpuId < 0 ? "CPU" : "Vulkan GPU"));
    }

    public async Task<FrameUpscaleResult> UpscaleFrameAsync(byte[] rgb24, int width, int height,
        string modelId, ExecutionProviderKind provider, int tileSize, int tileOverlap,
        CancellationToken cancellationToken = default)
    {
        if (!_cpuAvailable && !_directMlAvailable)
            throw new InvalidOperationException(_aiRuntimeError ?? "The AI runtime is unavailable.");
        if (rgb24.Length != width * height * 3)
            throw new ArgumentException("The frame buffer does not match the supplied dimensions.", nameof(rgb24));
        var descriptor = ModelCatalog.Find(modelId)
                         ?? throw new InvalidOperationException("The selected AI model is unknown.");
        if (descriptor.Task != ModelTask.ImageUpscale)
            throw new InvalidOperationException("The selected model is not an image upscaling model.");
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
            throw new NotSupportedException("NCNN image models are available in the Image Upscaler. Video frame upscaling currently uses ONNX models.");
        if (descriptor.FixedInputWidth > 0)
            throw new NotSupportedException("Fixed-tile restoration models are available in the Image Upscaler; video frame upscaling uses dynamic ONNX models.");
        var modelPath = ResolveModelPath(descriptor)
                        ?? throw new InvalidOperationException("The selected AI model is not installed.");
        var outputWidth = checked(width * descriptor.ScaleFactor);
        var outputHeight = checked(height * descriptor.ScaleFactor);
        if ((long)outputWidth * outputHeight > 120_000_000)
            throw new InvalidOperationException("The upscaled frame would exceed the 120 megapixel safety limit.");

        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4] = rgb24[i * 3 + 2];
            bgra[i * 4 + 1] = rgb24[i * 3 + 1];
            bgra[i * 4 + 2] = rgb24[i * 3];
            bgra[i * 4 + 3] = 255;
        }

        var attempts = GetProviderAttempts(provider);
        var errors = new List<string>();
        string? warning = null;
        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveSettings = new UpscaleSettings
            {
                Method = UpscaleMethod.Ai,
                ModelId = modelId,
                ScaleFactor = descriptor.ScaleFactor,
                TileSize = tileSize,
                TileOverlap = tileOverlap
            };
            var effectiveTile = GetTileSize(attempt, effectiveSettings);
            var overlap = GetOverlap(attempt, effectiveSettings, effectiveTile);
            try
            {
                var holder = await GetSessionAsync(modelPath, attempt, cancellationToken);
                holder.BeginUse();
                try
                {
                    await _inferenceGate.WaitAsync(cancellationToken);
                    try
                    {
                        var result = await Task.Run(() => RunTiledInference(holder, bgra, width, height,
                            descriptor.ScaleFactor, effectiveTile, overlap, new Progress<ProcessingProgress>(),
                            cancellationToken), cancellationToken);
                        var output = new byte[result.Width * result.Height * 3];
                        for (var i = 0; i < result.Width * result.Height; i++)
                        {
                            output[i * 3] = result.Bgra[i * 4 + 2];
                            output[i * 3 + 1] = result.Bgra[i * 4 + 1];
                            output[i * 3 + 2] = result.Bgra[i * 4];
                        }

                        return new FrameUpscaleResult
                        {
                            Rgb24 = output,
                            Width = result.Width,
                            Height = result.Height,
                            Provider = attempt,
                            Warning = warning
                        };
                    }
                    finally
                    {
                        _inferenceGate.Release();
                    }
                }
                finally
                {
                    holder.EndUse();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frame upscaling with {Provider} failed", attempt);
                errors.Add($"{ProviderName(attempt)}: {ex.Message}");
                RemoveSession(modelPath, attempt);
                if (attempt != ExecutionProviderKind.Cpu)
                    warning = $"{ProviderName(attempt)} failed; video frames fell back to CPU inference.";
            }
        }

        throw new InvalidOperationException("Frame upscaling failed on every available execution provider. " +
                                            string.Join(" | ", errors));
    }

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 32)
                return VerificationResult.Fail("The output image file is empty.");
            using var image = new MagickImage(outputPath);
            var formatId = UpscaleFormatMap.ToId(image.Format);
            if (!string.Equals(formatId, request.OutputFormatId, StringComparison.OrdinalIgnoreCase))
                return VerificationResult.Fail($"The output format is {formatId ?? image.Format.ToString()}, expected {request.OutputFormatId}.");

            if (request.Settings is UpscaleSettings settings && request.InputInfo is { Width: > 0, Height: > 0 } input)
            {
                var (expectedWidth, expectedHeight) = CalculateTargetDimensions(input.Width.Value, input.Height.Value, settings);
                if ((int)image.Width != expectedWidth || (int)image.Height != expectedHeight)
                    return VerificationResult.Fail(
                        $"The output is {image.Width} × {image.Height}, expected {expectedWidth} × {expectedHeight}.");
                if (input.HasAlpha && settings.PreserveAlpha && !image.HasAlpha)
                    return VerificationResult.Fail("The source had transparency, but the output has no alpha channel.");
            }

            var info = new MediaInfo
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                Extension = Path.GetExtension(outputPath).ToLowerInvariant(),
                FileSizeBytes = new FileInfo(outputPath).Length,
                Category = MediaCategory.Image,
                FormatId = formatId,
                FormatName = FormatCatalog.FindById(formatId)?.DisplayName ?? image.Format.ToString(),
                Width = (int)image.Width,
                Height = (int)image.Height,
                HasAlpha = image.HasAlpha,
                FrameCount = 1,
                EngineId = Id
            };
            return VerificationResult.Ok(info,
                $"{info.FormatName} · {info.Width} × {info.Height} · {FormatBytes(info.FileSizeBytes)}");
        }
        catch (Exception ex)
        {
            return VerificationResult.Fail("The output image could not be reopened.", details: ex.ToString());
        }
    }

    public async Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var image = new MagickImage(inputPath);
                image.AutoOrient();
                image.Thumbnail((uint)Math.Max(16, maxSize), (uint)Math.Max(16, maxSize));
                image.Quality = 85;
                image.Write(outputPath, MagickFormat.Png);
                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail creation failed for {Path}", inputPath);
                return (string?)null;
            }
        }, cancellationToken);
    }

    public async Task<ModelValidationResult> ValidateAsync(string modelPath, ModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
            return ModelValidationResult.Fail("The model file does not exist.");
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan)
        {
            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(modelPath));
            var modelDirectory = installRoot is null || string.IsNullOrWhiteSpace(descriptor.ModelDirectoryName)
                ? null
                : Path.Combine(installRoot, descriptor.ModelDirectoryName.Replace('/', Path.DirectorySeparatorChar));
            if (modelDirectory is null || !Directory.Exists(modelDirectory))
                return ModelValidationResult.Fail("The NCNN model directory is missing.");
            return ModelValidationResult.Ok(descriptor.ScaleFactor, "NCNN/Vulkan package", "NCNN/Vulkan package");
        }
        var length = new FileInfo(modelPath).Length;
        if (length < 1024)
            return ModelValidationResult.Fail("The model file is too small to be a valid ONNX model.");
        if (!_cpuAvailable && !_directMlAvailable)
            return ModelValidationResult.Fail(_aiRuntimeError ?? "ONNX Runtime is unavailable.");
        if (descriptor.Task == ModelTask.FrameInterpolation)
            return await _rife.ValidateModelAsync(modelPath, descriptor, cancellationToken);

        try
        {
            using var session = CreateSession(modelPath, ExecutionProviderKind.Cpu, out var inputName, out var outputName,
                out var inputDims, out var outputDims);
            var fixedInput = descriptor.FixedInputWidth > 0 && descriptor.FixedInputHeight > 0;
            if (!IsDynamicInput(inputDims) && !fixedInput)
                return ModelValidationResult.Fail("The model requires a fixed input size and no fixed-tile profile is defined.",
                    FormatShape(inputDims));
            if (fixedInput && inputDims.Length == 4 && inputDims[2] > 0 && inputDims[3] > 0 &&
                (inputDims[2] != descriptor.FixedInputWidth || inputDims[3] != descriptor.FixedInputHeight))
                return ModelValidationResult.Fail(
                    $"The model expects {descriptor.FixedInputWidth}×{descriptor.FixedInputHeight}, but the graph reports {FormatShape(inputDims)}.");
            if (inputDims.Length != 4 || (inputDims[1] > 0 && inputDims[1] != 3))
                return ModelValidationResult.Fail("The model does not accept a 4-D NCHW RGB input.", FormatShape(inputDims));
            if (outputDims.Length != 4 || (outputDims[1] > 0 && outputDims[1] != 3))
                return ModelValidationResult.Fail("The model does not produce a 3-channel image output.", FormatShape(outputDims));

            var testSize = fixedInput ? descriptor.FixedInputWidth : Math.Max(16, descriptor.MinimumInputSize);
            if (testSize % 2 != 0) testSize++;
            var data = new float[3 * testSize * testSize];
            Array.Fill(data, 0.5f);
            var tensor = new DenseTensor<float>(data, [1, 3, testSize, testSize]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)], [outputName]);
            var output = results.First().AsTensor<float>();
            var dims = output.Dimensions.ToArray();
            if (dims.Length != 4 || dims[2] != testSize * descriptor.ScaleFactor || dims[3] != testSize * descriptor.ScaleFactor)
            {
                if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0 && dims[2] % testSize == 0 && dims[3] % testSize == 0)
                {
                    var scale = dims[2] / testSize;
                    return ModelValidationResult.Fail(
                        $"The model upscales by {scale}×, but the catalogue expects {descriptor.ScaleFactor}×.",
                        $"Input {FormatShape(inputDims)} → output {FormatShape(dims)}");
                }

                return ModelValidationResult.Fail("The model produced an unexpected output tensor.",
                    $"Input {FormatShape(inputDims)} → output {FormatShape(dims)}");
            }

            return ModelValidationResult.Ok(descriptor.ScaleFactor, FormatShape(inputDims), FormatShape(dims));
        }
        catch (Exception ex)
        {
            return ModelValidationResult.Fail("The ONNX model could not be loaded: " + ex.Message, ex.ToString());
        }
    }

    private async Task<AiTiledResult> RunTiledInference(SessionHolder holder, byte[] sourceBgra, int width, int height,
        int scale, int tileSize, int overlap, IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var outputWidth = width * scale;
        var outputHeight = height * scale;
        var outputLength = checked(outputWidth * outputHeight * 4);
        var outputData = new byte[outputLength];
        for (var i = 3; i < outputLength; i += 4) outputData[i] = 255;

        var tilesX = (width + tileSize - 1) / tileSize;
        var tilesY = (height + tileSize - 1) / tileSize;
        var totalTiles = tilesX * tilesY;
        var completed = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var coreY = 0; coreY < height; coreY += tileSize)
        {
            for (var coreX = 0; coreX < width; coreX += tileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var coreW = Math.Min(tileSize, width - coreX);
                var coreH = Math.Min(tileSize, height - coreY);
                var x0 = Math.Max(0, coreX - overlap);
                var y0 = Math.Max(0, coreY - overlap);
                var x1 = Math.Min(width, coreX + coreW + overlap);
                var y1 = Math.Min(height, coreY + coreH + overlap);
                var tileW = x1 - x0;
                var tileH = y1 - y0;
                var plane = tileW * tileH;
                var count = plane * 3;
                var buffer = ArrayPool<float>.Shared.Rent(count);
                try
                {
                    for (var y = 0; y < tileH; y++)
                    {
                        var sourceRow = (y0 + y) * width * 4;
                        var destRow = y * tileW;
                        for (var x = 0; x < tileW; x++)
                        {
                            var sourceIndex = sourceRow + (x0 + x) * 4;
                            var destIndex = destRow + x;
                            buffer[destIndex] = sourceBgra[sourceIndex + 2] / 255f;
                            buffer[plane + destIndex] = sourceBgra[sourceIndex + 1] / 255f;
                            buffer[2 * plane + destIndex] = sourceBgra[sourceIndex] / 255f;
                        }
                    }

                    var tensor = new DenseTensor<float>(buffer.AsMemory(0, count), [1, 3, tileH, tileW]);
                    using var results = holder.Session.Run([NamedOnnxValue.CreateFromTensor(holder.InputName, tensor)],
                        [holder.OutputName]);
                    var outputTensor = results.First().AsTensor<float>();
                    var dims = outputTensor.Dimensions.ToArray();
                    if (dims.Length != 4 || dims[2] != tileH * scale || dims[3] != tileW * scale)
                        throw new InvalidOperationException(
                            $"The model returned an unexpected tensor shape: {FormatShape(dims)}.");

                    var outW = tileW * scale;
                    var outH = tileH * scale;
                    var outPlane = outW * outH;
                    var dense = outputTensor as DenseTensor<float>;
                    var span = dense is not null ? dense.Buffer.Span : Span<float>.Empty;
                    var coreOffsetX = (coreX - x0) * scale;
                    var coreOffsetY = (coreY - y0) * scale;
                    for (var oy = 0; oy < coreH * scale; oy++)
                    {
                        var outRow = (coreY * scale + oy) * outputWidth;
                        var tileRow = (coreOffsetY + oy) * outW + coreOffsetX;
                        for (var ox = 0; ox < coreW * scale; ox++)
                        {
                            var tileIndex = tileRow + ox;
                            float r, g, b;
                            if (span.Length > 0)
                            {
                                r = span[tileIndex];
                                g = span[outPlane + tileIndex];
                                b = span[2 * outPlane + tileIndex];
                            }
                            else
                            {
                                r = outputTensor[0, 0, coreOffsetY + oy, coreOffsetX + ox];
                                g = outputTensor[0, 1, coreOffsetY + oy, coreOffsetX + ox];
                                b = outputTensor[0, 2, coreOffsetY + oy, coreOffsetX + ox];
                            }

                            var outputIndex = (outRow + coreX * scale + ox) * 4;
                            outputData[outputIndex] = ToByte(b);
                            outputData[outputIndex + 1] = ToByte(g);
                            outputData[outputIndex + 2] = ToByte(r);
                        }
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer);
                }

                completed++;
                var fraction = 0.05 + 0.85 * completed / totalTiles;
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                var eta = completed > 0 ? elapsed / completed * (totalTiles - completed) : 0;
                progress.Report(new ProcessingProgress
                {
                    Fraction = Math.Clamp(fraction, 0, 0.9),
                    Message = $"AI upscale · tile {completed} of {totalTiles}" + (eta > 1 ? $" · ~{FormatEta(eta)} left" : ""),
                    EtaSeconds = eta
                });
            }
        }

        return new AiTiledResult(outputData, outputWidth, outputHeight);
    }

    private MagickImage CreateOutputImage(AiTiledResult result, bool hasAlpha)
    {
        var settings = new PixelReadSettings((uint)result.Width, (uint)result.Height, StorageType.Char, PixelMapping.BGRA);
        var image = new MagickImage(result.Bgra, settings) { Depth = 8 };
        if (!hasAlpha)
        {
            image.HasAlpha = false;
        }

        return image;
    }

    private ExecutionProviderKind[] GetProviderAttempts(ExecutionProviderKind requested)
    {
        return requested switch
        {
            ExecutionProviderKind.Cpu => [ExecutionProviderKind.Cpu],
            ExecutionProviderKind.DirectML when _directMlAvailable =>
                [ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu],
            ExecutionProviderKind.Vulkan when _directMlAvailable =>
                [ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu],
            ExecutionProviderKind.Vulkan => [ExecutionProviderKind.Cpu],
            _ when _directMlAvailable => [ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu],
            _ => [ExecutionProviderKind.Cpu]
        };
    }

    private int GetTileSize(ExecutionProviderKind provider, UpscaleSettings settings)
    {
        var requested = settings.TileSize > 0 ? settings.TileSize : 0;
        var max = provider == ExecutionProviderKind.DirectML ? 1024 : 512;
        var size = requested > 0 ? Math.Clamp(requested, 64, max) : provider == ExecutionProviderKind.DirectML ? 512 : 192;
        return Math.Max(16, size - size % 4);
    }

    private static int GetOverlap(ExecutionProviderKind provider, UpscaleSettings settings, int tileSize)
    {
        var overlap = settings.TileOverlap > 0
            ? settings.TileOverlap
            : provider == ExecutionProviderKind.DirectML ? 32 : 16;
        return Math.Clamp(overlap, 0, Math.Max(0, tileSize / 2 - 1));
    }

    private async Task<SessionHolder> GetSessionAsync(string modelPath, ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        var ticks = File.GetLastWriteTimeUtc(modelPath).Ticks;
        var key = new SessionKey(modelPath, ticks, provider);
        if (_sessions.TryGetValue(key, out var existing))
            return existing;

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out existing)) return existing;
            foreach (var oldKey in _sessions.Keys.Where(k => k.Provider == provider &&
                         string.Equals(k.Path, modelPath, StringComparison.OrdinalIgnoreCase) && k != key).ToList())
            {
                if (_sessions.TryRemove(oldKey, out var old)) old.Dispose();
            }

            var holder = await Task.Run(() =>
            {
                var session = CreateSession(modelPath, provider, out var inputName, out var outputName,
                    out _, out _);
                return new SessionHolder(key, session, inputName, outputName);
            }, cancellationToken);
            _sessions[key] = holder;
            return holder;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private InferenceSession CreateSession(string modelPath, ExecutionProviderKind provider,
        out string inputName, out string outputName, out int[] inputDims, out int[] outputDims)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        if (provider == ExecutionProviderKind.DirectML)
        {
            options.AppendExecutionProvider_DML(0);
            options.EnableMemoryPattern = false;
        }
        else
        {
            options.AppendExecutionProvider_CPU(0);
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            options.InterOpNumThreads = 1;
        }

        try
        {
            var session = new InferenceSession(modelPath, options);
            var input = session.InputMetadata.First();
            var output = session.OutputMetadata.First();
            inputName = input.Key;
            outputName = output.Key;
            inputDims = input.Value.Dimensions.ToArray();
            outputDims = output.Value.Dimensions.ToArray();
            return session;
        }
        finally
        {
            options.Dispose();
        }
    }

    private string? ResolveModelPath(ModelDescriptor descriptor)
    {
        var directory = string.IsNullOrWhiteSpace(_settings.Settings.Upscaler.ModelDirectory)
            ? _paths.DefaultModelDirectory
            : _settings.Settings.Upscaler.ModelDirectory;
        var path = Path.Combine(directory, descriptor.FileName);
        return File.Exists(path) ? path : null;
    }

    private void RemoveSession(string modelPath, ExecutionProviderKind provider)
    {
        foreach (var key in _sessions.Keys.Where(k => k.Provider == provider &&
                     string.Equals(k.Path, modelPath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_sessions.TryRemove(key, out var holder)) holder.Dispose();
        }
    }

    private void CleanupIdleSessions()
    {
        var cutoff = Environment.TickCount64 - (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        foreach (var (key, holder) in _sessions)
        {
            if (holder.IsIdleSince(cutoff) && _sessions.TryRemove(key, out var removed))
            {
                removed.Dispose();
                _logger.LogDebug("Released idle AI model session {Model} {Provider}", key.Path, key.Provider);
            }
        }
    }

    private static void ApplyMetadataPolicy(MagickImage image, UpscaleSettings settings)
    {
        if (!settings.PreserveIccProfile)
        {
            try { image.RemoveProfile("icc"); } catch { }
        }

        if (!settings.PreserveMetadata)
        {
            foreach (var profile in new[] { "exif", "iptc", "8bim", "xmp", "photoshop", "xml" })
            {
                try { image.RemoveProfile(profile); } catch { }
            }
        }
    }

    private static void CopyProfiles(MagickImage source, MagickImage target, UpscaleSettings settings)
    {
        if (settings.PreserveIccProfile)
        {
            try
            {
                var icc = source.GetColorProfile();
                if (icc is not null) target.SetProfile(icc);
            }
            catch { }
        }

        if (settings.PreserveMetadata)
        {
            try
            {
                foreach (var name in source.ProfileNames)
                {
                    if (name is "icc" or "exif" or "8bim") continue;
                    var profile = source.GetProfile(name);
                    if (profile is not null) target.SetProfile(profile);
                }
            }
            catch { }
        }
    }

    private static void ApplyOutputSettings(MagickImage image, FormatDefinition format, UpscaleSettings settings)
    {
        image.Quality = (uint)Math.Clamp(settings.OutputQuality, 1, 100);
        switch (format.Id)
        {
            case "png":
                image.Settings.SetDefine("png:compression-level", "6");
                break;
            case "jpg":
                image.Settings.Interlace = Interlace.Jpeg;
                break;
            case "avif":
                image.Settings.SetDefine("heic:speed", "6");
                break;
            case "tif":
                image.Settings.Compression = CompressionMethod.LZW;
                break;
            case "gif":
                image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                break;
        }
    }

    private static FilterType MapFilter(ResamplingAlgorithm algorithm) => algorithm switch
    {
        ResamplingAlgorithm.NearestNeighbor => FilterType.Point,
        ResamplingAlgorithm.Bilinear => FilterType.Triangle,
        ResamplingAlgorithm.Bicubic => FilterType.Cubic,
        ResamplingAlgorithm.Lanczos => FilterType.Lanczos,
        ResamplingAlgorithm.MitchellNetravali => FilterType.Mitchell,
        _ => FilterType.Lanczos
    };

    private static (int Width, int Height) CalculateTargetDimensions(int sourceWidth, int sourceHeight,
        UpscaleSettings settings)
    {
        if (settings.CustomWidth is > 0 && settings.CustomHeight is > 0)
            return (settings.CustomWidth.Value, settings.CustomHeight.Value);
        if (settings.CustomWidth is > 0)
            return (settings.CustomWidth.Value,
                Math.Max(1, (int)Math.Round(sourceHeight * (settings.CustomWidth.Value / (double)sourceWidth))));
        if (settings.CustomHeight is > 0)
            return (Math.Max(1, (int)Math.Round(sourceWidth * (settings.CustomHeight.Value / (double)sourceHeight))),
                settings.CustomHeight.Value);
        var factor = Math.Clamp(settings.ScaleFactor, 1, 16);
        return (Math.Max(1, sourceWidth * factor), Math.Max(1, sourceHeight * factor));
    }

    private static byte[] ExtractAlpha(byte[] bgra, int width, int height)
    {
        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = bgra[i * 4 + 3];
        return alpha;
    }

    private static byte[] ResizeAlpha(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var target = new byte[targetWidth * targetHeight];
        var scaleX = sourceWidth / (double)targetWidth;
        var scaleY = sourceHeight / (double)targetHeight;
        for (var y = 0; y < targetHeight; y++)
        {
            var sy = Math.Clamp((y + 0.5) * scaleY - 0.5, 0, sourceHeight - 1);
            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(sourceHeight - 1, y0 + 1);
            var fy = sy - y0;
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Clamp((x + 0.5) * scaleX - 0.5, 0, sourceWidth - 1);
                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(sourceWidth - 1, x0 + 1);
                var fx = sx - x0;
                var top = source[y0 * sourceWidth + x0] * (1 - fx) + source[y0 * sourceWidth + x1] * fx;
                var bottom = source[y1 * sourceWidth + x0] * (1 - fx) + source[y1 * sourceWidth + x1] * fx;
                target[y * targetWidth + x] = (byte)Math.Clamp(top * (1 - fy) + bottom * fy, 0, 255);
            }
        }

        return target;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(value * 255f + 0.5f, 0f, 255f);

    private static string FormatEta(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private static bool IsDynamicInput(int[] dims) =>
        dims.Length == 4 && dims[2] <= 0 && dims[3] <= 0;

    private static string FormatShape(IEnumerable<int> dims) => string.Join(" × ", dims.Select(d => d <= 0 ? "?" : d.ToString(CultureInfo.InvariantCulture)));

    private static string ProviderName(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.DirectML => "DirectML (GPU)",
            ExecutionProviderKind.Vulkan => "Vulkan (GPU)",
            _ => "CPU"
        };

    private static string FriendlyError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("OOM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Allocation", StringComparison.OrdinalIgnoreCase))
            return "The AI model ran out of memory. Close other applications or reduce the automatic tile size.";
        if (message.Contains("Dml", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DirectML", StringComparison.OrdinalIgnoreCase))
            return "GPU inference failed. Try Automatic or CPU in the upscaler settings.";
        return "AI upscaling failed: " + message.Split('\n')[0];
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    public void Dispose()
    {
        _sessionCleanup.Dispose();
        foreach (var holder in _sessions.Values) holder.Dispose();
        _sessions.Clear();
        _sessionGate.Dispose();
        _inferenceGate.Dispose();
    }

    private readonly record struct SessionKey(string Path, long Ticks, ExecutionProviderKind Provider);

    private sealed record AiTiledResult(byte[] Bgra, int Width, int Height);

    private sealed class SessionHolder : IDisposable
    {
        private long _lastUsed = Environment.TickCount64;
        private int _useCount;

        public SessionHolder(SessionKey key, InferenceSession session, string inputName, string outputName)
        {
            Key = key;
            Session = session;
            InputName = inputName;
            OutputName = outputName;
        }

        public SessionKey Key { get; }
        public InferenceSession Session { get; }
        public string InputName { get; }
        public string OutputName { get; }

        public void BeginUse()
        {
            Interlocked.Increment(ref _useCount);
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
        }

        public void EndUse()
        {
            Interlocked.Decrement(ref _useCount);
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
        }

        public bool IsIdleSince(long cutoff) =>
            Volatile.Read(ref _useCount) == 0 && Volatile.Read(ref _lastUsed) < cutoff;

        public void Dispose() => Session.Dispose();
    }
}

public static class UpscaleFormatMap
{
    private static readonly Dictionary<string, MagickFormat> ToMagickMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = MagickFormat.Png,
        ["jpg"] = MagickFormat.Jpeg,
        ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif,
        ["tif"] = MagickFormat.Tiff,
        ["bmp"] = MagickFormat.Bmp,
        ["gif"] = MagickFormat.Gif,
        ["ico"] = MagickFormat.Ico,
        ["heic"] = MagickFormat.Heic,
        ["jxl"] = MagickFormat.Jxl,
        ["tga"] = MagickFormat.Tga,
        ["pnm"] = MagickFormat.Pnm,
        ["svg"] = MagickFormat.Svg,
        ["psd"] = MagickFormat.Psd,
        ["exr"] = MagickFormat.Exr,
        ["hdr"] = MagickFormat.Hdr,
        ["qoi"] = MagickFormat.Qoi,
        ["dds"] = MagickFormat.Dds
    };

    private static readonly Dictionary<MagickFormat, string> FromMagickMap =
        ToMagickMap.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    static UpscaleFormatMap()
    {
        FromMagickMap[MagickFormat.Ppm] = "pnm";
        FromMagickMap[MagickFormat.Pgm] = "pnm";
        FromMagickMap[MagickFormat.Pbm] = "pnm";
    }

    public static MagickFormat? ToMagick(string formatId) =>
        ToMagickMap.TryGetValue(formatId, out var format) ? format : null;

    public static string? ToId(MagickFormat format) =>
        FromMagickMap.TryGetValue(format, out var id) ? id :
        format switch
        {
            MagickFormat.Jpeg => "jpg",
            MagickFormat.Tiff => "tif",
            _ => null
        };
}
