using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.FFmpeg;

namespace PixelForge.Engines.FFmpegTools;

/// <summary>
/// Streaming video super-resolution and frame interpolation. FFmpeg supplies
/// raw RGB frames over a pipe, the selected ONNX models process each frame or
/// frame pair, and the result is encoded back through a second FFmpeg pipe.
/// The pipeline is bounded: only the previous and current frames are held in
/// memory while a video is processed.
/// </summary>
public sealed class VideoUpscalingEngine : IProcessingEngine
{
    private readonly IProcessRunner _runner;
    private readonly IModelManager _models;
    private readonly IFrameUpscaler _frameUpscaler;
    private readonly IFrameInterpolator _interpolator;
    private readonly ILogger<VideoUpscalingEngine> _logger;
    private readonly FFmpegLocator _locator;
    private readonly FFprobeService _probe;
    private readonly FFmpegCapabilityService _capabilities;

    public VideoUpscalingEngine(IProcessRunner runner, IModelManager models, IFrameUpscaler frameUpscaler,
        IFrameInterpolator interpolator, IAppPaths paths, ILogger<VideoUpscalingEngine> logger)
    {
        _runner = runner;
        _models = models;
        _frameUpscaler = frameUpscaler;
        _interpolator = interpolator;
        _logger = logger;
        _locator = FFmpegLocator.Discover();
        _probe = new FFprobeService(runner, _locator);
        _capabilities = new FFmpegCapabilityService(runner, _locator, paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FFmpegCapabilityService>.Instance);
    }

    public string Id => EngineIds.VideoUpscaling;
    public string DisplayName => "Video upscaling engine";
    public MediaCategory[] Categories => [MediaCategory.Video];

    public async Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable)
        {
            return new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = _locator.Error
            };
        }

        try
        {
            var formats = FormatCatalog.VideoFormats
                .Where(f => string.Equals(f.Id, "mp4", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.ToMediaFormat(Id, true, true))
                .ToList();
            return new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                Version = "FFmpeg n9.0.2 · ONNX Runtime",
                IsAvailable = true,
                Formats = formats
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video upscaling probe failed");
            return new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = ex.Message
            };
        }
    }

    public async Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        var info = await _probe.InspectAsync(path, cancellationToken);
        return info.Category == MediaCategory.Video ? info : info with { Category = MediaCategory.Video };
    }

    public async Task<ValidationResult> ValidateAsync(ProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();
        if (request.Operation != OperationKind.VideoUpscale || request.Settings is not VideoUpscaleSettings settings)
        {
            issues.Add(ValidationIssue.Error("video.upscale.settings", "The video upscaling settings are invalid."));
            return new ValidationResult { Issues = issues };
        }

        if (!_locator.IsAvailable)
        {
            issues.Add(ValidationIssue.Error("video.upscale.ffmpeg", _locator.Error ?? "FFmpeg is unavailable."));
            return new ValidationResult { Issues = issues };
        }

        if (!settings.EnableSuperResolution && !settings.EnableInterpolation)
            issues.Add(ValidationIssue.Error("video.upscale.none",
                "Enable video super-resolution, frame interpolation, or both."));
        if (!string.Equals(settings.ContainerFormatId, "mp4", StringComparison.OrdinalIgnoreCase))
            issues.Add(ValidationIssue.Error("video.upscale.container", "The Video Upscaler writes MP4 output."));

        var input = request.InputInfo;
        if (input is null || input.Category != MediaCategory.Video || !input.IsReadable)
        {
            issues.Add(ValidationIssue.Error("video.upscale.input", "Choose a readable video file."));
            return new ValidationResult { Issues = issues };
        }
        if (input.Width is not > 0 || input.Height is not > 0)
            issues.Add(ValidationIssue.Error("video.upscale.dimensions", "The input video dimensions could not be determined."));

        if (settings.EnableSuperResolution)
        {
            var model = _models.GetInstalled(settings.ImageModelId);
            if (model is null || !model.IsValid)
                issues.Add(ValidationIssue.Error("video.upscale.model.missing",
                    "The selected image upscaling model is not installed or failed validation."));
            else if (model.Descriptor.Task != ModelTask.ImageUpscale)
                issues.Add(ValidationIssue.Error("video.upscale.model.task",
                    "The selected model is not an image upscaling model."));
            else if (settings.ScaleFactor != model.Descriptor.ScaleFactor)
                issues.Add(ValidationIssue.Error("video.upscale.scale",
                    $"{model.Descriptor.Name} produces {model.Descriptor.ScaleFactor}× output. Select that native scale."));
        }

        if (settings.EnableInterpolation)
        {
            var model = _models.GetInstalled(settings.InterpolationModelId);
            if (model is null || !model.IsValid)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.missing",
                    "The selected frame interpolation model is not installed or failed validation."));
            else if (model.Descriptor.Task != ModelTask.FrameInterpolation)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.task",
                    "The selected model is not a frame interpolation model."));
            if (settings.FrameRateMultiplier is < 2 or > 4)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.multiplier",
                    "Frame interpolation must be 2×, 3× or 4×."));
        }

        if (input.Width is > 0 && input.Height is > 0)
        {
            var scale = settings.EnableSuperResolution ? Math.Max(1, settings.ScaleFactor) : 1;
            var width = input.Width.Value * scale;
            var height = input.Height.Value * scale;
            if (width > 15360 || height > 15360 || (long)width * height > 100_000_000)
                issues.Add(ValidationIssue.Error("video.upscale.too.large",
                    $"The output would be {width} × {height}, which exceeds the safe video limit."));
        }

        if (input.AudioCodec is null)
            issues.Add(ValidationIssue.Info("video.upscale.noaudio", "The source has no audio stream."));
        if (input.SubtitleStreamCount > 0)
            issues.Add(ValidationIssue.Warning("video.upscale.subtitles",
                "Subtitle streams are not preserved and will be dropped."));
        if (input.ChapterCount > 0)
            issues.Add(ValidationIssue.Warning("video.upscale.chapters", "Chapters are not preserved."));
        if (input.RotationDegrees is not (null or 0))
            issues.Add(ValidationIssue.Warning("video.upscale.rotation",
                "Rotation metadata is applied to the output frames; the MP4 itself does not carry a rotation flag."));

        var encoder = await ResolveEncoderAsync(settings, cancellationToken);
        if (encoder is null)
            issues.Add(ValidationIssue.Error("video.upscale.encoder", "No usable H.264 encoder is available."));

        return new ValidationResult { Issues = issues };
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default)
    {
        if (request.Settings is not VideoUpscaleSettings settings)
            return ProcessingResult.Fail("The video upscaling settings are invalid.");
        if (!_locator.IsAvailable || _locator.FfmpegPath is null)
            return ProcessingResult.Fail(_locator.Error ?? "FFmpeg is unavailable.");

        var info = request.InputInfo ?? await _probe.InspectAsync(request.InputPath, cancellationToken);
        if (info.Category != MediaCategory.Video || info.Width is not > 0 || info.Height is not > 0)
            return ProcessingResult.Fail("The input video could not be inspected.");

        var sourceWidth = info.Width.Value;
        var sourceHeight = info.Height.Value;
        var sourceFps = info.FrameRate is > 0 ? info.FrameRate.Value : 30;
        var duration = info.DurationSeconds ?? 0;
        var multiplier = settings.EnableInterpolation ? Math.Clamp(settings.FrameRateMultiplier, 2, 4) : 1;
        var outputFps = sourceFps * multiplier;

        var imageModel = settings.EnableSuperResolution ? _models.GetInstalled(settings.ImageModelId) : null;
        if (settings.EnableSuperResolution && imageModel is null)
            return ProcessingResult.Fail("The selected image upscaling model is not installed.");
        var scale = settings.EnableSuperResolution ? imageModel!.Descriptor.ScaleFactor : 1;
        var outputWidth = MakeEven(sourceWidth * scale);
        var outputHeight = MakeEven(sourceHeight * scale);
        if ((long)outputWidth * outputHeight > 100_000_000)
            return ProcessingResult.Fail("The output frame is too large for safe processing.");

        var interpolationPath = settings.EnableInterpolation
            ? await _models.ResolveModelPathAsync(settings.InterpolationModelId, cancellationToken)
            : null;
        if (settings.EnableInterpolation && interpolationPath is null)
            return ProcessingResult.Fail("The selected frame interpolation model is not installed.");

        var encoder = await ResolveEncoderAsync(settings, cancellationToken);
        if (encoder is null)
            return ProcessingResult.Fail("No usable H.264 encoder is available.");

        var audioCopy = info.AudioCodec is "aac" or "mp3" or "ac3" or "eac3" or "alac";
        var warnings = new List<string>();
        if (info.AudioCodec is not null && !audioCopy)
            warnings.Add($"The source audio is {info.AudioCodec}; it was re-encoded to AAC for MP4 compatibility.");
        if (info.SubtitleStreamCount > 0)
            warnings.Add("Subtitle streams were not preserved.");
        if (info.ChapterCount > 0)
            warnings.Add("Chapters were not preserved.");
        if (info.RotationDegrees is not (null or 0))
            warnings.Add("Rotation metadata was applied to the output frames; the MP4 itself has no rotation flag.");

        var stopwatch = Stopwatch.StartNew();
        using var decoder = StartDecoder(request.InputPath, sourceWidth, sourceHeight);
        using var encoderProcess = StartEncoder(request.InputPath, request.TempOutputPath, outputWidth, outputHeight,
            outputFps, encoder, settings, info, audioCopy);

        var decoderErrors = new BoundedText();
        var encoderErrors = new BoundedText();
        decoder.ErrorDataReceived += (_, e) => decoderErrors.Add(e.Data);
        encoderProcess.ErrorDataReceived += (_, e) => encoderErrors.Add(e.Data);
        decoder.BeginErrorReadLine();
        encoderProcess.BeginErrorReadLine();

        var sourceFrameBytes = checked(sourceWidth * sourceHeight * 3);
        var bufferA = new byte[sourceFrameBytes];
        var bufferB = new byte[sourceFrameBytes];
        var currentBuffer = bufferA;
        byte[]? previous = null;
        long inputFrames = 0;
        long outputFrames = 0;
        var totalFrames = duration > 0 ? (long)Math.Round(duration * sourceFps) : 0;
        var sceneCuts = 0;
        ExecutionProviderKind? actualProvider = null;

        try
        {
            while (await ReadFrameAsync(decoder.StandardOutput.BaseStream, currentBuffer, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputFrames++;
                byte[] processed;
                if (settings.EnableSuperResolution)
                {
                    var upscaled = await _frameUpscaler.UpscaleFrameAsync(currentBuffer, sourceWidth, sourceHeight,
                        settings.ImageModelId, settings.ExecutionProvider, settings.TileSize, settings.TileOverlap,
                        cancellationToken);
                    processed = upscaled.Rgb24;
                    actualProvider ??= upscaled.Provider;
                    if (upscaled.Warning is not null && !warnings.Contains(upscaled.Warning))
                        warnings.Add(upscaled.Warning);
                    if (upscaled.Width != outputWidth || upscaled.Height != outputHeight)
                        processed = ResizeRgbBilinear(processed, upscaled.Width, upscaled.Height,
                            outputWidth, outputHeight);
                }
                else if (sourceWidth == outputWidth && sourceHeight == outputHeight)
                {
                    processed = currentBuffer;
                }
                else
                {
                    processed = ResizeRgbBilinear(currentBuffer, sourceWidth, sourceHeight, outputWidth, outputHeight);
                }

                var previousSource = processed;
                var isCut = previous is not null &&
                            (settings.EnableInterpolation || settings.EnableTemporalStability) &&
                            IsSceneCut(previous, processed, outputWidth, outputHeight);
                if (settings.EnableTemporalStability && previous is not null && !isCut)
                {
                    processed = StabilizeTemporal(previous, processed, outputWidth, outputHeight,
                        settings.TemporalStabilityStrength);
                }

                if (previous is not null && settings.EnableInterpolation)
                {
                    if (isCut)
                    {
                        sceneCuts++;
                        for (var i = 1; i < multiplier; i++)
                        {
                            await WriteFrameAsync(encoderProcess.StandardInput.BaseStream, processed, cancellationToken);
                            outputFrames++;
                        }
                    }
                    else
                    {
                        for (var i = 1; i < multiplier; i++)
                        {
                            var interpolated = await _interpolator.InterpolateFrameAsync(interpolationPath!,
                                previous, processed, outputWidth, outputHeight, i / (double)multiplier,
                                settings.ExecutionProvider, cancellationToken);
                            if (interpolated.Warning is not null && !warnings.Contains(interpolated.Warning))
                                warnings.Add(interpolated.Warning);
                            actualProvider ??= interpolated.Provider;
                            await WriteFrameAsync(encoderProcess.StandardInput.BaseStream, interpolated.Rgb24,
                                cancellationToken);
                            outputFrames++;
                        }
                    }
                }

                await WriteFrameAsync(encoderProcess.StandardInput.BaseStream, processed, cancellationToken);
                outputFrames++;
                previous = previousSource;
                currentBuffer = ReferenceEquals(currentBuffer, bufferA) ? bufferB : bufferA;

                var fraction = totalFrames > 0 ? Math.Clamp(inputFrames / (double)totalFrames, 0, 1) : -1;
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                var eta = totalFrames > inputFrames && inputFrames > 0
                    ? elapsed / inputFrames * (totalFrames - inputFrames)
                    : (double?)null;
                progress.Report(new ProcessingProgress
                {
                    Fraction = fraction < 0 ? -1 : 0.03 + fraction * 0.92,
                    Message = $"Video upscale · frame {inputFrames}" + (totalFrames > 0 ? $" of {totalFrames}" : "") +
                              (settings.EnableInterpolation ? $" · {multiplier}× interpolation" : ""),
                    EtaSeconds = eta,
                    CurrentFrame = (int)Math.Min(int.MaxValue, inputFrames),
                    TotalFrames = totalFrames > 0 ? (int)Math.Min(int.MaxValue, totalFrames) : null
                });
            }

            encoderProcess.StandardInput.BaseStream.Close();
            await encoderProcess.WaitForExitAsync(cancellationToken);
            await decoder.WaitForExitAsync(cancellationToken);
            stopwatch.Stop();

            if (decoder.ExitCode != 0)
                return ProcessingResult.Fail("FFmpeg could not decode the input video.",
                    decoderErrors.ToString());
            if (encoderProcess.ExitCode != 0)
                return ProcessingResult.Fail("FFmpeg could not encode the processed video. " +
                                             encoderErrors.ToString(),
                    encoderErrors.ToString());
            if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
                return ProcessingResult.Fail("The encoder produced no output file.");

            if (sceneCuts > 0)
                warnings.Add($"{sceneCuts} scene cut{(sceneCuts == 1 ? "" : "s")} were detected; frames were duplicated across the cut instead of interpolated.");
            if (settings.EnableTemporalStability)
                warnings.Add("Optional temporal stability was applied with motion-adaptive blending; moving edges retain the current frame.");

            var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
            if (!verification.IsValid)
                return ProcessingResult.Fail(verification.Summary, verification.Details);

            var bytes = new FileInfo(request.TempOutputPath).Length;
            progress.Report(ProcessingProgress.Percent(1, "Done"));
            return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo, bytes, stopwatch.Elapsed,
                    verification, warnings)
                with
                {
                    RequestedProvider = settings.ExecutionProvider.ToString(),
                    ActualProvider = actualProvider is null
                        ? "CPU (FFmpeg pipeline only)"
                        : (actualProvider == ExecutionProviderKind.DirectML ? "DirectML (GPU)" : "CPU"),
                    FallbackReason = settings.ExecutionProvider == ExecutionProviderKind.DirectML &&
                                     actualProvider != ExecutionProviderKind.DirectML
                        ? "DirectML was requested but the frame pipeline ran on the CPU."
                        : null,
                    ModelId = string.Join(" + ", new[]
                    {
                        settings.EnableSuperResolution ? settings.ImageModelId : null,
                        settings.EnableInterpolation ? settings.InterpolationModelId : null
                    }.Where(id => !string.IsNullOrWhiteSpace(id))),
                    RuntimeVersion = "FFmpeg + ONNX Runtime"
                };
        }
        catch (OperationCanceledException)
        {
            Kill(decoder);
            Kill(encoderProcess);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video upscaling failed for {Input}", request.InputPath);
            Kill(decoder);
            Kill(encoderProcess);
            return ProcessingResult.Fail("Video upscaling failed: " + ex.Message, ex.ToString());
        }
    }

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (request.Settings is not VideoUpscaleSettings settings)
            return VerificationResult.Fail("The video upscaling settings are invalid.");
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
            return VerificationResult.Fail("The output video is empty.");
        var actual = await _probe.InspectAsync(outputPath, cancellationToken);
        if (!actual.IsReadable || actual.Category != MediaCategory.Video)
            return VerificationResult.Fail(actual.Error ?? "The output video could not be reopened.");

        var input = request.InputInfo;
        var sourceWidth = input?.Width ?? 0;
        var sourceHeight = input?.Height ?? 0;
        var sourceFps = input?.FrameRate is > 0 ? input.FrameRate.Value : 30;
        var multiplier = settings.EnableInterpolation ? Math.Clamp(settings.FrameRateMultiplier, 2, 4) : 1;
        var expectedFps = sourceFps * multiplier;
        var expectedWidth = sourceWidth > 0 ? MakeEven(sourceWidth * (settings.EnableSuperResolution ? settings.ScaleFactor : 1)) : 0;
        var expectedHeight = sourceHeight > 0 ? MakeEven(sourceHeight * (settings.EnableSuperResolution ? settings.ScaleFactor : 1)) : 0;

        if (expectedWidth > 0 && (actual.Width != expectedWidth || actual.Height != expectedHeight))
            return VerificationResult.Fail(
                $"The output is {actual.Width} × {actual.Height}, expected {expectedWidth} × {expectedHeight}.");
        if (actual.FrameRate is > 0 && Math.Abs(actual.FrameRate.Value - expectedFps) > Math.Max(0.5, expectedFps * 0.02))
            return VerificationResult.Fail(
                $"The output is {actual.FrameRate:0.###} fps, expected {expectedFps:0.###} fps.");
        if (input?.DurationSeconds is > 0 && actual.DurationSeconds is > 0 &&
            Math.Abs(actual.DurationSeconds.Value - input.DurationSeconds.Value) > Math.Max(0.6, input.DurationSeconds.Value * 0.03))
            return VerificationResult.Fail(
                $"The output duration is {actual.DurationSeconds:0.00}s, expected about {input.DurationSeconds:0.00}s.");
        if (input?.AudioCodec is not null && settings.AudioCodecId != FormatIds.None && actual.AudioCodec is null)
            return VerificationResult.Fail("The source had audio, but the output has no audio stream.");
        if (input?.AudioCodec is null && actual.AudioCodec is not null)
            return VerificationResult.Fail("The output contains an unexpected audio stream.");

        var summary =
            $"{actual.Width} × {actual.Height} · {actual.FrameRate:0.###} fps · {actual.DurationSeconds:0.00}s" +
            (actual.AudioCodec is null ? " · silent" : $" · {actual.AudioCodec}");
        return VerificationResult.Ok(actual, summary);
    }

    public async Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable || _locator.FfmpegPath is null) return null;
        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath,
            Arguments =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-ss", "1", "-i", inputPath,
                "-frames:v", "1", "-vf", $"scale={Math.Max(16, maxSize)}:-2:flags=lanczos",
                outputPath
            ],
            Timeout = TimeSpan.FromMinutes(2),
            MaxCapturedLines = 80
        }, cancellationToken);
        return result.Succeeded && File.Exists(outputPath) ? outputPath : null;
    }

    private Process StartDecoder(string inputPath, int width, int height)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _locator.FfmpegPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-i", inputPath,
                     "-map", "0:v:0", "-an", "-sn", "-dn",
                     "-f", "rawvideo", "-pix_fmt", "rgb24", "-fps_mode", "passthrough", "pipe:1"
                 })
            startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    private Process StartEncoder(string inputPath, string outputPath, int width, int height, double fps,
        EncoderCapability encoder, VideoUpscaleSettings settings, MediaInfo input, bool audioCopy)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _locator.FfmpegPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        void Add(params string[] values)
        {
            foreach (var value in values) startInfo.ArgumentList.Add(value);
        }

        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-y");
        Add("-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}",
            "-r", fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:0");
        Add("-i", inputPath);
        Add("-map", "0:v:0", "-map", "1:a:0?");
        Add("-c:v", encoder.Id);
        Add("-b:v", $"{Math.Clamp(settings.VideoBitrateKbps, 100, 200_000)}k");
        if (encoder.Id == "libopenh264") Add("-rc_mode", "bitrate");
        Add("-pix_fmt", "yuv420p");
        if (audioCopy) Add("-c:a", "copy");
        else if (input.AudioCodec is not null) Add("-c:a", "aac", "-b:a", $"{Math.Clamp(settings.AudioBitrateKbps, 8, 1536)}k");
        Add("-shortest", "-movflags", "+faststart", outputPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    private async Task<EncoderCapability?> ResolveEncoderAsync(VideoUpscaleSettings settings,
        CancellationToken cancellationToken)
    {
        if (!_locator.IsAvailable) return null;
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        if (!capabilities.Capability.IsAvailable) return null;
        var codec = capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
            string.Equals(c.Id, "h264", StringComparison.OrdinalIgnoreCase));
        if (codec is null) return null;
        var encoders = codec.Encoders.Where(e => e.IsAvailable).OrderBy(e => e.Priority).ToList();
        if (!string.IsNullOrWhiteSpace(settings.VideoEncoderId))
        {
            var requested = encoders.FirstOrDefault(e =>
                string.Equals(e.Id, settings.VideoEncoderId, StringComparison.OrdinalIgnoreCase));
            if (requested is not null) return requested;
        }
        return encoders.FirstOrDefault(e => e.Id == "libopenh264")
               ?? encoders.FirstOrDefault(e => !e.IsHardware)
               ?? encoders.FirstOrDefault();
    }

    private static async Task<bool> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException("The decoder ended in the middle of a frame.");
            offset += read;
        }
        return true;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(frame.AsMemory(0, frame.Length), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Motion-adaptive temporal stabilization. Only pixels whose local luma
    /// difference is small are blended; moving edges remain the current frame.
    /// Scene cuts are handled by the caller and reset the blend.
    /// </summary>
    private static byte[] StabilizeTemporal(byte[] previous, byte[] current, int width, int height, int strength)
    {
        if (previous.Length != current.Length || width <= 0 || height <= 0) return current;
        var amount = Math.Clamp(strength, 0, 100) / 100.0 * 0.55;
        if (amount <= 0) return current;
        var output = new byte[current.Length];
        const double threshold = 20.0;
        for (var i = 0; i < current.Length; i += 3)
        {
            var lumaPrevious = (previous[i] * 299 + previous[i + 1] * 587 + previous[i + 2] * 114) / 1000.0;
            var lumaCurrent = (current[i] * 299 + current[i + 1] * 587 + current[i + 2] * 114) / 1000.0;
            var difference = Math.Abs(lumaPrevious - lumaCurrent);
            if (difference >= threshold)
            {
                output[i] = current[i];
                output[i + 1] = current[i + 1];
                output[i + 2] = current[i + 2];
                continue;
            }

            var blend = amount * (1.0 - difference / threshold);
            output[i] = (byte)Math.Clamp((int)Math.Round(current[i] * (1 - blend) + previous[i] * blend), 0, 255);
            output[i + 1] = (byte)Math.Clamp((int)Math.Round(current[i + 1] * (1 - blend) + previous[i + 1] * blend), 0, 255);
            output[i + 2] = (byte)Math.Clamp((int)Math.Round(current[i + 2] * (1 - blend) + previous[i + 2] * blend), 0, 255);
        }
        return output;
    }

    private static bool IsSceneCut(byte[] first, byte[] second, int width, int height)
    {
        if (first.Length != second.Length || width <= 0 || height <= 0) return false;
        long difference = 0;
        var samples = 0;
        var stepY = Math.Max(1, height / 36);
        var stepX = Math.Max(1, width / 36);
        for (var y = 0; y < height; y += stepY)
        {
            for (var x = 0; x < width; x += stepX)
            {
                var index = (y * width + x) * 3;
                var lumaFirst = (first[index] * 299 + first[index + 1] * 587 + first[index + 2] * 114) / 1000;
                var lumaSecond = (second[index] * 299 + second[index + 1] * 587 + second[index + 2] * 114) / 1000;
                difference += Math.Abs(lumaFirst - lumaSecond);
                samples++;
            }
        }
        return samples > 0 && difference / (double)samples > 42;
    }

    private static byte[] ResizeRgbBilinear(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var target = new byte[width * height * 3];
        var scaleX = sourceWidth / (double)width;
        var scaleY = sourceHeight / (double)height;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) * scaleY - 0.5, 0, sourceHeight - 1);
            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(sourceHeight - 1, y0 + 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) * scaleX - 0.5, 0, sourceWidth - 1);
                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(sourceWidth - 1, x0 + 1);
                var fx = sx - x0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source[(y0 * sourceWidth + x0) * 3 + channel] * (1 - fx) +
                              source[(y0 * sourceWidth + x1) * 3 + channel] * fx;
                    var bottom = source[(y1 * sourceWidth + x0) * 3 + channel] * (1 - fx) +
                                 source[(y1 * sourceWidth + x1) * 3 + channel] * fx;
                    target[(y * width + x) * 3 + channel] =
                        (byte)Math.Clamp(top * (1 - fy) + bottom * fy, 0, 255);
                }
            }
        }
        return target;
    }

    private static int MakeEven(int value) => Math.Max(2, value % 2 == 0 ? value : value - 1);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may already have exited.
        }
    }

    private sealed class BoundedText
    {
        private readonly Queue<string> _lines = new();
        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 30) _lines.Dequeue();
            }
        }
        public override string ToString()
        {
            lock (_lines) return string.Join(Environment.NewLine, _lines);
        }
    }
}
