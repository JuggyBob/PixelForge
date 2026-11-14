using System.Diagnostics;
using System.Globalization;
using ImageMagick;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.Images;

public sealed class ImageEngine : IProcessingEngine
{
    private readonly ILogger<ImageEngine> _logger;
    private static readonly object InitLock = new();
    private static bool _initialized;

    public ImageEngine(ILogger<ImageEngine> logger)
    {
        _logger = logger;
        EnsureInitialized();
    }

    public string Id => EngineIds.Images;
    public string DisplayName => "Image engine";
    public MediaCategory[] Categories => [MediaCategory.Image];

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            ResourceLimits.Memory = 1024UL * 1024 * 1024;
            ResourceLimits.Disk = 8192UL * 1024 * 1024;
            ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 8);
            ResourceLimits.Width = 32768;
            ResourceLimits.Height = 32768;
            _initialized = true;
        }
    }

    public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureInitialized();
            var formats = new List<MediaFormat>();
            foreach (var definition in FormatCatalog.ImageFormats)
            {
                var info = MagickNET.SupportedFormats.FirstOrDefault(f =>
                    string.Equals(f.Format.ToString(), definition.TechnicalId, StringComparison.OrdinalIgnoreCase));
                var canRead = info?.SupportsReading ?? false;
                var canWrite = (info?.SupportsWriting ?? false) &&
                               definition.Id is not ("svg" or "heic" or "psd");
                var unavailable = canRead || canWrite ? null : $"The installed ImageMagick build does not support {definition.DisplayName}.";
                formats.Add(definition.ToMediaFormat(Id, canRead, canWrite, unavailableReason: unavailable));
            }

            var capability = new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                Version = $"{MagickNET.ImageMagickVersion} (Magick.NET {MagickNET.Version})",
                IsAvailable = true,
                Formats = formats,
                Algorithms =
                [
                    new() { Id = "nearest", DisplayName = "Nearest Neighbor", Description = "Fastest; preserves hard pixel edges and is ideal for pixel art." },
                    new() { Id = "bilinear", DisplayName = "Bilinear", Description = "Smooth and fast; can soften fine detail." },
                    new() { Id = "bicubic", DisplayName = "Bicubic", Description = "Balanced smoothing and sharpness for photographs." },
                    new() { Id = "lanczos", DisplayName = "Lanczos", Description = "High-quality sharpening with minimal ringing; a strong general default." },
                    new() { Id = "mitchell", DisplayName = "Mitchell-Netravali", Description = "Soft, natural results with reduced halos and ringing." }
                ]
            };

            return Task.FromResult(capability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image engine probe failed");
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
                    _ => ImageFormatMapper.ToId(first.Format)
                };
                if (formatId is null || !FormatCatalog.ImageFormats.Any(f => string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase)))
                {
                    return new MediaInfo
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
                var isVector = definition?.IsVector ?? first.Format == MagickFormat.Svg;
                var isAnimatedFormat = definition?.SupportsAnimation == true && formatId is not ("ico" or "psd");
                var isMultipageFormat = definition?.SupportsMultipage == true;
                var orientation = primary.Orientation;
                var width = (int)primary.Width;
                var height = (int)primary.Height;
                if (orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom)
                    (width, height) = (height, width);

                double? duration = null;
                if (collection.Count > 1 && isAnimatedFormat)
                {
                    var total = collection.Sum(frame => (double)frame.AnimationDelay / 100.0);
                    if (total > 0) duration = total;
                }

                var hasAlpha = collection.Any(frame => frame.HasAlpha);
                var formatName = definition?.DisplayName ?? first.Format.ToString();
                var info = new MediaInfo
                {
                    FilePath = Path.GetFullPath(path),
                    FileName = Path.GetFileName(path),
                    Extension = extension,
                    FileSizeBytes = new FileInfo(path).Length,
                    Category = MediaCategory.Image,
                    FormatId = formatId,
                    FormatName = formatName,
                    Width = width,
                    Height = height,
                    DurationSeconds = duration,
                    HasAlpha = hasAlpha,
                    IsAnimated = isAnimatedFormat && collection.Count > 1,
                    IsMultipage = isMultipageFormat && collection.Count > 1,
                    IsVector = isVector,
                    FrameCount = collection.Count,
                    RotationDegrees = OrientationDegrees(orientation),
                    ColorSpace = primary.ColorSpace.ToString(),
                    EngineId = Id,
                    Tags = new Dictionary<string, string>
                    {
                        ["Frames"] = collection.Count.ToString(CultureInfo.InvariantCulture),
                        ["HasIccProfile"] = primary.HasProfile("icc") ? "true" : "false",
                        ["HasExif"] = primary.GetExifProfile() is not null ? "true" : "false"
                    }
                };
                return info;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect image {Path}", path);
                return new MediaInfo
                {
                    FilePath = Path.GetFullPath(path),
                    FileName = Path.GetFileName(path),
                    Extension = Path.GetExtension(path).ToLowerInvariant(),
                    FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                    Category = MediaCategory.Image,
                    Error = SimplifyError(ex),
                    EngineId = Id
                };
            }
        }, cancellationToken);
    }

    public Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation != OperationKind.ImageConvert || request.Settings is not ImageConversionSettings settings)
            return Task.FromResult(ValidationResult.Failure("image.operation", "The image engine only supports image conversion."));

        var format = FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null || !ImageFormatMapper.CanWrite(format.Id))
            return Task.FromResult(ValidationResult.Failure("image.format", "The selected image format is not available."));

        if (request.InputInfo is { IsReadable: false })
            return Task.FromResult(ValidationResult.Failure("image.input", request.InputInfo.Error ?? "The input image is unreadable."));

        return Task.FromResult(ValidationResult.Success());
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request.Settings is not ImageConversionSettings settings)
            return ProcessingResult.Fail("The image conversion settings are invalid.");

        var definition = FormatCatalog.FindById(request.OutputFormatId) ?? FormatCatalog.FindById(settings.OutputFormatId);
        if (definition is null)
            return ProcessingResult.Fail("The output image format is not recognized.");

        var magickFormat = ImageFormatMapper.ToMagick(definition.Id);
        if (magickFormat is null)
            return ProcessingResult.Fail($"The image engine cannot encode {definition.DisplayName}.");

        try
        {
            return await Task.Run(() =>
            {
                var warnings = new List<string>();
                var backgroundColor = new MagickColor(settings.BackgroundColor);
                using var collection = new MagickImageCollection();
                progress.Report(ProcessingProgress.Percent(0.02, "Reading image…"));

                var readSettings = BuildReadSettings(request.InputPath, settings);
                if (readSettings is null)
                    collection.Read(request.InputPath);
                else
                    collection.Read(request.InputPath, readSettings);

                cancellationToken.ThrowIfCancellationRequested();
                if (collection.Count == 0)
                    return ProcessingResult.Fail("The image contains no frames.");

                var sourceFrames = collection.Count;
                var sourceFormatId = request.InputInfo?.FormatId;
                var sourceIsAnimated = request.InputInfo?.IsAnimated == true;
                var sourceIsMultipage = request.InputInfo?.IsMultipage == true;
                if (!sourceIsAnimated && !sourceIsMultipage && sourceFrames > 1)
                {
                    var inferred = request.InputInfo?.FormatId ?? ImageFormatMapper.ToId(collection[0].Format);
                    sourceIsAnimated = inferred is "gif" or "webp" or "png" or "avif" or "jxl";
                    sourceIsMultipage = inferred is "tif" or "ico" or "psd";
                }

                var targetSupportsContent = sourceIsAnimated
                    ? definition.SupportsAnimation
                    : sourceIsMultipage
                        ? definition.SupportsMultipage
                        : true;
                if (sourceFrames > 1 && !targetSupportsContent && !settings.FlattenAnimation && settings.PreserveAnimation)
                    return ProcessingResult.Fail(
                        $"{definition.DisplayName} cannot preserve {(sourceIsAnimated ? "animation" : "multiple pages")}. Enable 'Flatten animation' explicitly.");

                var flatten = sourceFrames > 1 &&
                              (settings.FlattenAnimation || !settings.PreserveAnimation || !targetSupportsContent);
                if (flatten)
                {
                    var keepIndex = sourceFormatId is "ico" or "psd"
                        ? collection.IndexOf(collection.OrderByDescending(f => (long)f.Width * f.Height).First())
                        : 0;
                    for (var i = collection.Count - 1; i >= 0; i--)
                    {
                        if (i != keepIndex) collection.RemoveAt(i);
                    }

                    warnings.Add(sourceIsAnimated
                        ? $"Only the first of {sourceFrames} animation frames was kept."
                        : $"Only one of {sourceFrames} pages/frames was kept.");
                }

                foreach (var image in collection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    image.AutoOrient();
                }

                if (collection.Count > 1 && definition.SupportsAnimation &&
                    definition.Id is "gif" or "webp" or "png" or "avif")
                {
                    collection.Coalesce();
                }

                var first = collection[0];
                var (targetWidth, targetHeight, cropToFill) = ComputeTargetDimensions((int)first.Width, (int)first.Height, settings);
                var hasResize = targetWidth != (int)first.Width || targetHeight != (int)first.Height;
                var supportsAlpha = definition.SupportsAlpha &&
                                    settings.TransparencyMode == TransparencyMode.Preserve;

                var frameIndex = 0;
                foreach (var image in collection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (hasResize || cropToFill)
                    {
                        progress.Report(ProcessingProgress.Percent(0.2 + 0.3 * frameIndex / Math.Max(1, collection.Count),
                            collection.Count > 1 ? $"Resizing frame {frameIndex + 1} of {collection.Count}…" : "Resizing…"));
                        Resize(image, targetWidth, targetHeight, cropToFill, settings.Resampling);
                    }

                    if (!supportsAlpha)
                    {
                        image.BackgroundColor = backgroundColor;
                        image.Alpha(AlphaAction.Remove);
                    }

                    ApplyMetadataPolicy(image, settings);
                    ApplyFormatSettings(image, definition, settings);
                    frameIndex++;
                }

                progress.Report(ProcessingProgress.Percent(0.65, "Encoding image…"));
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.Id == "ico" && collection.Count == 1)
                {
                    var image = collection[0];
                    var sizes = SanitizeIcoSizes(settings.IcoSizes);
                    if (!string.IsNullOrWhiteSpace(sizes)) image.Settings.SetDefine("icon:auto-resize", sizes);
                    image.Write(request.TempOutputPath, MagickFormat.Ico);
                }
                else if (collection.Count == 1)
                {
                    collection[0].Write(request.TempOutputPath, magickFormat.Value);
                }
                else
                {
                    collection.Write(request.TempOutputPath, magickFormat.Value);
                }

                progress.Report(ProcessingProgress.Percent(0.95, "Finalizing…"));
                stopwatch.Stop();
                var output = new FileInfo(request.TempOutputPath);
                if (!output.Exists || output.Length == 0)
                    return ProcessingResult.Fail("The image encoder did not produce an output file.");

                return ProcessingResult.Ok(request.TempOutputPath, null, output.Length, stopwatch.Elapsed,
                    warnings: warnings);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image conversion failed for {Input}", request.InputPath);
            stopwatch.Stop();
            return ProcessingResult.Fail(SimplifyError(ex), ex.ToString(), stopwatch.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (request.Settings is not ImageConversionSettings settings)
                    return VerificationResult.Fail("The image settings are invalid.");
                var definition = FormatCatalog.FindById(request.OutputFormatId);
                if (definition is null) return VerificationResult.Fail("The output format is unknown.");

                var file = new FileInfo(outputPath);
                if (!file.Exists || file.Length < 16)
                    return VerificationResult.Fail("The output image file is empty.");

                using var collection = new MagickImageCollection();
                collection.Ping(outputPath);
                if (collection.Count == 0)
                    return VerificationResult.Fail("The output image has no frames.");

                var first = collection[0];
                var actualFormatId = ImageFormatMapper.ToId(first.Format);
                if (!string.Equals(actualFormatId, definition.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return VerificationResult.Fail(
                        $"The output format is {actualFormatId ?? first.Format.ToString()}, expected {definition.Id}.");
                }

                var issues = new List<ValidationIssue>();
                var input = request.InputInfo;
                if (input?.Width is > 0 && input.Height is > 0 && definition.Id != "ico")
                {
                    var (expectedWidth, expectedHeight, _) = ComputeTargetDimensions(input.Width.Value, input.Height.Value, settings);
                    var actualWidth = (int)first.Width;
                    var actualHeight = (int)first.Height;
                    if (actualWidth != expectedWidth || actualHeight != expectedHeight)
                    {
                        return VerificationResult.Fail(
                            $"The output is {actualWidth} × {actualHeight}, expected {expectedWidth} × {expectedHeight}.");
                    }
                }

                if (input is { HasAlpha: true } && definition.SupportsAlpha && settings.TransparencyMode == TransparencyMode.Preserve &&
                    !first.HasAlpha)
                {
                    issues.Add(ValidationIssue.Warning("image.verify.alpha", "The source had transparency, but the output appears to have no alpha channel."));
                }

                if (settings.PreserveIccProfile && input?.Tags.TryGetValue("HasIccProfile", out var hadIcc) == true &&
                    hadIcc == "true" && first.HasProfile("icc") == false && definition.SupportsIccProfile)
                {
                    issues.Add(ValidationIssue.Warning("image.verify.icc", "The source ICC profile was not preserved."));
                }

                if (settings.PreserveMetadata && input is not null && definition.SupportsMetadata &&
                    first.GetExifProfile() is null && input.Tags.TryGetValue("HasExif", out var hadExif) && hadExif == "true")
                {
                    issues.Add(ValidationIssue.Warning("image.verify.metadata", "The source EXIF metadata was not preserved."));
                }

                if (first.Orientation is not (Orientation.Undefined or Orientation.TopLeft))
                    issues.Add(ValidationIssue.Warning("image.verify.orientation", "The output still carries a non-normal orientation tag."));

                var actual = new MediaInfo
                {
                    FilePath = outputPath,
                    FileName = Path.GetFileName(outputPath),
                    Extension = Path.GetExtension(outputPath).ToLowerInvariant(),
                    FileSizeBytes = file.Length,
                    Category = MediaCategory.Image,
                    FormatId = actualFormatId,
                    FormatName = definition.DisplayName,
                    Width = (int)first.Width,
                    Height = (int)first.Height,
                    HasAlpha = first.HasAlpha,
                    IsAnimated = collection.Count > 1 && definition.SupportsAnimation,
                    IsMultipage = collection.Count > 1 && definition.Id is "tif" or "psd" or "ico",
                    FrameCount = collection.Count,
                    EngineId = Id
                };

                return new VerificationResult
                {
                    IsValid = true,
                    Summary = $"{definition.DisplayName} · {actual.Width} × {actual.Height} · {FormatBytes(file.Length)}",
                    Issues = issues,
                    ActualInfo = actual
                };
            }
            catch (Exception ex)
            {
                return VerificationResult.Fail("The output image could not be reopened.", details: ex.ToString());
            }
        }, cancellationToken);
    }

    public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var image = new MagickImage(inputPath);
                image.AutoOrient();
                image.Thumbnail((uint)Math.Max(16, maxSize), (uint)Math.Max(16, maxSize));
                if (!image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                }
                image.Quality = 85;
                image.Write(outputPath, MagickFormat.Png);
                return (string?)outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail creation failed for {Path}", inputPath);
                return null;
            }
        }, cancellationToken);
    }

    private static MagickReadSettings? BuildReadSettings(string path, ImageConversionSettings settings)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new MagickImageInfo(path);
            var width = (int)info.Width;
            var height = (int)info.Height;
            if (width <= 0 || height <= 0) return null;
            var (targetWidth, targetHeight, _) = ComputeTargetDimensions(width, height, settings);
            var scale = Math.Max(targetWidth / (double)width, targetHeight / (double)height);
            var density = Math.Clamp(96 * scale, 48, 2400);
            return new MagickReadSettings { Density = new Density(density) };
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height, bool CropToFill) ComputeTargetDimensions(int sourceWidth, int sourceHeight,
        ImageConversionSettings settings)
    {
        if (settings.ResizeWidth is null && settings.ResizeHeight is null)
            return (sourceWidth, sourceHeight, false);

        var width = settings.ResizeWidth;
        var height = settings.ResizeHeight;
        if (width is null && height is null) return (sourceWidth, sourceHeight, false);

        if (settings.ResizeMode == ImageResizeMode.Stretch || !settings.PreserveAspectRatio)
        {
            return (Math.Max(1, width ?? sourceWidth), Math.Max(1, height ?? sourceHeight), false);
        }

        if (width is not null && height is not null)
        {
            var scale = settings.ResizeMode == ImageResizeMode.Fill
                ? Math.Max(width.Value / (double)sourceWidth, height.Value / (double)sourceHeight)
                : Math.Min(width.Value / (double)sourceWidth, height.Value / (double)sourceHeight);
            return (Math.Max(1, (int)Math.Round(sourceWidth * scale)),
                Math.Max(1, (int)Math.Round(sourceHeight * scale)),
                settings.ResizeMode == ImageResizeMode.Fill);
        }

        if (width is not null)
        {
            var scale = width.Value / (double)sourceWidth;
            return (width.Value, Math.Max(1, (int)Math.Round(sourceHeight * scale)), false);
        }

        var heightScale = height!.Value / (double)sourceHeight;
        return (Math.Max(1, (int)Math.Round(sourceWidth * heightScale)), height.Value, false);
    }

    private static void Resize(IMagickImage<ushort> image, int targetWidth, int targetHeight, bool cropToFill,
        ResamplingAlgorithm algorithm)
    {
        var filter = MapFilter(algorithm);
        if (cropToFill)
        {
            var imageWidth = (int)image.Width;
            var imageHeight = (int)image.Height;
            var scale = Math.Max(targetWidth / (double)imageWidth, targetHeight / (double)imageHeight);
            var coverWidth = Math.Max(targetWidth, (int)Math.Round(imageWidth * scale));
            var coverHeight = Math.Max(targetHeight, (int)Math.Round(imageHeight * scale));
            image.Resize((uint)coverWidth, (uint)coverHeight, filter);
            image.Crop((uint)targetWidth, (uint)targetHeight, Gravity.Center);
        }
        else
        {
            image.Resize((uint)Math.Max(1, targetWidth), (uint)Math.Max(1, targetHeight), filter);
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

    private static void ApplyMetadataPolicy(IMagickImage<ushort> image, ImageConversionSettings settings)
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

    private static void ApplyFormatSettings(IMagickImage<ushort> image, FormatDefinition definition, ImageConversionSettings settings)
    {
        image.Quality = (uint)Math.Clamp(settings.Quality, 1, 100);

        switch (definition.Id)
        {
            case "png":
                image.Settings.SetDefine("png:compression-level",
                    Math.Clamp(settings.CompressionLevel ?? 6, 0, 9).ToString(CultureInfo.InvariantCulture));
                break;
            case "jpg":
                image.Settings.Interlace = settings.Progressive ? Interlace.Jpeg : Interlace.NoInterlace;
                break;
            case "webp":
                if (settings.Lossless)
                {
                    image.Settings.SetDefine("webp:lossless", "true");
                }
                break;
            case "avif":
                image.Settings.SetDefine("heic:speed", Math.Clamp(settings.AvifSpeed, 0, 10).ToString(CultureInfo.InvariantCulture));
                break;
            case "tif":
                image.Settings.Compression = settings.TiffCompression.ToLowerInvariant() switch
                {
                    "none" => CompressionMethod.NoCompression,
                    "zip" => CompressionMethod.Zip,
                    "jpeg" => CompressionMethod.JPEG,
                    _ => CompressionMethod.LZW
                };
                break;
            case "gif":
                if (settings.GifColors < 256)
                {
                    image.Quantize(new QuantizeSettings
                    {
                        Colors = (uint)Math.Clamp(settings.GifColors, 2, 256),
                        DitherMethod = DitherMethod.FloydSteinberg
                    });
                }
                break;
        }
    }

    private static string SanitizeIcoSizes(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "256,128,64,48,32,16";
        var sizes = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n is >= 16 and <= 256)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();
        return sizes.Count == 0 ? "256,128,64,48,32,16" : string.Join(",", sizes);
    }

    private static int OrientationDegrees(Orientation orientation) => orientation switch
    {
        Orientation.RightTop => 90,
        Orientation.BottomRight => 180,
        Orientation.LeftBottom => 270,
        _ => 0
    };

    private static string SimplifyError(Exception ex)
    {
        var message = ex.Message;
        var newline = message.IndexOf('\n');
        if (newline > 0) message = message[..newline];
        return message.Trim();
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";
}

public static class ImageFormatMapper
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
        ["psd"] = MagickFormat.Psd,
        ["svg"] = MagickFormat.Svg,
        ["exr"] = MagickFormat.Exr,
        ["hdr"] = MagickFormat.Hdr,
        ["qoi"] = MagickFormat.Qoi,
        ["dds"] = MagickFormat.Dds
    };

    private static readonly Dictionary<MagickFormat, string> FromMagickMap =
        ToMagickMap.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    static ImageFormatMapper()
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

    public static bool CanWrite(string formatId)
    {
        var format = ToMagick(formatId);
        if (format is null) return false;
        if (formatId is "svg" or "heic" or "psd") return false;
        var info = MagickNET.SupportedFormats.FirstOrDefault(f => f.Format == format);
        return info?.SupportsWriting ?? false;
    }
}
