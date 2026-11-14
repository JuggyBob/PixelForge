using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.FFmpeg;

namespace PixelForge.Engines.FFmpegTools;

/// <summary>
/// Audio and video tools built on the bundled FFmpeg/FFprobe. Every operation uses
/// the same central queue, temporary output, and verification path as conversion.
/// </summary>
public sealed partial class FFmpegToolsEngine : IProcessingEngine
{
    private readonly IProcessRunner _runner;
    private readonly IAppPaths _paths;
    private readonly ILogger<FFmpegToolsEngine> _logger;
    private readonly FFmpegLocator _locator;
    private readonly FFprobeService _probe;
    private readonly FFmpegCapabilityService _capabilities;
    private readonly string? _rnnModelPath;

    public FFmpegToolsEngine(IProcessRunner runner, IAppPaths paths, ILogger<FFmpegToolsEngine> logger)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
        _locator = FFmpegLocator.Discover();
        _probe = new FFprobeService(runner, _locator);
        _capabilities = new FFmpegCapabilityService(runner, _locator, paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FFmpegCapabilityService>.Instance);
        _rnnModelPath = ResolveRnnModelPath();
    }

    public string Id => EngineIds.FFmpegTools;
    public string DisplayName => "Audio & video tools";
    public MediaCategory[] Categories => [MediaCategory.Audio, MediaCategory.Video];

    public async Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var baseCapability = capabilities.Capability;
        return new EngineCapability
        {
            EngineId = Id,
            DisplayName = DisplayName,
            Version = baseCapability.Version,
            IsAvailable = baseCapability.IsAvailable,
            Error = baseCapability.Error,
            Formats = baseCapability.Formats.Select(f => f with { EngineId = Id }).ToList(),
            VideoCodecs = baseCapability.VideoCodecs,
            AudioCodecs = baseCapability.AudioCodecs,
            ExecutionProviders =
            [
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = true,
                    Details = "Audio and video tools run locally in FFmpeg."
                }
            ],
            Algorithms =
            [
                new() { Id = "loudness", DisplayName = "EBU R128 loudness normalisation", Description = "Two-pass integrated loudness measurement with true-peak limiting." },
                new() { Id = "rnnoise", DisplayName = "RNNoise", Description = "Recurrent neural-network speech denoising bundled with the application." },
                new() { Id = "afftdn", DisplayName = "Spectral denoise", Description = "FFT-based noise reduction for general recordings." },
                new() { Id = "silence", DisplayName = "Silence detection", Description = "Detect and remove silent regions using FFmpeg silencedetect." },
                new() { Id = "deshake", DisplayName = "Video stabilisation", Description = "Local FFmpeg deshake filter." },
                new() { Id = "hdr", DisplayName = "HDR to SDR", Description = "zscale and hable tone mapping." }
            ]
        };
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable) return Task.FromResult<MediaInfo?>(null);
        return _probe.InspectAsync(path, cancellationToken)!;
    }

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        return request.Operation switch
        {
            OperationKind.AudioProcess when request.Settings is AudioToolSettings audio =>
                await VerifyAudioOutputAsync(request, audio, outputPath, cancellationToken),
            OperationKind.VideoProcess when request.Settings is VideoToolSettings { Tool: VideoToolOperation.Subtitles, SubtitleMode: SubtitleToolMode.Extract or SubtitleToolMode.Convert } video =>
                await VerifySubtitleOutputAsync(request, video, outputPath, cancellationToken),
            OperationKind.VideoProcess when request.Settings is VideoToolSettings video =>
                await VerifyVideoOutputAsync(request, video, outputPath, cancellationToken),
            _ => VerificationResult.Fail("The audio/video tool settings are invalid.")
        };
    }

    public async Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable) return null;
        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-ss", "1", "-i", inputPath,
                "-frames:v", "1", "-an", "-sn",
                "-vf", $"scale={Math.Max(16, maxSize)}:-2:flags=lanczos",
                outputPath
            ],
            WorkingDirectory = _paths.TempDirectory,
            Timeout = TimeSpan.FromSeconds(60),
            MaxCapturedLines = 40
        }, cancellationToken);
        return result.Succeeded && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
            ? outputPath
            : null;
    }

    public async Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable)
            return ValidationResult.Failure("ffmpeg.tools.missing", _locator.Error ?? "FFmpeg is unavailable.");
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        if (!capabilities.Capability.IsAvailable)
            return ValidationResult.Failure("ffmpeg.tools.unavailable", capabilities.Error ?? "FFmpeg is unavailable.");

        return request.Operation switch
        {
            OperationKind.AudioProcess when request.Settings is AudioToolSettings audio =>
                ValidateAudioTool(audio, request, capabilities),
            OperationKind.VideoProcess when request.Settings is VideoToolSettings video =>
                ValidateVideoTool(video, request, capabilities),
            _ => ValidationResult.Failure("ffmpeg.tools.operation", "The audio/video tools engine does not support this operation.")
        };
    }

    private ValidationResult ValidateAudioTool(AudioToolSettings settings, ProcessingRequest request,
        FFmpegCapabilities capabilities)
    {
        var issues = new List<ValidationIssue>();
        if (!capabilities.Capability.Formats.Any(f =>
                f.Category == MediaCategory.Audio && f.CanWrite &&
                string.Equals(f.Id, settings.OutputFormatId, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(ValidationIssue.Error("audio.tools.format", "The selected audio output format is not available."));
        }

        var info = request.InputInfo;
        var isVideo = info?.Category == MediaCategory.Video;
        var isAudio = info?.Category == MediaCategory.Audio;
        if (settings.Tool == AudioToolOperation.ExtractFromVideo)
        {
            if (!isVideo)
                issues.Add(ValidationIssue.Error("audio.extract.input", "Choose a video file with an audio track."));
            else if (info?.AudioCodec is null)
                issues.Add(ValidationIssue.Error("audio.extract.noaudio", "The selected video has no audio stream."));
        }
        else if (!isVideo && !isAudio)
        {
            issues.Add(ValidationIssue.Error("audio.tools.input", "Choose an audio file or a video with an audio track."));
        }
        else if (isVideo && info?.AudioCodec is null)
        {
            issues.Add(ValidationIssue.Error("audio.tools.noaudio", "The selected video has no audio stream."));
        }

        if (settings.Tool == AudioToolOperation.NormalizeLoudness)
        {
            if (settings.TargetLufs is < -40 or > -5)
                issues.Add(ValidationIssue.Error("audio.loudness.target", "Choose a target between -40 and -5 LUFS.", "TargetLufs"));
            if (settings.TruePeakDb is < -9 or > 0)
                issues.Add(ValidationIssue.Error("audio.loudness.peak", "True peak must be between -9 and 0 dBTP.", "TruePeakDb"));
        }

        if (settings.Tool == AudioToolOperation.ReduceNoise &&
            settings.NoiseMethod == NoiseReductionMethod.Rnnoise && _rnnModelPath is null)
            issues.Add(ValidationIssue.Error("audio.noise.model", "The bundled RNNoise model (std.rnnn) was not found."));

        if (settings.Tool == AudioToolOperation.RemoveSilence)
        {
            if (settings.SilenceThresholdDb is < -80 or > -10)
                issues.Add(ValidationIssue.Error("audio.silence.threshold", "Silence threshold must be between -80 and -10 dB."));
            if (settings.MinSilenceSeconds is < 0.1 or > 30)
                issues.Add(ValidationIssue.Error("audio.silence.duration", "Minimum silence must be between 0.1 and 30 seconds."));
            if (settings.KeepSilenceMarginSeconds is < 0 or > 2)
                issues.Add(ValidationIssue.Error("audio.silence.margin", "Keep margin must be between 0 and 2 seconds."));
        }

        if (settings.Tool == AudioToolOperation.Trim)
        {
            var duration = info?.DurationSeconds ?? 0;
            if (settings.TrimStartSeconds < 0)
                issues.Add(ValidationIssue.Error("audio.trim.start", "Trim start cannot be negative."));
            if (settings.TrimEndSeconds > 0 && settings.TrimEndSeconds <= settings.TrimStartSeconds)
                issues.Add(ValidationIssue.Error("audio.trim.range", "Trim end must be after trim start.", "TrimEndSeconds"));
            if (duration > 0 && settings.TrimStartSeconds >= duration)
                issues.Add(ValidationIssue.Error("audio.trim.start", "Trim start is beyond the end of the file."));
        }

        if (settings.Tool == AudioToolOperation.Merge)
        {
            var paths = MergePaths(request.InputPath, settings.MergeInputPaths);
            if (paths.Count < 2)
                issues.Add(ValidationIssue.Error("audio.merge.count", "Add at least two audio files to merge."));
            var missing = paths.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
                issues.Add(ValidationIssue.Error("audio.merge.missing",
                    $"These merge inputs do not exist: {string.Join(", ", missing.Select(Path.GetFileName))}"));
        }

        if (settings.Tool == AudioToolOperation.ConvertChannelsAndRate)
        {
            if (settings.Channels is not (0 or 1 or 2))
                issues.Add(ValidationIssue.Error("audio.channels", "Channels must be mono (1) or stereo (2)."));
            if (settings.SampleRate != 0 && settings.SampleRate is < 8000 or > 192000)
                issues.Add(ValidationIssue.Error("audio.samplerate", "Sample rate must be between 8000 and 192000 Hz."));
        }

        if (settings.Tool == AudioToolOperation.EditMetadata &&
            settings.CoverArtPath is { Length: > 0 } cover && !File.Exists(cover))
            issues.Add(ValidationIssue.Error("audio.metadata.cover", "The selected cover art file does not exist."));

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
                OperationKind.AudioProcess when request.Settings is AudioToolSettings audio =>
                    await ProcessAudioToolAsync(request, audio, progress, cancellationToken),
                OperationKind.VideoProcess when request.Settings is VideoToolSettings video =>
                    await ProcessVideoToolAsync(request, video, progress, cancellationToken),
                _ => ProcessingResult.Fail("The audio/video tools engine does not support this operation.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio/video tool failed for {Input}", request.InputPath);
            return ProcessingResult.Fail(FriendlyError(ex.Message), ex.ToString());
        }
    }

    private async Task<ProcessingResult> ProcessAudioToolAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        return settings.Tool switch
        {
            AudioToolOperation.NormalizeLoudness => await ProcessLoudnessAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.ReduceNoise => await ProcessNoiseReductionAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.RemoveSilence => await ProcessSilenceRemovalAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.Trim => await ProcessAudioTrimAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.Merge => await ProcessAudioMergeAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.ConvertChannelsAndRate => await ProcessChannelRateAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.EditMetadata => await ProcessAudioMetadataAsync(request, settings, progress, cancellationToken),
            AudioToolOperation.ExtractFromVideo => await ProcessAudioExtractAsync(request, settings, progress, cancellationToken),
            _ => ProcessingResult.Fail("The selected audio tool is not available.")
        };
    }

    private async Task<ProcessingResult> ProcessLoudnessAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(ProcessingProgress.Indeterminate("Measuring integrated loudness…"));
        var measurement = await AnalyzeLoudnessAsync(request.InputPath, settings.TargetLufs, settings.TruePeakDb,
            settings.LoudnessRange, cancellationToken);
        var warnings = new List<string>();
        string filter;
        if (measurement is null)
        {
            filter = $"loudnorm=I={F(settings.TargetLufs)}:TP={F(settings.TruePeakDb)}:LRA={F(settings.LoudnessRange)}";
            warnings.Add("Integrated loudness could not be measured; a single-pass loudness correction was applied.");
        }
        else
        {
            filter = "loudnorm=" +
                     $"I={F(settings.TargetLufs)}:TP={F(settings.TruePeakDb)}:LRA={F(settings.LoudnessRange)}:" +
                     $"measured_I={F(measurement.InputI)}:measured_TP={F(measurement.InputTp)}:" +
                     $"measured_LRA={F(measurement.InputLra)}:measured_thresh={F(measurement.InputThresh)}:" +
                     $"offset={F(measurement.TargetOffset)}:linear=true";
        }

        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn", "-af", filter]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate > 0 ? settings.SampleRate : 48000, settings.Channels,
            copy: false, preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        var result = await ExecuteAudioAsync(request, settings, progress, args, request.InputInfo?.DurationSeconds ?? 0,
            warnings, null, cancellationToken);
        return result;
    }

    private async Task<ProcessingResult> ProcessNoiseReductionAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        string? workDirectory = null;
        string filter;
        if (settings.NoiseMethod == NoiseReductionMethod.Rnnoise)
        {
            if (_rnnModelPath is null)
                return ProcessingResult.Fail("The bundled RNNoise model (std.rnnn) was not found.");
            workDirectory = Path.GetDirectoryName(_rnnModelPath);
            filter = "arnndn=m=std.rnnn";
        }
        else
        {
            var amount = Math.Clamp(settings.NoiseReductionDb, 1, 48);
            filter = $"afftdn=nr={F(amount)}:nf=-45";
        }

        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn", "-af", filter]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy: false,
            preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        var warnings = new List<string>
        {
            settings.NoiseMethod == NoiseReductionMethod.Rnnoise
                ? "RNNoise is optimised for speech; music and strong effects may be altered."
                : "Spectral denoising reduces steady background noise and can soften quiet detail."
        };
        return await ExecuteAudioAsync(request, settings, progress, args, request.InputInfo?.DurationSeconds ?? 0,
            warnings, workDirectory, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessSilenceRemovalAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var duration = request.InputInfo?.DurationSeconds ?? 0;
        if (duration <= 0)
        {
            var info = await _probe.InspectAsync(request.InputPath, cancellationToken);
            duration = info.DurationSeconds ?? 0;
        }

        progress.Report(ProcessingProgress.Indeterminate("Detecting silent regions…"));
        var silences = await DetectSilenceAsync(request.InputPath, settings.SilenceThresholdDb,
            settings.MinSilenceSeconds, duration, cancellationToken);
        var warnings = new List<string>();
        var filter = BuildSilenceRemovalFilter(silences, duration, Math.Clamp(settings.KeepSilenceMarginSeconds, 0, 2),
            warnings);
        if (filter is null)
        {
            warnings.Add("No silent regions were detected; the audio was re-encoded without cutting.");
            filter = "anull";
        }

        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn", "-af", filter]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy: false,
            preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        warnings.Add($"{silences.Count} silent region{(silences.Count == 1 ? "" : "s")} detected.");
        return await ExecuteAudioAsync(request, settings, progress, args, duration, warnings, null, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessAudioTrimAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var duration = request.InputInfo?.DurationSeconds ?? 0;
        var end = settings.TrimEndSeconds > 0
            ? settings.TrimEndSeconds
            : duration > 0 ? duration : 0;
        if (end <= settings.TrimStartSeconds)
            return ProcessingResult.Fail("Trim end must be after trim start.");
        var filter = $"atrim=start={F(settings.TrimStartSeconds)}:end={F(end)},asetpts=PTS-STARTPTS";
        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn", "-af", filter]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy: false,
            preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        return await ExecuteAudioAsync(request, settings, progress, args, end - settings.TrimStartSeconds,
            [], null, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessAudioMergeAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var paths = MergePaths(request.InputPath, settings.MergeInputPaths);
        if (paths.Count < 2)
            return ProcessingResult.Fail("Add at least two audio files to merge.");
        foreach (var path in paths)
        {
            if (!File.Exists(path)) return ProcessingResult.Fail($"The merge input does not exist: {Path.GetFileName(path)}");
        }

        var args = BuildAudioInput(paths[0]);
        for (var i = 1; i < paths.Count; i++)
            args.AddRange(["-i", paths[i]]);

        var layout = settings.Channels == 1 ? "mono" : "stereo";
        var filters = new List<string>();
        for (var i = 0; i < paths.Count; i++)
            filters.Add($"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts={layout}[a{i}]");
        var inputs = string.Concat(Enumerable.Range(0, paths.Count).Select(i => $"[a{i}]"));
        filters.Add($"{inputs}concat=n={paths.Count}:v=0:a=1[aout]");
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[aout]", "-vn", "-sn", "-dn"]);

        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate > 0 ? settings.SampleRate : 48000, settings.Channels,
            copy: false, preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        var totalDuration = 0d;
        foreach (var path in paths)
        {
            var info = await _probe.InspectAsync(path, cancellationToken);
            totalDuration += info.DurationSeconds ?? 0;
        }

        var warnings = new List<string>
        {
            "Inputs are resampled to 48 kHz and converted to a common channel layout, then concatenated without gaps."
        };
        return await ExecuteAudioAsync(request, settings, progress, args, totalDuration, warnings, null, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessChannelRateAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn"]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy: false,
            preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        return await ExecuteAudioAsync(request, settings, progress, args, request.InputInfo?.DurationSeconds ?? 0,
            [], null, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessAudioMetadataAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var args = BuildAudioInput(request.InputPath);
        var inputCodec = request.InputInfo?.AudioCodec;
        var copy = settings.MetadataMode != AudioMetadataMode.RemoveAll &&
                   inputCodec is not null &&
                   !string.IsNullOrWhiteSpace(settings.OutputFormatId) &&
                   CompatibilityMatrix.CanCopyAudio(settings.OutputFormatId, inputCodec);
        args.AddRange(["-map", "0:a:0?", "-vn", "-sn", "-dn"]);

        if (settings.RemoveCoverArt)
        {
            // Mapping only audio removes attached pictures.
        }
        else if (!string.IsNullOrWhiteSpace(settings.CoverArtPath))
        {
            if (!File.Exists(settings.CoverArtPath))
                return ProcessingResult.Fail("The selected cover art file does not exist.");
            args.AddRange(["-i", settings.CoverArtPath, "-map", "1:v:0", "-c:v", "copy",
                "-disposition:v", "attached_pic", "-metadata:s:v", "title=Album cover",
                "-metadata:s:v", "comment=Cover (front)"]);
        }

        var removed = settings.MetadataMode == AudioMetadataMode.RemoveAll;
        args.AddRange(["-map_metadata", removed ? "-1" : "0"]);
        AddMetadataArgs(args, settings.MetaTitle, settings.MetaArtist, settings.MetaAlbum,
            settings.MetaAlbumArtist, settings.MetaGenre, settings.MetaYear, settings.MetaComment,
            settings.MetaComposer, settings.MetaTrack, settings.MetaDisc);

        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy, preserveMetadata: !removed);
        var warnings = new List<string>();
        if (copy && inputCodec is not null &&
            !string.Equals(inputCodec, CodecForAudioFormat(settings.OutputFormatId), StringComparison.OrdinalIgnoreCase))
            warnings.Add($"The {inputCodec} audio stream is copied without re-encoding.");
        return await ExecuteAudioAsync(request, settings, progress, args, request.InputInfo?.DurationSeconds ?? 0,
            warnings, null, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessAudioExtractAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var streamIndex = Math.Max(0, settings.AudioStreamIndex);
        var inputCodec = request.InputInfo?.Streams
            .Where(s => s.Type == "audio")
            .OrderBy(s => s.Index)
            .Skip(streamIndex)
            .FirstOrDefault()?.Codec;
        var copy = settings.CopyAudioStream && inputCodec is not null &&
                   CompatibilityMatrix.CanCopyAudio(settings.OutputFormatId, inputCodec);
        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", $"0:a:{streamIndex}?", "-vn", "-sn", "-dn"]);
        var encoder = ResolveAudioEncoder(settings.OutputFormatId);
        AppendAudioOutput(args, request, settings.OutputFormatId, encoder, settings.BitrateKbps,
            settings.SampleRate, settings.Channels, copy,
            preserveMetadata: settings.MetadataMode != AudioMetadataMode.RemoveAll);
        var warnings = new List<string>();
        if (copy) warnings.Add("The original audio stream was copied without re-encoding.");
        else if (settings.CopyAudioStream)
            warnings.Add("The source audio codec is not compatible with the selected container, so the audio was re-encoded.");
        return await ExecuteAudioAsync(request, settings, progress, args, request.InputInfo?.DurationSeconds ?? 0,
            warnings, null, cancellationToken);
    }

    private async Task<ProcessingResult> ExecuteAudioAsync(ProcessingRequest request, AudioToolSettings settings,
        IProgress<ProcessingProgress> progress, List<string> args, double duration, List<string> warnings,
        string? workingDirectory, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var result = await RunWithProgressAsync(args, duration, progress, workingDirectory,
            TimeSpan.FromHours(12), cancellationToken);
        started.Stop();
        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (result.TimedOut) return ProcessingResult.Fail("FFmpeg timed out while processing the audio.", result.StandardError, started.Elapsed);
        if (!result.Succeeded)
            return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError, started.Elapsed);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg completed but produced no audio output.");

        progress.Report(ProcessingProgress.Percent(0.96, "Verifying audio…"));
        var verification = await VerifyAudioOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid)
            return ProcessingResult.Fail(verification.Summary, verification.Details, started.Elapsed);
        progress.Report(ProcessingProgress.Percent(1, "Audio ready."));
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification,
            warnings.Concat(verification.Issues.Where(i => i.Severity == ValidationSeverity.Info).Select(i => i.Message)).ToList());
    }

    private async Task<VerificationResult> VerifyAudioOutputAsync(ProcessingRequest request, AudioToolSettings settings,
        string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            var actual = await _probe.InspectAsync(outputPath, cancellationToken);
            if (!actual.IsReadable)
                return VerificationResult.Fail($"The output could not be inspected: {actual.Error}", actual);
            if (actual.AudioCodec is null)
                return VerificationResult.Fail("The output contains no audio stream.", actual);

            var issues = new List<ValidationIssue>();
            var input = request.InputInfo;
            if (settings.SampleRate > 0 && actual.SampleRate is > 0 && actual.SampleRate != settings.SampleRate)
                issues.Add(ValidationIssue.Warning("audio.verify.samplerate",
                    $"The output sample rate is {actual.SampleRate} Hz, expected {settings.SampleRate} Hz."));
            if (settings.Channels > 0 && actual.Channels is > 0 && actual.Channels != settings.Channels)
                issues.Add(ValidationIssue.Warning("audio.verify.channels",
                    $"The output has {actual.Channels} channel(s), expected {settings.Channels}."));

            var expectedDuration = settings.Tool switch
            {
                AudioToolOperation.Trim => settings.TrimEndSeconds > 0
                    ? settings.TrimEndSeconds - settings.TrimStartSeconds
                    : (input?.DurationSeconds ?? 0) - settings.TrimStartSeconds,
                AudioToolOperation.Merge => await SumDurationsAsync(request.InputPath, settings.MergeInputPaths, cancellationToken),
                _ => input?.DurationSeconds ?? 0
            };
            if (settings.Tool == AudioToolOperation.RemoveSilence)
            {
                if (actual.DurationSeconds is > 0 && input?.DurationSeconds is > 0 &&
                    actual.DurationSeconds > input.DurationSeconds + 0.5)
                    return VerificationResult.Fail("The silence-removed output is longer than the source.", actual);
            }
            else if (expectedDuration > 0 && actual.DurationSeconds is > 0)
            {
                var tolerance = settings.Tool == AudioToolOperation.Merge
                    ? Math.Max(2.0, expectedDuration * 0.04)
                    : Math.Max(1.0, expectedDuration * 0.04);
                if (Math.Abs(expectedDuration - actual.DurationSeconds.Value) > tolerance)
                    return VerificationResult.Fail(
                        $"The output duration is {actual.DurationSeconds:0.00}s, expected about {expectedDuration:0.00}s.", actual);
            }

            if (settings.Tool == AudioToolOperation.NormalizeLoudness)
            {
                var measured = await AnalyzeLoudnessAsync(outputPath, settings.TargetLufs, settings.TruePeakDb,
                    settings.LoudnessRange, cancellationToken);
                if (measured is not null)
                {
                    var delta = Math.Abs(measured.InputI - settings.TargetLufs);
                    if (delta > 1.5)
                        issues.Add(ValidationIssue.Warning("audio.verify.loudness",
                            $"Measured integrated loudness is {measured.InputI:0.0} LUFS; the target was {settings.TargetLufs:0.0} LUFS."));
                    issues.Add(ValidationIssue.Info("audio.loudness.measured",
                        $"Measured {measured.InputI:0.0} LUFS, true peak {measured.InputTp:0.0} dBTP."));
                }
            }

            var info = actual with { EngineId = Id };
            return new VerificationResult
            {
                IsValid = true,
                Summary = $"{actual.FormatName ?? settings.OutputFormatId} · {actual.AudioCodec} · " +
                          $"{actual.SampleRate ?? 0} Hz · {actual.Channels ?? 0} ch · {actual.DurationSeconds:0.0}s",
                Issues = issues,
                ActualInfo = info
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VerificationResult.Fail("The output audio could not be verified.", details: ex.ToString());
        }
    }

    private async Task<double> SumDurationsAsync(string mainPath, IReadOnlyList<string> mergePaths,
        CancellationToken cancellationToken)
    {
        var paths = MergePaths(mainPath, mergePaths);
        var total = 0d;
        foreach (var path in paths)
        {
            var info = await _probe.InspectAsync(path, cancellationToken);
            total += info.DurationSeconds ?? 0;
        }
        return total;
    }

    private async Task<ProcessResult> RunWithProgressAsync(List<string> args, double duration,
        IProgress<ProcessingProgress> progress, string? workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var parser = new FFmpegProgressParser(duration, progress);
        return await _runner.RunStreamingAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments = args,
            WorkingDirectory = workingDirectory ?? _paths.TempDirectory,
            Timeout = timeout,
            MaxCapturedLines = 600
        }, parser.ParseLine, null, cancellationToken);
    }

    private static List<string> BuildAudioInput(string path) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-progress", "pipe:1", "-nostats",
        "-loglevel", "warning",
        "-i", path
    ];

    private void AppendAudioOutput(List<string> args, ProcessingRequest request, string formatId,
        string encoder, int bitrateKbps, int sampleRate, int channels, bool copy, bool preserveMetadata)
    {
        if (copy) args.AddRange(["-c:a", "copy"]);
        else
        {
            args.AddRange(["-c:a", encoder]);
            if (!IsLosslessAudio(formatId))
                args.AddRange(["-b:a", Math.Clamp(bitrateKbps, 8, 1536).ToString(CultureInfo.InvariantCulture) + "k"]);
        }

        if (sampleRate > 0 && !copy)
            args.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
        if (channels > 0 && !copy)
            args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-map_metadata", preserveMetadata ? "0" : "-1"]);
        if (formatId == "mp3") args.AddRange(["-id3v2_version", "3"]);
        if (formatId == "m4a") args.AddRange(["-movflags", "+faststart"]);
        var muxer = FormatCatalog.FindById(formatId)?.MuxerName ?? formatId;
        args.AddRange(["-f", muxer, request.TempOutputPath]);
    }

    private static bool IsLosslessAudio(string formatId) => formatId is "wav" or "flac";

    private string ResolveAudioEncoder(string formatId)
    {
        var preferred = formatId switch
        {
            "wav" => "pcm_s16le",
            "flac" => "flac",
            "mp3" => "libmp3lame",
            "m4a" => "aac",
            "aac" => "aac",
            "ogg" => "libvorbis",
            "opus" => "libopus",
            _ => "aac"
        };
        var available = _capabilities.GetAsync().GetAwaiter().GetResult().AvailableEncoders;
        if (available.Contains(preferred)) return preferred;
        var fallback = formatId switch
        {
            "mp3" => new[] { "libmp3lame" },
            "m4a" or "aac" => new[] { "aac", "aac_mf" },
            "ogg" => new[] { "libvorbis" },
            "opus" => new[] { "libopus" },
            "flac" => new[] { "flac" },
            "wav" => new[] { "pcm_s16le" },
            _ => Array.Empty<string>()
        };
        return fallback.FirstOrDefault(available.Contains) ?? preferred;
    }

    private static string? CodecForAudioFormat(string formatId) => formatId switch
    {
        "mp3" => "mp3",
        "m4a" => "aac",
        "aac" => "aac",
        "ogg" => "vorbis",
        "opus" => "opus",
        "flac" => "flac",
        "wav" => "pcm_s16le",
        _ => null
    };

    private static void AddMetadataArgs(List<string> args, string? title, string? artist, string? album,
        string? albumArtist, string? genre, string? year, string? comment, string? composer, string? track, string? disc)
    {
        if (!string.IsNullOrWhiteSpace(title)) args.AddRange(["-metadata", "title=" + title.Trim()]);
        if (!string.IsNullOrWhiteSpace(artist)) args.AddRange(["-metadata", "artist=" + artist.Trim()]);
        if (!string.IsNullOrWhiteSpace(album)) args.AddRange(["-metadata", "album=" + album.Trim()]);
        if (!string.IsNullOrWhiteSpace(albumArtist)) args.AddRange(["-metadata", "album_artist=" + albumArtist.Trim()]);
        if (!string.IsNullOrWhiteSpace(genre)) args.AddRange(["-metadata", "genre=" + genre.Trim()]);
        if (!string.IsNullOrWhiteSpace(year)) args.AddRange(["-metadata", "date=" + year.Trim()]);
        if (!string.IsNullOrWhiteSpace(comment)) args.AddRange(["-metadata", "comment=" + comment.Trim()]);
        if (!string.IsNullOrWhiteSpace(composer)) args.AddRange(["-metadata", "composer=" + composer.Trim()]);
        if (!string.IsNullOrWhiteSpace(track)) args.AddRange(["-metadata", "track=" + track.Trim()]);
        if (!string.IsNullOrWhiteSpace(disc)) args.AddRange(["-metadata", "disc=" + disc.Trim()]);
    }

    private async Task<LoudnessMeasurement?> AnalyzeLoudnessAsync(string path, double targetI, double targetTp,
        double targetLra, CancellationToken cancellationToken)
    {
        var filter = $"loudnorm=I={F(targetI)}:TP={F(targetTp)}:LRA={F(targetLra)}:print_format=json";
        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments =
            [
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                "-i", path, "-map", "0:a:0?", "-vn", "-sn", "-dn",
                "-af", filter, "-f", "null", "-"
            ],
            WorkingDirectory = _paths.TempDirectory,
            Timeout = TimeSpan.FromHours(4),
            MaxCapturedLines = 800
        }, cancellationToken);
        if (!result.Succeeded) return null;
        return ParseLoudness(result.StandardError);
    }

    private static LoudnessMeasurement? ParseLoudness(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            return new LoudnessMeasurement(
                ReadDouble(root, "input_i"),
                ReadDouble(root, "input_tp"),
                ReadDouble(root, "input_lra"),
                ReadDouble(root, "input_thresh"),
                ReadDouble(root, "target_offset"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private async Task<List<SilenceRegion>> DetectSilenceAsync(string path, double thresholdDb,
        double minSilenceSeconds, double duration, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath!,
            Arguments =
            [
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                "-i", path, "-map", "0:a:0?", "-vn", "-sn", "-dn",
                "-af", $"silencedetect=noise={F(thresholdDb)}dB:d={F(minSilenceSeconds)}",
                "-f", "null", "-"
            ],
            WorkingDirectory = _paths.TempDirectory,
            Timeout = TimeSpan.FromHours(4),
            MaxCapturedLines = 1200
        }, cancellationToken);
        var regions = new List<SilenceRegion>();
        double? start = null;
        foreach (var line in result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var startMatch = Regex.Match(line, @"silence_start:\s*(-?[0-9.]+)");
            if (startMatch.Success &&
                double.TryParse(startMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                start = Math.Max(0, s);
                continue;
            }

            var endMatch = Regex.Match(line, @"silence_end:\s*(-?[0-9.]+)");
            if (endMatch.Success &&
                double.TryParse(endMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
            {
                var begin = start ?? 0;
                if (e > begin) regions.Add(new SilenceRegion(begin, e));
                start = null;
            }
        }

        if (start is not null)
        {
            var end = duration > 0 ? duration : start.Value;
            if (end > start.Value) regions.Add(new SilenceRegion(start.Value, end));
        }

        return regions;
    }

    private static string? BuildSilenceRemovalFilter(IReadOnlyList<SilenceRegion> silences, double duration,
        double margin, List<string> warnings)
    {
        if (silences.Count == 0) return null;
        if (duration <= 0) duration = silences.Max(s => s.End);
        var keep = new List<(double Start, double End)>();
        var cursor = 0d;
        foreach (var silence in silences.OrderBy(s => s.Start))
        {
            var keepEnd = Math.Min(duration, Math.Max(cursor, silence.Start + margin));
            if (keepEnd > cursor + 0.02) keep.Add((cursor, keepEnd));
            cursor = Math.Max(cursor, Math.Min(duration, silence.End - margin));
        }
        if (cursor < duration - 0.02) keep.Add((cursor, duration));
        if (keep.Count == 0)
        {
            warnings.Add("All detected audio was silent; the output is a short preserved lead-in.");
            return "atrim=start=0:end=0.05,asetpts=PTS-STARTPTS";
        }

        var expression = string.Join("+", keep.Select(k =>
            $"between(t,{F(k.Start)},{F(k.End)})"));
        return $"aselect='{expression}',asetpts=N/SR/TB";
    }

    private static List<string> MergePaths(string mainPath, IReadOnlyList<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!list.Contains(Path.GetFullPath(mainPath), StringComparer.OrdinalIgnoreCase))
            list.Insert(0, Path.GetFullPath(mainPath));
        return list;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private string? ResolveRnnModelPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "audio", "rnnoise", "std.rnnn"),
            Path.Combine(AppContext.BaseDirectory, "rnnoise", "std.rnnn")
        };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "third_party", "audio", "rnnoise", "std.rnnn"));
        return candidates.FirstOrDefault(File.Exists);
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
        if (text.Contains("Error initializing filter", StringComparison.OrdinalIgnoreCase))
            return "A processing filter could not be initialized with the selected settings.";
        if (text.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
            return "The requested stream is not present in the source file.";
        if (text.Contains("loudnorm", StringComparison.OrdinalIgnoreCase))
            return "Loudness measurement or normalisation failed. Try a different target or disable linear mode.";
        if (text.Contains("arnndn", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rnnoise", StringComparison.OrdinalIgnoreCase))
            return "RNNoise could not process this audio. Try the spectral denoiser.";
        var last = lines.LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? "FFmpeg could not complete the operation." : last;
    }

    private sealed record LoudnessMeasurement(double InputI, double InputTp, double InputLra, double InputThresh,
        double TargetOffset);

    private sealed record SilenceRegion(double Start, double End);

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
                        Report(Math.Clamp(seconds / _duration, 0, 0.99), seconds);
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
                : "Processing…";
            _progress.Report(ProcessingProgress.Percent(fraction, message));
        }

        private static string FormatTime(double seconds) => seconds >= 3600
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";
}
