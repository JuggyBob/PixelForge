using Microsoft.Extensions.DependencyInjection;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Harness;

internal static class ToolsHarness
{
    public static async Task ToolsMatrixAsync(IServiceProvider services, string fixtures, string runRoot)
    {
        var outDir = Path.Combine(runRoot, "tools-matrix");
        Directory.CreateDirectory(outDir);
        var inspector = services.GetRequiredService<IMediaInspector>();
        var jobs = services.GetRequiredService<IJobService>();

        async Task Run(string name, OperationKind operation, OperationSettings settings, string input, MediaInfo? infoOverride = null)
        {
            try
            {
                var info = infoOverride ?? await inspector.InspectAsync(input);
                var request = new JobRequest
                {
                    Operation = operation,
                    Title = name,
                    Settings = settings,
                    Inputs = [new JobInput { Path = input, Info = info }]
                };
                var validation = await jobs.ValidateAsync(request);
                if (!validation.IsValid)
                {
                    Console.WriteLine($"\n{name}: VALIDATION FAIL {validation.Summary}");
                    foreach (var issue in validation.Issues) Console.WriteLine("  " + issue.Message);
                    return;
                }

                var job = await jobs.EnqueueAsync(request);
                await WaitAsync(jobs, job.Id, TimeSpan.FromMinutes(10));
                Console.WriteLine($"\n{name}: {job.State} {job.BatchSummary ?? job.Error}");
                foreach (var item in job.Items)
                {
                    Console.WriteLine($"  {item.State} {item.OutputPath} {item.Error}");
                    if (!string.IsNullOrWhiteSpace(item.ErrorDetails))
                        Console.WriteLine("  details: " + item.ErrorDetails.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
                    if (item.Warnings.Count > 0) Console.WriteLine("  warnings: " + string.Join(" | ", item.Warnings));
                    if (item.VerificationSummary is not null) Console.WriteLine("  verification: " + item.VerificationSummary);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n{name}: EXCEPTION {ex}");
            }
        }

        var png = Path.Combine(fixtures, "transparent.png");
        var jpg = Path.Combine(fixtures, "photo-exif-orientation.jpg");
        var wav = Path.Combine(fixtures, "tone.wav");
        var video = Path.Combine(fixtures, "clip-h264.mp4");
        var longVideo = Path.Combine(fixtures, "clip-with-subs-chapters.mkv");

        await Run("image optimize webp", OperationKind.ImageProcess, new ImageToolSettings
        {
            Tool = ImageToolOperation.Optimize, OutputFormatId = "webp", Quality = 82,
            OutputDirectory = outDir, FileNameSuffix = "_opt"
        }, jpg);

        await Run("image batch edit", OperationKind.ImageProcess, new ImageToolSettings
        {
            Tool = ImageToolOperation.BatchEdit, OutputFormatId = "jpg", Quality = 88,
            DenoiseStrength = 20, SharpenAmount = 60, Brightness = 105, Contrast = 108,
            Saturation = 110, ResizeWidth = 800, PreserveAspectRatio = true,
            WatermarkKind = WatermarkKind.Text, WatermarkText = "PixelForge test",
            WatermarkPosition = "BottomRight", WatermarkOpacity = 0.4, WatermarkScale = 0.25,
            OutputDirectory = outDir, FileNameSuffix = "_edit"
        }, jpg);

        await Run("image background removal", OperationKind.ImageProcess, new ImageToolSettings
        {
            Tool = ImageToolOperation.RemoveBackground, OutputFormatId = "png",
            ExecutionProvider = ExecutionProviderKind.Auto, BackgroundColor = "transparent",
            OutputDirectory = outDir, FileNameSuffix = "_nobg"
        }, png);

        await Run("image metadata", OperationKind.ImageProcess, new ImageToolSettings
        {
            Tool = ImageToolOperation.EditMetadata, OutputFormatId = "jpg", Quality = 90,
            MetaDescription = "PixelForge metadata test", MetaArtist = "PixelForge",
            MetaCopyright = "Test only", MetaComment = "Local metadata edit",
            OutputDirectory = outDir, FileNameSuffix = "_meta"
        }, jpg);

        await Run("audio loudness", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.NormalizeLoudness, OutputFormatId = "wav",
            TargetLufs = -16, TruePeakDb = -1.5, LoudnessRange = 11,
            OutputDirectory = outDir, FileNameSuffix = "_lufs"
        }, wav);

        await Run("audio spectral denoise", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.ReduceNoise, OutputFormatId = "wav",
            NoiseMethod = NoiseReductionMethod.Spectral, NoiseReductionDb = 12,
            OutputDirectory = outDir, FileNameSuffix = "_denoise"
        }, wav);

        await Run("audio rnnoise", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.ReduceNoise, OutputFormatId = "wav",
            NoiseMethod = NoiseReductionMethod.Rnnoise,
            OutputDirectory = outDir, FileNameSuffix = "_rnnoise"
        }, wav);

        await Run("audio silence removal", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.RemoveSilence, OutputFormatId = "wav",
            SilenceThresholdDb = -45, MinSilenceSeconds = 0.3, KeepSilenceMarginSeconds = 0.1,
            OutputDirectory = outDir, FileNameSuffix = "_silence"
        }, wav);

        await Run("audio trim", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.Trim, OutputFormatId = "mp3", BitrateKbps = 192,
            TrimStartSeconds = 0.5, TrimEndSeconds = 2.5,
            OutputDirectory = outDir, FileNameSuffix = "_trim"
        }, wav);

        await Run("audio channels and rate", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.ConvertChannelsAndRate, OutputFormatId = "flac",
            Channels = 1, SampleRate = 44100,
            OutputDirectory = outDir, FileNameSuffix = "_mono"
        }, wav);

        await Run("audio metadata", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.EditMetadata, OutputFormatId = "mp3", BitrateKbps = 192,
            MetadataMode = AudioMetadataMode.Edit, MetaTitle = "PixelForge tone",
            MetaArtist = "PixelForge", MetaAlbum = "Tests", MetaGenre = "Test", MetaYear = "2026",
            OutputDirectory = outDir, FileNameSuffix = "_tag"
        }, wav);

        await Run("audio extract", OperationKind.AudioProcess, new AudioToolSettings
        {
            Tool = AudioToolOperation.ExtractFromVideo, OutputFormatId = "mp3",
            BitrateKbps = 192, CopyAudioStream = true,
            OutputDirectory = outDir, FileNameSuffix = "_extracted"
        }, video);

        await Run("video compress", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Compress, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", TargetSizeMb = 2, AudioBitrateKbps = 96,
            CompressionPreset = VideoCompressionPreset.Balanced, AllowTwoPass = true,
            OutputDirectory = outDir, FileNameSuffix = "_compressed"
        }, video);

        await Run("video trim", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Trim, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", TrimStartSeconds = 0.5, TrimEndSeconds = 2.5, FrameAccurateTrim = true,
            OutputDirectory = outDir, FileNameSuffix = "_trim"
        }, video);

        await Run("video stabilize", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Stabilize, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", StabilizationStrength = 0.4,
            OutputDirectory = outDir, FileNameSuffix = "_stable"
        }, video);

        await Run("video thumbnail", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Thumbnail, ThumbnailFormatId = "jpg", ThumbnailTimeSeconds = 1,
            OutputDirectory = outDir, FileNameSuffix = "_thumb"
        }, video);

        await Run("video gif", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Gif, GifStartSeconds = 0, GifEndSeconds = 2, GifFps = 10, GifWidth = 480,
            OutputDirectory = outDir, FileNameSuffix = "_gif"
        }, video);

        await Run("video extract audio", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.ExtractAudio, ExtractAudioFormatId = "mp3",
            AudioBitrateKbps = 192, CopyAudioStream = true,
            OutputDirectory = outDir, FileNameSuffix = "_audio"
        }, video);

        await Run("video replace audio", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.ReplaceAudio, ContainerFormatId = "mp4", VideoCodecId = "copy",
            AudioCodecId = "aac", ReplacementAudioPath = wav, AudioBitrateKbps = 160,
            OutputDirectory = outDir, FileNameSuffix = "_replaced"
        }, video);

        await Run("video subtitles extract", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Subtitles, SubtitleMode = SubtitleToolMode.Extract,
            SubtitleStreamIndex = 0, SubtitleFormatId = "srt",
            OutputDirectory = outDir, FileNameSuffix = "_subs"
        }, longVideo);

        await Run("video subtitles embed", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Subtitles, SubtitleMode = SubtitleToolMode.Embed,
            ContainerFormatId = "mkv", VideoCodecId = "h264", AudioCodecId = "copy",
            OutputDirectory = outDir, FileNameSuffix = "_embedded"
        }, longVideo);

        await Run("video resize and fps", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.ResizeFrameRate, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", ResizeWidth = 640, ResizeHeight = 360, FrameRate = 24,
            OutputDirectory = outDir, FileNameSuffix = "_resized"
        }, video);

        await Run("video metadata", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.EditMetadata, ContainerFormatId = "mkv", VideoCodecId = "copy",
            AudioCodecId = "copy", MetaTitle = "PixelForge metadata test", MetaArtist = "PixelForge",
            OutputDirectory = outDir, FileNameSuffix = "_vmeta"
        }, video);

        await Run("video hdr to sdr", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.HdrToSdr, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", ToneMapPeakNits = 100,
            OutputDirectory = outDir, FileNameSuffix = "_sdr"
        }, video);

        var ffmpeg = Path.GetFullPath(Path.Combine(fixtures, "..", "..", "third_party", "ffmpeg", "bin", "ffmpeg.exe"));
        var hdrBase = Path.Combine(outDir, "hdr-source-base.webm");
        var hdrInput = Path.Combine(outDir, "hdr-source.webm");
        if (File.Exists(ffmpeg))
        {
            RunProcess(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", video, "-t", "2",
                "-c:v", "libvpx-vp9", "-b:v", "2M", "-pix_fmt", "yuv420p10le", "-an", "-f", "webm", hdrBase]);
            RunProcess(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", hdrBase,
                "-c", "copy", "-color_primaries", "bt2020", "-color_trc", "smpte2084",
                "-colorspace", "bt2020nc", hdrInput]);
            if (File.Exists(hdrInput))
            {
                await Run("video hdr to sdr (generated HDR)", OperationKind.VideoProcess, new VideoToolSettings
                {
                    Tool = VideoToolOperation.HdrToSdr, ContainerFormatId = "mp4", VideoCodecId = "h264",
                    AudioCodecId = "aac", ToneMapPeakNits = 100,
                    OutputDirectory = outDir, FileNameSuffix = "_sdr_hdr"
                }, hdrInput);
            }
        }

        await Run("video merge", OperationKind.VideoProcess, new VideoToolSettings
        {
            Tool = VideoToolOperation.Merge, ContainerFormatId = "mp4", VideoCodecId = "h264",
            AudioCodecId = "aac", MergeInputPaths = [Path.Combine(fixtures, "clip-vertical.mp4")],
            OutputDirectory = outDir, FileNameSuffix = "_merged"
        }, video);

        var markdownPath = Path.Combine(outDir, "sample.md");
        File.WriteAllText(markdownPath,
            "# PixelForge document test\n\nUnicode: café, naïve, 日本語.\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n- one\n- two\n");
        var docOut = Path.Combine(outDir, "documents");
        Directory.CreateDirectory(docOut);
        foreach (var format in new[] { "docx", "odt", "epub", "html", "pdf", "txt" })
        {
            await Run($"document md -> {format}", format == "epub" ? OperationKind.EbookConvert : OperationKind.DocumentConvert,
                format == "epub"
                    ? new EbookConversionSettings { OutputFormatId = format, Title = "PixelForge test", OutputDirectory = docOut }
                    : new DocumentConversionSettings { OutputFormatId = format, Title = "PixelForge test", OutputDirectory = docOut },
                markdownPath);
        }

        var archiveSource = Path.Combine(outDir, "archive-source");
        Directory.CreateDirectory(Path.Combine(archiveSource, "nested"));
        File.WriteAllText(Path.Combine(archiveSource, "hello.txt"), "hello archive");
        File.WriteAllText(Path.Combine(archiveSource, "nested", "unicode-日本.txt"), "unicode");
        var archiveOut = Path.Combine(outDir, "archives");
        Directory.CreateDirectory(archiveOut);
        foreach (var format in new[] { "zip", "7z", "tar", "gz", "bz2", "xz", "zst" })
        {
            await Run($"archive create {format}", OperationKind.ArchiveConvert, new ArchiveConversionSettings
            {
                OutputFormatId = format, Action = ArchiveAction.Create, CompressionLevel = 5,
                OutputDirectory = archiveOut
            }, Path.Combine(archiveSource, "hello.txt"));
        }

        var folderInfo = new MediaInfo
        {
            FilePath = archiveSource,
            FileName = Path.GetFileName(archiveSource),
            Extension = "",
            Category = MediaCategory.Archive,
            FormatId = "folder",
            FormatName = "Folder",
            EngineId = EngineIds.Archives
        };
        await Run("archive create folder", OperationKind.ArchiveConvert, new ArchiveConversionSettings
        {
            OutputFormatId = "zip", Action = ArchiveAction.Create, CompressionLevel = 5,
            OutputDirectory = archiveOut
        }, archiveSource, folderInfo);

        var zipPath = Directory.EnumerateFiles(archiveOut, "*.zip").FirstOrDefault();
        if (zipPath is not null)
        {
            await Run("archive repackage zip -> 7z", OperationKind.ArchiveConvert, new ArchiveConversionSettings
            {
                OutputFormatId = "7z", Action = ArchiveAction.Repackage, CompressionLevel = 5,
                OutputDirectory = archiveOut
            }, zipPath);

            await Run("archive extract zip", OperationKind.ArchiveConvert, new ArchiveConversionSettings
            {
                OutputFormatId = "zip", Action = ArchiveAction.Extract, CompressionLevel = 5,
                OutputDirectory = Path.Combine(outDir, "extracted")
            }, zipPath);

            var extracted = Directory.EnumerateDirectories(Path.Combine(outDir, "extracted")).FirstOrDefault();
            if (extracted is null)
                throw new InvalidOperationException("Archive extraction produced no output folder.");
            var extractedFiles = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).ToList();
            var hello = extractedFiles.FirstOrDefault(f => Path.GetFileName(f) == "hello.txt");
            if (hello is null || File.ReadAllText(hello) != "hello archive")
                throw new InvalidOperationException("Archive extraction did not preserve file contents.");
            Console.WriteLine($"archive extract verification: {extractedFiles.Count} file(s), hello.txt contents match");
        }
    }

    public static async Task WatchAsync(IServiceProvider services, string fixtures, string runRoot)
    {
        var watch = services.GetRequiredService<IWatchFolderService>();
        var input = Path.Combine(runRoot, "watch-input");
        var output = Path.Combine(runRoot, "watch-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var rule = await watch.CreateAsync(new WatchFolderRule
        {
            Name = "Harness watch",
            InputDirectory = input,
            OutputDirectory = output,
            IncludeSubdirectories = true,
            Operation = OperationKind.ImageConvert,
            Settings = new ImageConversionSettings
            {
                OutputFormatId = "webp",
                Quality = 80,
                OutputDirectory = output,
                CollisionPolicy = CollisionPolicy.UniqueName
            },
            IsEnabled = true
        });
        Console.WriteLine($"Created watch rule {rule.Id}");
        var source = Path.Combine(input, "dropped.png");
        File.Copy(Path.Combine(fixtures, "transparent.png"), source, overwrite: true);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var outputs = Directory.EnumerateFiles(output, "*.webp", SearchOption.AllDirectories).ToList();
            if (outputs.Count > 0)
            {
                Console.WriteLine($"Watch produced {outputs[0]} (source still exists={File.Exists(source)})");
                break;
            }

            await Task.Delay(500);
        }

        var after = Directory.EnumerateFiles(output, "*.webp", SearchOption.AllDirectories).Count();
        Console.WriteLine($"Watch outputs: {after}; source exists={File.Exists(source)}");
        await watch.SetEnabledAsync(rule.Id, false);
        var paused = (await watch.GetRulesAsync()).First(r => r.Id == rule.Id);
        Console.WriteLine($"Rule enabled after pause: {paused.IsEnabled}");
        await watch.DeleteAsync(rule.Id);
        Console.WriteLine($"Rules after delete: {(await watch.GetRulesAsync()).Count}");
        if (after == 0) throw new InvalidOperationException("Watch folder did not produce an output.");
        if (!File.Exists(source)) throw new InvalidOperationException("Watch folder removed the source file.");
    }

    private static async Task WaitAsync(IJobService jobs, Guid jobId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = jobs.GetJob(jobId);
            if (job is not null && job.IsTerminal) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Job {jobId} did not finish within {timeout}.");
    }

    private static void RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Console.WriteLine($"helper process failed ({process.ExitCode}): {stderr.Split('\n').LastOrDefault()}");
    }
}
