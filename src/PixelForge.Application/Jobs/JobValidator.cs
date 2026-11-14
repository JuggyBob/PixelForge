using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Jobs;

public sealed class JobValidator
{
    private readonly ICapabilityRegistry _registry;
    private readonly IEnumerable<IProcessingEngine> _engines;
    private readonly IModelManager _models;
    private readonly IFileNameService _fileNames;
    private readonly IOutputPathService _outputPaths;
    private readonly ILogger<JobValidator> _logger;

    public JobValidator(ICapabilityRegistry registry, IEnumerable<IProcessingEngine> engines, IModelManager models,
        IFileNameService fileNames, IOutputPathService outputPaths, ILogger<JobValidator> logger)
    {
        _registry = registry;
        _engines = engines;
        _models = models;
        _fileNames = fileNames;
        _outputPaths = outputPaths;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();
        if (request.Inputs.Count == 0)
        {
            issues.Add(ValidationIssue.Error("input.empty", "Add at least one file before creating a job."));
            return new ValidationResult { Issues = issues };
        }

        if (request.Settings.Operation != request.Operation &&
            !(request.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional &&
              request.Settings is UpscaleSettings))
        {
            issues.Add(ValidationIssue.Error("settings.operation", "The selected operation does not match the job settings."));
        }

        if (string.IsNullOrWhiteSpace(request.Settings.OutputDirectory))
            issues.Add(ValidationIssue.Error("output.empty", "Choose an output folder.", "OutputDirectory"));
        else if (!IsValidDirectory(request.Settings.OutputDirectory))
            issues.Add(ValidationIssue.Error("output.invalid", "The output folder is not valid.", "OutputDirectory"));
        else
            CheckFreeSpace(request.Settings.OutputDirectory, issues);

        var engineId = GetRequiredEngineId(request.Operation);
        var engine = _registry.GetEngine(engineId);
        if (engine is null || !engine.IsAvailable)
        {
            issues.Add(ValidationIssue.Error("engine.unavailable",
                $"{EngineDisplayName(engineId)} is not available. Open Settings → Diagnostics for details.",
                details: engine?.Error));
            return new ValidationResult { Issues = issues };
        }

        foreach (var input in request.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInputBasics(input, request, issues);
        }

        switch (request.Operation)
        {
            case OperationKind.ImageConvert when request.Settings is ImageConversionSettings image:
                ValidateImage(image, request, issues);
                break;
            case OperationKind.AudioConvert when request.Settings is AudioConversionSettings audio:
                ValidateAudio(audio, request, issues);
                break;
            case OperationKind.VideoConvert when request.Settings is VideoConversionSettings video:
                ValidateVideo(video, request, issues);
                break;
            case OperationKind.UpscaleAi or OperationKind.UpscaleTraditional when request.Settings is UpscaleSettings upscale:
                await ValidateUpscaleAsync(upscale, request, issues, cancellationToken);
                break;
            case OperationKind.DocumentConvert when request.Settings is DocumentConversionSettings:
            case OperationKind.EbookConvert when request.Settings is EbookConversionSettings:
                ValidateContentFormat(request,
                    request.Operation == OperationKind.DocumentConvert ? MediaCategory.Document : MediaCategory.Ebook, issues);
                break;
            case OperationKind.ArchiveConvert when request.Settings is ArchiveConversionSettings archive:
                ValidateArchive(archive, request, issues);
                break;
            case OperationKind.AudioProcess when request.Settings is AudioToolSettings audioTool:
                ValidateAudioTool(audioTool, request, issues);
                break;
            case OperationKind.ImageProcess when request.Settings is ImageToolSettings imageTool:
                ValidateImageTool(imageTool, request, issues);
                break;
            case OperationKind.VideoProcess when request.Settings is VideoToolSettings videoTool:
                ValidateVideoTool(videoTool, request, issues);
                break;
            case OperationKind.VideoUpscale when request.Settings is VideoUpscaleSettings videoUpscale:
                await ValidateVideoUpscaleAsync(videoUpscale, request, issues, cancellationToken);
                break;
            case OperationKind.OcrDocument when request.Settings is OcrSettings ocr:
                ValidateOcr(ocr, request, issues);
                break;
            default:
                issues.Add(ValidationIssue.Error("settings.type", "The job settings are not compatible with the operation."));
                break;
        }

        ValidateOutputNaming(request, issues);
        await ValidateWithEngineAsync(request, engineId, issues, cancellationToken);
        return new ValidationResult { Issues = issues };
    }

    private void ValidateInputBasics(JobInput input, JobRequest request, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(input.Path))
        {
            issues.Add(ValidationIssue.Error("input.path", "An input path is empty."));
            return;
        }

        if (!File.Exists(input.Path) &&
            !(request.Operation == OperationKind.ArchiveConvert &&
              request.Settings is ArchiveConversionSettings { Action: ArchiveAction.Create } &&
              Directory.Exists(input.Path)))
        {
            issues.Add(ValidationIssue.Error("input.missing", $"{Path.GetFileName(input.Path)} no longer exists."));
            return;
        }

        var info = input.Info;
        if (info is null)
        {
            issues.Add(ValidationIssue.Warning("input.uninspected", $"{Path.GetFileName(input.Path)} has not been inspected yet."));
            return;
        }

        if (!info.IsReadable)
        {
            issues.Add(ValidationIssue.Error("input.unreadable",
                $"{info.FileName}: {info.Error ?? "the file could not be read."}"));
            return;
        }

        switch (request.Operation)
        {
            case OperationKind.ImageConvert when info.Category != MediaCategory.Image:
                issues.Add(ValidationIssue.Error("input.category",
                    $"{info.FileName} is {info.Category.ToString().ToLowerInvariant()}, not an image. Use the matching converter."));
                break;
            case OperationKind.AudioConvert when info.Category == MediaCategory.Image:
                issues.Add(ValidationIssue.Error("input.category",
                    $"{info.FileName} is an image. Choose Image conversion instead."));
                break;
            case OperationKind.AudioConvert when info.Category == MediaCategory.Video && info.AudioCodec is null:
                issues.Add(ValidationIssue.Error("input.noaudio", $"{info.FileName} has no audio stream to extract."));
                break;
            case OperationKind.VideoConvert when info.Category != MediaCategory.Video:
                issues.Add(ValidationIssue.Error("input.category",
                    $"{info.FileName} is not a video. Use the Image or Audio converter for this file."));
                break;
            case OperationKind.UpscaleAi or OperationKind.UpscaleTraditional when info.Category != MediaCategory.Image:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not an image."));
                break;
            case OperationKind.DocumentConvert when info.Category is not (MediaCategory.Document or MediaCategory.Ebook):
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not a document."));
                break;
            case OperationKind.EbookConvert when info.Category is not (MediaCategory.Document or MediaCategory.Ebook):
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not a document or e-book."));
                break;
            case OperationKind.ArchiveConvert when request.Settings is ArchiveConversionSettings { Action: ArchiveAction.Repackage } &&
                                                   info.Category != MediaCategory.Archive:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not an archive to repackage."));
                break;
            case OperationKind.ArchiveConvert:
                break;
            case OperationKind.AudioProcess when info.Category == MediaCategory.Image:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is an image, not audio."));
                break;
            case OperationKind.AudioProcess when info.Category == MediaCategory.Video && info.AudioCodec is null:
                issues.Add(ValidationIssue.Error("input.noaudio", $"{info.FileName} has no audio stream."));
                break;
            case OperationKind.ImageProcess when info.Category != MediaCategory.Image:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not an image."));
                break;
            case OperationKind.VideoProcess when info.Category != MediaCategory.Video:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not a video."));
                break;
            case OperationKind.VideoUpscale when info.Category != MediaCategory.Video:
                issues.Add(ValidationIssue.Error("input.category", $"{info.FileName} is not a video."));
                break;
        }
    }

    private void ValidateImage(ImageConversionSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var format = FindOutputFormat(settings.OutputFormatId, MediaCategory.Image, issues);
        if (format is null) return;

        ValidateImageResize(settings.ResizeWidth, settings.ResizeHeight, settings.PreserveAspectRatio, issues);
        if (settings.Quality is < 1 or > 100)
            issues.Add(ValidationIssue.Error("image.quality", "Image quality must be between 1 and 100.", "Quality"));
        if (settings.CompressionLevel is < 0 or > 9)
            issues.Add(ValidationIssue.Error("image.compression", "PNG compression must be between 0 and 9.", "CompressionLevel"));
        if (settings.GifColors is < 2 or > 256)
            issues.Add(ValidationIssue.Error("image.gifcolors", "GIF colours must be between 2 and 256.", "GifColors"));
        if (settings.AvifSpeed is < 0 or > 10)
            issues.Add(ValidationIssue.Error("image.avifspeed", "AVIF speed must be between 0 and 10.", "AvifSpeed"));

        foreach (var input in request.Inputs)
        {
            var info = input.Info;
            if (info is null || !info.IsReadable) continue;
            if (info.IsVector && settings.ResizeWidth is null && settings.ResizeHeight is null)
            {
                issues.Add(ValidationIssue.Error("image.vector.resolution",
                    $"{info.FileName} is a vector image. Choose an output width or height to rasterize it.", "ResizeWidth"));
            }

            if ((info.IsAnimated || info.IsMultipage) && !settings.FlattenAnimation)
            {
                var targetSupports = info.IsAnimated ? format.SupportsAnimation : format.SupportsMultipage;
                if (!targetSupports)
                {
                    issues.Add(ValidationIssue.Error("image.animation.unsupported",
                        $"{info.FileName} contains {(info.IsAnimated ? "animation" : "multiple pages")}, but {format.DisplayName} cannot preserve it. " +
                        "Enable 'Flatten animation' to use a single frame explicitly."));
                }
                else if (!settings.PreserveAnimation)
                {
                    issues.Add(ValidationIssue.Error("image.animation.disabled",
                        $"{info.FileName} is animated or multi-page. Enable 'Flatten animation' if you intend to keep only one frame."));
                }
            }

            if (settings.PreserveMetadata && !format.SupportsMetadata && !format.IsVector)
                issues.Add(ValidationIssue.Warning("image.metadata.unsupported",
                    $"{format.DisplayName} does not store image metadata; metadata will not be preserved."));
            if (settings.PreserveIccProfile && !format.SupportsIccProfile && !format.IsVector)
                issues.Add(ValidationIssue.Warning("image.icc.unsupported",
                    $"{format.DisplayName} does not store ICC colour profiles; the profile will not be preserved."));

            if (info.HasAlpha && !format.SupportsAlpha && settings.TransparencyMode == TransparencyMode.Preserve)
            {
                issues.Add(ValidationIssue.Warning("image.alpha.flatten",
                    $"{info.FileName} has transparency; {format.DisplayName} cannot store it. Transparency will be flattened onto {settings.BackgroundColor}."));
            }
        }

        if (settings.TransparencyMode == TransparencyMode.Flatten && !IsColor(settings.BackgroundColor))
            issues.Add(ValidationIssue.Error("image.background", "Choose a valid background colour such as #FFFFFF.", "BackgroundColor"));

        if (settings.PreserveAnimation && settings.FlattenAnimation)
            issues.Add(ValidationIssue.Warning("image.animation.conflict",
                "'Preserve animation' and 'Flatten animation' are both selected; animation will be flattened."));
    }

    private void ValidateAudio(AudioConversionSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var format = FindOutputFormat(settings.OutputFormatId, MediaCategory.Audio, issues);
        if (format is null) return;

        var codecs = _registry.GetAudioCodecs(format.Id, forVideo: false);
        if (codecs.Count == 0)
        {
            issues.Add(ValidationIssue.Error("audio.codec.none",
                $"No usable audio encoder can write {format.DisplayName}. Reinstall FFmpeg or choose another format."));
            return;
        }

        var codec = codecs.FirstOrDefault(c => string.Equals(c.Id, settings.CodecId, StringComparison.OrdinalIgnoreCase));
        if (codec is null)
        {
            issues.Add(ValidationIssue.Error("audio.codec.invalid",
                $"The selected audio codec cannot be written to {format.DisplayName}.", "CodecId"));
            return;
        }

        var encoders = codec.Encoders.Where(e => e.IsAvailable).OrderBy(e => e.Priority).ToList();
        if (encoders.Count == 0)
        {
            issues.Add(ValidationIssue.Error("audio.encoder.none", $"No encoder is available for {codec.DisplayName}."));
            return;
        }

        var encoder = ResolveEncoder(encoders, settings.EncoderId);
        if (!string.IsNullOrWhiteSpace(settings.EncoderId) && encoder is null)
            issues.Add(ValidationIssue.Error("audio.encoder.invalid", "The selected audio encoder is not available.", "EncoderId"));

        if (encoder is not null)
        {
            if (encoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                (settings.BitrateKbps < 8 || settings.BitrateKbps > 1536))
                issues.Add(ValidationIssue.Error("audio.bitrate", "Audio bitrate must be between 8 and 1536 kbps.", "BitrateKbps"));

            if (encoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                settings.RateControlMode == AudioRateControlMode.VariableQuality &&
                encoder.QualityMin is not null && encoder.QualityMax is not null &&
                (settings.Quality < encoder.QualityMin || settings.Quality > encoder.QualityMax))
                issues.Add(ValidationIssue.Error("audio.quality",
                    $"{encoder.QualityLabel ?? "Quality"} must be between {encoder.QualityMin:0} and {encoder.QualityMax:0}.",
                    "Quality"));
        }

        if (settings.SampleRate != 0 && (settings.SampleRate < 8000 || settings.SampleRate > 192000))
            issues.Add(ValidationIssue.Error("audio.samplerate", "Choose a sample rate between 8,000 and 192,000 Hz.", "SampleRate"));
        if (settings.Channels is < 0 or > 8)
            issues.Add(ValidationIssue.Error("audio.channels", "Choose 0 (source), 1 (mono), or 2 (stereo).", "Channels"));

        foreach (var input in request.Inputs)
        {
            if (input.Info?.Category == MediaCategory.Video)
                issues.Add(ValidationIssue.Info("audio.extract",
                    $"{input.Info.FileName}: audio will be extracted from the video stream."));
        }
    }

    private void ValidateVideo(VideoConversionSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var container = FindOutputFormat(settings.ContainerFormatId, MediaCategory.Video, issues);
        if (container is null) return;

        var isGif = string.Equals(container.Id, "gif", StringComparison.OrdinalIgnoreCase);
        var copyVideo = settings.VideoCodecId == FormatIds.Copy;
        var copyAudio = settings.AudioCodecId == FormatIds.Copy;
        var noAudio = settings.AudioCodecId == FormatIds.None;

        if (isGif)
        {
            if (!copyVideo && !string.Equals(settings.VideoCodecId, "gif", StringComparison.OrdinalIgnoreCase))
                issues.Add(ValidationIssue.Error("video.gif.codec", "Animated GIF output requires the GIF video codec."));
            if (!noAudio && !copyAudio)
                issues.Add(ValidationIssue.Warning("video.gif.audio", "Animated GIF has no audio; the audio stream will be omitted."));
            if (settings.SubtitleMode != SubtitleMode.Drop)
                issues.Add(ValidationIssue.Warning("video.gif.subtitles", "Animated GIF has no subtitle support; subtitles will be dropped."));
        }
        else if (!copyVideo)
        {
            var videoCodecs = _registry.GetVideoCodecs(container.Id);
            if (videoCodecs.Count == 0)
            {
                issues.Add(ValidationIssue.Error("video.codec.none",
                    $"No usable video encoder can write {container.DisplayName}."));
            }
            else
            {
                var codec = videoCodecs.FirstOrDefault(c =>
                    string.Equals(c.Id, settings.VideoCodecId, StringComparison.OrdinalIgnoreCase));
                if (codec is null)
                {
                    issues.Add(ValidationIssue.Error("video.codec.invalid",
                        $"{settings.VideoCodecId} cannot be encoded into {container.DisplayName}. Choose a compatible video codec.",
                        "VideoCodecId"));
                }
                else
                {
                    ValidateVideoEncoder(settings, codec, issues);
                }
            }
        }

        if (!isGif && !noAudio && !copyAudio)
        {
            var audioCodecs = _registry.GetAudioCodecs(container.Id, forVideo: true);
            if (audioCodecs.Count == 0)
            {
                issues.Add(ValidationIssue.Error("video.audio.none",
                    $"No usable audio encoder can be written to {container.DisplayName}."));
            }
            else
            {
                var codec = audioCodecs.FirstOrDefault(c =>
                    string.Equals(c.Id, settings.AudioCodecId, StringComparison.OrdinalIgnoreCase));
                if (codec is null)
                {
                    issues.Add(ValidationIssue.Error("video.audio.invalid",
                        $"{settings.AudioCodecId} cannot be encoded into {container.DisplayName}. Choose a compatible audio codec.",
                        "AudioCodecId"));
                }
                else
                {
                    var available = codec.Encoders.Where(e => e.IsAvailable).OrderBy(e => e.Priority).ToList();
                    var encoder = ResolveEncoder(available, settings.AudioEncoderId);
                    if (encoder is not null)
                    {
                        if (encoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                            (settings.AudioBitrateKbps < 8 || settings.AudioBitrateKbps > 1536))
                            issues.Add(ValidationIssue.Error("video.audio.bitrate", "Audio bitrate must be between 8 and 1536 kbps.", "AudioBitrateKbps"));
                    }
                }
            }
        }

        if (settings.ResizeMode != VideoResizeMode.None)
            ValidateImageResize(settings.Width, settings.Height, settings.PreserveAspectRatio, issues);
        if (settings.FrameRate != 0 && (settings.FrameRate < 1 || settings.FrameRate > 240))
            issues.Add(ValidationIssue.Error("video.framerate", "Frame rate must be between 1 and 240 fps, or 0 to keep the source.", "FrameRate"));
        if (settings.VideoBitrateKbps is < 100 or > 200_000)
            issues.Add(ValidationIssue.Error("video.bitrate", "Video bitrate must be between 100 and 200,000 kbps.", "VideoBitrateKbps"));
        if (settings.AudioChannels is < 0 or > 8)
            issues.Add(ValidationIssue.Error("video.audio.channels", "Audio channels must be 0 (source), 1, or 2.", "AudioChannels"));
        if (settings.AudioSampleRate != 0 && (settings.AudioSampleRate < 8000 || settings.AudioSampleRate > 192000))
            issues.Add(ValidationIssue.Error("video.audio.samplerate", "Audio sample rate must be between 8,000 and 192,000 Hz.", "AudioSampleRate"));

        if (!CompatibilityMatrix.ContainerSupportsSubtitles(container.Id) && settings.SubtitleMode != SubtitleMode.Drop)
            issues.Add(ValidationIssue.Warning("video.subtitles.unsupported",
                $"{container.DisplayName} does not support subtitle streams; subtitles will be dropped."));
        if (!CompatibilityMatrix.ContainerSupportsChapters(container.Id) && settings.PreserveChapters)
            issues.Add(ValidationIssue.Warning("video.chapters.unsupported",
                $"{container.DisplayName} does not support chapters; chapters will not be preserved."));

        foreach (var input in request.Inputs)
        {
            var info = input.Info;
            if (info is null || !info.IsReadable) continue;
            if (info.Category != MediaCategory.Video) continue;

            if (copyVideo)
            {
                if (string.IsNullOrWhiteSpace(info.VideoCodec))
                    issues.Add(ValidationIssue.Error("video.copy.novideo", $"{info.FileName} has no video stream to copy."));
                else if (!CompatibilityMatrix.CanCopyVideo(container.Id, info.VideoCodec))
                    issues.Add(ValidationIssue.Error("video.copy.incompatible",
                        $"{info.FileName} uses {info.VideoCodec}, which cannot be stream-copied into {container.DisplayName}. " +
                        "Transcode the video stream instead."));
            }

            if (copyAudio && info.AudioCodec is not null && !CompatibilityMatrix.CanCopyAudio(container.Id, info.AudioCodec))
            {
                issues.Add(ValidationIssue.Error("audio.copy.incompatible",
                    $"{info.FileName} audio is {info.AudioCodec}, which cannot be stream-copied into {container.DisplayName}. " +
                    "Transcode the audio stream instead."));
            }

            if (settings.FrameRate > 0 && info.FrameRate is > 0 &&
                Math.Abs(info.FrameRate.Value - settings.FrameRate) < 0.01 &&
                !copyVideo)
            {
                issues.Add(ValidationIssue.Info("video.framerate.same",
                    $"{info.FileName} already uses {settings.FrameRate:0.###} fps."));
            }
        }

        if (settings.EncoderPreset.Length > 0 && settings.EncoderPreset.Length > 64)
            issues.Add(ValidationIssue.Error("video.encoderpreset", "The encoder preset name is too long.", "EncoderPreset"));
    }

    private void ValidateVideoEncoder(VideoConversionSettings settings, CodecCapability codec, List<ValidationIssue> issues)
    {
        var available = codec.Encoders.Where(e => e.IsAvailable).OrderBy(e => e.Priority).ToList();
        var encoder = ResolveEncoder(available, settings.VideoEncoderId);
        if (encoder is null)
        {
            if (!string.IsNullOrWhiteSpace(settings.VideoEncoderId))
                issues.Add(ValidationIssue.Error("video.encoder.invalid", "The selected video encoder is not available.", "VideoEncoderId"));
            else
                issues.Add(ValidationIssue.Error("video.encoder.none", $"No usable encoder is available for {codec.DisplayName}."));
            return;
        }

        if (encoder.RateControl != RateControlCapabilities.None &&
            settings.RateControlMode == VideoRateControlMode.ConstantQuality)
        {
            if (!encoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                !encoder.RateControl.HasFlag(RateControlCapabilities.Quantizer))
            {
                issues.Add(ValidationIssue.Error("video.ratecontrol.unsupported",
                    $"{encoder.DisplayName} does not expose constant-quality encoding. Use a target bitrate instead.",
                    "RateControlMode"));
            }
            else if (encoder.QualityMin is not null && encoder.QualityMax is not null &&
                     (settings.QualityValue < encoder.QualityMin || settings.QualityValue > encoder.QualityMax))
            {
                issues.Add(ValidationIssue.Error("video.quality",
                    $"{encoder.QualityLabel ?? "Quality"} must be between {encoder.QualityMin:0} and {encoder.QualityMax:0} for {encoder.DisplayName}.",
                    "QualityValue"));
            }
        }
        else if (encoder.RateControl != RateControlCapabilities.None &&
                 !encoder.RateControl.HasFlag(RateControlCapabilities.Bitrate))
        {
            issues.Add(ValidationIssue.Error("video.ratecontrol.unsupported",
                $"{encoder.DisplayName} does not support target-bitrate encoding.", "RateControlMode"));
        }

        if (encoder.SupportsPresets && !string.IsNullOrWhiteSpace(settings.EncoderPreset) &&
            !encoder.Presets.Contains(settings.EncoderPreset, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Error("video.preset.invalid",
                $"The encoder preset '{settings.EncoderPreset}' is not supported by {encoder.DisplayName}.", "EncoderPreset"));
        }

        if (encoder.IsHardware)
            issues.Add(ValidationIssue.Info("video.encoder.hardware", $"{encoder.DisplayName} will use the hardware encoder."));
    }

    private async Task ValidateUpscaleAsync(UpscaleSettings settings, JobRequest request, List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        if ((settings.Method == UpscaleMethod.Ai) != (request.Operation == OperationKind.UpscaleAi))
            issues.Add(ValidationIssue.Error("upscale.method", "The selected upscaling method does not match the job operation."));

        foreach (var input in request.Inputs)
        {
            var info = input.Info;
            if (info is null || !info.IsReadable) continue;
            if (info.IsAnimated || info.IsMultipage)
                issues.Add(ValidationIssue.Error("upscale.animated",
                    $"{info.FileName} is animated or multi-page. Upscaling cannot preserve that content; convert it to a still image first."));
            if (info.IsVector || string.Equals(info.Extension, ".svg", StringComparison.OrdinalIgnoreCase))
                issues.Add(ValidationIssue.Error("upscale.vector",
                    $"{info.FileName} is a vector image. Rasterize it in the image converter before upscaling."));
        }

        if (settings.ScaleFactor is < 1 or > 16)
            issues.Add(ValidationIssue.Error("upscale.scale", "Scale factor must be between 1× and 16×.", "ScaleFactor"));
        if (settings.CustomWidth is not null || settings.CustomHeight is not null)
        {
            if (settings.CustomWidth is < 1 or > 32768 || settings.CustomHeight is < 1 or > 32768)
                issues.Add(ValidationIssue.Error("upscale.dimensions", "Custom dimensions must be between 1 and 32,768 pixels."));
        }

        if (settings.Method == UpscaleMethod.Ai)
        {
            var model = _models.GetInstalled(settings.ModelId);
            if (model is null || !model.IsValid)
            {
                issues.Add(ValidationIssue.Error("upscale.model.missing",
                    "The selected AI model is not installed or failed validation. Open Model Management to install it.",
                    "ModelId"));
            }

            var provider = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == settings.ExecutionProvider);
            if (settings.ExecutionProvider != ExecutionProviderKind.Auto && provider is not null && !provider.IsAvailable)
                issues.Add(ValidationIssue.Error("upscale.provider",
                    $"{provider.DisplayName} is not available on this system. Choose Automatic or CPU."));
            else if (settings.ExecutionProvider == ExecutionProviderKind.DirectML &&
                     _registry.ExecutionProviders.All(p => p.Kind != ExecutionProviderKind.DirectML || !p.IsAvailable))
                issues.Add(ValidationIssue.Error("upscale.provider",
                    "DirectML is not available. Choose Automatic or CPU."));

            var directScale = model?.Descriptor.SupportedScales.Count is null or 0 ||
                              model.Descriptor.SupportedScales.Contains(settings.ScaleFactor);
            if (model is { IsValid: true } && model.Descriptor.ScaleFactor > 0 && settings.CustomWidth is null && settings.CustomHeight is null &&
                settings.ScaleFactor != model.Descriptor.ScaleFactor && !directScale)
            {
                issues.Add(ValidationIssue.Warning("upscale.model.scale",
                    $"{model.Descriptor.Name} is a {model.Descriptor.ScaleFactor}× model. A secondary resize will be used to produce {settings.ScaleFactor}×."));
            }

            if (settings.TileSize != 0 && settings.TileSize < 64)
                issues.Add(ValidationIssue.Error("upscale.tile", "Tile size must be at least 64 pixels, or 0 for automatic.", "TileSize"));
            if (settings.TileOverlap < 0 || (settings.TileSize > 0 && settings.TileOverlap >= settings.TileSize / 2))
                issues.Add(ValidationIssue.Error("upscale.overlap", "Tile overlap must be smaller than half the tile size.", "TileOverlap"));
        }
        else
        {
            if (!Enum.IsDefined(settings.Algorithm))
                issues.Add(ValidationIssue.Error("upscale.algorithm", "Choose a valid resampling algorithm.", "Algorithm"));
        }

        if (!string.Equals(settings.OutputFormatId, FormatIds.Auto, StringComparison.OrdinalIgnoreCase))
        {
            FindOutputFormat(settings.OutputFormatId, MediaCategory.Image, issues);
        }

        foreach (var input in request.Inputs)
        {
            var info = input.Info;
            if (info is null || !info.IsReadable || info.Width is null || info.Height is null) continue;
            var (outWidth, outHeight) = CalculateUpscaleDimensions(info.Width.Value, info.Height.Value, settings);
            if (outWidth > 32768 || outHeight > 32768 || (long)outWidth * outHeight > 120_000_000)
                issues.Add(ValidationIssue.Error("upscale.output.too.large",
                    $"{info.FileName} would produce a {outWidth} × {outHeight} image, which exceeds the safe output limit."));
            else if ((long)outWidth * outHeight > 64_000_000)
                issues.Add(ValidationIssue.Warning("upscale.output.large",
                    $"{info.FileName} will produce a {outWidth} × {outHeight} image. This may require several gigabytes of memory."));
            if (outWidth < 2 || outHeight < 2)
                issues.Add(ValidationIssue.Error("upscale.output.too.small", $"{info.FileName} would produce an invalid image size."));
        }
    }

    public static (int Width, int Height) CalculateUpscaleDimensions(int width, int height, UpscaleSettings settings)
    {
        if (settings.CustomWidth is > 0 && settings.CustomHeight is > 0)
            return (settings.CustomWidth.Value, settings.CustomHeight.Value);
        if (settings.CustomWidth is > 0)
            return (settings.CustomWidth.Value, Math.Max(1, (int)Math.Round(height * (settings.CustomWidth.Value / (double)width))));
        if (settings.CustomHeight is > 0)
            return (Math.Max(1, (int)Math.Round(width * (settings.CustomHeight.Value / (double)height))), settings.CustomHeight.Value);
        var factor = Math.Clamp(settings.ScaleFactor, 1, 16);
        return (Math.Max(1, width * factor), Math.Max(1, height * factor));
    }

    private void ValidateOutputNaming(JobRequest request, List<ValidationIssue> issues)
    {
        if (request.Operation == OperationKind.OcrDocument) return;
        if (string.IsNullOrWhiteSpace(request.Settings.OutputDirectory)) return;
        var suffix = _fileNames.BuildDefaultSuffix(request.Settings);
        var adjusted = 0;
        foreach (var input in request.Inputs)
        {
            if (!File.Exists(input.Path)) continue;
            var extension = ResolveOutputExtension(request, input);
            if (string.IsNullOrWhiteSpace(extension)) continue;
            try
            {
                var path = _outputPaths.BuildOutputPath(input.Path, request.Settings.OutputDirectory, extension, suffix,
                    input.RelativeDirectory, request.Settings.CollisionPolicy, out var note);
                if (!string.IsNullOrWhiteSpace(note) && note.Contains("unique", StringComparison.OrdinalIgnoreCase)) adjusted++;
                if (File.Exists(path) && request.Settings.CollisionPolicy == CollisionPolicy.Skip)
                    issues.Add(ValidationIssue.Info("output.skip", $"{Path.GetFileName(path)} already exists and will be skipped."));
            }
            catch (Exception ex)
            {
                issues.Add(ValidationIssue.Error("output.path", $"Could not build an output path: {ex.Message}"));
                break;
            }
        }

        if (adjusted > 0)
            issues.Add(ValidationIssue.Info("output.unique", $"{adjusted} output name(s) will be adjusted to avoid replacing existing files."));
    }

    private string? ResolveOutputExtension(JobRequest request, JobInput input)
    {
        switch (request.Settings)
        {
            case ImageConversionSettings image:
                return _registry.FindFormat(image.OutputFormatId, MediaCategory.Image)?.NormalizedExtension;
            case AudioConversionSettings audio:
                return _registry.FindFormat(audio.OutputFormatId, MediaCategory.Audio)?.NormalizedExtension;
            case VideoConversionSettings video:
                return _registry.FindFormat(video.ContainerFormatId, MediaCategory.Video)?.NormalizedExtension;
            case VideoUpscaleSettings videoUpscale:
                return _registry.FindFormat(videoUpscale.ContainerFormatId, MediaCategory.Video)?.NormalizedExtension
                       ?? ".mp4";
            case UpscaleSettings upscale:
                if (!string.Equals(upscale.OutputFormatId, FormatIds.Auto, StringComparison.OrdinalIgnoreCase))
                    return _registry.FindFormat(upscale.OutputFormatId, MediaCategory.Image)?.NormalizedExtension;
                var actual = input.Info?.FormatId;
                var format = actual is null ? null : _registry.FindFormat(actual, MediaCategory.Image);
                return format?.NormalizedExtension ?? ".png";
            case DocumentConversionSettings document:
                return _registry.FindFormat(document.OutputFormatId, MediaCategory.Document)?.NormalizedExtension;
            case EbookConversionSettings ebook:
                return _registry.FindFormat(ebook.OutputFormatId, MediaCategory.Ebook)?.NormalizedExtension;
            case ArchiveConversionSettings archive:
                if (archive.Action == ArchiveAction.Extract) return null;
                return _registry.FindFormat(archive.OutputFormatId, MediaCategory.Archive)?.NormalizedExtension;
            case AudioToolSettings audioTool:
                return _registry.FindFormat(audioTool.OutputFormatId, MediaCategory.Audio)?.NormalizedExtension;
            case ImageToolSettings imageTool:
                return _registry.FindFormat(imageTool.OutputFormatId, MediaCategory.Image)?.NormalizedExtension;
            case VideoToolSettings videoTool:
                return videoTool.Tool switch
                {
                    VideoToolOperation.Thumbnail => _registry.FindFormat(videoTool.ThumbnailFormatId, MediaCategory.Image)?.NormalizedExtension,
                    VideoToolOperation.ExtractAudio => _registry.FindFormat(videoTool.ExtractAudioFormatId, MediaCategory.Audio)?.NormalizedExtension,
                    VideoToolOperation.Subtitles when videoTool.SubtitleMode == SubtitleToolMode.Extract => "." + videoTool.SubtitleFormatId,
                    VideoToolOperation.Gif => ".gif",
                    _ => _registry.FindFormat(videoTool.ContainerFormatId, MediaCategory.Video)?.NormalizedExtension
                };
        }

        return null;
    }

    private MediaFormat? FindOutputFormat(string id, MediaCategory category, List<ValidationIssue> issues)
    {
        var format = _registry.FindFormat(id, category);
        if (format is null || !format.CanWrite)
        {
            issues.Add(ValidationIssue.Error("format.invalid", $"The selected {category.ToString().ToLowerInvariant()} format is not available."));
            return null;
        }

        return format;
    }

    private static EncoderCapability? ResolveEncoder(IReadOnlyList<EncoderCapability> encoders, string? preferredId)
    {
        if (encoders.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferredId))
            return encoders.FirstOrDefault(e => string.Equals(e.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        return encoders[0];
    }

    private static void ValidateImageResize(int? width, int? height, bool preserveAspect, List<ValidationIssue> issues)
    {
        if (width is not null && (width < 1 || width > 32768))
            issues.Add(ValidationIssue.Error("resize.width", "Width must be between 1 and 32,768 pixels.", "ResizeWidth"));
        if (height is not null && (height < 1 || height > 32768))
            issues.Add(ValidationIssue.Error("resize.height", "Height must be between 1 and 32,768 pixels.", "ResizeHeight"));
        if (preserveAspect && width is null && height is null)
            return;
    }

    private static bool IsColor(string value) =>
        value.StartsWith('#') && (value.Length == 7 || value.Length == 9) &&
        value[1..].All(Uri.IsHexDigit);

    private static bool IsValidDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return !File.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    private static void CheckFreeSpace(string directory, List<ValidationIssue> issues)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < 500L * 1024 * 1024)
                issues.Add(ValidationIssue.Warning("output.space", $"The output drive has only {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} GB free."));
        }
        catch
        {
            // Disk checks are best-effort.
        }
    }

    private void ValidateContentFormat(JobRequest request, MediaCategory category, List<ValidationIssue> issues)
    {
        var formatId = request.Settings switch
        {
            DocumentConversionSettings document => document.OutputFormatId,
            EbookConversionSettings ebook => ebook.OutputFormatId,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(formatId)) return;
        var format = _registry.FindFormat(formatId, category);
        if (format is null || !format.CanWrite)
            issues.Add(ValidationIssue.Error("content.format", "The selected target format is not available for this engine."));
    }

    private void ValidateArchive(ArchiveConversionSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var format = _registry.FindFormat(settings.OutputFormatId, MediaCategory.Archive);
        if (format is null || !format.CanWrite)
            issues.Add(ValidationIssue.Error("archive.format", "The selected archive format is not available."));
        if (settings.CompressionLevel is < 0 or > 19)
            issues.Add(ValidationIssue.Error("archive.level", "Compression level must be between 0 and 19.", "CompressionLevel"));
        if (settings.Action == ArchiveAction.Repackage &&
            request.Inputs.Any(i => i.Info is { Category: not MediaCategory.Archive }))
            issues.Add(ValidationIssue.Error("archive.repackage.input", "Archive repackaging requires archive inputs."));
    }

    private void ValidateAudioTool(AudioToolSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var format = _registry.FindFormat(settings.OutputFormatId, MediaCategory.Audio);
        if (format is null || !format.CanWrite)
            issues.Add(ValidationIssue.Error("audio.tool.format", "The selected audio output format is not available."));
        if (settings.TrimEndSeconds > 0 && settings.TrimEndSeconds <= settings.TrimStartSeconds)
            issues.Add(ValidationIssue.Error("audio.trim.range", "Trim end must be after trim start.", "TrimEndSeconds"));
        if (settings.Tool == AudioToolOperation.ExtractFromVideo &&
            request.Inputs.Any(i => i.Info is { Category: not (MediaCategory.Video or MediaCategory.Audio) }))
            issues.Add(ValidationIssue.Error("audio.extract.input", "Audio extraction requires a video or audio input."));
        if (settings.Tool == AudioToolOperation.Merge && settings.MergeInputPaths.Count < 1)
            issues.Add(ValidationIssue.Error("audio.merge.input", "Add at least two audio files to merge."));
        if (settings.Tool == AudioToolOperation.NormalizeLoudness && settings.TargetLufs is < -40 or > -5)
            issues.Add(ValidationIssue.Error("audio.loudness.target", "Choose a target loudness between -40 and -5 LUFS.", "TargetLufs"));
    }

    private void ValidateImageTool(ImageToolSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        var format = _registry.FindFormat(settings.OutputFormatId, MediaCategory.Image);
        if (format is null || !format.CanWrite)
            issues.Add(ValidationIssue.Error("image.tool.format", "The selected image output format is not available."));
        if (settings.Quality is < 1 or > 100)
            issues.Add(ValidationIssue.Error("image.tool.quality", "Quality must be between 1 and 100.", "Quality"));
        if (settings.CropWidth is <= 0 || settings.CropHeight is <= 0)
            issues.Add(ValidationIssue.Error("image.crop.size", "Crop width and height must be positive."));
        if (settings.WatermarkKind == WatermarkKind.Image && string.IsNullOrWhiteSpace(settings.WatermarkImagePath))
            issues.Add(ValidationIssue.Error("image.watermark.image", "Choose a watermark image.", "WatermarkImagePath"));
    }

    private void ValidateVideoTool(VideoToolSettings settings, JobRequest request, List<ValidationIssue> issues)    {
        if (settings.Tool == VideoToolOperation.Thumbnail)
        {
            var imageFormat = _registry.FindFormat(settings.ThumbnailFormatId, MediaCategory.Image);
            if (imageFormat is null || !imageFormat.CanWrite)
                issues.Add(ValidationIssue.Error("video.thumbnail.format", "The selected thumbnail image format is unavailable."));
            return;
        }
        if (settings.Tool == VideoToolOperation.ExtractAudio)
        {
            var audioFormat = _registry.FindFormat(settings.ExtractAudioFormatId, MediaCategory.Audio);
            if (audioFormat is null || !audioFormat.CanWrite)
                issues.Add(ValidationIssue.Error("video.audio.format", "The selected audio extraction format is unavailable."));
            return;
        }
        if (settings.Tool == VideoToolOperation.Subtitles && settings.SubtitleMode == SubtitleToolMode.Extract)
            return;
        var container = _registry.FindFormat(settings.ContainerFormatId, MediaCategory.Video);
        if (container is null || !container.CanWrite)
            issues.Add(ValidationIssue.Error("video.tool.format", "The selected video container is unavailable."));
        if (settings.TrimEndSeconds > 0 && settings.TrimEndSeconds <= settings.TrimStartSeconds)
            issues.Add(ValidationIssue.Error("video.trim.range", "Trim end must be after trim start.", "TrimEndSeconds"));
        if (settings.TargetSizeMb <= 0 && settings.Tool == VideoToolOperation.Compress)
            issues.Add(ValidationIssue.Error("video.targetsize", "Choose a target size greater than zero.", "TargetSizeMb"));
    }
    private static void ValidateOcr(OcrSettings settings, JobRequest request, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(settings.DocumentName))
            issues.Add(ValidationIssue.Error("ocr.name", "Enter a document name.", nameof(settings.DocumentName)));
        if (settings.PagePaths.Count == 0)
            issues.Add(ValidationIssue.Error("ocr.pages", "Add at least one image page."));
        if (settings.PagePaths.Count != request.Inputs.Count)
            issues.Add(ValidationIssue.Error("ocr.pages.count",
                "The page list changed while the job was being prepared. Add the images again."));
    }

    private async Task ValidateVideoUpscaleAsync(VideoUpscaleSettings settings, JobRequest request,
        List<ValidationIssue> issues, CancellationToken cancellationToken)
    {
        if (!settings.EnableSuperResolution && !settings.EnableInterpolation)
            issues.Add(ValidationIssue.Error("video.upscale.none",
                "Enable video super-resolution, frame interpolation, or both."));

        if (!string.Equals(settings.ContainerFormatId, "mp4", StringComparison.OrdinalIgnoreCase))
            issues.Add(ValidationIssue.Error("video.upscale.container",
                "The Video Upscaler currently writes MP4 output.", "ContainerFormatId"));

        if (settings.VideoBitrateKbps is < 100 or > 200_000)
            issues.Add(ValidationIssue.Error("video.upscale.bitrate",
                "Video bitrate must be between 100 and 200,000 kbps.", "VideoBitrateKbps"));
        if (settings.AudioBitrateKbps is < 8 or > 1536)
            issues.Add(ValidationIssue.Error("video.upscale.audio.bitrate",
                "Audio bitrate must be between 8 and 1,536 kbps.", "AudioBitrateKbps"));

        if (settings.EnableSuperResolution)
        {
            var model = _models.GetInstalled(settings.ImageModelId);
            if (model is null || !model.IsValid)
                issues.Add(ValidationIssue.Error("video.upscale.model.missing",
                    "The selected image upscaling model is not installed or failed validation. Open Model Management to install it.",
                    "ImageModelId"));
            else if (model.Descriptor.Task != ModelTask.ImageUpscale)
                issues.Add(ValidationIssue.Error("video.upscale.model.task",
                    "The selected model is not an image upscaling model.", "ImageModelId"));
            else if (settings.ScaleFactor != model.Descriptor.ScaleFactor)
                issues.Add(ValidationIssue.Error("video.upscale.scale",
                    $"{model.Descriptor.Name} is a {model.Descriptor.ScaleFactor}× model. Choose that native scale so the video receives a genuine AI upscale.",
                    "ScaleFactor"));
            if (settings.TileSize != 0 && settings.TileSize < 64)
                issues.Add(ValidationIssue.Error("video.upscale.tile", "Tile size must be at least 64 pixels, or 0 for automatic.", "TileSize"));
            if (settings.TileOverlap < 0 || (settings.TileSize > 0 && settings.TileOverlap >= settings.TileSize / 2))
                issues.Add(ValidationIssue.Error("video.upscale.overlap", "Tile overlap must be smaller than half the tile size.", "TileOverlap"));
        }

        if (settings.EnableInterpolation)
        {
            var model = _models.GetInstalled(settings.InterpolationModelId);
            if (model is null || !model.IsValid)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.missing",
                    "The selected frame-interpolation model is not installed or failed validation.",
                    "InterpolationModelId"));
            else if (model.Descriptor.Task != ModelTask.FrameInterpolation)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.task",
                    "The selected interpolation model is not a frame-interpolation model.",
                    "InterpolationModelId"));
            if (settings.FrameRateMultiplier is < 2 or > 4)
                issues.Add(ValidationIssue.Error("video.upscale.interpolation.multiplier",
                    "Frame interpolation must be 2×, 3× or 4×.", "FrameRateMultiplier"));
        }

        foreach (var input in request.Inputs)
        {
            var info = input.Info;
            if (info is null || !info.IsReadable) continue;
            if (info.SubtitleStreamCount > 0)
                issues.Add(ValidationIssue.Warning("video.upscale.subtitles",
                    $"{info.FileName}: subtitle streams are not preserved by the Video Upscaler and will be dropped."));
            if (info.ChapterCount > 0)
                issues.Add(ValidationIssue.Warning("video.upscale.chapters",
                    $"{info.FileName}: chapters are not preserved by the Video Upscaler."));
            if (info.AudioCodec is null)
                issues.Add(ValidationIssue.Info("video.upscale.noaudio",
                    $"{info.FileName} has no audio stream; the output will be silent."));
            if (info.FrameRate is not > 0)
                issues.Add(ValidationIssue.Warning("video.upscale.fps",
                    $"{info.FileName}: the source frame rate could not be determined. A 30 fps assumption will be used."));
            if (info.Width is > 0 && info.Height is > 0 && settings.EnableSuperResolution)
            {
                var scale = settings.ScaleFactor;
                var width = info.Width.Value * scale;
                var height = info.Height.Value * scale;
                if (width > 15360 || height > 15360 || (long)width * height > 100_000_000)
                    issues.Add(ValidationIssue.Error("video.upscale.too.large",
                        $"{info.FileName} would produce {width} × {height}, which exceeds the safe video limit."));
            }
        }

        await Task.CompletedTask;
    }

    private async Task ValidateWithEngineAsync(JobRequest request, string engineId, List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var engine = _engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));
        if (engine is null || request.Inputs.Count == 0) return;
        var formatId = ResolveOutputFormatId(request);
        if (string.IsNullOrWhiteSpace(formatId)) return;
        try
        {
            var input = request.Inputs[0];
            var validation = await engine.ValidateAsync(new ProcessingRequest
            {
                JobId = Guid.Empty,
                ItemId = Guid.Empty,
                Operation = request.Operation,
                Settings = request.Settings,
                InputPath = input.Path,
                TempOutputPath = Path.Combine(Path.GetTempPath(), "pixelforge-validation-" + Guid.NewGuid().ToString("N")),
                OutputFormatId = formatId,
                TempDirectory = Path.GetTempPath(),
                InputInfo = input.Info,
                RelativeDirectory = input.RelativeDirectory
            }, cancellationToken);
            foreach (var issue in validation.Issues)
            {
                if (issues.Any(existing => existing.Code == issue.Code && existing.Message == issue.Message)) continue;
                issues.Add(issue);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engine-level validation failed for {Operation}", request.Operation);
            issues.Add(ValidationIssue.Warning("engine.validation",
                "The processing engine could not complete its optional validation. The job will be checked again before processing."));
        }
    }

    private string? ResolveOutputFormatId(JobRequest request)
    {
        var input = request.Inputs.FirstOrDefault()?.Info;
        return request.Settings switch
        {
            ImageConversionSettings image => image.OutputFormatId,
            AudioConversionSettings audio => audio.OutputFormatId,
            VideoConversionSettings video => video.ContainerFormatId,
            VideoUpscaleSettings videoUpscale => videoUpscale.ContainerFormatId,
            UpscaleSettings upscale when !string.Equals(upscale.OutputFormatId, FormatIds.Auto, StringComparison.OrdinalIgnoreCase) =>
                upscale.OutputFormatId,
            UpscaleSettings => input?.FormatId is { } actual && _registry.FindFormat(actual, MediaCategory.Image) is { CanWrite: true }
                ? actual
                : "png",
            DocumentConversionSettings document => document.OutputFormatId,
            EbookConversionSettings ebook => ebook.OutputFormatId,
            ArchiveConversionSettings archive => archive.Action == ArchiveAction.Extract ? "folder" : archive.OutputFormatId,
            AudioToolSettings audioTool => audioTool.OutputFormatId,
            ImageToolSettings imageTool => imageTool.OutputFormatId,
            VideoToolSettings videoTool => videoTool.Tool switch
            {
                VideoToolOperation.Thumbnail => videoTool.ThumbnailFormatId,
                VideoToolOperation.ExtractAudio => videoTool.ExtractAudioFormatId,
                VideoToolOperation.Subtitles when videoTool.SubtitleMode == SubtitleToolMode.Extract =>
                    videoTool.SubtitleFormatId,
                VideoToolOperation.Gif => "gif",
                _ => videoTool.ContainerFormatId
            },
            _ => null
        };
    }

    private static string GetRequiredEngineId(OperationKind operation) => operation switch
    {
        OperationKind.ImageConvert => EngineIds.Images,
        OperationKind.AudioConvert or OperationKind.VideoConvert => EngineIds.FFmpeg,
        OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => EngineIds.Upscaling,
        OperationKind.DocumentConvert or OperationKind.EbookConvert => EngineIds.Documents,
        OperationKind.ArchiveConvert => EngineIds.Archives,
        OperationKind.AudioProcess or OperationKind.VideoProcess => EngineIds.FFmpegTools,
        OperationKind.ImageProcess => EngineIds.ImageTools,
        OperationKind.VideoUpscale => EngineIds.VideoUpscaling,
        OperationKind.OcrDocument => EngineIds.Ocr,
        _ => ""
    };

    private static string EngineDisplayName(string engineId) => engineId switch
    {
        EngineIds.Images => "The image engine",
        EngineIds.FFmpeg => "FFmpeg",
        EngineIds.Upscaling => "The upscaling engine",
        EngineIds.Documents => "The document engine",
        EngineIds.Archives => "The archive engine",
        EngineIds.FFmpegTools => "The audio/video tools engine",
        EngineIds.ImageTools => "The image tools engine",
        EngineIds.VideoUpscaling => "The video upscaling engine",
        EngineIds.Ocr => "The OCR engine",
        _ => "The processing engine"
    };
}
