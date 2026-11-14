using System.Diagnostics;
using System.Globalization;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.Images;

namespace PixelForge.Engines.ImageTools;

/// <summary>
/// Local image tools that share the application's Magick.NET and ONNX Runtime stack.
/// Operations are deliberately expressed as an explicit, deterministic pipeline:
/// crop → resize → denoise → sharpen → colour → watermark → metadata → encode.
/// </summary>
public sealed class ImageToolsEngine : IProcessingEngine, IDisposable
{
    private readonly ImageEngine _imageEngine;
    private readonly IAppPaths _paths;
    private readonly ILogger<ImageToolsEngine> _logger;
    private readonly object _sessionLock = new();
    private readonly string? _backgroundModelPath;
    private readonly string? _runtimeError;
    private readonly bool _cpuAvailable;
    private readonly bool _directMlAvailable;
    private readonly string _runtimeVersion;
    private InferenceSession? _backgroundSession;
    private string? _backgroundInputName;
    private string? _backgroundOutputName;
    private long _backgroundModelTicks;
    private static bool _magickInitialized;
    private static readonly object MagickInitLock = new();

    public ImageToolsEngine(ImageEngine imageEngine, IAppPaths paths, ILogger<ImageToolsEngine> logger)
    {
        _imageEngine = imageEngine;
        _paths = paths;
        _logger = logger;
        EnsureMagickInitialized();
        _backgroundModelPath = ResolveBackgroundModelPath();
        _runtimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            _cpuAvailable = providers.Contains("CPUExecutionProvider", StringComparer.OrdinalIgnoreCase);
            _directMlAvailable = providers.Contains("DmlExecutionProvider", StringComparer.OrdinalIgnoreCase);
            if (!_cpuAvailable && !_directMlAvailable)
                _runtimeError = "ONNX Runtime reported no execution providers.";
        }
        catch (Exception ex)
        {
            _runtimeError = ex.Message;
            _logger.LogError(ex, "Image tools could not initialize ONNX Runtime");
        }
    }

    public string Id => EngineIds.ImageTools;
    public string DisplayName => "Image tools";
    public MediaCategory[] Categories => [MediaCategory.Image];

    private static void EnsureMagickInitialized()
    {
        if (_magickInitialized) return;
        lock (MagickInitLock)
        {
            if (_magickInitialized) return;
            ResourceLimits.Memory = 1536UL * 1024 * 1024;
            ResourceLimits.Disk = 8192UL * 1024 * 1024;
            ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 8);
            ResourceLimits.Width = 32768;
            ResourceLimits.Height = 32768;
            _magickInitialized = true;
        }
    }

    public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var formats = new List<MediaFormat>();
            foreach (var definition in FormatCatalog.ImageFormats)
            {
                var info = MagickNET.SupportedFormats.FirstOrDefault(f =>
                    string.Equals(f.Format.ToString(), definition.TechnicalId, StringComparison.OrdinalIgnoreCase));
                var canRead = info?.SupportsReading ?? false;
                var canWrite = (info?.SupportsWriting ?? false) && definition.Id is not ("svg" or "heic" or "psd");
                formats.Add(definition.ToMediaFormat(Id, canRead, canWrite,
                    unavailableReason: canRead || canWrite ? null : $"The bundled ImageMagick build does not support {definition.DisplayName}."));
            }

            var providers = new List<ExecutionProviderInfo>
            {
                new()
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = _cpuAvailable,
                    DeviceName = Environment.MachineName,
                    Details = _cpuAvailable ? $"ONNX Runtime {_runtimeVersion}" : _runtimeError
                },
                new()
                {
                    Kind = ExecutionProviderKind.DirectML,
                    DisplayName = "DirectML (GPU)",
                    IsAvailable = _directMlAvailable,
                    DeviceName = _directMlAvailable ? "DirectML adapter 0" : null,
                    Details = _directMlAvailable
                        ? "Background removal can use an AMD, NVIDIA or Intel GPU through DirectML."
                        : _runtimeError ?? "DirectML is not available in this build or driver."
                }
            };

            return Task.FromResult(new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                Version = $"ImageMagick {MagickNET.ImageMagickVersion} · ONNX Runtime {_runtimeVersion}",
                IsAvailable = true,
                Formats = formats,
                ExecutionProviders = providers,
                Algorithms =
                [
                    new() { Id = "optimize", DisplayName = "Optimise", Description = "Re-encode with quality and lossless controls." },
                    new() { Id = "batch-edit", DisplayName = "Batch edit", Description = "Crop, resize, denoise, sharpen, colour, watermark and metadata in a fixed order." },
                    new() { Id = "background", DisplayName = "Background removal", Description = "Local u2netp salient-object segmentation with transparent output." },
                    new() { Id = "metadata", DisplayName = "Metadata", Description = "Inspect and edit EXIF/IPTC metadata." }
                ]
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image tools probe failed");
            return Task.FromResult(new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = ex.Message
            });
        }
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default) => _imageEngine.TryInspectAsync(path, categoryHint, cancellationToken);

    public Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Settings is not ImageToolSettings settings)
            return Task.FromResult(ValidationResult.Failure("image.tools.settings", "The image tool settings are invalid."));
        if (request.Operation != OperationKind.ImageProcess)
            return Task.FromResult(ValidationResult.Failure("image.tools.operation", "The image tools engine only supports image tool operations."));

        var issues = new List<ValidationIssue>();
        var formatId = request.OutputFormatId is { Length: > 0 } ? request.OutputFormatId : settings.OutputFormatId;
        var format = FormatCatalog.FindById(formatId);
        if (format is null || !ImageFormatMapper.CanWrite(format.Id))
            return Task.FromResult(ValidationResult.Failure("image.tools.format", "The selected image output format is not available."));

        if (request.InputInfo is { IsReadable: false })
            return Task.FromResult(ValidationResult.Failure("image.tools.input",
                request.InputInfo.Error ?? "The input image cannot be read."));
        if (request.InputInfo is { IsAnimated: true } or { IsMultipage: true })
            return Task.FromResult(ValidationResult.Failure("image.tools.frames",
                "Animated and multi-page images are not supported by image tools. Use the Converter to select or flatten a frame."));

        if (settings.Tool == ImageToolOperation.RemoveBackground)
        {
            if (_backgroundModelPath is null)
                issues.Add(ValidationIssue.Error("image.tools.model",
                    "The bundled background-removal model (u2netp) was not found."));
            if (!_cpuAvailable && !_directMlAvailable)
                issues.Add(ValidationIssue.Error("image.tools.runtime",
                    _runtimeError ?? "The AI runtime is unavailable, so background removal cannot run."));
            if (!format.SupportsAlpha && !IsTransparent(settings.BackgroundColor))
                issues.Add(ValidationIssue.Warning("image.tools.alpha",
                    $"{format.DisplayName} cannot store transparency. The result will be flattened onto the selected colour."));
            else if (!format.SupportsAlpha && IsTransparent(settings.BackgroundColor))
                issues.Add(ValidationIssue.Error("image.tools.alpha",
                    $"{format.DisplayName} cannot store a transparent background. Choose PNG, WebP, AVIF or another alpha-capable format."));
        }

        if (settings.Tool == ImageToolOperation.BatchEdit)
        {
            if (settings.CropWidth is <= 0 || settings.CropHeight is <= 0)
                issues.Add(ValidationIssue.Error("image.tools.crop", "Crop width and height must both be positive."));
            if (settings.ResizeWidth is <= 0)
                issues.Add(ValidationIssue.Error("image.tools.resize", "Resize width must be greater than zero."));
            if (settings.ResizeHeight is <= 0)
                issues.Add(ValidationIssue.Error("image.tools.resize", "Resize height must be greater than zero."));
            if (settings.WatermarkKind == WatermarkKind.Image &&
                (string.IsNullOrWhiteSpace(settings.WatermarkImagePath) || !File.Exists(settings.WatermarkImagePath)))
                issues.Add(ValidationIssue.Error("image.tools.watermark",
                    "Choose an existing image file for the watermark."));
            if (settings.WatermarkKind == WatermarkKind.Text && string.IsNullOrWhiteSpace(settings.WatermarkText))
                issues.Add(ValidationIssue.Error("image.tools.watermark", "Enter watermark text."));
        }

        if (settings.Quality is < 1 or > 100)
            issues.Add(ValidationIssue.Error("image.tools.quality", "Quality must be between 1 and 100."));
        if (!format.SupportsAlpha && !settings.PreserveAlpha)
            issues.Add(ValidationIssue.Info("image.tools.flatten", "The output format is flattened because it does not support alpha."));

        return Task.FromResult(new ValidationResult { Issues = issues });
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (request.Settings is not ImageToolSettings settings)
            return ProcessingResult.Fail("The image tool settings are invalid.");
        try
        {
            return settings.Tool switch
            {
                ImageToolOperation.RemoveBackground => await ProcessBackgroundRemovalAsync(request, settings, progress, cancellationToken),
                ImageToolOperation.BatchEdit => await ProcessBatchEditAsync(request, settings, progress, cancellationToken),
                ImageToolOperation.EditMetadata => await ProcessMetadataAsync(request, settings, progress, cancellationToken),
                _ => await ProcessOptimizeAsync(request, settings, progress, cancellationToken)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image tool {Tool} failed for {Input}", settings.Tool, request.InputPath);
            return ProcessingResult.Fail(FriendlyError(ex), ex.ToString());
        }
    }

    public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var settings = request.Settings as ImageToolSettings;
                var formatId = request.OutputFormatId is { Length: > 0 }
                    ? request.OutputFormatId
                    : settings?.OutputFormatId;
                var definition = FormatCatalog.FindById(formatId);
                if (definition is null) return VerificationResult.Fail("The image output format is unknown.");

                var file = new FileInfo(outputPath);
                if (!file.Exists || file.Length < 16)
                    return VerificationResult.Fail("The output image file is empty.");

                using var image = new MagickImage(outputPath);
                var actualFormat = ImageFormatMapper.ToId(image.Format);
                if (!string.Equals(actualFormat, definition.Id, StringComparison.OrdinalIgnoreCase))
                    return VerificationResult.Fail($"The output format is {actualFormat ?? image.Format.ToString()}, expected {definition.Id}.");

                var issues = new List<ValidationIssue>();
                if (settings is not null)
                {
                    var expected = ExpectedDimensions(settings, request.InputInfo);
                    if (expected is { } dimensions && dimensions.Width > 0 && dimensions.Height > 0 &&
                        ((int)image.Width != dimensions.Width || (int)image.Height != dimensions.Height))
                    {
                        return VerificationResult.Fail(
                            $"The output is {image.Width} × {image.Height}, expected {dimensions.Width} × {dimensions.Height}.");
                    }

                    if (settings.Tool == ImageToolOperation.RemoveBackground && IsTransparent(settings.BackgroundColor))
                    {
                        if (!image.HasAlpha)
                            return VerificationResult.Fail("Background removal completed but the output has no alpha channel.");
                        var alpha = image.Statistics().GetChannel(PixelChannel.Alpha);
                        if (alpha is not null && Math.Abs(alpha.Minimum - alpha.Maximum) < 0.001)
                            issues.Add(ValidationIssue.Warning("image.verify.alpha",
                                "The output alpha channel is uniform. The model may not have found a distinct subject."));
                    }

                    if (settings.Tool == ImageToolOperation.Optimize && request.InputInfo is { FileSizeBytes: > 0 })
                    {
                        var saved = request.InputInfo.FileSizeBytes - file.Length;
                        if (saved > 0)
                            issues.Add(ValidationIssue.Info("image.optimize.saved",
                                $"Saved {FormatBytes(saved)} ({saved * 100d / request.InputInfo.FileSizeBytes:0.0}%)."));
                        else if (saved < 0 && definition.IsLossy)
                            issues.Add(ValidationIssue.Info("image.optimize.larger",
                                $"The optimised file is {FormatBytes(-saved)} larger than the source."));
                    }
                }

                var info = new MediaInfo
                {
                    FilePath = outputPath,
                    FileName = Path.GetFileName(outputPath),
                    Extension = Path.GetExtension(outputPath).ToLowerInvariant(),
                    FileSizeBytes = file.Length,
                    Category = MediaCategory.Image,
                    FormatId = actualFormat,
                    FormatName = definition.DisplayName,
                    Width = (int)image.Width,
                    Height = (int)image.Height,
                    HasAlpha = image.HasAlpha,
                    FrameCount = 1,
                    EngineId = Id,
                    Tags = new Dictionary<string, string>
                    {
                        ["HasIccProfile"] = image.HasProfile("icc") ? "true" : "false",
                        ["HasExif"] = image.GetExifProfile() is not null ? "true" : "false",
                        ["HasIptc"] = image.GetIptcProfile() is not null ? "true" : "false",
                        ["HasXmp"] = image.GetXmpProfile() is not null ? "true" : "false"
                    }
                };

                return new VerificationResult
                {
                    IsValid = true,
                    Summary = $"{definition.DisplayName} · {info.Width} × {info.Height} · {FormatBytes(file.Length)}",
                    Issues = issues,
                    ActualInfo = info
                };
            }
            catch (Exception ex)
            {
                return VerificationResult.Fail("The output image could not be reopened.", details: ex.ToString());
            }
        }, cancellationToken);
    }

    public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default) =>
        _imageEngine.CreateThumbnailAsync(inputPath, outputPath, maxSize, cancellationToken);

    private async Task<ProcessingResult> ProcessOptimizeAsync(ProcessingRequest request, ImageToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var format = FormatCatalog.FindById(request.OutputFormatId) ?? FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output image format is unknown.");
        var magickFormat = ImageFormatMapper.ToMagick(format.Id);
        if (magickFormat is null) return ProcessingResult.Fail($"The image engine cannot encode {format.DisplayName}.");

        progress.Report(ProcessingProgress.Percent(0.05, "Reading image…"));
        using var image = new MagickImage(request.InputPath);
        image.AutoOrient();
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var hadAlpha = image.HasAlpha && !image.IsOpaque;
        if (hadAlpha && !format.SupportsAlpha)
        {
            image.BackgroundColor = new MagickColor(settings.PreserveAlpha ? "#FFFFFF" : settings.BackgroundColor);
            image.Alpha(AlphaAction.Remove);
            warnings.Add($"{format.DisplayName} cannot store transparency; the image was flattened.");
        }
        else if (!settings.PreserveAlpha)
        {
            image.BackgroundColor = new MagickColor(settings.BackgroundColor);
            image.Alpha(AlphaAction.Remove);
        }

        ApplyMetadataPolicy(image, settings);
        ApplyFormatSettings(image, format, settings);
        progress.Report(ProcessingProgress.Percent(0.6, "Encoding image…"));
        cancellationToken.ThrowIfCancellationRequested();
        image.Write(request.TempOutputPath, magickFormat.Value);
        stopwatch.Stop();
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("The image encoder produced no output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        progress.Report(ProcessingProgress.Percent(1, "Image optimised."));
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, verification, warnings);
    }

    private async Task<ProcessingResult> ProcessBatchEditAsync(ProcessingRequest request, ImageToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var format = FormatCatalog.FindById(request.OutputFormatId) ?? FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output image format is unknown.");
        var magickFormat = ImageFormatMapper.ToMagick(format.Id);
        if (magickFormat is null) return ProcessingResult.Fail($"The image engine cannot encode {format.DisplayName}.");

        progress.Report(ProcessingProgress.Percent(0.04, "Reading image…"));
        using var image = new MagickImage(request.InputPath);
        image.AutoOrient();
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var steps = new List<string>();

        if (settings.CropWidth is > 0 && settings.CropHeight is > 0)
        {
            progress.Report(ProcessingProgress.Percent(0.12, "Cropping…"));
            Crop(image, settings);
            steps.Add("crop");
        }
        else if (!string.IsNullOrWhiteSpace(settings.CropAspectRatio))
        {
            progress.Report(ProcessingProgress.Percent(0.12, "Cropping to aspect ratio…"));
            CropToAspect(image, settings.CropAspectRatio);
            steps.Add("aspect crop");
        }

        if (settings.ResizeWidth is > 0 || settings.ResizeHeight is > 0)
        {
            progress.Report(ProcessingProgress.Percent(0.25, "Resizing…"));
            ResizeImage(image, settings);
            steps.Add("resize");
        }

        if (settings.DenoiseStrength > 0)
        {
            progress.Report(ProcessingProgress.Percent(0.35, "Denoising…"));
            var order = (uint)Math.Clamp((int)Math.Round(settings.DenoiseStrength / 20.0), 1, 5);
            image.ReduceNoise(order);
            steps.Add($"denoise {order}");
        }

        if (settings.SharpenAmount > 0)
        {
            progress.Report(ProcessingProgress.Percent(0.45, "Sharpening…"));
            var sigma = Math.Clamp(settings.SharpenAmount / 100.0, 0.1, 3.0);
            image.Sharpen(Math.Clamp(settings.SharpenRadius, 0.1, 5.0), sigma);
            steps.Add("sharpen");
        }

        if (Math.Abs(settings.Brightness - 100) > 0.01 || Math.Abs(settings.Contrast - 100) > 0.01)
        {
            progress.Report(ProcessingProgress.Percent(0.53, "Adjusting brightness and contrast…"));
            image.BrightnessContrast(new Percentage(settings.Brightness - 100), new Percentage(settings.Contrast - 100));
            steps.Add("brightness/contrast");
        }

        if (Math.Abs(settings.Saturation - 100) > 0.01)
        {
            progress.Report(ProcessingProgress.Percent(0.58, "Adjusting saturation…"));
            image.Modulate(new Percentage(100), new Percentage(Math.Clamp(settings.Saturation, 0, 300)));
            steps.Add("saturation");
        }

        if (Math.Abs(settings.Exposure) > 0.001)
        {
            progress.Report(ProcessingProgress.Percent(0.62, "Adjusting exposure…"));
            image.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, Math.Clamp(settings.Exposure, -5, 5)));
            steps.Add("exposure");
        }

        if (Math.Abs(settings.Gamma - 1.0) > 0.001)
        {
            progress.Report(ProcessingProgress.Percent(0.66, "Applying gamma…"));
            image.Level(new Percentage(0), new Percentage(100), 1.0 / Math.Clamp(settings.Gamma, 0.1, 5.0));
            steps.Add("gamma");
        }

        if (settings.WatermarkKind != WatermarkKind.None)
        {
            progress.Report(ProcessingProgress.Percent(0.72, "Applying watermark…"));
            ApplyWatermark(image, settings);
            steps.Add("watermark");
        }

        ApplyMetadataEdit(image, settings);
        if (settings.RemoveAllMetadata)
        {
            RemoveAllMetadata(image);
            warnings.Add("All source metadata was removed as requested.");
        }

        var hadAlpha = image.HasAlpha && !image.IsOpaque;
        if (hadAlpha && !format.SupportsAlpha)
        {
            image.BackgroundColor = new MagickColor("#FFFFFF");
            image.Alpha(AlphaAction.Remove);
            warnings.Add($"{format.DisplayName} cannot store transparency; the image was flattened.");
        }
        else if (!settings.PreserveAlpha)
        {
            image.BackgroundColor = new MagickColor(settings.BackgroundColor);
            image.Alpha(AlphaAction.Remove);
        }

        ApplyFormatSettings(image, format, settings);
        progress.Report(ProcessingProgress.Percent(0.86, "Encoding image…"));
        cancellationToken.ThrowIfCancellationRequested();
        image.Write(request.TempOutputPath, magickFormat.Value);
        stopwatch.Stop();
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("The image encoder produced no output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        if (steps.Count > 0)
            warnings.Add("Applied: " + string.Join(" → ", steps) + ".");
        progress.Report(ProcessingProgress.Percent(1, "Image edit complete."));
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, verification, warnings);
    }

    private async Task<ProcessingResult> ProcessMetadataAsync(ProcessingRequest request, ImageToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var format = FormatCatalog.FindById(request.OutputFormatId) ?? FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output image format is unknown.");
        var magickFormat = ImageFormatMapper.ToMagick(format.Id);
        if (magickFormat is null) return ProcessingResult.Fail($"The image engine cannot encode {format.DisplayName}.");
        if (!format.SupportsMetadata)
            return ProcessingResult.Fail($"{format.DisplayName} does not support editable metadata.");

        progress.Report(ProcessingProgress.Percent(0.1, "Reading metadata…"));
        using var image = new MagickImage(request.InputPath);
        image.AutoOrient();
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.RemoveAllMetadata)
            RemoveAllMetadata(image);
        else
            ApplyMetadataEdit(image, settings);
        ApplyFormatSettings(image, format, settings);
        progress.Report(ProcessingProgress.Percent(0.7, "Writing image…"));
        image.Write(request.TempOutputPath, magickFormat.Value);
        stopwatch.Stop();

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        progress.Report(ProcessingProgress.Percent(1, "Metadata updated."));
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, verification);
    }

    private async Task<ProcessingResult> ProcessBackgroundRemovalAsync(ProcessingRequest request, ImageToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        if (_backgroundModelPath is null)
            return ProcessingResult.Fail("The bundled background-removal model (u2netp) was not found.");
        if (!_cpuAvailable && !_directMlAvailable)
            return ProcessingResult.Fail(_runtimeError ?? "The AI runtime is unavailable.");

        var format = FormatCatalog.FindById(request.OutputFormatId) ?? FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null) return ProcessingResult.Fail("The output image format is unknown.");
        var magickFormat = ImageFormatMapper.ToMagick(format.Id);
        if (magickFormat is null) return ProcessingResult.Fail($"The image engine cannot encode {format.DisplayName}.");
        if (!format.SupportsAlpha && IsTransparent(settings.BackgroundColor))
            return ProcessingResult.Fail($"{format.DisplayName} cannot store a transparent background. Choose PNG, WebP, AVIF or another alpha-capable format.");

        var stopwatch = Stopwatch.StartNew();
        progress.Report(ProcessingProgress.Percent(0.04, "Reading image…"));
        using var source = new MagickImage(request.InputPath);
        source.AutoOrient();
        if ((long)source.Width * source.Height > 80_000_000)
            return ProcessingResult.Fail("The image is too large for background removal. Resize it below 80 megapixels first.");
        var width = (int)source.Width;
        var height = (int)source.Height;
        source.ColorSpace = ColorSpace.sRGB;
        source.Depth = 8;
        var bgra = source.ToByteArray(MagickFormat.Bgra);
        if (bgra.Length != width * height * 4)
            return ProcessingResult.Fail("The image could not be prepared for inference.");

        var warnings = new List<string>();
        var attempts = GetProviderAttempts(settings.ExecutionProvider);
        var errors = new List<string>();
        byte[]? mask = null;
        ExecutionProviderKind actualProvider = ExecutionProviderKind.Cpu;
        var inputSize = 320;

        foreach (var provider in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress.Report(ProcessingProgress.Percent(0.12, $"Loading segmentation model · {ProviderName(provider)}…"));
                var session = GetBackgroundSession(provider, out var inputName, out var outputName, out inputSize);
                var tensorSize = inputSize;
                progress.Report(ProcessingProgress.Percent(0.22, "Segmenting subject…"));
                mask = await Task.Run(() => RunSegmentation(session, inputName, outputName, tensorSize, bgra, width, height,
                    progress, cancellationToken), cancellationToken);
                actualProvider = provider;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background removal with {Provider} failed", provider);
                errors.Add($"{ProviderName(provider)}: {ex.Message}");
                ResetBackgroundSession();
                if (provider != ExecutionProviderKind.Cpu)
                    warnings.Add($"{ProviderName(provider)} failed: {ex.Message}. Retrying on the CPU.");
            }
        }

        if (mask is null)
            return ProcessingResult.Fail("Background removal failed on every available execution provider. " +
                                         string.Join(" | ", errors), string.Join(Environment.NewLine, errors));

        progress.Report(ProcessingProgress.Percent(0.86, "Compositing subject…"));
        for (var i = 0; i < width * height; i++)
        {
            var existing = bgra[i * 4 + 3];
            bgra[i * 4 + 3] = (byte)Math.Clamp(existing * mask[i] / 255, 0, 255);
        }

        using var output = new MagickImage(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA))
        {
            Depth = 8
        };
        output.HasAlpha = true;
        CopyProfiles(source, output, settings);
        if (!IsTransparent(settings.BackgroundColor))
        {
            output.BackgroundColor = new MagickColor(settings.BackgroundColor);
            output.Alpha(AlphaAction.Remove);
            warnings.Add($"The transparent result was flattened onto {settings.BackgroundColor}.");
        }

        ApplyFormatSettings(output, format, settings);
        progress.Report(ProcessingProgress.Percent(0.94, "Writing image…"));
        output.Write(request.TempOutputPath, magickFormat.Value);
        stopwatch.Stop();
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("The image encoder produced no output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        warnings.Add($"u2netp inference ran on {ProviderName(actualProvider)}.");
        var finalVerification = verification with
        {
            Summary = $"{verification.Summary} · {ProviderName(actualProvider)}",
            Issues = verification.Issues.Concat(warnings.Select(w => ValidationIssue.Info("image.background.info", w))).ToList()
        };
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, finalVerification, warnings);
    }

    private byte[] RunSegmentation(InferenceSession session, string inputName, string outputName, int tensorSize,
        byte[] sourceBgra, int width, int height, IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var count = 3 * tensorSize * tensorSize;
        var data = new float[count];
        var scaleX = width / (double)tensorSize;
        var scaleY = height / (double)tensorSize;
        for (var y = 0; y < tensorSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sy = Math.Clamp((int)((y + 0.5) * scaleY), 0, height - 1);
            for (var x = 0; x < tensorSize; x++)
            {
                var sx = Math.Clamp((int)((x + 0.5) * scaleX), 0, width - 1);
                var index = (sy * width + sx) * 4;
                var dst = y * tensorSize + x;
                // BGRA source → RGB planar, ImageNet normalization used by U²-Net.
                data[dst] = (sourceBgra[index + 2] / 255f - 0.485f) / 0.229f;
                data[tensorSize * tensorSize + dst] = (sourceBgra[index + 1] / 255f - 0.456f) / 0.224f;
                data[2 * tensorSize * tensorSize + dst] = (sourceBgra[index] / 255f - 0.406f) / 0.225f;
            }
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName,
            new DenseTensor<float>(data, [1, 3, tensorSize, tensorSize]))], [outputName]);
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 4)
            throw new InvalidOperationException($"The segmentation model returned an unexpected shape: {string.Join(" × ", dims)}.");

        var outHeight = dims[2] > 0 ? dims[2] : tensorSize;
        var outWidth = dims[3] > 0 ? dims[3] : tensorSize;
        var plane = outWidth * outHeight;
        var values = new float[plane];
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < plane; i++)
        {
            var value = output[0, 0, i / outWidth, i % outWidth];
            values[i] = value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        var range = max - min;
        var mask = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sy = Math.Clamp((int)((y + 0.5) * outHeight / height), 0, outHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((int)((x + 0.5) * outWidth / width), 0, outWidth - 1);
                var value = range < 1e-6f ? 0f : (values[sy * outWidth + sx] - min) / range;
                mask[y * width + x] = (byte)Math.Clamp(value * 255f + 0.5f, 0f, 255f);
            }
        }

        progress.Report(ProcessingProgress.Percent(0.82, "Segmentation complete."));
        return mask;
    }

    private InferenceSession GetBackgroundSession(ExecutionProviderKind requested, out string inputName,
        out string outputName, out int inputSize)
    {
        var modelPath = _backgroundModelPath ?? throw new InvalidOperationException("The background model is not installed.");
        var ticks = File.GetLastWriteTimeUtc(modelPath).Ticks;
        lock (_sessionLock)
        {
            if (_backgroundSession is not null && _backgroundModelTicks == ticks &&
                _backgroundInputName is not null && _backgroundOutputName is not null)
            {
                inputName = _backgroundInputName;
                outputName = _backgroundOutputName;
                inputSize = GetInputSize(_backgroundSession);
                return _backgroundSession;
            }

            _backgroundSession?.Dispose();
            _backgroundSession = null;
            var providers = GetProviderAttempts(requested);
            Exception? lastError = null;
            foreach (var provider in providers)
            {
                try
                {
                    var session = CreateSession(modelPath, provider, out var input, out var output);
                    _backgroundSession = session;
                    _backgroundInputName = input;
                    _backgroundOutputName = output;
                    _backgroundModelTicks = ticks;
                    inputName = input;
                    outputName = output;
                    inputSize = GetInputSize(session);
                    return session;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex, "Could not create segmentation session with {Provider}", provider);
                }
            }

            throw new InvalidOperationException("The background-removal model could not be loaded.", lastError);
        }
    }

    private static InferenceSession CreateSession(string modelPath, ExecutionProviderKind provider,
        out string inputName, out string outputName)
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
            var outputs = session.OutputMetadata;
            inputName = input.Key;
            outputName = outputs.First().Key;
            // Prefer a single-channel mask output when the model exposes several heads.
            foreach (var candidate in outputs)
            {
                var dims = candidate.Value.Dimensions;
                if (dims.Length == 4 && (dims[1] <= 0 || dims[1] == 1))
                {
                    outputName = candidate.Key;
                    break;
                }
            }

            return session;
        }
        finally
        {
            options.Dispose();
        }
    }

    private static int GetInputSize(InferenceSession session)
    {
        var dims = session.InputMetadata.First().Value.Dimensions;
        var size = dims.Length == 4 && dims[2] > 0 ? dims[2] : 320;
        return Math.Clamp(size, 64, 2048);
    }

    private void ResetBackgroundSession()
    {
        lock (_sessionLock)
        {
            _backgroundSession?.Dispose();
            _backgroundSession = null;
            _backgroundInputName = null;
            _backgroundOutputName = null;
            _backgroundModelTicks = 0;
        }
    }

    private ExecutionProviderKind[] GetProviderAttempts(ExecutionProviderKind requested) => requested switch
    {
        ExecutionProviderKind.Cpu => [ExecutionProviderKind.Cpu],
        ExecutionProviderKind.DirectML when _directMlAvailable => [ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu],
        _ when _directMlAvailable => [ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu],
        _ => [ExecutionProviderKind.Cpu]
    };

    private string? ResolveBackgroundModelPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "models", "segmentation", "u2netp.onnx"),
            Path.Combine(AppContext.BaseDirectory, "segmentation", "u2netp.onnx")
        };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "third_party", "models", "segmentation", "u2netp.onnx"));
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) _logger.LogWarning("Bundled u2netp background-removal model was not found");
        return path;
    }

    private static void Crop(MagickImage image, ImageToolSettings settings)
    {
        var x = Math.Max(0, settings.CropX ?? 0);
        var y = Math.Max(0, settings.CropY ?? 0);
        var width = Math.Min((uint)settings.CropWidth!.Value, image.Width - (uint)x);
        var height = Math.Min((uint)settings.CropHeight!.Value, image.Height - (uint)y);
        if (width == 0 || height == 0)
            throw new InvalidOperationException("The crop rectangle lies outside the image.");
        image.Crop(new MagickGeometry(x, y, width, height));
    }

    private static void CropToAspect(MagickImage image, string aspect)
    {
        var parts = aspect.Split(':', 'x', '/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var aw) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) ||
            aw <= 0 || ah <= 0)
            throw new InvalidOperationException("The crop aspect ratio is invalid. Use a value such as 16:9.");

        var target = aw / ah;
        var current = image.Width / (double)image.Height;
        uint width;
        uint height;
        if (current > target)
        {
            height = image.Height;
            width = (uint)Math.Round(height * target);
        }
        else
        {
            width = image.Width;
            height = (uint)Math.Round(width / target);
        }

        image.Crop(width, height, Gravity.Center);
    }

    private static void ResizeImage(MagickImage image, ImageToolSettings settings)
    {
        var sourceWidth = (int)image.Width;
        var sourceHeight = (int)image.Height;
        var width = settings.ResizeWidth;
        var height = settings.ResizeHeight;
        if (width is null && height is null) return;
        int targetWidth;
        int targetHeight;
        if (!settings.PreserveAspectRatio)
        {
            targetWidth = width ?? sourceWidth;
            targetHeight = height ?? sourceHeight;
        }
        else if (width is not null && height is not null)
        {
            var scale = Math.Min(width.Value / (double)sourceWidth, height.Value / (double)sourceHeight);
            targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        }
        else if (width is not null)
        {
            targetWidth = width.Value;
            targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * (width.Value / (double)sourceWidth)));
        }
        else
        {
            targetHeight = height!.Value;
            targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * (height.Value / (double)sourceHeight)));
        }

        if ((long)targetWidth * targetHeight > 120_000_000)
            throw new InvalidOperationException("The target image exceeds the 120 megapixel safety limit.");
        image.Resize((uint)targetWidth, (uint)targetHeight, FilterType.Lanczos);
    }

    private static void ApplyWatermark(MagickImage image, ImageToolSettings settings)
    {
        using var watermark = settings.WatermarkKind == WatermarkKind.Image
            ? LoadImageWatermark(settings)
            : LoadTextWatermark(image, settings);
        var maxWidth = Math.Max(16, (int)Math.Round(image.Width * Math.Clamp(settings.WatermarkScale, 0.02, 0.9)));
        var maxHeight = Math.Max(16, (int)Math.Round(image.Height * Math.Clamp(settings.WatermarkScale, 0.02, 0.9)));
        if (watermark.Width > maxWidth || watermark.Height > maxHeight)
        {
            var scale = Math.Min(maxWidth / (double)watermark.Width, maxHeight / (double)watermark.Height);
            watermark.Resize((uint)Math.Max(1, (int)Math.Round(watermark.Width * scale)),
                (uint)Math.Max(1, (int)Math.Round(watermark.Height * scale)), FilterType.Lanczos);
        }

        if (!watermark.HasAlpha) watermark.Alpha(AlphaAction.Set);
        watermark.Evaluate(Channels.Alpha, EvaluateOperator.Multiply,
            Math.Clamp(settings.WatermarkOpacity <= 0 ? 0.6 : settings.WatermarkOpacity, 0.05, 1.0));
        var (x, y) = GetPosition((int)image.Width, (int)image.Height, (int)watermark.Width, (int)watermark.Height,
            settings.WatermarkPosition, Math.Max(0, settings.WatermarkMargin));
        image.Composite(watermark, x, y, CompositeOperator.Over);
    }

    private static MagickImage LoadTextWatermark(MagickImage source, ImageToolSettings settings)
    {
        var text = string.IsNullOrWhiteSpace(settings.WatermarkText) ? "PixelForge" : settings.WatermarkText!;
        var size = Math.Clamp((int)Math.Round(source.Width * 0.055), 14, 160);
        var readSettings = new MagickReadSettings
        {
            FontFamily = "Segoe UI",
            FontPointsize = size,
            FillColor = MagickColors.White,
            BackgroundColor = MagickColors.Transparent,
            TextGravity = Gravity.Center
        };
        try
        {
            return new MagickImage($"label:{text}", readSettings);
        }
        catch (MagickException)
        {
            var canvas = new MagickImage(MagickColors.Transparent, (uint)Math.Max(32, size * Math.Max(2, text.Length)),
                (uint)Math.Max(32, size * 2));
            canvas.Settings.FontFamily = "Segoe UI";
            canvas.Settings.FontPointsize = size;
            canvas.Settings.FillColor = MagickColors.White;
            canvas.Annotate(text, Gravity.Center);
            return canvas;
        }
    }

    private static MagickImage LoadImageWatermark(ImageToolSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WatermarkImagePath) || !File.Exists(settings.WatermarkImagePath))
            throw new InvalidOperationException("The watermark image could not be found.");
        var image = new MagickImage(settings.WatermarkImagePath);
        image.AutoOrient();
        return image;
    }

    private static (int X, int Y) GetPosition(int canvasWidth, int canvasHeight, int width, int height,
        string position, int margin)
    {
        var p = (position ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var x = p switch
        {
            "bottomleft" or "topleft" or "middleleft" or "left" => margin,
            "bottomright" or "topright" or "middleright" or "right" => canvasWidth - width - margin,
            _ => (canvasWidth - width) / 2
        };
        var y = p switch
        {
            "topleft" or "topright" or "topcenter" or "top" => margin,
            "bottomleft" or "bottomright" or "bottomcenter" or "bottom" => canvasHeight - height - margin,
            _ => (canvasHeight - height) / 2
        };
        return (Math.Clamp(x, 0, Math.Max(0, canvasWidth - width)),
            Math.Clamp(y, 0, Math.Max(0, canvasHeight - height)));
    }

    private static void ApplyMetadataEdit(MagickImage image, ImageToolSettings settings)
    {
        var hasChanges = settings.MetaDescription is not null || settings.MetaCopyright is not null ||
                         settings.MetaArtist is not null || settings.MetaComment is not null ||
                         settings.MetaDateTaken is not null;
        if (!hasChanges) return;

        var exif = image.GetExifProfile() ?? new ExifProfile();
        if (!string.IsNullOrWhiteSpace(settings.MetaDescription))
            exif.SetValue(ExifTag.ImageDescription, settings.MetaDescription!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaCopyright))
            exif.SetValue(ExifTag.Copyright, settings.MetaCopyright!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaArtist))
            exif.SetValue(ExifTag.Artist, settings.MetaArtist!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaComment))
            exif.SetValue(ExifTag.XPComment, System.Text.Encoding.Unicode.GetBytes(settings.MetaComment!.Trim() + "\0"));
        if (settings.MetaDateTaken is { } date)
        {
            var formatted = date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            exif.SetValue(ExifTag.DateTimeOriginal, formatted);
            exif.SetValue(ExifTag.DateTimeDigitized, formatted);
            exif.SetValue(ExifTag.DateTime, formatted);
        }
        image.SetProfile(exif);

        var iptc = image.GetIptcProfile() ?? new IptcProfile();
        if (!string.IsNullOrWhiteSpace(settings.MetaDescription))
            iptc.SetValue(IptcTag.Caption, settings.MetaDescription!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaCopyright))
            iptc.SetValue(IptcTag.CopyrightNotice, settings.MetaCopyright!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaArtist))
            iptc.SetValue(IptcTag.Byline, settings.MetaArtist!.Trim());
        if (!string.IsNullOrWhiteSpace(settings.MetaComment))
            iptc.SetValue(IptcTag.SpecialInstructions, settings.MetaComment!.Trim());
        image.SetProfile(iptc);
    }

    private static void RemoveAllMetadata(MagickImage image)
    {
        foreach (var profile in new[] { "exif", "iptc", "8bim", "xmp", "photoshop", "xml", "icc" })
        {
            try { image.RemoveProfile(profile); } catch { }
        }
    }

    private static void ApplyMetadataPolicy(MagickImage image, ImageToolSettings settings)
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

    private static void CopyProfiles(MagickImage source, MagickImage target, ImageToolSettings settings)
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
            foreach (var name in new[] { "exif", "iptc", "xmp" })
            {
                try
                {
                    var profile = source.GetProfile(name);
                    if (profile is not null) target.SetProfile(profile);
                }
                catch { }
            }
        }
    }

    private static void ApplyFormatSettings(MagickImage image, FormatDefinition format, ImageToolSettings settings)
    {
        image.Quality = (uint)Math.Clamp(settings.Quality, 1, 100);
        switch (format.Id)
        {
            case "png":
                image.Settings.SetDefine("png:compression-level", Math.Clamp(settings.PngCompression, 0, 9)
                    .ToString(CultureInfo.InvariantCulture));
                break;
            case "jpg":
                image.Settings.Interlace = Interlace.Jpeg;
                break;
            case "webp":
                if (settings.Lossless)
                    image.Settings.SetDefine("webp:lossless", "true");
                break;
            case "avif":
                image.Settings.SetDefine("heic:speed", Math.Clamp(settings.AvifSpeed, 0, 10).ToString(CultureInfo.InvariantCulture));
                break;
            case "tif":
                image.Settings.Compression = CompressionMethod.LZW;
                break;
            case "gif":
                if (settings.GifColors is >= 2 and < 256)
                    image.Quantize(new QuantizeSettings { Colors = (uint)settings.GifColors, DitherMethod = DitherMethod.FloydSteinberg });
                break;
        }
    }

    private static (int Width, int Height)? ExpectedDimensions(ImageToolSettings settings, MediaInfo? input)
    {
        if (settings.Tool != ImageToolOperation.BatchEdit || input is not { Width: > 0, Height: > 0 } info)
            return null;
        var width = info.Width.Value;
        var height = info.Height.Value;
        if (settings.CropWidth is > 0 && settings.CropHeight is > 0)
        {
            width = Math.Min(settings.CropWidth.Value, width);
            height = Math.Min(settings.CropHeight.Value, height);
        }
        else if (!string.IsNullOrWhiteSpace(settings.CropAspectRatio))
        {
            var parts = settings.CropAspectRatio.Split(':', 'x', '/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var aw) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) &&
                aw > 0 && ah > 0)
            {
                var target = aw / ah;
                var current = width / (double)height;
                if (current > target) width = (int)Math.Round(height * target);
                else height = (int)Math.Round(width / target);
            }
        }

        if (settings.ResizeWidth is > 0 || settings.ResizeHeight is > 0)
        {
            var rw = settings.ResizeWidth;
            var rh = settings.ResizeHeight;
            if (!settings.PreserveAspectRatio)
            {
                width = rw ?? width;
                height = rh ?? height;
            }
            else if (rw is not null && rh is not null)
            {
                var scale = Math.Min(rw.Value / (double)width, rh.Value / (double)height);
                width = Math.Max(1, (int)Math.Round(width * scale));
                height = Math.Max(1, (int)Math.Round(height * scale));
            }
            else if (rw is not null)
            {
                height = Math.Max(1, (int)Math.Round(height * (rw.Value / (double)width)));
                width = rw.Value;
            }
            else if (rh is not null)
            {
                width = Math.Max(1, (int)Math.Round(width * (rh.Value / (double)height)));
                height = rh.Value;
            }
        }

        return (width, height);
    }

    private static bool IsTransparent(string? color) =>
        string.IsNullOrWhiteSpace(color) ||
        color.Equals("transparent", StringComparison.OrdinalIgnoreCase) ||
        color.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static string ProviderName(ExecutionProviderKind provider) =>
        provider == ExecutionProviderKind.DirectML ? "DirectML (GPU)" : "CPU";

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    private static string FriendlyError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Allocation", StringComparison.OrdinalIgnoreCase))
            return "The image operation ran out of memory. Close other applications or try a smaller image.";
        if (message.Contains("Dml", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DirectML", StringComparison.OrdinalIgnoreCase))
            return "GPU inference failed. Try the CPU execution provider in the image tools settings.";
        var newline = message.IndexOf('\n');
        if (newline > 0) message = message[..newline];
        return message.Trim();
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _backgroundSession?.Dispose();
            _backgroundSession = null;
        }
    }
}
