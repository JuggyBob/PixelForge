using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.FFmpeg;

public sealed class FFmpegEngine : IProcessingEngine
{
    private readonly IProcessRunner _processRunner;
    private readonly FFmpegLocator _locator;
    private readonly FFprobeService _probe;
    private readonly FFmpegCapabilityService _capabilities;
    private readonly ILogger<FFmpegEngine> _logger;

    public FFmpegEngine(IProcessRunner processRunner, IAppPaths paths, ILogger<FFmpegEngine> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
        _locator = FFmpegLocator.Discover();
        _probe = new FFprobeService(processRunner, _locator);
        _capabilities = new FFmpegCapabilityService(processRunner, _locator, paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FFmpegCapabilityService>.Instance);
    }

    public string Id => EngineIds.FFmpeg;
    public string DisplayName => "FFmpeg";
    public MediaCategory[] Categories => [MediaCategory.Audio, MediaCategory.Video];

    public async Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = await _capabilities.GetAsync(forceRefresh: false, cancellationToken);
        return capabilities.Capability;
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable) return Task.FromResult<MediaInfo?>(null);
        return InspectInternalAsync(path, cancellationToken);
    }

    private async Task<MediaInfo?> InspectInternalAsync(string path, CancellationToken cancellationToken)
    {
        var info = await _probe.InspectAsync(path, cancellationToken);
        return info;
    }

    public async Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable)
            return ValidationResult.Failure("ffmpeg.missing", _locator.Error ?? "FFmpeg is unavailable.");

        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        if (!capabilities.Capability.IsAvailable)
            return ValidationResult.Failure("ffmpeg.unavailable", capabilities.Error ?? "FFmpeg is unavailable.");

        return request.Operation switch
        {
            OperationKind.AudioConvert when request.Settings is AudioConversionSettings audio =>
                ValidateAudioRequest(audio, request, capabilities.Capability),
            OperationKind.VideoConvert when request.Settings is VideoConversionSettings video =>
                ValidateVideoRequest(video, request, capabilities.Capability),
            _ => ValidationResult.Failure("ffmpeg.operation", "FFmpeg does not support this operation.")
        };
    }

    private static ValidationResult ValidateAudioRequest(AudioConversionSettings settings, ProcessingRequest request,
        EngineCapability capability)
    {
        var issues = new List<ValidationIssue>();
        var container = capability.Formats.FirstOrDefault(f =>
            f.Category == MediaCategory.Audio && f.CanWrite &&
            string.Equals(f.Id, settings.OutputFormatId, StringComparison.OrdinalIgnoreCase));
        if (container is null)
            return ValidationResult.Failure("ffmpeg.audio.container", "The selected audio container is not available.");

        var codec = capability.AudioCodecs.FirstOrDefault(c => string.Equals(c.Id, settings.CodecId, StringComparison.OrdinalIgnoreCase));
        if (codec is null)
            return ValidationResult.Failure("ffmpeg.audio.codec", "The selected audio codec is not available.");
        if (!CompatibilityMatrix.IsAudioCodecCompatible(container.Id, codec.Id))
            issues.Add(ValidationIssue.Error("ffmpeg.audio.incompatible",
                $"{codec.DisplayName} cannot be stored in {container.DisplayName}.", "CodecId"));

        var encoder = codec.Encoders.FirstOrDefault(e => e.IsAvailable && string.Equals(e.Id, settings.EncoderId, StringComparison.OrdinalIgnoreCase))
                      ?? codec.Encoders.FirstOrDefault(e => e.IsAvailable);
        if (encoder is null)
            issues.Add(ValidationIssue.Error("ffmpeg.audio.encoder", "No usable audio encoder is available."));

        if (request.InputInfo is { IsReadable: true } info && info.AudioCodec is null)
            issues.Add(ValidationIssue.Error("ffmpeg.audio.noaudio", "The input has no audio stream."));

        if (settings.BitrateKbps is < 8 or > 1536)
            issues.Add(ValidationIssue.Error("ffmpeg.audio.bitrate", "Bitrate must be between 8 and 1536 kbps."));
        return new ValidationResult { Issues = issues };
    }

    private static ValidationResult ValidateVideoRequest(VideoConversionSettings settings, ProcessingRequest request,
        EngineCapability capability)
    {
        var issues = new List<ValidationIssue>();
        var container = capability.Formats.FirstOrDefault(f =>
            f.Category == MediaCategory.Video && f.CanWrite &&
            string.Equals(f.Id, settings.ContainerFormatId, StringComparison.OrdinalIgnoreCase));
        if (container is null)
            return ValidationResult.Failure("ffmpeg.video.container", "The selected video container is not available.");

        var copyVideo = settings.VideoCodecId == FormatIds.Copy;
        if (copyVideo && (settings.ResizeMode != VideoResizeMode.None || settings.FrameRate > 0))
            issues.Add(ValidationIssue.Error("ffmpeg.video.copy.filters",
                "Resolution and frame-rate changes require transcoding; stream copy cannot apply filters."));

        if (!copyVideo && !string.Equals(container.Id, "gif", StringComparison.OrdinalIgnoreCase))
        {
            var codec = capability.VideoCodecs.FirstOrDefault(c =>
                string.Equals(c.Id, settings.VideoCodecId, StringComparison.OrdinalIgnoreCase));
            if (codec is null)
                issues.Add(ValidationIssue.Error("ffmpeg.video.codec", "The selected video codec is not available."));
            else if (!CompatibilityMatrix.IsVideoCodecCompatible(container.Id, codec.Id))
                issues.Add(ValidationIssue.Error("ffmpeg.video.incompatible",
                    $"{codec.DisplayName} cannot be stored in {container.DisplayName}."));
            else if (!codec.Encoders.Any(e => e.IsAvailable))
                issues.Add(ValidationIssue.Error("ffmpeg.video.encoder", "No usable video encoder is available for this codec."));
        }

        var copyAudio = settings.AudioCodecId == FormatIds.Copy;
        var noAudio = settings.AudioCodecId == FormatIds.None;
        if (!copyAudio && !noAudio && CompatibilityMatrix.ContainerSupportsAudio(container.Id))
        {
            var audioCodec = capability.AudioCodecs.FirstOrDefault(c =>
                string.Equals(c.Id, settings.AudioCodecId, StringComparison.OrdinalIgnoreCase));
            if (audioCodec is null)
                issues.Add(ValidationIssue.Error("ffmpeg.video.audiocodec", "The selected audio codec is not available."));
            else if (!CompatibilityMatrix.IsAudioCodecCompatible(container.Id, audioCodec.Id))
                issues.Add(ValidationIssue.Error("ffmpeg.video.audioincompatible",
                    $"{audioCodec.DisplayName} cannot be stored in {container.DisplayName}."));
        }

        var info = request.InputInfo;
        if (info is not null)
        {
            if (copyVideo && info.VideoCodec is not null && !CompatibilityMatrix.CanCopyVideo(container.Id, info.VideoCodec))
                issues.Add(ValidationIssue.Error("ffmpeg.video.copy.incompatible",
                    $"{info.FileName} uses {info.VideoCodec}, which cannot be copied into {container.DisplayName}."));
            if (copyAudio && info.AudioCodec is not null && !CompatibilityMatrix.CanCopyAudio(container.Id, info.AudioCodec))
                issues.Add(ValidationIssue.Error("ffmpeg.video.audiocopy.incompatible",
                    $"{info.FileName} audio uses {info.AudioCodec}, which cannot be copied into {container.DisplayName}."));
        }

        return new ValidationResult { Issues = issues };
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable)
            return ProcessingResult.Fail(_locator.Error ?? "FFmpeg is unavailable.");

        try
        {
            return request.Operation switch
            {
                OperationKind.AudioConvert when request.Settings is AudioConversionSettings audio =>
                    await ProcessAudioAsync(request, audio, progress, cancellationToken),
                OperationKind.VideoConvert when request.Settings is VideoConversionSettings video =>
                    await ProcessVideoAsync(request, video, progress, cancellationToken),
                _ => ProcessingResult.Fail("FFmpeg does not support this operation.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg processing failed for {Input}", request.InputPath);
            return ProcessingResult.Fail("FFmpeg failed to process this file.", ex.ToString());
        }
    }

    private async Task<ProcessingResult> ProcessAudioAsync(ProcessingRequest request, AudioConversionSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capability = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var codec = capability.Capability.AudioCodecs.FirstOrDefault(c =>
            string.Equals(c.Id, settings.CodecId, StringComparison.OrdinalIgnoreCase));
        if (codec is null)
            return ProcessingResult.Fail("The selected audio codec is not available.");
        var encoder = codec.Encoders.FirstOrDefault(e => e.IsAvailable &&
                            string.Equals(e.Id, settings.EncoderId, StringComparison.OrdinalIgnoreCase))
                      ?? codec.Encoders.FirstOrDefault(e => e.IsAvailable);
        if (encoder is null)
            return ProcessingResult.Fail("No usable audio encoder is available.");

        var format = FormatCatalog.FindById(settings.OutputFormatId);
        if (format is null)
            return ProcessingResult.Fail("The selected audio format is unknown.");

        var args = CommonInputArguments(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn"]);
        args.AddRange(["-c:a", encoder.Id]);
        ApplyAudioOptions(args, CodecCatalog.FindEncoder(encoder.Id), settings);
        if (settings.PreserveMetadata && !string.Equals(format.Id, "aac", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-map_metadata", "0"]);
        else
            args.AddRange(["-map_metadata", "-1"]);
        if (string.Equals(format.Id, "mp3", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-id3v2_version", "3"]);
        if (string.Equals(format.Id, "m4a", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", format.MuxerName ?? format.Id, request.TempOutputPath]);

        var duration = request.InputInfo?.DurationSeconds ?? 0;
        var parser = new FFmpegProgressParser(duration, progress);
        var started = Stopwatch.StartNew();
        var result = await _processRunner.RunStreamingAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments = args,
            Timeout = TimeSpan.FromHours(12),
            MaxCapturedLines = 300
        }, parser.ParseLine, null, cancellationToken);
        started.Stop();

        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (result.TimedOut) return ProcessingResult.Fail("FFmpeg timed out while converting the audio.", result.StandardError);
        if (!result.Succeeded)
            return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError, started.Elapsed);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg completed but produced no audio output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid)
            return ProcessingResult.Fail(verification.Summary, verification.Details, started.Elapsed, result.StandardError);
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification);
    }

    private async Task<ProcessingResult> ProcessVideoAsync(ProcessingRequest request, VideoConversionSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capability = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var container = capability.Capability.Formats.FirstOrDefault(f =>
            f.Category == MediaCategory.Video &&
            string.Equals(f.Id, settings.ContainerFormatId, StringComparison.OrdinalIgnoreCase));
        if (container is null)
            return ProcessingResult.Fail("The selected video container is not available.");

        var isGif = string.Equals(container.Id, "gif", StringComparison.OrdinalIgnoreCase);
        var args = CommonInputArguments(request.InputPath);
        if (isGif)
        {
            BuildGifCommand(args, request, settings);
        }
        else
        {
            args.AddRange(["-map", "0:v:0"]);
            BuildVideoCommand(args, request, settings, container, capability.Capability);
        }

        args.AddRange(["-f", container.MuxerName ?? container.Id, request.TempOutputPath]);
        var duration = request.InputInfo?.DurationSeconds ?? 0;
        var parser = new FFmpegProgressParser(duration, progress);
        var started = Stopwatch.StartNew();
        var result = await _processRunner.RunStreamingAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments = args,
            Timeout = TimeSpan.FromHours(24),
            MaxCapturedLines = 400
        }, parser.ParseLine, null, cancellationToken);
        started.Stop();

        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (result.TimedOut) return ProcessingResult.Fail("FFmpeg timed out while converting the video.", result.StandardError);
        if (!result.Succeeded)
            return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError, started.Elapsed);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg completed but produced no video output.");

        var verification = await VerifyOutputAsync(request, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid)
            return ProcessingResult.Fail(verification.Summary, verification.Details, started.Elapsed, result.StandardError);
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification);
    }

    private void BuildVideoCommand(List<string> args, ProcessingRequest request, VideoConversionSettings settings,
        MediaFormat container, EngineCapability capability)
    {
        var copyVideo = settings.VideoCodecId == FormatIds.Copy;
        EncoderCapability? encoder = null;
        if (!copyVideo)
        {
            var codec = capability.VideoCodecs.FirstOrDefault(c =>
                string.Equals(c.Id, settings.VideoCodecId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The video codec {settings.VideoCodecId} is not available.");
            encoder = codec.Encoders.FirstOrDefault(e => e.IsAvailable &&
                        string.Equals(e.Id, settings.VideoEncoderId, StringComparison.OrdinalIgnoreCase))
                      ?? codec.Encoders.First(e => e.IsAvailable);
            args.AddRange(["-c:v", encoder.Id]);
            ApplyVideoOptions(args, CodecCatalog.FindEncoder(encoder.Id), settings, request.InputInfo, encoder);
        }
        else
        {
            args.AddRange(["-c:v", "copy"]);
        }

        if (string.Equals(settings.AudioCodecId, FormatIds.None, StringComparison.OrdinalIgnoreCase) ||
            !CompatibilityMatrix.ContainerSupportsAudio(container.Id))
        {
            args.Add("-an");
        }
        else if (string.Equals(settings.AudioCodecId, FormatIds.Copy, StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-map", "0:a:0?", "-c:a", "copy"]);
        }
        else
        {
            var audioCodec = capability.AudioCodecs.FirstOrDefault(c =>
                string.Equals(c.Id, settings.AudioCodecId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The audio codec {settings.AudioCodecId} is not available.");
            var audioEncoder = audioCodec.Encoders.FirstOrDefault(e => e.IsAvailable &&
                                string.Equals(e.Id, settings.AudioEncoderId, StringComparison.OrdinalIgnoreCase))
                               ?? audioCodec.Encoders.First(e => e.IsAvailable);
            args.AddRange(["-map", "0:a:0?", "-c:a", audioEncoder.Id]);
            ApplyAudioOptions(args, CodecCatalog.FindEncoder(audioEncoder.Id), new AudioConversionSettings
            {
                CodecId = audioCodec.Id,
                EncoderId = audioEncoder.Id,
                BitrateKbps = settings.AudioBitrateKbps,
                SampleRate = settings.AudioSampleRate,
                Channels = settings.AudioChannels,
                RateControlMode = AudioRateControlMode.ConstantBitrate,
                Quality = 5
            });
        }

        if (settings.SubtitleMode == SubtitleMode.Drop || !CompatibilityMatrix.ContainerSupportsSubtitles(container.Id))
        {
            args.Add("-sn");
        }
        else
        {
            args.AddRange(["-map", "0:s?"]);
            var subtitleCodec = container.Id switch
            {
                "mp4" or "mov" => "mov_text",
                "webm" => "webvtt",
                "mkv" when settings.SubtitleMode == SubtitleMode.Convert => "srt",
                _ => settings.SubtitleMode == SubtitleMode.Copy ? "copy" : "mov_text"
            };
            args.AddRange(["-c:s", subtitleCodec]);
        }

        if (settings.PreserveChapters && CompatibilityMatrix.ContainerSupportsChapters(container.Id))
            args.AddRange(["-map_chapters", "0"]);
        else
            args.AddRange(["-map_chapters", "-1"]);

        if (settings.PreserveMetadata) args.AddRange(["-map_metadata", "0"]);
        else args.AddRange(["-map_metadata", "-1"]);

        if (settings.FastStart && CompatibilityMatrix.ContainerSupportsFastStart(container.Id))
            args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-max_muxing_queue_size", "4096"]);
    }

    private void BuildGifCommand(List<string> args, ProcessingRequest request, VideoConversionSettings settings)
    {
        var inputWidth = request.InputInfo?.Width ?? 640;
        var inputHeight = request.InputInfo?.Height ?? 360;
        var targetWidth = settings.Width ?? inputWidth;
        var targetHeight = settings.Height ?? inputHeight;
        if (settings.ResizeMode != VideoResizeMode.None && settings.Width is not null && settings.Height is not null)
        {
            var (width, height) = ComputeVideoDimensions(inputWidth, inputHeight, settings, 2);
            targetWidth = width;
            targetHeight = height;
        }

        var fps = settings.FrameRate > 0
            ? settings.FrameRate
            : Math.Clamp(request.InputInfo?.FrameRate ?? 12, 1, 15);
        var scale = settings.ResizeMode switch
        {
            VideoResizeMode.Fit when settings.Width is not null && settings.Height is not null =>
                $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black",
            VideoResizeMode.Crop when settings.Width is not null && settings.Height is not null =>
                $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop={targetWidth}:{targetHeight}",
            VideoResizeMode.Stretch when settings.Width is not null && settings.Height is not null =>
                $"scale={targetWidth}:{targetHeight}:flags=lanczos",
            _ => $"scale={targetWidth}:-2:flags=lanczos"
        };

        var filter = $"{scale},fps={fps.ToString(CultureInfo.InvariantCulture)},split[gifA][gifB];" +
                     "[gifA]palettegen=stats_mode=diff[gifPalette];" +
                     "[gifB][gifPalette]paletteuse=dither=bayer:bayer_scale=5[gifOut]";
        args.AddRange(["-filter_complex", filter, "-map", "[gifOut]", "-an", "-sn", "-dn", "-c:v", "gif", "-loop", "0"]);
        if (settings.PreserveMetadata) args.AddRange(["-map_metadata", "0"]);
        else args.AddRange(["-map_metadata", "-1"]);
    }

    private static void ApplyVideoOptions(List<string> args, EncoderDefinition? definition,
        VideoConversionSettings settings, MediaInfo? input, EncoderCapability encoder)
    {
        var encoderId = encoder.Id;
        var preset = ResolvePreset(settings, definition, encoderId);

        args.AddRange(["-pix_fmt", encoderId == "prores_ks" ? "yuv422p10le" : "yuv420p"]);

        var filters = BuildVideoFilters(settings, input, encoder.DimensionMultiple);
        if (!string.IsNullOrWhiteSpace(filters)) args.AddRange(["-vf", filters]);

        if (encoderId == "prores_ks")
        {
            args.AddRange(["-profile:v", preset ?? "standard"]);
            return;
        }

        if (settings.RateControlMode == VideoRateControlMode.ConstantQuality &&
            (encoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) ||
             encoder.RateControl.HasFlag(RateControlCapabilities.Quantizer)))
        {
            var q = Math.Round(settings.QualityValue, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            if (encoderId.Contains("_amf", StringComparison.Ordinal))
            {
                args.AddRange(["-rc", "cqp", "-qp_i", q, "-qp_p", q]);
                if (encoderId == "h264_amf") args.AddRange(["-qp_b", q]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-quality", preset]);
            }
            else if (encoderId.Contains("_nvenc", StringComparison.Ordinal))
            {
                args.AddRange(["-rc", "constqp", "-qp", q]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId.Contains("_qsv", StringComparison.Ordinal))
            {
                args.AddRange(["-global_quality", q]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId == "libsvtav1")
            {
                args.AddRange(["-crf", q]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId is "libvpx-vp9" or "libvpx" or "libaom-av1")
            {
                args.AddRange(["-crf", q, "-b:v", "0"]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-cpu-used", preset]);
            }
            else if (encoderId == "mpeg4")
            {
                args.AddRange(["-q:v", q]);
            }
        }
        else
        {
            var bitrate = settings.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
            args.AddRange(["-b:v", bitrate]);
            if (encoderId.Contains("_amf", StringComparison.Ordinal))
            {
                args.AddRange(["-rc", "cbr"]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-quality", preset]);
            }
            else if (encoderId.Contains("_nvenc", StringComparison.Ordinal))
            {
                args.AddRange(["-rc", "vbr"]);
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId.Contains("_qsv", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId == "libopenh264")
            {
                args.AddRange(["-rc_mode", "bitrate", "-profile:v", "high"]);
            }
            else if (encoderId == "libsvtav1")
            {
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-preset", preset]);
            }
            else if (encoderId is "libvpx-vp9" or "libvpx" or "libaom-av1")
            {
                if (!string.IsNullOrWhiteSpace(preset)) args.AddRange(["-cpu-used", preset]);
            }
        }

        if (settings.EncoderPreset.Length > 0 && encoder.SupportsPresets && !string.Equals(settings.EncoderPreset, preset, StringComparison.OrdinalIgnoreCase))
        {
            // The explicit preset is used by ResolvePreset; this branch is intentionally empty.
        }
    }

    private static string? ResolvePreset(VideoConversionSettings settings, EncoderDefinition? definition, string encoderId)
    {
        if (!string.IsNullOrWhiteSpace(settings.EncoderPreset) &&
            (definition is null || definition.Presets.Count == 0 ||
             definition.Presets.Contains(settings.EncoderPreset, StringComparer.OrdinalIgnoreCase)))
            return settings.EncoderPreset;

        return encoderId switch
        {
            "h264_amf" or "hevc_amf" or "av1_amf" => settings.Preset switch
            {
                ConversionPreset.Fast => "speed",
                ConversionPreset.HighQuality => "quality",
                _ => "balanced"
            },
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => settings.Preset switch
            {
                ConversionPreset.Fast => "p3",
                ConversionPreset.HighQuality => "p6",
                _ => "p4"
            },
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => settings.Preset switch
            {
                ConversionPreset.Fast => "fast",
                ConversionPreset.HighQuality => "slow",
                _ => "medium"
            },
            "libsvtav1" => settings.Preset switch
            {
                ConversionPreset.Fast => "10",
                ConversionPreset.HighQuality => "4",
                _ => "8"
            },
            "libvpx-vp9" => settings.Preset switch
            {
                ConversionPreset.Fast => "5",
                ConversionPreset.HighQuality => "0",
                _ => "2"
            },
            "libvpx" => settings.Preset switch
            {
                ConversionPreset.Fast => "8",
                ConversionPreset.HighQuality => "0",
                _ => "4"
            },
            "libaom-av1" => settings.Preset switch
            {
                ConversionPreset.Fast => "8",
                ConversionPreset.HighQuality => "0",
                _ => "4"
            },
            "prores_ks" => settings.Preset switch
            {
                ConversionPreset.Fast => "lt",
                ConversionPreset.HighQuality => "hq",
                _ => "standard"
            },
            _ => definition?.DefaultPreset
        };
    }

    private static string? BuildVideoFilters(VideoConversionSettings settings, MediaInfo? input, int dimensionMultiple)
    {
        var filters = new List<string>();
        if (settings.ResizeMode != VideoResizeMode.None && (settings.Width is not null || settings.Height is not null))
        {
            var inputWidth = input?.Width ?? 1920;
            var inputHeight = input?.Height ?? 1080;
            var (targetWidth, targetHeight) = ComputeVideoDimensions(inputWidth, inputHeight, settings, dimensionMultiple);
            switch (settings.ResizeMode)
            {
                case VideoResizeMode.Fit:
                    filters.Add($"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos");
                    filters.Add($"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black");
                    break;
                case VideoResizeMode.Crop:
                    filters.Add($"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase:flags=lanczos");
                    filters.Add($"crop={targetWidth}:{targetHeight}");
                    break;
                default:
                    filters.Add($"scale={targetWidth}:{targetHeight}:flags=lanczos");
                    break;
            }
        }
        else if (dimensionMultiple > 2)
        {
            var multiple = dimensionMultiple.ToString(CultureInfo.InvariantCulture);
            filters.Add($"scale=trunc(iw/{multiple})*{multiple}:trunc(ih/{multiple})*{multiple}:flags=lanczos");
        }
        else
        {
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos");
        }

        filters.Add("setsar=1");
        if (settings.FrameRate > 0)
            filters.Add($"fps={settings.FrameRate.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(",", filters);
    }

    private static (int Width, int Height) ComputeVideoDimensions(int sourceWidth, int sourceHeight,
        VideoConversionSettings settings, int multiple)
    {
        var width = settings.Width;
        var height = settings.Height;
        if (width is null && height is null) return (RoundTo(sourceWidth, multiple), RoundTo(sourceHeight, multiple));

        if (settings.ResizeMode == VideoResizeMode.Stretch || !settings.PreserveAspectRatio)
            return (RoundTo(width ?? sourceWidth, multiple), RoundTo(height ?? sourceHeight, multiple));

        if (width is not null && height is not null)
        {
            var scale = settings.ResizeMode == VideoResizeMode.Crop
                ? Math.Max(width.Value / (double)sourceWidth, height.Value / (double)sourceHeight)
                : Math.Min(width.Value / (double)sourceWidth, height.Value / (double)sourceHeight);
            if (settings.ResizeMode == VideoResizeMode.Fit)
                return (RoundTo(width.Value, multiple), RoundTo(height.Value, multiple));
            return (RoundTo((int)Math.Round(sourceWidth * scale), multiple),
                RoundTo((int)Math.Round(sourceHeight * scale), multiple));
        }

        if (width is not null)
            return (RoundTo(width.Value, multiple), RoundTo((int)Math.Round(sourceHeight * (width.Value / (double)sourceWidth)), multiple));
        var computedWidth = (int)Math.Round(sourceWidth * (height!.Value / (double)sourceHeight));
        return (RoundTo(computedWidth, multiple), RoundTo(height.Value, multiple));
    }

    private static int RoundTo(int value, int multiple)
    {
        if (multiple <= 1) return Math.Max(2, value);
        var rounded = (int)Math.Round(value / (double)multiple, MidpointRounding.AwayFromZero) * multiple;
        return Math.Max(multiple, rounded);
    }

    private static void ApplyAudioOptions(List<string> args, EncoderDefinition? definition, AudioConversionSettings settings)
    {
        if (definition is null) return;

        if (settings.SampleRate > 0)
            args.AddRange(["-ar", settings.SampleRate.ToString(CultureInfo.InvariantCulture)]);
        if (settings.Channels > 0)
            args.AddRange(["-ac", settings.Channels.ToString(CultureInfo.InvariantCulture)]);

        if (definition.Id == "flac")
        {
            args.AddRange(["-compression_level", Math.Clamp(settings.CompressionLevel ?? 5, 0, 12).ToString(CultureInfo.InvariantCulture)]);
            return;
        }

        if (definition.Id == "pcm_s16le") return;

        if (settings.RateControlMode == AudioRateControlMode.VariableQuality &&
            definition.RateControl.HasFlag(RateControlCapabilities.ConstantQuality))
        {
            args.AddRange(["-q:a", settings.Quality.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (definition.RateControl.HasFlag(RateControlCapabilities.Bitrate))
        {
            args.AddRange(["-b:a", settings.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        }

        if (definition.Id == "libopus")
            args.AddRange(["-vbr", "on"]);
    }

    private static List<string> CommonInputArguments(string inputPath) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-progress", "pipe:1", "-nostats",
        "-loglevel", "warning",
        "-i", inputPath
    ];

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 64)
                return VerificationResult.Fail("The output file is empty or missing.");

            var actual = await _probe.InspectAsync(outputPath, cancellationToken);
            if (!actual.IsReadable)
                return VerificationResult.Fail($"The output could not be verified: {actual.Error}", actual);

            return request.Operation switch
            {
                OperationKind.AudioConvert when request.Settings is AudioConversionSettings audio =>
                    VerifyAudio(request, actual, audio),
                OperationKind.VideoConvert when request.Settings is VideoConversionSettings video =>
                    VerifyVideo(request, actual, video),
                _ => VerificationResult.Fail("The output verification settings are invalid.", actual)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VerificationResult.Fail("The output could not be inspected.", details: ex.ToString());
        }
    }

    private static VerificationResult VerifyAudio(ProcessingRequest request, MediaInfo actual, AudioConversionSettings settings)
    {
        var issues = new List<ValidationIssue>();
        var input = request.InputInfo;
        if (actual.AudioCodec is null)
            return VerificationResult.Fail("The output contains no audio stream.", actual);
        if (!string.Equals(actual.AudioCodec, settings.CodecId, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Fail($"The output audio codec is {actual.AudioCodec}, expected {settings.CodecId}.", actual);
        if (!string.Equals(actual.FormatId, settings.OutputFormatId, StringComparison.OrdinalIgnoreCase))
            issues.Add(ValidationIssue.Warning("verify.container",
                $"The output container is {actual.FormatId ?? actual.FormatName}, expected {settings.OutputFormatId}."));

        if (input?.DurationSeconds is > 0 && actual.DurationSeconds is > 0)
        {
            var tolerance = Math.Max(1.5, input.DurationSeconds.Value * 0.03);
            if (Math.Abs(input.DurationSeconds.Value - actual.DurationSeconds.Value) > tolerance)
                return VerificationResult.Fail(
                    $"The output duration is {actual.DurationSeconds:0.0}s, expected about {input.DurationSeconds:0.0}s.", actual);
        }

        if (settings.SampleRate > 0 && actual.SampleRate is > 0 && actual.SampleRate != settings.SampleRate)
            issues.Add(ValidationIssue.Warning("verify.samplerate",
                $"The output sample rate is {actual.SampleRate} Hz, expected {settings.SampleRate} Hz."));
        if (settings.Channels > 0 && actual.Channels is > 0 && actual.Channels != settings.Channels)
            issues.Add(ValidationIssue.Warning("verify.channels",
                $"The output has {actual.Channels} channel(s), expected {settings.Channels}."));

        if (settings.PreserveMetadata && input is not null)
        {
            var sourceHasMetadata = input.Tags.Keys.Any(k => k is "title" or "artist" or "album" or "comment");
            var outputHasMetadata = actual.Tags.Keys.Any(k => k is "title" or "artist" or "album" or "comment");
            if (sourceHasMetadata && !outputHasMetadata)
                issues.Add(ValidationIssue.Warning("verify.metadata", "Source audio metadata was not found in the output."));
        }

        return new VerificationResult
        {
            IsValid = true,
            Summary = $"{actual.FormatName ?? settings.OutputFormatId} · {actual.AudioCodec} · " +
                      $"{actual.SampleRate ?? 0} Hz · {actual.Channels ?? 0} ch · {actual.DurationSeconds:0.0}s",
            Issues = issues,
            ActualInfo = actual
        };
    }

    private static VerificationResult VerifyVideo(ProcessingRequest request, MediaInfo actual, VideoConversionSettings settings)
    {
        var issues = new List<ValidationIssue>();
        var input = request.InputInfo;
        var isGif = string.Equals(settings.ContainerFormatId, "gif", StringComparison.OrdinalIgnoreCase);
        if (actual.VideoCodec is null)
            return VerificationResult.Fail("The output contains no video stream.", actual);

        var expectedVideoCodec = settings.VideoCodecId == FormatIds.Copy
            ? input?.VideoCodec
            : settings.VideoCodecId;
        if (!isGif && expectedVideoCodec is not null &&
            !string.Equals(actual.VideoCodec, expectedVideoCodec, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Fail($"The output video codec is {actual.VideoCodec}, expected {expectedVideoCodec}.", actual);

        if (!isGif && !string.Equals(actual.FormatId, settings.ContainerFormatId, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Fail(
                $"The output container is {actual.FormatId ?? actual.FormatName}, expected {settings.ContainerFormatId}.", actual);

        var expectedAudio = settings.AudioCodecId switch
        {
            FormatIds.None => null,
            FormatIds.Copy => input?.AudioCodec,
            _ => settings.AudioCodecId
        };
        if (isGif)
        {
            expectedAudio = null;
            if (actual.AudioCodec is not null)
                issues.Add(ValidationIssue.Warning("verify.gif.audio", "The GIF output unexpectedly contains an audio stream."));
        }
        else if (expectedAudio is null && input?.AudioCodec is not null && settings.AudioCodecId != FormatIds.None)
            issues.Add(ValidationIssue.Warning("verify.audio", "The source had audio but no audio codec was selected."));
        if (expectedAudio is not null && actual.AudioCodec is null && input?.AudioCodec is not null)
            return VerificationResult.Fail("The output is missing the requested audio stream.", actual);
        if (expectedAudio is not null && actual.AudioCodec is not null &&
            !string.Equals(actual.AudioCodec, expectedAudio, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Fail($"The output audio codec is {actual.AudioCodec}, expected {expectedAudio}.", actual);
        if (settings.AudioCodecId == FormatIds.None && actual.AudioCodec is not null)
            return VerificationResult.Fail("The output unexpectedly contains an audio stream.", actual);

        if (!isGif && input?.DurationSeconds is > 0 && actual.DurationSeconds is > 0)
        {
            var tolerance = Math.Max(2.0, input.DurationSeconds.Value * 0.03);
            if (Math.Abs(input.DurationSeconds.Value - actual.DurationSeconds.Value) > tolerance)
                return VerificationResult.Fail(
                    $"The output duration is {actual.DurationSeconds:0.0}s, expected about {input.DurationSeconds:0.0}s.", actual);
        }

        if (input is { Width: > 0, Height: > 0 } && settings.VideoCodecId != FormatIds.Copy)
        {
            var encoderMultiple = settings.VideoEncoderId.Contains("libkvazaar", StringComparison.OrdinalIgnoreCase) ? 8 : 2;
            var (expectedWidth, expectedHeight) = ComputeVideoDimensions(input.Width.Value, input.Height.Value, settings, encoderMultiple);
            if (actual.Width != expectedWidth || actual.Height != expectedHeight)
            {
                var fitTolerance = settings.ResizeMode is VideoResizeMode.None;
                if (!fitTolerance)
                    issues.Add(ValidationIssue.Warning("verify.resolution",
                        $"The output is {actual.Width} × {actual.Height}, expected {expectedWidth} × {expectedHeight}."));
            }
        }

        if (settings.FrameRate > 0 && actual.FrameRate is > 0 &&
            Math.Abs(actual.FrameRate.Value - settings.FrameRate) > 0.25)
            issues.Add(ValidationIssue.Warning("verify.framerate",
                $"The output frame rate is {actual.FrameRate:0.###}, expected {settings.FrameRate:0.###}."));

        if (!isGif && input?.SubtitleStreamCount is > 0 && settings.SubtitleMode != SubtitleMode.Drop &&
            CompatibilityMatrix.ContainerSupportsSubtitles(settings.ContainerFormatId) &&
            (actual.SubtitleStreamCount ?? 0) < input.SubtitleStreamCount)
            issues.Add(ValidationIssue.Warning("verify.subtitles", "Some subtitle streams were not preserved."));

        if (!isGif && settings.PreserveChapters && CompatibilityMatrix.ContainerSupportsChapters(settings.ContainerFormatId) &&
            input?.ChapterCount is > 0 && (actual.ChapterCount ?? 0) == 0)
            issues.Add(ValidationIssue.Warning("verify.chapters", "Chapters were not preserved."));

        if (settings.VideoCodecId == FormatIds.Copy && input?.VideoCodec is not null &&
            actual.Streams.FirstOrDefault(s => s.Type == "video") is { Profile: not null } actualVideo &&
            input.Streams.FirstOrDefault(s => s.Type == "video") is { Profile: not null } inputVideo &&
            !string.Equals(actualVideo.Profile, inputVideo.Profile, StringComparison.OrdinalIgnoreCase))
            issues.Add(ValidationIssue.Warning("verify.copyprofile", "The stream-copied video profile differs from the source."));

        return new VerificationResult
        {
            IsValid = true,
            Summary = $"{actual.FormatName ?? settings.ContainerFormatId} · {actual.VideoCodec}" +
                      $"{(actual.AudioCodec is null ? "" : " · " + actual.AudioCodec)} · {actual.Width} × {actual.Height} · {actual.DurationSeconds:0.0}s",
            Issues = issues,
            ActualInfo = actual
        };
    }

    public async Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable) return null;
        foreach (var seek in new[] { "1", "0" })
        {
            try
            {
                var result = await _processRunner.RunAsync(new ProcessRequest
                {
                    FileName = _locator.FfmpegPath!,
                    Arguments =
                    [
                        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                        "-ss", seek, "-i", inputPath,
                        "-frames:v", "1", "-an", "-sn",
                        "-vf", $"scale={maxSize}:-2:flags=lanczos",
                        outputPath
                    ],
                    Timeout = TimeSpan.FromSeconds(60),
                    MaxCapturedLines = 40
                }, cancellationToken);
                if (result.Succeeded && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    return outputPath;
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail extraction failed for {Path}", inputPath);
                return null;
            }
        }

        return null;
    }

    private static string FriendlyError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = string.Join("\n", lines);
        if (text.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase))
            return "The selected encoder is not available in this FFmpeg build.";
        if (text.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not currently supported in container", StringComparison.OrdinalIgnoreCase))
            return "The selected codec is not compatible with the output container.";
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "The output file could not be written. Check folder permissions.";
        if (text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
            return "A source file or folder could not be found.";
        if (text.Contains("Invalid argument", StringComparison.OrdinalIgnoreCase))
            return "FFmpeg rejected one of the selected settings.";
        if (text.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cannot load", StringComparison.OrdinalIgnoreCase))
            return "The selected encoder could not be initialized. Try another encoder or the CPU path.";
        if (text.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Stream map", StringComparison.OrdinalIgnoreCase) && text.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
            return "The requested stream is not present in the source file.";
        var last = lines.LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? "FFmpeg could not complete the conversion." : last;
    }

    private sealed class FFmpegProgressParser
    {
        private readonly double _duration;
        private readonly IProgress<ProcessingProgress> _progress;
        private readonly long _started = Environment.TickCount64;
        private long _lastReport;
        private double _lastFraction = -1;
        private string? _speed;

        public FFmpegProgressParser(double duration, IProgress<ProcessingProgress> progress)
        {
            _duration = duration;
            _progress = progress;
        }

        public void ParseLine(string line)
        {
            var index = line.IndexOf('=');
            if (index <= 0) return;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            switch (key)
            {
                case "out_time_us":
                case "out_time_ms":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && _duration > 0)
                    {
                        var seconds = micros / 1_000_000.0;
                        var fraction = Math.Clamp(seconds / _duration, 0, 0.99);
                        Report(fraction, seconds);
                    }
                    break;
                case "speed":
                    _speed = value;
                    break;
                case "progress" when value == "end":
                    _progress.Report(ProcessingProgress.Percent(1, "Finalizing…"));
                    break;
            }
        }

        private void Report(double fraction, double seconds)
        {
            var now = Environment.TickCount64;
            if (now - _lastReport < 180 && Math.Abs(fraction - _lastFraction) < 0.005) return;
            _lastReport = now;
            _lastFraction = fraction;
            var message = _duration > 0
                ? $"{FormatTime(seconds)} of {FormatTime(_duration)}{(_speed is null ? "" : " · " + _speed)}"
                : "Converting…";
            _progress.Report(ProcessingProgress.Percent(fraction, message));
        }

        private static string FormatTime(double seconds) => seconds >= 3600
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    }
}
