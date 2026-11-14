using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.FFmpeg;

namespace PixelForge.Engines.FFmpegTools;

public sealed partial class FFmpegToolsEngine
{
    private ValidationResult ValidateVideoTool(VideoToolSettings settings, ProcessingRequest request,
        FFmpegCapabilities capabilities)
    {
        var issues = new List<ValidationIssue>();
        var info = request.InputInfo;
        if (info is not { Category: MediaCategory.Video })
        {
            issues.Add(ValidationIssue.Error("video.tools.input", "Choose a video file for this tool."));
            return new ValidationResult { Issues = issues };
        }

        if (settings.Tool == VideoToolOperation.Thumbnail)
        {
            var format = FormatCatalog.FindById(settings.ThumbnailFormatId);
            if (format is null || format.Category != MediaCategory.Image ||
                format.Id is not ("jpg" or "png" or "webp" or "avif" or "tif" or "bmp"))
                issues.Add(ValidationIssue.Error("video.thumbnail.format", "Choose a supported thumbnail image format."));
            if (info.DurationSeconds is > 0 && settings.ThumbnailTimeSeconds >= info.DurationSeconds)
                issues.Add(ValidationIssue.Error("video.thumbnail.time", "Choose a timestamp inside the video.", "ThumbnailTimeSeconds"));
            return new ValidationResult { Issues = issues };
        }

        if (settings.Tool == VideoToolOperation.ExtractAudio)
        {
            var format = FormatCatalog.FindById(settings.ExtractAudioFormatId);
            if (format is null || format.Category != MediaCategory.Audio ||
                !capabilities.Capability.Formats.Any(f => f.Id == format.Id && f.Category == MediaCategory.Audio && f.CanWrite))
                issues.Add(ValidationIssue.Error("video.audio.format", "Choose an available audio format."));
            if (info.AudioCodec is null)
                issues.Add(ValidationIssue.Error("video.audio.none", "The video has no audio stream to extract."));
            return new ValidationResult { Issues = issues };
        }

        if (settings.Tool == VideoToolOperation.Subtitles && settings.SubtitleMode is SubtitleToolMode.Extract or SubtitleToolMode.Convert)
        {
            if (settings.SubtitleFormatId is not ("srt" or "vtt" or "ass"))
                issues.Add(ValidationIssue.Error("video.subtitles.format", "Choose SRT, VTT or ASS subtitle output."));
            if ((info.SubtitleStreamCount ?? 0) == 0)
                issues.Add(ValidationIssue.Error("video.subtitles.none", "The video has no subtitle streams."));
            var subtitle = GetSubtitleStream(info, settings.SubtitleStreamIndex);
            if (subtitle is not null && IsBitmapSubtitle(subtitle.Codec))
                issues.Add(ValidationIssue.Error("video.subtitles.bitmap",
                    "Image-based subtitles cannot be converted to text without OCR. Choose a text subtitle track."));
            return new ValidationResult { Issues = issues };
        }

        if (settings.Tool == VideoToolOperation.Merge)
        {
            var paths = MergePaths(request.InputPath, settings.MergeInputPaths);
            if (paths.Count < 2)
                issues.Add(ValidationIssue.Error("video.merge.count", "Add at least two compatible videos to merge."));
            var missing = paths.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
                issues.Add(ValidationIssue.Error("video.merge.missing",
                    $"These merge inputs do not exist: {string.Join(", ", missing.Select(Path.GetFileName))}"));
        }

        if (settings.Tool == VideoToolOperation.ReplaceAudio)
        {
            if (string.IsNullOrWhiteSpace(settings.ReplacementAudioPath) || !File.Exists(settings.ReplacementAudioPath))
                issues.Add(ValidationIssue.Error("video.audio.replacement", "Choose an audio file to use as the replacement."));
            if (info.VideoCodec is null)
                issues.Add(ValidationIssue.Error("video.audio.novideo", "The input has no video stream."));
        }

        if (settings.Tool == VideoToolOperation.Trim)
        {
            var duration = info.DurationSeconds ?? 0;
            if (settings.TrimStartSeconds < 0)
                issues.Add(ValidationIssue.Error("video.trim.start", "Trim start cannot be negative."));
            if (settings.TrimEndSeconds > 0 && settings.TrimEndSeconds <= settings.TrimStartSeconds)
                issues.Add(ValidationIssue.Error("video.trim.range", "Trim end must be after trim start.", "TrimEndSeconds"));
            if (duration > 0 && settings.TrimStartSeconds >= duration)
                issues.Add(ValidationIssue.Error("video.trim.start", "Trim start is beyond the end of the video."));
        }

        if (settings.Tool == VideoToolOperation.Gif)
        {
            if (settings.GifEndSeconds <= settings.GifStartSeconds)
                issues.Add(ValidationIssue.Error("video.gif.range", "GIF end must be after the start.", "GifEndSeconds"));
            if (settings.GifFps is < 1 or > 30)
                issues.Add(ValidationIssue.Error("video.gif.fps", "GIF frame rate must be between 1 and 30 fps."));
            if (settings.GifWidth is < 16 or > 1920)
                issues.Add(ValidationIssue.Error("video.gif.width", "GIF width must be between 16 and 1920 pixels."));
        }

        if (settings.Tool == VideoToolOperation.Compress)
        {
            if (settings.TargetSizeMb <= 0)
                issues.Add(ValidationIssue.Error("video.targetsize", "Choose a target size greater than zero.", "TargetSizeMb"));
        }

        if (settings.Tool == VideoToolOperation.ResizeFrameRate)
        {
            if (settings.ResizeWidth is <= 0)
                issues.Add(ValidationIssue.Error("video.resize.width", "Width must be greater than zero."));
            if (settings.ResizeHeight is <= 0)
                issues.Add(ValidationIssue.Error("video.resize.height", "Height must be greater than zero."));
            if (settings.FrameRate is < 0 or > 240)
                issues.Add(ValidationIssue.Error("video.resize.fps", "Frame rate must be between 0 and 240."));
        }

        if (settings.Tool is not (VideoToolOperation.Thumbnail or VideoToolOperation.ExtractAudio))
        {
            var container = settings.Tool == VideoToolOperation.Gif ? "gif" : settings.ContainerFormatId;
            var format = capabilities.Capability.Formats.FirstOrDefault(f =>
                f.Category == MediaCategory.Video && f.CanWrite &&
                string.Equals(f.Id, container, StringComparison.OrdinalIgnoreCase));
            if (format is null)
                issues.Add(ValidationIssue.Error("video.tools.container", "The selected video container is not available."));
        }

        var needsVideoEncoder = settings.Tool is VideoToolOperation.Compress or VideoToolOperation.Stabilize or
            VideoToolOperation.HdrToSdr or VideoToolOperation.ResizeFrameRate or
            VideoToolOperation.Merge or VideoToolOperation.ReplaceAudio ||
            (settings.Tool == VideoToolOperation.Trim && settings.FrameAccurateTrim);
        if (needsVideoEncoder)
        {
            var container = settings.ContainerFormatId;
            var requestedCodec = string.IsNullOrWhiteSpace(settings.VideoCodecId) || settings.VideoCodecId == FormatIds.Copy
                ? DefaultVideoCodec(container)
                : settings.VideoCodecId;
            var codec = capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                            string.Equals(c.Id, requestedCodec, StringComparison.OrdinalIgnoreCase))
                        ?? capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                            string.Equals(c.Id, DefaultVideoCodec(container), StringComparison.OrdinalIgnoreCase));
            if (codec is null || !codec.Encoders.Any(e => e.IsAvailable))
                issues.Add(ValidationIssue.Error("video.tools.encoder", "No usable video encoder is available for this container."));
            else if (!CompatibilityMatrix.IsVideoCodecCompatible(container, codec.Id))
                issues.Add(ValidationIssue.Warning("video.tools.codec",
                    $"{codec.DisplayName} is not compatible with the selected container; PixelForge will use the container default."));
        }

        return new ValidationResult { Issues = issues };
    }

    private async Task<ProcessingResult> ProcessVideoToolAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        return settings.Tool switch
        {
            VideoToolOperation.Compress => await ProcessVideoCompressAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Trim => await ProcessVideoTrimAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Merge => await ProcessVideoMergeAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Stabilize => await ProcessVideoStabilizeAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Thumbnail => await ProcessThumbnailAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Gif => await ProcessGifAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.ExtractAudio => await ProcessVideoExtractAudioAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.ReplaceAudio => await ProcessVideoReplaceAudioAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.Subtitles => await ProcessSubtitleAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.EditMetadata => await ProcessVideoMetadataAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.HdrToSdr => await ProcessHdrAsync(request, settings, progress, cancellationToken),
            VideoToolOperation.ResizeFrameRate => await ProcessResizeFrameRateAsync(request, settings, progress, cancellationToken),
            _ => ProcessingResult.Fail("The selected video tool is not available.")
        };
    }

    private async Task<ProcessingResult> ProcessVideoCompressAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var attempts = GetEncoderAttempts(plan, settings.PreferHardware, capabilities);
        var duration = await GetDurationAsync(request, cancellationToken);
        var warnings = new List<string>(planWarnings);
        var container = settings.ContainerFormatId;
        var audioPlan = ResolveVideoAudioPlan(settings, container, capabilities, request.InputInfo);
        var targetBitrate = 0;
        if (settings.TargetSizeMb > 0 && duration > 0)
        {
            targetBitrate = ComputeTargetVideoBitrate(settings.TargetSizeMb, duration, audioPlan);
            if (targetBitrate < 100)
            {
                targetBitrate = 100;
                warnings.Add("The requested target is very small; the video bitrate was raised to the minimum practical value.");
            }
        }
        else if (settings.TargetSizeMb > 0)
        {
            warnings.Add("The source duration is unknown, so the target size could not be converted into a bitrate.");
        }

        var useBitrate = targetBitrate > 0 || plan.UseBitrate;
        var bitrate = targetBitrate > 0 ? targetBitrate : plan.BitrateKbps;
        var filter = BuildVideoFilter(settings, request.InputInfo, out var filterWarnings);
        warnings.AddRange(filterWarnings);

        foreach (var encoder in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var passLog = Path.Combine(_paths.TempDirectory, "ffmpeg2pass-" + Guid.NewGuid().ToString("N"));
            var twoPass = targetBitrate > 0 && settings.AllowTwoPass && SupportsTwoPass(encoder);
            try
            {
                if (twoPass)
                {
                    var first = BuildCompressArgs(request, settings, plan with { EncoderId = encoder },
                        filter, audioPlan, useBitrate, bitrate, pass: 1, passLog, isFirstPass: true, outputPath: "-");
                    progress.Report(ProcessingProgress.Indeterminate("Two-pass compression · analysis pass…"));
                    var firstResult = await RunVideoCommandAsync(first, 0, progress, null,
                        TimeSpan.FromHours(24), cancellationToken);
                    if (firstResult.WasCancelled) throw new OperationCanceledException(cancellationToken);
                    if (!firstResult.Succeeded)
                    {
                        if (IsHardwareEncoder(encoder, capabilities))
                        {
                            warnings.Add($"{encoder} could not complete the analysis pass; falling back to a software encoder.");
                            continue;
                        }

                        return ProcessingResult.Fail(FriendlyError(firstResult.StandardError),
                            firstResult.StandardError);
                    }
                }

                var args = BuildCompressArgs(request, settings, plan with { EncoderId = encoder },
                    filter, audioPlan, useBitrate, bitrate, twoPass ? 2 : 0, passLog, isFirstPass: false,
                    outputPath: request.TempOutputPath);
                var started = Stopwatch.StartNew();
                var result = await RunVideoCommandAsync(args, duration, progress, null,
                    TimeSpan.FromHours(24), cancellationToken);
                started.Stop();
                if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
                if (result.TimedOut)
                {
                    if (IsHardwareEncoder(encoder, capabilities)) { warnings.Add($"{encoder} timed out; retrying on the CPU."); continue; }
                    return ProcessingResult.Fail("FFmpeg timed out while compressing the video.", result.StandardError, started.Elapsed);
                }

                if (!result.Succeeded)
                {
                    if (IsHardwareEncoder(encoder, capabilities))
                    {
                        warnings.Add($"{encoder} failed: {FriendlyError(result.StandardError)} Retrying on the CPU.");
                        continue;
                    }

                    return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError, started.Elapsed);
                }

                if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
                    return ProcessingResult.Fail("FFmpeg completed but produced no video output.");

                var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath,
                    cancellationToken);
                if (!verification.IsValid)
                    return ProcessingResult.Fail(verification.Summary, verification.Details, started.Elapsed);
                var actualBytes = new FileInfo(request.TempOutputPath).Length;
                if (settings.TargetSizeMb > 0)
                {
                    var targetBytes = (long)(settings.TargetSizeMb * 1024 * 1024);
                    var over = actualBytes - targetBytes;
                    if (over > Math.Max(1024 * 1024, targetBytes * 0.08))
                    {
                        warnings.Add($"The output is {FormatBytes(actualBytes)}, about {over * 100d / targetBytes:0.0}% over the requested {settings.TargetSizeMb:0.##} MB target. Exact size guarantees are not always possible.");
                    }
                    else if (actualBytes < targetBytes * 0.65)
                    {
                        warnings.Add($"The output is {FormatBytes(actualBytes)}, well below the requested target. The source may not contain enough detail to use the budget.");
                    }
                }

                var summary = verification with
                {
                    Summary = $"{verification.Summary}{(targetBitrate > 0 ? $" · target {settings.TargetSizeMb:0.##} MB" : "")}",
                    Issues = verification.Issues.Concat(warnings.Select(w => ValidationIssue.Info("video.compress.info", w))).ToList()
                };
                progress.Report(ProcessingProgress.Percent(1, "Video ready."));
                return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo, actualBytes,
                    started.Elapsed, summary, warnings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsHardwareEncoder(encoder, capabilities))
                {
                    warnings.Add($"{encoder} failed: {ex.Message} Retrying on the CPU.");
                    continue;
                }

                return ProcessingResult.Fail(FriendlyError(ex.Message), ex.ToString());
            }
            finally
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(_paths.TempDirectory,
                                 Path.GetFileName(passLog) + "*"))
                        File.Delete(file);
                }
                catch { }
            }
        }

        return ProcessingResult.Fail("Video compression failed on every available encoder.", string.Join(Environment.NewLine, warnings));
    }

    private List<string> BuildCompressArgs(ProcessingRequest request, VideoToolSettings settings, VideoEncodePlan plan,
        string? filter, VideoAudioPlan audioPlan, bool useBitrate, int bitrate, int pass, string passLog,
        bool isFirstPass, string outputPath)
    {
        var args = BaseVideoInput(request.InputPath);
        if (!string.IsNullOrWhiteSpace(filter)) args.AddRange(["-vf", filter]);
        AddVideoEncoderArgs(args, plan, useBitrate, bitrate, pass, passLog);
        if (isFirstPass)
        {
            args.AddRange(["-an", "-sn", "-dn", "-f", "null", outputPath]);
            return args;
        }

        AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
        AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
        AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, outputPath);
        return args;
    }

    private async Task<ProcessingResult> ProcessVideoTrimAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var duration = await GetDurationAsync(request, cancellationToken);
        var end = settings.TrimEndSeconds > 0 ? settings.TrimEndSeconds : duration;
        if (end <= settings.TrimStartSeconds)
            return ProcessingResult.Fail("Trim end must be after trim start.");
        var length = end - settings.TrimStartSeconds;
        var warnings = new List<string>();

        if (!settings.FrameAccurateTrim)
        {
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "warning",
                "-ss", F(settings.TrimStartSeconds), "-i", request.InputPath,
                "-t", F(length), "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero",
                "-map_metadata", settings.RemoveAllMetadata ? "-1" : "0",
                "-max_muxing_queue_size", "4096",
                "-f", FormatCatalog.FindById(settings.ContainerFormatId)?.MuxerName ?? settings.ContainerFormatId,
                request.TempOutputPath
            };
            var result = await RunVideoCommandAsync(args, length, progress, null, TimeSpan.FromHours(12), cancellationToken);
            if (result.Succeeded && File.Exists(request.TempOutputPath) && new FileInfo(request.TempOutputPath).Length > 0)
            {
                warnings.Add("Stream-copy trimming cuts at the nearest keyframe; enable frame-accurate trim to re-encode.");
                var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
                if (verification.IsValid)
                    return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
                        new FileInfo(request.TempOutputPath).Length, result.Duration, verification, warnings);
            }

            warnings.Add("Stream-copy trimming was not possible; the selected range was re-encoded.");
        }

        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        warnings.AddRange(planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        return await ExecuteWithVideoEncoderAsync(request, settings, progress, length, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                args.AddRange(["-ss", F(settings.TrimStartSeconds), "-t", F(length)]);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessVideoMergeAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var paths = MergePaths(request.InputPath, settings.MergeInputPaths);
        if (paths.Count < 2) return ProcessingResult.Fail("Add at least two videos to merge.");
        var infos = new List<MediaInfo>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) return ProcessingResult.Fail($"The merge input does not exist: {Path.GetFileName(path)}");
            var info = await _probe.InspectAsync(path, cancellationToken);
            if (!info.IsReadable || info.Category != MediaCategory.Video)
                return ProcessingResult.Fail($"{Path.GetFileName(path)} is not a readable video.");
            infos.Add(info);
        }

        var totalDuration = infos.Sum(i => i.DurationSeconds ?? 0);
        var warnings = new List<string>();
        var outputFormatId = settings.ContainerFormatId;

        if (CanStreamCopyMerge(infos, outputFormatId))
        {
            var listPath = Path.Combine(_paths.TempDirectory, "merge-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                await File.WriteAllLinesAsync(listPath, paths.Select(p => "file '" + p.Replace("'", "'\\''") + "'"),
                    new UTF8Encoding(false), cancellationToken);
                var args = new List<string>
                {
                    "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "warning",
                    "-f", "concat", "-safe", "0", "-i", listPath,
                    "-map", "0", "-c", "copy", "-map_metadata", settings.RemoveAllMetadata ? "-1" : "0",
                    "-max_muxing_queue_size", "4096"
                };
                if (outputFormatId is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
                args.AddRange(["-f", FormatCatalog.FindById(outputFormatId)?.MuxerName ?? outputFormatId,
                    request.TempOutputPath]);
                var result = await RunVideoCommandAsync(args, totalDuration, progress, null,
                    TimeSpan.FromHours(12), cancellationToken);
                if (result.Succeeded && File.Exists(request.TempOutputPath) && new FileInfo(request.TempOutputPath).Length > 0)
                {
                    warnings.Add("The videos were stream-copied into a single file without re-encoding.");
                    var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
                    if (verification.IsValid)
                        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
                            new FileInfo(request.TempOutputPath).Length, result.Duration, verification, warnings);
                }

                warnings.Add("Stream-copy merge was not possible; the videos were re-encoded to a common format.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stream-copy merge failed; falling back to re-encoding");
            }
            finally
            {
                try { if (File.Exists(listPath)) File.Delete(listPath); } catch { }
            }
        }

        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        warnings.AddRange(planWarnings);
        var first = infos[0];
        var width = RoundEven(settings.ResizeWidth ?? first.Width ?? 1920);
        var height = RoundEven(settings.ResizeHeight ?? first.Height ?? 1080);
        var fps = settings.FrameRate > 0 ? settings.FrameRate : first.FrameRate is > 0 ? first.FrameRate!.Value : 30;
        var anyAudio = infos.Any(i => i.AudioCodec is not null);
        var audioPlan = anyAudio
            ? new VideoAudioPlan(false, false, ResolveVideoAudioEncoder(outputFormatId, capabilities), settings.AudioBitrateKbps)
            : VideoAudioPlan.NoAudio;
        warnings.Add("Re-encoded merging scales each input to a common resolution and frame rate; subtitle streams are not preserved.");
        if (!anyAudio) warnings.Add("None of the inputs contained audio, so the merged video has no audio stream.");

        return await ExecuteWithVideoEncoderAsync(request, settings, progress, totalDuration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(paths[0]);
                for (var i = 1; i < paths.Count; i++) args.AddRange(["-i", paths[i]]);
                var filters = new List<string>();
                for (var i = 0; i < infos.Count; i++)
                {
                    filters.Add($"[{i}:v]scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos," +
                                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={F(fps)}[v{i}]");
                    if (anyAudio)
                    {
                        var duration = infos[i].DurationSeconds ?? 0;
                        if (infos[i].AudioCodec is not null)
                            filters.Add($"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]");
                        else
                            filters.Add($"anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration={F(Math.Max(0.1, duration))},asetpts=PTS-STARTPTS[a{i}]");
                    }
                }

                var streams = string.Concat(Enumerable.Range(0, infos.Count)
                    .Select(i => $"[v{i}]" + (anyAudio ? $"[a{i}]" : "")));
                filters.Add(anyAudio
                    ? $"{streams}concat=n={infos.Count}:v=1:a=1[vout][aout]"
                    : $"{streams}concat=n={infos.Count}:v=1:a=0[vout]");
                args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[vout]"]);
                if (anyAudio) args.AddRange(["-map", "[aout]"]);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                if (anyAudio)
                {
                    args.AddRange(["-c:a", audioPlan.EncoderId, "-b:a",
                        Math.Clamp(audioPlan.BitrateKbps, 8, 512).ToString(CultureInfo.InvariantCulture) + "k"]);
                }
                else args.Add("-an");
                AppendVideoMetadataAndOutput(args, settings, outputFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessVideoStabilizeAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        var duration = await GetDurationAsync(request, cancellationToken);
        var search = Math.Clamp((int)Math.Round((8 + settings.StabilizationStrength * 56) / 16.0) * 16, 16, 64);
        var filter = $"deshake=rx={search}:ry={search}:edge=mirror";
        var warnings = new List<string>(planWarnings)
        {
            "Stabilisation analyses every frame and re-encodes the video; long clips can take several times the source duration."
        };
        return await ExecuteWithVideoEncoderAsync(request, settings, progress, duration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                args.AddRange(["-vf", filter]);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessResizeFrameRateAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        var duration = await GetDurationAsync(request, cancellationToken);
        var filter = BuildVideoFilter(settings, request.InputInfo, out var filterWarnings);
        var warnings = new List<string>(planWarnings);
        warnings.AddRange(filterWarnings);
        if (request.InputInfo is { Width: > 0, Height: > 0 } info &&
            settings.ResizeWidth is > 0 && settings.ResizeWidth > info.Width)
            warnings.Add("The requested resolution is larger than the source. This uses Lanczos scaling, not AI upscaling.");
        return await ExecuteWithVideoEncoderAsync(request, settings, progress, duration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                if (!string.IsNullOrWhiteSpace(filter)) args.AddRange(["-vf", filter]);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessHdrAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        var duration = await GetDurationAsync(request, cancellationToken);
        var color = await GetColorInfoAsync(request.InputPath, cancellationToken);
        var warnings = new List<string>(planWarnings);
        var knownHdr = color is not null && IsHdrTransfer(color.Value.Transfer);
        var highBitDepth = request.InputInfo?.ColorSpace is { } pixelFormat &&
                           (pixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) ||
                            pixelFormat.Contains("12", StringComparison.OrdinalIgnoreCase) ||
                            pixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase) ||
                            pixelFormat.Contains("p016", StringComparison.OrdinalIgnoreCase));
        if (!knownHdr && !highBitDepth)
            return ProcessingResult.Fail(
                "The input does not carry HDR transfer metadata (PQ or HLG). HDR-to-SDR conversion is not applicable to this file.");
        if (!knownHdr)
            warnings.Add("HDR transfer metadata was not detected; a BT.2020 input was assumed because the source is high bit depth.");
        else
            warnings.Add($"HDR input detected ({color!.Value.Primaries} / {color.Value.Transfer}); the output is SDR BT.709.");
        var peak = Math.Clamp(settings.ToneMapPeakNits, 50, 1000);
        var use2020 = color?.Primaries?.Contains("2020", StringComparison.OrdinalIgnoreCase) == true || knownHdr || highBitDepth;
        var inputAll = use2020 ? "bt2020" : "bt709";
        var filter = $"colorspace=iall={inputAll}:all=bt709:trc=linear,format=gbrpf32le," +
                     $"tonemap=tonemap=hable:desat=0:peak={F(peak)}," +
                     "colorspace=iall=bt709:all=bt709,format=yuv420p";
        return await ExecuteWithVideoEncoderAsync(request, settings, progress, duration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                args.AddRange(["-vf", filter]);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessThumbnailAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var duration = await GetDurationAsync(request, cancellationToken);
        var time = Math.Clamp(settings.ThumbnailTimeSeconds, 0, duration > 0 ? Math.Max(0, duration - 0.1) : 0);
        var formatId = settings.ThumbnailFormatId;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "warning",
            "-ss", F(time), "-i", request.InputPath,
            "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", "thumbnail=30"
        };
        if (formatId == "jpg") args.AddRange(["-q:v", "2"]);
        if (formatId == "png") args.AddRange(["-compression_level", "6"]);
        args.AddRange(["-update", "1", "-f", "image2", request.TempOutputPath]);
        progress.Report(ProcessingProgress.Indeterminate("Extracting a representative frame…"));
        var result = await RunVideoCommandAsync(args, 0, progress, null, TimeSpan.FromMinutes(10), cancellationToken);
        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (!result.Succeeded)
            return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg produced no thumbnail.");
        var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        progress.Report(ProcessingProgress.Percent(1, "Thumbnail ready."));
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, result.Duration, verification,
            [$"Frame selected at {time:0.###}s using FFmpeg's thumbnail filter."]);
    }

    private async Task<ProcessingResult> ProcessGifAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var length = settings.GifEndSeconds - settings.GifStartSeconds;
        if (length <= 0) return ProcessingResult.Fail("GIF end must be after the start.");
        var width = Math.Clamp(settings.GifWidth, 16, 1920);
        width -= width % 2;
        var filter = $"fps={F(Math.Clamp(settings.GifFps, 1, 30))},scale={width}:-2:flags=lanczos," +
                     "split[gifA][gifB];[gifA]palettegen=stats_mode=diff[palette];" +
                     "[gifB][palette]paletteuse=dither=bayer:bayer_scale=5[gifout]";
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "warning",
            "-ss", F(settings.GifStartSeconds), "-t", F(length), "-i", request.InputPath,
            "-filter_complex", filter, "-map", "[gifout]", "-an", "-sn", "-dn",
            "-c:v", "gif", "-loop", "0",
            "-f", "gif", request.TempOutputPath
        };
        var result = await RunVideoCommandAsync(args, length, progress, null, TimeSpan.FromHours(6), cancellationToken);
        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (!result.Succeeded) return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg produced no GIF output.");
        var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, result.Duration, verification,
            ["GIF is less efficient than modern video formats; the output can be substantially larger."]);
    }

    private async Task<ProcessingResult> ProcessVideoExtractAudioAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var inputCodec = request.InputInfo?.Streams
            .Where(s => s.Type == "audio")
            .OrderBy(s => s.Index)
            .Skip(Math.Max(0, settings.AudioStreamIndex))
            .FirstOrDefault()?.Codec;
        var copy = settings.CopyAudioStream && inputCodec is not null &&
                   CompatibilityMatrix.CanCopyAudio(settings.ExtractAudioFormatId, inputCodec);
        var args = BuildAudioInput(request.InputPath);
        args.AddRange(["-map", $"0:a:{Math.Max(0, settings.AudioStreamIndex)}?", "-vn", "-sn", "-dn"]);
        var encoder = ResolveAudioEncoder(settings.ExtractAudioFormatId);
        AppendAudioOutput(args, request, settings.ExtractAudioFormatId, encoder, settings.AudioBitrateKbps,
            0, 0, copy, true);
        var started = Stopwatch.StartNew();
        var result = await RunVideoCommandAsync(args, request.InputInfo?.DurationSeconds ?? 0, progress, null,
            TimeSpan.FromHours(6), cancellationToken);
        started.Stop();
        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (!result.Succeeded) return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError);
        if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
            return ProcessingResult.Fail("FFmpeg produced no audio output.");
        var verification = await VerifyVideoExtractAudioAsync(request, settings, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification,
            copy ? ["The original audio stream was copied without re-encoding."] : []);
    }

    private async Task<ProcessingResult> ProcessVideoReplaceAudioAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ReplacementAudioPath) || !File.Exists(settings.ReplacementAudioPath))
            return ProcessingResult.Fail("The replacement audio file does not exist.");
        var replacement = await _probe.InspectAsync(settings.ReplacementAudioPath, cancellationToken);
        if (!replacement.IsReadable || replacement.AudioCodec is null)
            return ProcessingResult.Fail("The replacement file has no readable audio stream.");

        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var duration = await GetDurationAsync(request, cancellationToken);
        var warnings = new List<string>();
        if (duration > 0 && replacement.DurationSeconds is > 0 &&
            Math.Abs(duration - replacement.DurationSeconds.Value) > Math.Max(1.5, duration * 0.03))
            warnings.Add($"The replacement audio is {replacement.DurationSeconds:0.0}s while the video is {duration:0.0}s; the output will follow the shorter or longer stream as determined by FFmpeg.");
        if (Math.Abs(settings.AudioOffsetSeconds) > 0.001)
            warnings.Add($"Audio offset {settings.AudioOffsetSeconds:0.###}s applied. Check synchronisation before sharing.");

        var videoCopy = request.InputInfo?.VideoCodec is not null &&
                        CompatibilityMatrix.CanCopyVideo(settings.ContainerFormatId, request.InputInfo.VideoCodec);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, replacement);
        if (audioPlan.None)
            audioPlan = new VideoAudioPlan(false, false, ResolveVideoAudioEncoder(settings.ContainerFormatId, capabilities),
                settings.AudioBitrateKbps);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        warnings.AddRange(planWarnings);
        if (!videoCopy) warnings.Add("The source video codec is not compatible with the selected container, so the video was re-encoded.");

        var arguments = BaseVideoInput(request.InputPath);
        if (Math.Abs(settings.AudioOffsetSeconds) > 0.001)
            arguments.AddRange(["-itsoffset", F(settings.AudioOffsetSeconds)]);
        arguments.AddRange(["-i", settings.ReplacementAudioPath, "-map", "0:v:0", "-map", "1:a:0?"]);
        if (videoCopy) arguments.AddRange(["-c:v", "copy"]);
        else AddVideoEncoderArgs(arguments, plan, plan.UseBitrate, plan.BitrateKbps, 0, null);
        if (audioPlan.Copy) arguments.AddRange(["-c:a", "copy"]);
        else arguments.AddRange(["-c:a", audioPlan.EncoderId, "-b:a",
            Math.Clamp(audioPlan.BitrateKbps, 8, 512).ToString(CultureInfo.InvariantCulture) + "k"]);
        var subtitleCodec = ResolveSubtitleCodec(settings.ContainerFormatId);
        if (subtitleCodec is null) arguments.Add("-sn");
        else arguments.AddRange(["-map", "0:s?", "-c:s", subtitleCodec]);
        AppendVideoMetadataAndOutput(arguments, settings, settings.ContainerFormatId, request.TempOutputPath);

        var started = Stopwatch.StartNew();
        var result = await RunVideoCommandAsync(arguments, duration, progress, null, TimeSpan.FromHours(12), cancellationToken);
        started.Stop();
        if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
        if (!result.Succeeded) return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError);
        var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
        if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
        return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
            new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification, warnings);
    }

    private async Task<ProcessingResult> ProcessSubtitleAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var info = request.InputInfo;
        var subtitle = GetSubtitleStream(info, settings.SubtitleStreamIndex);
        if (settings.SubtitleMode is SubtitleToolMode.Extract or SubtitleToolMode.Convert)
        {
            if (subtitle is null)
                return ProcessingResult.Fail("The selected subtitle stream was not found.");
            if (IsBitmapSubtitle(subtitle.Codec))
                return ProcessingResult.Fail("Image-based subtitles cannot be converted to text without OCR.");
            var format = settings.SubtitleFormatId is "vtt" ? "webvtt" : settings.SubtitleFormatId;
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "warning",
                "-i", request.InputPath,
                "-map", $"0:s:{Math.Max(0, settings.SubtitleStreamIndex)}",
                "-c:s", format, "-f", format, request.TempOutputPath
            };
            var result = await RunVideoCommandAsync(args, 0, progress, null, TimeSpan.FromHours(2), cancellationToken);
            if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
            if (!result.Succeeded) return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError);
            var verification = await VerifySubtitleOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
            if (!verification.IsValid) return ProcessingResult.Fail(verification.Summary, verification.Details);
            return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
                new FileInfo(request.TempOutputPath).Length, result.Duration, verification,
                [$"{subtitle.Codec} subtitle track {settings.SubtitleStreamIndex} converted to {settings.SubtitleFormatId.ToUpperInvariant()}."]);
        }

        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        var duration = await GetDurationAsync(request, cancellationToken);
        var warnings = new List<string>(planWarnings);
        if (settings.SubtitleMode == SubtitleToolMode.Drop)
            warnings.Add("Subtitle streams were removed as requested.");
        else if ((info?.SubtitleStreamCount ?? 0) == 0)
            warnings.Add("The source has no subtitle streams; the video was remuxed without subtitles.");
        return await ExecuteWithVideoEncoderAsync(request, settings, progress, duration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessVideoMetadataAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken: cancellationToken);
        var plan = ResolveVideoPlan(settings, capabilities, out var planWarnings);
        var audioPlan = ResolveVideoAudioPlan(settings, settings.ContainerFormatId, capabilities, request.InputInfo);
        var duration = await GetDurationAsync(request, cancellationToken);
        var sameContainer = string.Equals(request.InputInfo?.FormatId, settings.ContainerFormatId,
            StringComparison.OrdinalIgnoreCase);
        var videoCopy = sameContainer && request.InputInfo?.VideoCodec is not null &&
                        CompatibilityMatrix.CanCopyVideo(settings.ContainerFormatId, request.InputInfo.VideoCodec);
        var warnings = new List<string>(planWarnings);
        if (!sameContainer)
            warnings.Add("Changing the container requires re-encoding the video and audio.");

        if (videoCopy)
        {
            var args = BaseVideoInput(request.InputPath);
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-map", "0:s?", "-c:v", "copy"]);
            if (audioPlan.None) args.Add("-an");
            else if (audioPlan.Copy) args.AddRange(["-c:a", "copy"]);
            else args.AddRange(["-c:a", audioPlan.EncoderId, "-b:a",
                Math.Clamp(audioPlan.BitrateKbps, 8, 512).ToString(CultureInfo.InvariantCulture) + "k"]);
            var subtitleCodec = ResolveSubtitleCodec(settings.ContainerFormatId);
            if (subtitleCodec is null) args.Add("-sn");
            else args.AddRange(["-c:s", subtitleCodec]);
            AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
            var result = await RunVideoCommandAsync(args, duration, progress, null, TimeSpan.FromHours(12), cancellationToken);
            if (result.Succeeded && File.Exists(request.TempOutputPath) && new FileInfo(request.TempOutputPath).Length > 0)
            {
                var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
                if (verification.IsValid)
                    return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
                        new FileInfo(request.TempOutputPath).Length, result.Duration, verification, warnings);
            }

            warnings.Add("Metadata-only remux was not possible; the video was re-encoded.");
        }

        return await ExecuteWithVideoEncoderAsync(request, settings, progress, duration, capabilities, plan,
            encoder =>
            {
                var args = BaseVideoInput(request.InputPath);
                AppendVideoAudio(args, settings, audioPlan, "0:a:0?");
                AppendVideoSubtitles(args, settings, settings.ContainerFormatId, request.InputInfo);
                AddVideoEncoderArgs(args, plan with { EncoderId = encoder }, plan.UseBitrate, plan.BitrateKbps, 0, null);
                AppendVideoMetadataAndOutput(args, settings, settings.ContainerFormatId, request.TempOutputPath);
                return args;
            }, warnings, cancellationToken);
    }

    private async Task<ProcessingResult> ExecuteWithVideoEncoderAsync(ProcessingRequest request, VideoToolSettings settings,
        IProgress<ProcessingProgress> progress, double duration, FFmpegCapabilities capabilities, VideoEncodePlan plan,
        Func<string, List<string>> buildArgs, List<string> warnings, CancellationToken cancellationToken)
    {
        var attempts = GetEncoderAttempts(plan, settings.PreferHardware, capabilities);
        foreach (var encoder in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var args = buildArgs(encoder);
                var started = Stopwatch.StartNew();
                var result = await RunVideoCommandAsync(args, duration, progress, null,
                    TimeSpan.FromHours(24), cancellationToken);
                started.Stop();
                if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
                if (result.TimedOut && IsHardwareEncoder(encoder, capabilities))
                {
                    warnings.Add($"{encoder} timed out; retrying on the CPU.");
                    continue;
                }

                if (!result.Succeeded)
                {
                    if (IsHardwareEncoder(encoder, capabilities))
                    {
                        warnings.Add($"{encoder} failed: {FriendlyError(result.StandardError)} Retrying on the CPU.");
                        continue;
                    }

                    return ProcessingResult.Fail(FriendlyError(result.StandardError), result.StandardError, started.Elapsed);
                }

                if (!File.Exists(request.TempOutputPath) || new FileInfo(request.TempOutputPath).Length == 0)
                    return ProcessingResult.Fail("FFmpeg completed but produced no output.");
                var verification = await VerifyVideoOutputAsync(request, settings, request.TempOutputPath, cancellationToken);
                if (!verification.IsValid)
                    return ProcessingResult.Fail(verification.Summary, verification.Details, started.Elapsed);
                progress.Report(ProcessingProgress.Percent(1, "Video ready."));
                return ProcessingResult.Ok(request.TempOutputPath, verification.ActualInfo,
                    new FileInfo(request.TempOutputPath).Length, started.Elapsed, verification, warnings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsHardwareEncoder(encoder, capabilities))
                {
                    warnings.Add($"{encoder} failed: {ex.Message} Retrying on the CPU.");
                    continue;
                }

                return ProcessingResult.Fail(FriendlyError(ex.Message), ex.ToString());
            }
        }

        return ProcessingResult.Fail("Video processing failed on every available encoder.",
            string.Join(Environment.NewLine, warnings));
    }

    private async Task<VerificationResult> VerifyVideoOutputAsync(ProcessingRequest request, VideoToolSettings settings,
        string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 64)
                return VerificationResult.Fail("The output file is empty or missing.");
            if (settings.Tool is VideoToolOperation.Thumbnail)
            {
                var thumb = await _probe.InspectAsync(outputPath, cancellationToken);
                if (!thumb.IsReadable || thumb.Width is null || thumb.Height is null)
                    return VerificationResult.Fail($"The thumbnail could not be inspected: {thumb.Error}", thumb);
                var thumbInfo = thumb with { Category = MediaCategory.Image, EngineId = Id };
                return VerificationResult.Ok(thumbInfo,
                    $"{settings.ThumbnailFormatId.ToUpperInvariant()} · {thumb.Width} × {thumb.Height} · {FormatBytes(new FileInfo(outputPath).Length)}");
            }

            var actual = await _probe.InspectAsync(outputPath, cancellationToken);
            if (!actual.IsReadable)
                return VerificationResult.Fail($"The output could not be inspected: {actual.Error}", actual);
            var issues = new List<ValidationIssue>();
            var input = request.InputInfo;

            if (settings.Tool == VideoToolOperation.ExtractAudio)
            {
                if (actual.AudioCodec is null)
                    return VerificationResult.Fail("The extracted file contains no audio stream.", actual);
                if (input?.DurationSeconds is > 0 && actual.DurationSeconds is > 0 &&
                    Math.Abs(input.DurationSeconds.Value - actual.DurationSeconds.Value) > 2.0)
                    issues.Add(ValidationIssue.Warning("video.extract.duration",
                        $"The extracted audio is {actual.DurationSeconds:0.0}s while the source is {input.DurationSeconds:0.0}s."));
                var audioInfo = actual with { Category = MediaCategory.Audio, EngineId = Id };
                return new VerificationResult
                {
                    IsValid = true,
                    Summary = $"{actual.FormatName ?? settings.ExtractAudioFormatId} · {actual.AudioCodec} · {actual.DurationSeconds:0.0}s",
                    Issues = issues,
                    ActualInfo = audioInfo
                };
            }

            if (actual.VideoCodec is null)
                return VerificationResult.Fail("The output contains no video stream.", actual);

            var expectedContainer = settings.Tool == VideoToolOperation.Gif ? "gif" : settings.ContainerFormatId;
            if (settings.Tool is not (VideoToolOperation.Gif or VideoToolOperation.Thumbnail) &&
                !string.Equals(actual.FormatId, expectedContainer, StringComparison.OrdinalIgnoreCase))
                return VerificationResult.Fail($"The output container is {actual.FormatId ?? actual.FormatName}, expected {expectedContainer}.", actual);

            var expectedVideoCodec = settings.Tool switch
            {
                VideoToolOperation.Gif => "gif",
                VideoToolOperation.Trim when !settings.FrameAccurateTrim => input?.VideoCodec,
                VideoToolOperation.ReplaceAudio when input?.VideoCodec is not null &&
                    CompatibilityMatrix.CanCopyVideo(settings.ContainerFormatId, input.VideoCodec) => input.VideoCodec,
                VideoToolOperation.EditMetadata when input?.VideoCodec is not null &&
                    string.Equals(input.FormatId, settings.ContainerFormatId, StringComparison.OrdinalIgnoreCase) => input.VideoCodec,
                _ => ResolveExpectedVideoCodec(settings, expectedContainer)
            };
            if (!string.IsNullOrWhiteSpace(expectedVideoCodec) &&
                !string.Equals(actual.VideoCodec, expectedVideoCodec, StringComparison.OrdinalIgnoreCase))
                return VerificationResult.Fail($"The output video codec is {actual.VideoCodec}, expected {expectedVideoCodec}.", actual);

            var expectedDuration = settings.Tool switch
            {
                VideoToolOperation.Trim => settings.TrimEndSeconds > 0
                    ? settings.TrimEndSeconds - settings.TrimStartSeconds
                    : (input?.DurationSeconds ?? 0) - settings.TrimStartSeconds,
                VideoToolOperation.Gif => settings.GifEndSeconds - settings.GifStartSeconds,
                VideoToolOperation.Merge => await SumDurationsAsync(request.InputPath, settings.MergeInputPaths, cancellationToken),
                _ => input?.DurationSeconds ?? 0
            };
            if (expectedDuration > 0 && actual.DurationSeconds is > 0)
            {
                var tolerance = settings.Tool switch
                {
                    VideoToolOperation.Trim when !settings.FrameAccurateTrim => Math.Max(3.0, expectedDuration * 0.08),
                    VideoToolOperation.Merge => Math.Max(3.0, expectedDuration * 0.05),
                    VideoToolOperation.Gif => Math.Max(1.0, expectedDuration * 0.08),
                    _ => Math.Max(2.0, expectedDuration * 0.04)
                };
                if (Math.Abs(actual.DurationSeconds.Value - expectedDuration) > tolerance)
                {
                    if (settings.Tool == VideoToolOperation.Trim && !settings.FrameAccurateTrim)
                        issues.Add(ValidationIssue.Warning("video.verify.trim",
                            $"The stream-copied output is {actual.DurationSeconds:0.0}s, expected about {expectedDuration:0.0}s; keyframe alignment can shift the cut."));
                    else
                        return VerificationResult.Fail(
                            $"The output duration is {actual.DurationSeconds:0.0}s, expected about {expectedDuration:0.0}s.", actual);
                }
            }

            if (settings.Tool == VideoToolOperation.ResizeFrameRate && input is { Width: > 0, Height: > 0 })
            {
                if (settings.ResizeWidth is > 0 || settings.ResizeHeight is > 0)
                {
                    if (actual.Width != input.Width || actual.Height != input.Height)
                    {
                        if (settings.ResizeWidth is > 0 && Math.Abs((actual.Width ?? 0) - settings.ResizeWidth.Value) > 2)
                            issues.Add(ValidationIssue.Warning("video.verify.resize",
                                $"The output is {actual.Width} × {actual.Height}, requested {settings.ResizeWidth} × {settings.ResizeHeight ?? input.Height}."));
                    }
                }
                if (settings.FrameRate > 0 && actual.FrameRate is > 0 &&
                    Math.Abs(actual.FrameRate.Value - settings.FrameRate) > 0.25)
                    issues.Add(ValidationIssue.Warning("video.verify.fps",
                        $"The output frame rate is {actual.FrameRate:0.###}, requested {settings.FrameRate:0.###}."));
            }

            if (settings.Tool == VideoToolOperation.Compress && settings.TargetSizeMb > 0)
            {
                var targetBytes = (long)(settings.TargetSizeMb * 1024 * 1024);
                var actualBytes = new FileInfo(outputPath).Length;
                if (actualBytes > targetBytes * 1.08 + 1024 * 1024)
                    issues.Add(ValidationIssue.Warning("video.verify.target",
                        $"The output is {FormatBytes(actualBytes)}, over the requested {settings.TargetSizeMb:0.##} MB target."));
            }

            if (settings.Tool == VideoToolOperation.ReplaceAudio)
            {
                if (actual.AudioCodec is null)
                    return VerificationResult.Fail("The output has no audio stream after replacement.", actual);
                if (input?.AudioCodec is not null && actual.AudioCodec == input.AudioCodec)
                    issues.Add(ValidationIssue.Info("video.audio.same",
                        "The output audio codec matches the original; check synchronisation and content."));
            }

            if (settings.Tool is VideoToolOperation.Subtitles or VideoToolOperation.EditMetadata &&
                input?.SubtitleStreamCount is > 0 && settings.SubtitleMode != SubtitleToolMode.Drop &&
                CompatibilityMatrix.ContainerSupportsSubtitles(settings.ContainerFormatId) &&
                (actual.SubtitleStreamCount ?? 0) < input.SubtitleStreamCount)
                issues.Add(ValidationIssue.Warning("video.verify.subtitles", "Some subtitle streams were not preserved."));

            var info = actual with { EngineId = Id };
            return new VerificationResult
            {
                IsValid = true,
                Summary = $"{actual.FormatName ?? expectedContainer} · {actual.VideoCodec}" +
                          $"{(actual.AudioCodec is null ? "" : " · " + actual.AudioCodec)} · " +
                          $"{actual.Width} × {actual.Height} · {actual.DurationSeconds:0.0}s · {FormatBytes(new FileInfo(outputPath).Length)}",
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
            return VerificationResult.Fail("The output video could not be verified.", details: ex.ToString());
        }
    }

    private async Task<VerificationResult> VerifyVideoExtractAudioAsync(ProcessingRequest request,
        VideoToolSettings settings, string outputPath, CancellationToken cancellationToken)
    {
        var actual = await _probe.InspectAsync(outputPath, cancellationToken);
        if (!actual.IsReadable || actual.AudioCodec is null)
            return VerificationResult.Fail($"The extracted audio could not be inspected: {actual.Error}", actual);
        var issues = new List<ValidationIssue>();
        if (request.InputInfo?.DurationSeconds is > 0 && actual.DurationSeconds is > 0 &&
            Math.Abs(request.InputInfo.DurationSeconds.Value - actual.DurationSeconds.Value) > 2.0)
            issues.Add(ValidationIssue.Warning("video.audio.duration",
                $"The extracted audio is {actual.DurationSeconds:0.0}s while the video is {request.InputInfo.DurationSeconds:0.0}s."));
        return new VerificationResult
        {
            IsValid = true,
            Summary = $"{actual.FormatName ?? settings.ExtractAudioFormatId} · {actual.AudioCodec} · {actual.SampleRate ?? 0} Hz · {actual.Channels ?? 0} ch",
            Issues = issues,
            ActualInfo = actual with { Category = MediaCategory.Audio, EngineId = Id }
        };
    }

    private async Task<VerificationResult> VerifySubtitleOutputAsync(ProcessingRequest request,
        VideoToolSettings settings, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(outputPath);
            if (!file.Exists || file.Length < 4)
                return VerificationResult.Fail("The subtitle output is empty.");
            var text = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var valid = settings.SubtitleFormatId switch
            {
                "srt" => text.Contains("-->", StringComparison.Ordinal),
                "vtt" => text.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase) &&
                         text.Contains("-->", StringComparison.Ordinal),
                "ass" => text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) &&
                         text.Contains("[Events]", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!valid) return VerificationResult.Fail("The subtitle output does not contain the expected structure.");
            var info = new MediaInfo
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                Extension = "." + settings.SubtitleFormatId,
                FileSizeBytes = file.Length,
                Category = MediaCategory.Document,
                FormatId = settings.SubtitleFormatId,
                FormatName = settings.SubtitleFormatId.ToUpperInvariant() + " subtitles",
                EngineId = Id
            };
            return VerificationResult.Ok(info,
                $"{settings.SubtitleFormatId.ToUpperInvariant()} subtitles · {FormatBytes(file.Length)}");
        }
        catch (Exception ex)
        {
            return VerificationResult.Fail("The subtitle output could not be verified.", details: ex.ToString());
        }
    }

    private async Task<ProcessResult> RunVideoCommandAsync(List<string> args, double duration,
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
            MaxCapturedLines = 800
        }, parser.ParseLine, null, cancellationToken);
    }

    private static List<string> BaseVideoInput(string path) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-progress", "pipe:1", "-nostats",
        "-loglevel", "warning",
        "-i", path
    ];

    private async Task<double> GetDurationAsync(ProcessingRequest request, CancellationToken cancellationToken)
    {
        if (request.InputInfo?.DurationSeconds is > 0) return request.InputInfo.DurationSeconds.Value;
        var info = await _probe.InspectAsync(request.InputPath, cancellationToken);
        return info.DurationSeconds ?? 0;
    }

    private VideoEncodePlan ResolveVideoPlan(VideoToolSettings settings, FFmpegCapabilities capabilities,
        out List<string> warnings)
    {
        warnings = new List<string>();
        var container = settings.Tool == VideoToolOperation.Gif ? "gif" : settings.ContainerFormatId;
        var requestedCodec = string.IsNullOrWhiteSpace(settings.VideoCodecId) || settings.VideoCodecId == FormatIds.Copy
            ? DefaultVideoCodec(container)
            : settings.VideoCodecId;
        var codec = capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, requestedCodec, StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, DefaultVideoCodec(container), StringComparison.OrdinalIgnoreCase));
        if (codec is null)
            throw new InvalidOperationException("No video codec is available for the selected container.");
        if (!CompatibilityMatrix.IsVideoCodecCompatible(container, codec.Id))
        {
            codec = capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, DefaultVideoCodec(container), StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Capability.VideoCodecs.FirstOrDefault(c =>
                        CompatibilityMatrix.IsVideoCodecCompatible(container, c.Id));
            if (codec is null)
                throw new InvalidOperationException($"No video codec is compatible with {container}.");
            warnings.Add($"{requestedCodec} is not compatible with the selected container; {codec.DisplayName} will be used.");
        }

        var available = codec.Encoders.Where(e => e.IsAvailable).ToList();
        if (available.Count == 0)
            throw new InvalidOperationException($"No usable encoder is available for {codec.DisplayName}.");
        var encoder = available.FirstOrDefault(e =>
                          !string.IsNullOrWhiteSpace(settings.VideoEncoderId) &&
                          string.Equals(e.Id, settings.VideoEncoderId, StringComparison.OrdinalIgnoreCase))
                      ?? (settings.PreferHardware
                          ? available.Where(e => e.IsHardware).OrderBy(e => e.Priority).FirstOrDefault()
                          : null)
                      ?? available.OrderBy(e => e.IsHardware).ThenBy(e => e.Priority).First();

        var definition = CodecCatalog.FindEncoder(encoder.Id);
        var min = definition?.QualityMin ?? 0;
        var max = definition?.QualityMax ?? 51;
        var span = Math.Max(1, max - min);
        var (quality, bitrate) = settings.CompressionPreset switch
        {
            VideoCompressionPreset.MaximumQuality => (min + span * 0.18, 12000),
            VideoCompressionPreset.SmallestFile => (min + span * 0.72, 2500),
            VideoCompressionPreset.WebOptimized => (min + span * 0.38, 5000),
            _ => (min + span * 0.45, 6000)
        };
        var useBitrate = definition is null ||
                         (!definition.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                          !definition.RateControl.HasFlag(RateControlCapabilities.Quantizer) &&
                          !definition.RateControl.HasFlag(RateControlCapabilities.None));
        if (container is "webm" && encoder.Id is "libvpx" or "libvpx-vp9")
            useBitrate = false;
        if (encoder.Id is "prores_ks" or "gif" or "libopenh264" or "h264_mf" or "hevc_mf")
            useBitrate = true;
        if (settings.CompressionPreset == VideoCompressionPreset.WebOptimized &&
            (container is not ("mp4" or "mov") || !string.Equals(codec.Id, "h264", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Web Optimized is most compatible with MP4, H.264 and AAC.");

        return new VideoEncodePlan(encoder.Id, codec.Id, (int)Math.Round(quality), bitrate, useBitrate,
            ResolvePreset(settings.CompressionPreset, encoder.Id));
    }

    private IEnumerable<string> GetEncoderAttempts(VideoEncodePlan plan, bool preferHardware,
        FFmpegCapabilities capabilities)
    {
        var encoders = capabilities.Capability.VideoCodecs
            .SelectMany(c => c.Encoders)
            .Where(e => e.IsAvailable)
            .ToList();
        var hardware = encoders.FirstOrDefault(e => string.Equals(e.Id, plan.EncoderId, StringComparison.OrdinalIgnoreCase))?.IsHardware == true;
        yield return plan.EncoderId;
        if (!hardware) yield break;
        var fallback = encoders.Where(e => !e.IsHardware &&
                                            string.Equals(e.CodecId, plan.CodecId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Priority).FirstOrDefault();
        if (fallback is not null) yield return fallback.Id;
    }

    private static bool IsHardwareEncoder(string encoderId, FFmpegCapabilities capabilities) =>
        capabilities.Capability.VideoCodecs.SelectMany(c => c.Encoders)
            .Any(e => e.IsHardware && string.Equals(e.Id, encoderId, StringComparison.OrdinalIgnoreCase));

    private static bool SupportsTwoPass(string encoderId) =>
        encoderId is "libvpx-vp9" or "libvpx" or "libaom-av1" or "libsvtav1";

    private static string DefaultVideoCodec(string container) => container switch
    {
        "webm" => "vp9",
        "avi" => "mpeg4",
        "mkv" => "h264",
        "gif" => "gif",
        _ => "h264"
    };

    private static string? ResolveExpectedVideoCodec(VideoToolSettings settings, string container)
    {
        var requested = string.IsNullOrWhiteSpace(settings.VideoCodecId) || settings.VideoCodecId == FormatIds.Copy
            ? DefaultVideoCodec(container)
            : settings.VideoCodecId;
        var codec = CodecCatalog.FindVideoCodec(requested);
        if (codec is not null && CompatibilityMatrix.IsVideoCodecCompatible(container, codec.Id))
            return codec.TechnicalId;
        return CodecCatalog.FindVideoCodec(DefaultVideoCodec(container))?.TechnicalId;
    }

    private static string? ResolvePreset(VideoCompressionPreset preset, string encoderId)
    {
        if (encoderId.Contains("_amf", StringComparison.Ordinal))
            return preset switch
            {
                VideoCompressionPreset.MaximumQuality => "quality",
                VideoCompressionPreset.SmallestFile or VideoCompressionPreset.WebOptimized => "speed",
                _ => "balanced"
            };
        if (encoderId.Contains("_nvenc", StringComparison.Ordinal))
            return preset switch
            {
                VideoCompressionPreset.MaximumQuality => "p6",
                VideoCompressionPreset.SmallestFile or VideoCompressionPreset.WebOptimized => "p2",
                _ => "p4"
            };
        if (encoderId.Contains("_qsv", StringComparison.Ordinal))
            return preset switch
            {
                VideoCompressionPreset.MaximumQuality => "slow",
                VideoCompressionPreset.SmallestFile or VideoCompressionPreset.WebOptimized => "fast",
                _ => "medium"
            };
        return encoderId switch
        {
            "libsvtav1" => preset switch
            {
                VideoCompressionPreset.MaximumQuality => "4",
                VideoCompressionPreset.SmallestFile => "12",
                VideoCompressionPreset.WebOptimized => "8",
                _ => "8"
            },
            "libvpx-vp9" => preset switch
            {
                VideoCompressionPreset.MaximumQuality => "0",
                VideoCompressionPreset.SmallestFile => "5",
                VideoCompressionPreset.WebOptimized => "3",
                _ => "2"
            },
            "libvpx" => preset switch
            {
                VideoCompressionPreset.MaximumQuality => "0",
                VideoCompressionPreset.SmallestFile => "8",
                VideoCompressionPreset.WebOptimized => "5",
                _ => "4"
            },
            "libaom-av1" => preset switch
            {
                VideoCompressionPreset.MaximumQuality => "2",
                VideoCompressionPreset.SmallestFile => "8",
                VideoCompressionPreset.WebOptimized => "5",
                _ => "4"
            },
            "prores_ks" => preset switch
            {
                VideoCompressionPreset.MaximumQuality => "hq",
                VideoCompressionPreset.SmallestFile => "lt",
                _ => "standard"
            },
            _ => null
        };
    }

    private void AddVideoEncoderArgs(List<string> args, VideoEncodePlan plan, bool useBitrate, int bitrate,
        int pass, string? passLog)
    {
        var encoderId = plan.EncoderId;
        args.AddRange(["-c:v", encoderId]);
        if (encoderId == "prores_ks")
        {
            args.AddRange(["-profile:v", plan.Preset ?? "standard", "-pix_fmt", "yuv422p10le"]);
            return;
        }

        args.AddRange(["-pix_fmt", "yuv420p"]);
        var q = plan.Quality.ToString(CultureInfo.InvariantCulture);
        var bitrateText = Math.Clamp(bitrate, 100, 100000).ToString(CultureInfo.InvariantCulture) + "k";
        if (encoderId.Contains("_amf", StringComparison.Ordinal))
        {
            args.AddRange(["-quality", plan.Preset ?? "balanced"]);
            if (useBitrate) args.AddRange(["-rc", "cbr", "-b:v", bitrateText]);
            else
            {
                args.AddRange(["-rc", "cqp", "-qp_i", q, "-qp_p", q]);
                if (encoderId == "h264_amf") args.AddRange(["-qp_b", q]);
            }
        }
        else if (encoderId.Contains("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", plan.Preset ?? "p4"]);
            if (useBitrate) args.AddRange(["-rc", "vbr", "-b:v", bitrateText]);
            else args.AddRange(["-rc", "constqp", "-qp", q]);
        }
        else if (encoderId.Contains("_qsv", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", plan.Preset ?? "medium"]);
            if (useBitrate) args.AddRange(["-b:v", bitrateText]);
            else args.AddRange(["-global_quality", q]);
        }
        else if (encoderId is "h264_mf" or "hevc_mf")
        {
            args.AddRange(["-b:v", bitrateText]);
        }
        else if (encoderId == "libopenh264")
        {
            args.AddRange(["-b:v", bitrateText, "-rc_mode", "bitrate", "-profile:v", "high"]);
        }
        else if (encoderId == "libsvtav1")
        {
            args.AddRange(["-preset", plan.Preset ?? "8"]);
            if (useBitrate) args.AddRange(["-b:v", bitrateText]);
            else args.AddRange(["-crf", q]);
        }
        else if (encoderId is "libvpx-vp9" or "libvpx" or "libaom-av1")
        {
            args.AddRange(["-cpu-used", plan.Preset ?? "4"]);
            if (useBitrate)
            {
                args.AddRange(["-b:v", bitrateText]);
                if (encoderId == "libvpx-vp9") args.AddRange(["-row-mt", "1"]);
            }
            else
            {
                args.AddRange(["-crf", q, "-b:v", "0"]);
                if (encoderId == "libvpx-vp9") args.AddRange(["-row-mt", "1"]);
            }
        }
        else if (encoderId == "mpeg4")
        {
            if (useBitrate) args.AddRange(["-b:v", bitrateText]);
            else args.AddRange(["-q:v", q]);
        }

        if (pass > 0 && !string.IsNullOrWhiteSpace(passLog))
            args.AddRange(["-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", passLog]);
    }

    private static void AppendVideoAudio(List<string> args, VideoToolSettings settings, VideoAudioPlan audio,
        string audioMap)
    {
        args.AddRange(["-map", "0:v:0"]);
        if (audio.None)
        {
            args.Add("-an");
            return;
        }

        args.AddRange(["-map", audioMap]);
        if (audio.Copy) args.AddRange(["-c:a", "copy"]);
        else
        {
            args.AddRange(["-c:a", audio.EncoderId, "-b:a",
                Math.Clamp(audio.BitrateKbps, 8, 512).ToString(CultureInfo.InvariantCulture) + "k"]);
        }
    }

    private static void AppendVideoSubtitles(List<string> args, VideoToolSettings settings, string containerId,
        MediaInfo? input)
    {
        if (settings.SubtitleMode == SubtitleToolMode.Drop ||
            !CompatibilityMatrix.ContainerSupportsSubtitles(containerId))
        {
            args.Add("-sn");
            return;
        }

        args.AddRange(["-map", "0:s?"]);
        var codec = ResolveSubtitleCodec(containerId) ?? "copy";
        if (settings.SubtitleMode == SubtitleToolMode.Embed && containerId is "mkv")
            codec = "copy";
        args.AddRange(["-c:s", codec]);
    }

    private static string? ResolveSubtitleCodec(string containerId) => containerId switch
    {
        "mp4" or "mov" => "mov_text",
        "webm" => "webvtt",
        "mkv" => "srt",
        _ => null
    };

    private static void AppendVideoMetadataAndOutput(List<string> args, VideoToolSettings settings, string formatId,
        string outputPath)
    {
        args.AddRange(["-map_metadata", settings.RemoveAllMetadata ? "-1" : "0"]);
        if (!settings.RemoveAllMetadata)
        {
            if (!string.IsNullOrWhiteSpace(settings.MetaTitle)) args.AddRange(["-metadata", "title=" + settings.MetaTitle.Trim()]);
            if (!string.IsNullOrWhiteSpace(settings.MetaArtist)) args.AddRange(["-metadata", "artist=" + settings.MetaArtist.Trim()]);
            if (!string.IsNullOrWhiteSpace(settings.MetaComment)) args.AddRange(["-metadata", "comment=" + settings.MetaComment.Trim()]);
            if (!string.IsNullOrWhiteSpace(settings.MetaYear)) args.AddRange(["-metadata", "date=" + settings.MetaYear.Trim()]);
        }

        if (formatId is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-max_muxing_queue_size", "4096"]);
        args.AddRange(["-f", FormatCatalog.FindById(formatId)?.MuxerName ?? formatId, outputPath]);
    }

    private static string? BuildVideoFilter(VideoToolSettings settings, MediaInfo? input, out List<string> warnings)
    {
        warnings = new List<string>();
        var filters = new List<string>();
        var sourceWidth = input?.Width ?? 1920;
        var sourceHeight = input?.Height ?? 1080;
        var width = settings.ResizeWidth;
        var height = settings.ResizeHeight;
        if (settings.CompressionPreset == VideoCompressionPreset.WebOptimized &&
            (sourceWidth > 1920 || sourceHeight > 1080))
        {
            width = 1920;
            height = 1080;
            warnings.Add("Web Optimized limits the output to 1920 × 1080.");
        }

        if (width is > 0 || height is > 0)
        {
            if (width is > 0 && height is > 0)
            {
                var w = RoundEven(width.Value);
                var h = RoundEven(height.Value);
                filters.Add($"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos");
                filters.Add($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2");
            }
            else if (width is > 0)
                filters.Add($"scale={RoundEven(width.Value)}:-2:flags=lanczos");
            else
                filters.Add($"scale=-2:{RoundEven(height!.Value)}:flags=lanczos");
            filters.Add("setsar=1");
        }
        else
        {
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos");
            filters.Add("setsar=1");
        }

        if (settings.FrameRate > 0) filters.Add($"fps={F(settings.FrameRate)}");
        return string.Join(",", filters);
    }

    private static int RoundEven(int value)
    {
        var even = Math.Max(2, value);
        return even % 2 == 0 ? even : even - 1;
    }

    private static int ComputeTargetVideoBitrate(double targetSizeMb, double durationSeconds,
        VideoAudioPlan audioPlan)
    {
        if (durationSeconds <= 0) return 0;
        var totalBits = targetSizeMb * 1024 * 1024 * 8 * 0.98;
        var audioKbps = audioPlan.None ? 0 : Math.Max(8, audioPlan.BitrateKbps);
        var videoKbps = (int)Math.Round(totalBits / durationSeconds / 1000.0 - audioKbps - 24);
        return Math.Clamp(videoKbps, 100, 100000);
    }

    private static VideoAudioPlan ResolveVideoAudioPlan(VideoToolSettings settings, string containerId,
        FFmpegCapabilities capabilities, MediaInfo? input)
    {
        if (input is not null && input.AudioCodec is null)
            return VideoAudioPlan.NoAudio;
        if (string.Equals(settings.AudioCodecId, FormatIds.None, StringComparison.OrdinalIgnoreCase))
            return VideoAudioPlan.NoAudio;
        if (string.Equals(settings.AudioCodecId, FormatIds.Copy, StringComparison.OrdinalIgnoreCase))
        {
            if (input?.AudioCodec is not null && CompatibilityMatrix.CanCopyAudio(containerId, input.AudioCodec))
                return new VideoAudioPlan(false, true, "", settings.AudioBitrateKbps);
        }

        var requested = string.IsNullOrWhiteSpace(settings.AudioCodecId) || settings.AudioCodecId == FormatIds.Copy
            ? DefaultAudioCodec(containerId)
            : settings.AudioCodecId;
        var codec = capabilities.Capability.AudioCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, requested, StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Capability.AudioCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, DefaultAudioCodec(containerId), StringComparison.OrdinalIgnoreCase));
        if (codec is null || !CompatibilityMatrix.IsAudioCodecCompatible(containerId, codec.Id))
        {
            codec = capabilities.Capability.AudioCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, DefaultAudioCodec(containerId), StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Capability.AudioCodecs.FirstOrDefault(c =>
                        CompatibilityMatrix.IsAudioCodecCompatible(containerId, c.Id));
        }
        if (codec is null) return VideoAudioPlan.NoAudio;
        var encoder = codec.Encoders.FirstOrDefault(e => e.IsAvailable &&
                          !string.IsNullOrWhiteSpace(settings.AudioEncoderId) &&
                          string.Equals(e.Id, settings.AudioEncoderId, StringComparison.OrdinalIgnoreCase))
                      ?? codec.Encoders.FirstOrDefault(e => e.IsAvailable);
        if (encoder is null) return VideoAudioPlan.NoAudio;
        return new VideoAudioPlan(false, false, encoder.Id, settings.AudioBitrateKbps);
    }

    private static string ResolveVideoAudioEncoder(string containerId, FFmpegCapabilities capabilities)
    {
        var codec = capabilities.Capability.AudioCodecs.FirstOrDefault(c =>
                        string.Equals(c.Id, DefaultAudioCodec(containerId), StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Capability.AudioCodecs.FirstOrDefault();
        return codec?.Encoders.FirstOrDefault(e => e.IsAvailable)?.Id ?? "aac";
    }

    private static string DefaultAudioCodec(string containerId) => containerId switch
    {
        "webm" => "opus",
        "avi" => "mp3",
        _ => "aac"
    };

    private static bool CanStreamCopyMerge(IReadOnlyList<MediaInfo> infos, string outputFormatId)
    {
        if (infos.Count < 2) return false;
        var first = infos[0];
        if (!string.Equals(first.FormatId, outputFormatId, StringComparison.OrdinalIgnoreCase)) return false;
        return infos.All(i =>
            string.Equals(i.FormatId, outputFormatId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.VideoCodec, first.VideoCodec, StringComparison.OrdinalIgnoreCase) &&
            i.Width == first.Width && i.Height == first.Height &&
            (i.AudioCodec is null && first.AudioCodec is null ||
             string.Equals(i.AudioCodec, first.AudioCodec, StringComparison.OrdinalIgnoreCase)) &&
            i.SampleRate == first.SampleRate && i.Channels == first.Channels &&
            (i.FrameRate is null && first.FrameRate is null ||
             i.FrameRate is not null && first.FrameRate is not null &&
             Math.Abs(i.FrameRate.Value - first.FrameRate.Value) < 0.05));
    }

    private static MediaStreamInfo? GetSubtitleStream(MediaInfo? info, int index) =>
        info?.Streams.Where(s => s.Type == "subtitle").OrderBy(s => s.Index).Skip(Math.Max(0, index)).FirstOrDefault();

    private static bool IsBitmapSubtitle(string? codec) =>
        codec is not null && (codec.Contains("pgs", StringComparison.OrdinalIgnoreCase) ||
                             codec.Contains("dvd_subtitle", StringComparison.OrdinalIgnoreCase) ||
                             codec.Contains("dvb_subtitle", StringComparison.OrdinalIgnoreCase) ||
                             codec.Contains("xsub", StringComparison.OrdinalIgnoreCase));

    private static bool IsHdrTransfer(string? transfer) =>
        transfer is not null && (transfer.Contains("2084", StringComparison.OrdinalIgnoreCase) ||
                                 transfer.Contains("pq", StringComparison.OrdinalIgnoreCase) ||
                                 transfer.Contains("hlg", StringComparison.OrdinalIgnoreCase) ||
                                 transfer.Contains("arib", StringComparison.OrdinalIgnoreCase));

    private async Task<(string? Primaries, string? Transfer)?> GetColorInfoAsync(string path,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfprobePath!,
            Arguments =
            [
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=color_primaries,color_transfer",
                "-of", "default=noprint_wrappers=1", path
            ],
            Timeout = TimeSpan.FromSeconds(30),
            MaxCapturedLines = 20
        }, cancellationToken);
        if (!result.Succeeded) return null;
        string? primaries = null;
        string? transfer = null;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0] == "color_primaries") primaries = parts[1];
            else if (parts[0] == "color_transfer") transfer = parts[1];
        }

        return (primaries, transfer);
    }

    private sealed record VideoEncodePlan(string EncoderId, string CodecId, int Quality, int BitrateKbps,
        bool UseBitrate, string? Preset);

    private sealed record VideoAudioPlan(bool None, bool Copy, string EncoderId, int BitrateKbps)
    {
        public static VideoAudioPlan NoAudio { get; } = new(true, false, "", 0);
    }
}
