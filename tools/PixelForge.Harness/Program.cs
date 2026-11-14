using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PixelForge.Composition;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Harness;

var repoRoot = FindRepoRoot();
var fixtures = Path.Combine(repoRoot, "testdata", "fixtures");
if (!Directory.Exists(fixtures))
{
    Console.Error.WriteLine($"Fixture folder not found: {fixtures}. Run tools/generate-fixtures.py first.");
    return 1;
}

var runRoot = Environment.GetEnvironmentVariable("PIXELFORGE_HARNESS_RUN_ROOT") is { Length: > 0 } existingRun
    ? Path.GetFullPath(existingRun)
    : Path.GetFullPath(Path.Combine(fixtures, "..", "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(runRoot);
Environment.SetEnvironmentVariable("PIXELFORGE_DATA_DIR", Path.Combine(runRoot, "appdata"));

var services = new ServiceCollection().AddPixelForge().BuildServiceProvider();
try
{
    await ServiceRegistration.InitializeAsync(services);
    await services.GetRequiredService<IJobService>().StartAsync();
    var scenario = args.FirstOrDefault() ?? "inspect";
    Console.WriteLine($"Scenario: {scenario}");
    Console.WriteLine($"Run folder: {runRoot}");
    switch (scenario)
    {
        case "inspect":
            await InspectAsync(services);
            break;
        case "smoke":
            await SmokeAsync(services, fixtures, runRoot);
            break;
        case "image-matrix":
            await ImageMatrixAsync(services, fixtures, runRoot);
            break;
        case "audio-matrix":
            await AudioMatrixAsync(services, fixtures, runRoot);
            break;
        case "video-matrix":
            await VideoMatrixAsync(services, fixtures, runRoot);
            break;
        case "folder":
            await FolderAsync(services, fixtures, runRoot);
            break;
        case "ai":
            await AiAsync(services, fixtures, runRoot);
            break;
        case "video-upscale":
            await VideoUpscaleAsync(services, fixtures, runRoot);
            break;
        case "video-cancel":
            await VideoCancelAsync(services, fixtures, runRoot);
            break;
        case "ocr":
            await OcrAsync(services, fixtures, runRoot);
            break;
        case "ocr-benchmark":
            await OcrBenchmarkAsync(services, fixtures, runRoot);
            break;
        case "advanced-ocr":
            await AdvancedOcrAsync(services, fixtures, runRoot);
            break;
        case "ai-benchmark":
            await AiBenchmarkAsync(services, fixtures, runRoot);
            break;
        case "ncnn":
            await NcnnAsync(services, fixtures, runRoot);
            break;
        case "swinir":
            await SwinIrAsync(services, fixtures, runRoot);
            break;
        case "span":
            await SpanAsync(services, fixtures, runRoot);
            break;
        case "restoration":
            await RestorationAsync(services, fixtures, runRoot);
            break;
        case "soak":
            await SoakAsync(services, fixtures, runRoot, args.Length > 1 ? int.Parse(args[1]) : 60);
            break;
        case "tools-matrix":
            await ToolsHarness.ToolsMatrixAsync(services, fixtures, runRoot);
            break;
        case "watch":
            await ToolsHarness.WatchAsync(services, fixtures, runRoot);
            break;
        default:
            Console.Error.WriteLine("Unknown scenario. Use inspect, smoke, image-matrix, audio-matrix, video-matrix, folder, ai, soak, tools-matrix, watch.");
            return 2;
    }

    return 0;
}
finally
{
    try
    {
        var jobService = services.GetService<IJobService>();
        if (jobService is not null) await jobService.StopAsync();
    }
    catch { }
    await services.DisposeAsync();
}

static string FindRepoRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "testdata", "fixtures")) &&
            Directory.Exists(Path.Combine(current.FullName, "third_party")))
            return current.FullName;
        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the PixelForge repository root from " + AppContext.BaseDirectory);
}

static async Task InspectAsync(IServiceProvider services)
{
    var registry = services.GetRequiredService<ICapabilityRegistry>();
    foreach (var engine in registry.Engines)
    {
        Console.WriteLine($"\n=== {engine.DisplayName} [{engine.EngineId}] ===");
        Console.WriteLine($"Available: {engine.IsAvailable}  Version: {engine.Version}");
        if (engine.Error is not null) Console.WriteLine($"Error: {engine.Error}");
        Console.WriteLine("Read formats:  " + string.Join(" ", engine.Formats.Where(f => f.CanRead).Select(f => f.NormalizedExtension)));
        Console.WriteLine("Write formats: " + string.Join(" ", engine.Formats.Where(f => f.CanWrite).Select(f => f.NormalizedExtension)));
        foreach (var codec in engine.VideoCodecs.Where(c => c.IsAvailable))
            Console.WriteLine($"  video {codec.Id,-8} encoders: " + string.Join(", ", codec.Encoders.Where(e => e.IsAvailable).Select(e => e.Id + (e.IsHardware ? "*" : ""))));
        foreach (var codec in engine.AudioCodecs.Where(c => c.IsAvailable))
            Console.WriteLine($"  audio {codec.Id,-10} encoders: " + string.Join(", ", codec.Encoders.Where(e => e.IsAvailable).Select(e => e.Id)));
        foreach (var provider in engine.ExecutionProviders)
            Console.WriteLine($"  provider {provider.DisplayName}: {provider.IsAvailable} {provider.Details}");
    }

    Console.WriteLine("\n--- image outputs --- " + string.Join(" ", registry.ImageOutputFormats.Select(f => f.NormalizedExtension)));
    Console.WriteLine("--- audio outputs --- " + string.Join(" ", registry.AudioOutputFormats.Select(f => f.NormalizedExtension)));
    Console.WriteLine("--- video outputs --- " + string.Join(" ", registry.VideoOutputFormats.Select(f => f.NormalizedExtension)));
    foreach (var format in registry.VideoOutputFormats)
        Console.WriteLine($"  {format.Id}: video [{string.Join(", ", registry.GetVideoCodecs(format.Id).Select(c => c.Id))}] audio [{string.Join(", ", registry.GetAudioCodecs(format.Id, true).Select(c => c.Id))}]");
    foreach (var format in registry.AudioOutputFormats)
        Console.WriteLine($"  {format.Id}: audio [{string.Join(", ", registry.GetAudioCodecs(format.Id, false).Select(c => c.Id))}]");
}

static async Task SmokeAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var outDir = Path.Combine(runRoot, "smoke-output");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();

    var input = await inspector.InspectAsync(Path.Combine(fixtures, "transparent.png"), MediaCategory.Image);
    Console.WriteLine($"Input: {input.FileName} {input.Width}x{input.Height} alpha={input.HasAlpha} format={input.FormatId}");
    var imageSettings = new ImageConversionSettings
    {
        OutputFormatId = "webp",
        OutputDirectory = outDir,
        Quality = 90,
        PreserveMetadata = true,
        PreserveIccProfile = true
    };
    var job = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.ImageConvert,
        Title = "Smoke image",
        Settings = imageSettings,
        Inputs = [new JobInput { Path = input.FilePath, Info = input }]
    });
    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(2));
    PrintJob(job);

    var audio = await inspector.InspectAsync(Path.Combine(fixtures, "tone.wav"), MediaCategory.Audio);
    var audioSettings = new AudioConversionSettings
    {
        OutputFormatId = "mp3",
        CodecId = "mp3",
        EncoderId = "libmp3lame",
        BitrateKbps = 192,
        OutputDirectory = outDir
    };
    var audioJob = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.AudioConvert,
        Title = "Smoke audio",
        Settings = audioSettings,
        Inputs = [new JobInput { Path = audio.FilePath, Info = audio }]
    });
    await WaitForJobAsync(jobs, audioJob.Id, TimeSpan.FromMinutes(2));
    PrintJob(audioJob);

    var videoPath = Path.Combine(fixtures, "clip-h264.mp4");
    var ffmpegEngine = services.GetServices<IProcessingEngine>().FirstOrDefault(e => e.Id == EngineIds.FFmpeg);
    if (ffmpegEngine is not null)
    {
        var direct = await ffmpegEngine.TryInspectAsync(videoPath, MediaCategory.Video);
        Console.WriteLine($"Direct FFmpeg inspect: {(direct is null ? "null" : $"Category={direct.Category} Format={direct.FormatId} {direct.Width}x{direct.Height} {direct.VideoCodec}/{direct.AudioCodec} duration={direct.DurationSeconds:0.0} error={direct.Error}")}");
        if (direct?.Tags.TryGetValue("Details", out var details) == true)
            Console.WriteLine("DETAILS:\n" + details);
    }
    var video = await inspector.InspectAsync(videoPath, MediaCategory.Video);
    Console.WriteLine($"Video input: Category={video.Category} Engine={video.EngineId} Format={video.FormatId} FormatName={video.FormatName} {video.Width}x{video.Height} {video.VideoCodec}/{video.AudioCodec} {video.DurationSeconds:0.0}s streams={video.Streams.Count} error={video.Error}");
    var videoSettings = new VideoConversionSettings
    {
        ContainerFormatId = "mkv",
        VideoCodecId = "copy",
        AudioCodecId = "copy",
        SubtitleMode = SubtitleMode.Copy,
        PreserveMetadata = true,
        PreserveChapters = true,
        OutputDirectory = outDir
    };
    var videoJob = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.VideoConvert,
        Title = "Smoke remux",
        Settings = videoSettings,
        Inputs = [new JobInput { Path = video.FilePath, Info = video }]
    });
    await WaitForJobAsync(jobs, videoJob.Id, TimeSpan.FromMinutes(2));
    PrintJob(videoJob);

    var upscaleInput = await inspector.InspectAsync(Path.Combine(fixtures, "bitmap.bmp"), MediaCategory.Image);
    var upscale = new UpscaleSettings
    {
        Method = UpscaleMethod.Traditional,
        Algorithm = ResamplingAlgorithm.Lanczos,
        ScaleFactor = 2,
        OutputFormatId = "png",
        OutputDirectory = outDir
    };
    var upJob = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.UpscaleTraditional,
        Title = "Smoke upscale",
        Settings = upscale,
        Inputs = [new JobInput { Path = upscaleInput.FilePath, Info = upscaleInput }]
    });
    await WaitForJobAsync(jobs, upJob.Id, TimeSpan.FromMinutes(2));
    PrintJob(upJob);

    foreach (var produced in Directory.EnumerateFiles(outDir))
    {
        var info = await inspector.InspectAsync(produced);
        Console.WriteLine($"OUTPUT {Path.GetFileName(produced)} -> {info.FormatId} {info.Width}x{info.Height} {info.Category} readable={info.IsReadable} error={info.Error}");
    }
}

static async Task ImageMatrixAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var outDir = Path.Combine(runRoot, "image-matrix");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var registry = services.GetRequiredService<ICapabilityRegistry>();
    var formats = registry.ImageOutputFormats.Select(f => f.Id).ToList();
    var inputs = new[]
    {
        "transparent.png", "photo-exif-orientation.jpg", "illustration.webp", "bitmap.bmp",
        "animated.gif", "multipage.tiff", "vector.svg", "large-6000x4000.jpg", "alpha.avif",
        "illustration.jxl", "sample.heic", "app-icon.ico"
    };

    foreach (var inputName in inputs)
    {
        var path = Path.Combine(fixtures, inputName);
        if (!File.Exists(path)) { Console.WriteLine($"SKIP {inputName} (missing)"); continue; }
        var info = await inspector.InspectAsync(path, MediaCategory.Image);
        Console.WriteLine($"\nINPUT {inputName}: {info.FormatId} {info.Width}x{info.Height} alpha={info.HasAlpha} animated={info.IsAnimated} multipage={info.IsMultipage} vector={info.IsVector} error={info.Error}");
        if (!info.IsReadable) continue;
        foreach (var format in formats)
        {
            var settings = new ImageConversionSettings
            {
                OutputFormatId = format,
                OutputDirectory = outDir,
                Quality = 88,
                ResizeWidth = info.IsVector ? 800 : null,
                PreserveMetadata = true,
                PreserveIccProfile = true,
                FlattenAnimation = (info.IsAnimated || info.IsMultipage),
                TransparencyMode = TransparencyMode.Preserve
            };
            try
            {
                var validation = await jobs.ValidateAsync(new JobRequest
                {
                    Operation = OperationKind.ImageConvert,
                    Title = "matrix",
                    Settings = settings,
                    Inputs = [new JobInput { Path = path, Info = info }]
                });
                if (!validation.IsValid)
                {
                    Console.WriteLine($"  {format,-5} SKIP validation: {validation.Summary}");
                    continue;
                }
                var job = await jobs.EnqueueAsync(new JobRequest
                {
                    Operation = OperationKind.ImageConvert,
                    Title = "matrix",
                    Settings = settings,
                    Inputs = [new JobInput { Path = path, Info = info }]
                });
                await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(3));
                var item = job.Items[0];
                if (item.State == JobItemState.Completed && item.OutputPath is not null)
                {
                    var actual = await inspector.InspectAsync(item.OutputPath, MediaCategory.Image);
                    Console.WriteLine($"  {format,-5} OK -> {Path.GetFileName(item.OutputPath)} {actual.Width}x{actual.Height} {actual.FormatId} alpha={actual.HasAlpha} frames={actual.FrameCount}");
                }
                else
                {
                    Console.WriteLine($"  {format,-5} FAIL {item.State}: {item.Error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {format,-5} EXCEPTION {ex.Message}");
            }
        }
    }
}

static async Task AudioMatrixAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var outDir = Path.Combine(runRoot, "audio-matrix");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var registry = services.GetRequiredService<ICapabilityRegistry>();
    var inputs = new[] { "tone.wav", "tone.mp3", "tone.flac", "tone.m4a", "tone.ogg", "tone.opus", "clip-h264.mp4" };
    foreach (var inputName in inputs)
    {
        var path = Path.Combine(fixtures, inputName);
        if (!File.Exists(path)) continue;
        var info = await inspector.InspectAsync(path, MediaCategory.Audio);
        Console.WriteLine($"\nINPUT {inputName}: {info.FormatId} {info.Category} {info.AudioCodec} {info.SampleRate}Hz {info.Channels}ch {info.DurationSeconds:0.0}s");
        foreach (var format in registry.AudioOutputFormats)
        {
            foreach (var codec in registry.GetAudioCodecs(format.Id, false))
            {
                var encoder = codec.Encoders.First();
                var settings = new AudioConversionSettings
                {
                    OutputFormatId = format.Id,
                    CodecId = codec.Id,
                    EncoderId = encoder.Id,
                    BitrateKbps = 192,
                    OutputDirectory = outDir
                };
                try
                {
                    var job = await jobs.EnqueueAsync(new JobRequest
                    {
                        Operation = OperationKind.AudioConvert,
                        Title = "audio matrix",
                        Settings = settings,
                        Inputs = [new JobInput { Path = path, Info = info }]
                    });
                    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(3));
                    var item = job.Items[0];
                    if (item.State == JobItemState.Completed && item.OutputPath is not null)
                    {
                        var actual = await inspector.InspectAsync(item.OutputPath, MediaCategory.Audio);
                        Console.WriteLine($"  {format.Id,-5}/{codec.Id,-10} OK {actual.FormatId} {actual.AudioCodec} {actual.SampleRate}Hz {actual.Channels}ch {actual.DurationSeconds:0.0}s");
                    }
                    else Console.WriteLine($"  {format.Id,-5}/{codec.Id,-10} FAIL {item.Error}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {format.Id,-5}/{codec.Id,-10} EXCEPTION {ex.Message}");
                }
            }
        }
    }
}

static async Task VideoMatrixAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var outDir = Path.Combine(runRoot, "video-matrix");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var registry = services.GetRequiredService<ICapabilityRegistry>();
    var inputs = new[] { "clip-h264.mp4", "clip-hevc.mkv", "clip-vp9.webm", "clip-mpeg4.avi", "clip-prores.mov", "clip-with-subs-chapters.mkv" };
    var scenarios = new List<(string Name, VideoConversionSettings Settings)>();
    foreach (var container in registry.VideoOutputFormats)
    {
        foreach (var codec in registry.GetVideoCodecs(container.Id).Take(1))
        {
            var encoder = codec.Encoders.First();
            var rateControl = encoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality)
                ? VideoRateControlMode.ConstantQuality
                : VideoRateControlMode.Bitrate;
            scenarios.Add(($"transcode {container.Id}/{codec.Id}/{encoder.Id}", new VideoConversionSettings
            {
                ContainerFormatId = container.Id,
                VideoCodecId = codec.Id,
                VideoEncoderId = encoder.Id,
                AudioCodecId = registry.GetAudioCodecs(container.Id, true).FirstOrDefault()?.Id ?? FormatIds.None,
                AudioEncoderId = registry.GetAudioCodecs(container.Id, true).FirstOrDefault()?.Encoders.FirstOrDefault()?.Id ?? "",
                RateControlMode = rateControl,
                QualityValue = rateControl == VideoRateControlMode.ConstantQuality ? encoder.QualityDefault ?? 23 : 0,
                VideoBitrateKbps = rateControl == VideoRateControlMode.Bitrate ? 8000 : 0,
                Preset = ConversionPreset.Fast,
                OutputDirectory = outDir
            }));
        }
    }

    foreach (var inputName in inputs)
    {
        var path = Path.Combine(fixtures, inputName);
        if (!File.Exists(path)) continue;
        var info = await inspector.InspectAsync(path, MediaCategory.Video);
        Console.WriteLine($"\nINPUT {inputName}: {info.FormatId} {info.Width}x{info.Height} {info.VideoCodec}/{info.AudioCodec} {info.FrameRate:0.##}fps {info.DurationSeconds:0.0}s subs={info.SubtitleStreamCount} chapters={info.ChapterCount}");
        foreach (var (name, settings) in scenarios)
        {
            try
            {
                var job = await jobs.EnqueueAsync(new JobRequest
                {
                    Operation = OperationKind.VideoConvert,
                    Title = name,
                    Settings = settings,
                    Inputs = [new JobInput { Path = path, Info = info }]
                });
                await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(6));
                var item = job.Items[0];
                if (item.State == JobItemState.Completed && item.OutputPath is not null)
                {
                    var actual = await inspector.InspectAsync(item.OutputPath, MediaCategory.Video);
                    Console.WriteLine($"  {name,-50} OK {actual.FormatId} {actual.Width}x{actual.Height} {actual.VideoCodec}/{actual.AudioCodec} {actual.DurationSeconds:0.0}s");
                }
                else Console.WriteLine($"  {name,-50} FAIL {item.Error}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-50} EXCEPTION {ex.Message}");
            }
        }
    }
}

static async Task FolderAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var scanner = services.GetRequiredService<IFolderScanner>();
    foreach (var recursive in new[] { false, true })
    {
        var result = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = Path.Combine(fixtures, "upscale-folder"),
            Recursive = recursive,
            PreserveFolderStructure = true,
            TargetCategory = MediaCategory.Image,
            RejectAnimated = true,
            RejectVector = true
        });
        Console.WriteLine($"\nRecursive={recursive}: {result.Summary}");
        foreach (var f in result.Files)
            Console.WriteLine($"  {f.RelativePath,-28} supported={f.IsSupported} valid={f.IsValid} {f.Reason}");
    }
}

static async Task VideoUpscaleAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    var downloads = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads");
    foreach (var (file, id) in new[]
             {
                 ("real_esrgan_x4.onnx", "realesrgan-x4plus"),
                 ("rife_v425lite.onnx", "rife-4.25-lite")
             })
    {
        var install = await models.InstallFromFileAsync(Path.Combine(downloads, file), id);
        Console.WriteLine($"Install {id}: success={install.Success} error={install.Error}");
        if (!install.Success) return;
    }

    var outDir = Path.Combine(runRoot, "video-upscale");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "video-upscale-sample.mp4"),
        MediaCategory.Video);
    Console.WriteLine($"Input: {input.Width}x{input.Height} {input.FrameRate:0.###}fps {input.DurationSeconds:0.00}s audio={input.AudioCodec}");

    var scenarios = new (string Name, bool Sr, bool Fi, int Multiplier, ExecutionProviderKind Provider)[]
    {
        ("interpolation-2x", false, true, 2, ExecutionProviderKind.DirectML),
        ("interpolation-2x-cpu", false, true, 2, ExecutionProviderKind.Cpu),
        ("super-resolution-4x", true, false, 2, ExecutionProviderKind.DirectML),
        ("combined-4x-2x", true, true, 2, ExecutionProviderKind.DirectML)
    };

    foreach (var scenario in scenarios)
    {
        var settings = new VideoUpscaleSettings
        {
            EnableSuperResolution = scenario.Sr,
            ImageModelId = "realesrgan-x4plus",
            ScaleFactor = 4,
            EnableInterpolation = scenario.Fi,
            InterpolationModelId = "rife-4.25-lite",
            FrameRateMultiplier = scenario.Multiplier,
            ExecutionProvider = scenario.Provider,
            OutputDirectory = outDir,
            FileNameSuffix = "_" + scenario.Name,
            VideoBitrateKbps = 6000,
            TileSize = 128,
            TileOverlap = 16
        };
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.VideoUpscale,
            Title = scenario.Name,
            Settings = settings,
            Inputs = [new JobInput { Path = input.FilePath, Info = input }]
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(30));
        PrintJob(job);
    }

    var silentPath = Path.Combine(fixtures, "video-upscale-silent.mp4");
    if (File.Exists(silentPath))
    {
        var silent = await inspector.InspectAsync(silentPath, MediaCategory.Video);
        var silentJob = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.VideoUpscale,
            Title = "interpolation-silent",
            Settings = new VideoUpscaleSettings
            {
                EnableSuperResolution = false,
                EnableInterpolation = true,
                InterpolationModelId = "rife-4.25-lite",
                FrameRateMultiplier = 2,
                ExecutionProvider = ExecutionProviderKind.DirectML,
                OutputDirectory = outDir,
                FileNameSuffix = "_silent",
                VideoBitrateKbps = 6000
            },
            Inputs = [new JobInput { Path = silent.FilePath, Info = silent }]
        });
        await WaitForJobAsync(jobs, silentJob.Id, TimeSpan.FromMinutes(30));
        PrintJob(silentJob);
    }

    var rotatedPath = Path.Combine(fixtures, "video-upscale-rotated.mp4");
    if (File.Exists(rotatedPath))
    {
        var rotated = await inspector.InspectAsync(rotatedPath, MediaCategory.Video);
        Console.WriteLine($"Rotated input: {rotated.Width}x{rotated.Height} rotation={rotated.RotationDegrees}");
        var rotatedJob = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.VideoUpscale,
            Title = "interpolation-rotated-90",
            Settings = new VideoUpscaleSettings
            {
                EnableSuperResolution = false,
                EnableInterpolation = true,
                InterpolationModelId = "rife-4.25-lite",
                FrameRateMultiplier = 2,
                ExecutionProvider = ExecutionProviderKind.DirectML,
                OutputDirectory = outDir,
                FileNameSuffix = "_rotated",
                VideoBitrateKbps = 6000
            },
            Inputs = [new JobInput { Path = rotated.FilePath, Info = rotated }]
        });
        await WaitForJobAsync(jobs, rotatedJob.Id, TimeSpan.FromMinutes(30));
        PrintJob(rotatedJob);
        var rotatedOutput = rotatedJob.Items.FirstOrDefault()?.OutputPath;
        if (rotatedOutput is not null)
        {
            var check = await inspector.InspectAsync(rotatedOutput, MediaCategory.Video);
            Console.WriteLine($"Rotated output: {check.Width}x{check.Height} rotation={check.RotationDegrees?.ToString() ?? "none"}");
        }
    }
}

static async Task NcnnAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    await models.InitializeAsync();
    foreach (var id in new[] { "waifu2x-cunet", "realcugan-se" })
    {
        var install = await models.DownloadAsync(id);
        Console.WriteLine($"Install {id}: success={install.Success} error={install.Error}");
        if (!install.Success) return;
    }

    var outDir = Path.Combine(runRoot, "ncnn");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();

    var scenarios = new (string Name, string ModelId, string Fixture, int Scale, int Noise,
        ExecutionProviderKind Provider)[]
    {
        ("waifu2x-cunet-2x", "waifu2x-cunet", "illustration.webp", 2, 0, ExecutionProviderKind.DirectML),
        ("waifu2x-cunet-4x", "waifu2x-cunet", "illustration.webp", 4, 0, ExecutionProviderKind.DirectML),
        ("waifu2x-cunet-alpha", "waifu2x-cunet", "transparent.png", 2, 0, ExecutionProviderKind.DirectML),
        ("waifu2x-cunet-cpu", "waifu2x-cunet", "illustration.webp", 2, 0, ExecutionProviderKind.Cpu),
        ("realcugan-se-3x", "realcugan-se", "illustration.webp", 3, 0, ExecutionProviderKind.DirectML)
    };

    foreach (var scenario in scenarios)
    {
        var input = await inspector.InspectAsync(Path.Combine(fixtures, scenario.Fixture), MediaCategory.Image);
        var settings = new UpscaleSettings
        {
            Method = UpscaleMethod.Ai,
            ModelId = scenario.ModelId,
            ScaleFactor = scenario.Scale,
            NoiseLevel = scenario.Noise,
            ExecutionProvider = scenario.Provider,
            OutputFormatId = "png",
            OutputDirectory = outDir,
            FileNameSuffix = "_" + scenario.Name,
            TileSize = 0,
            TileOverlap = 0
        };
        var stopwatch = Stopwatch.StartNew();
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.UpscaleAi,
            Title = scenario.Name,
            Settings = settings,
            Inputs = [new JobInput { Path = input.FilePath, Info = input }]
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(20));
        stopwatch.Stop();
        PrintJob(job);
        var item = job.Items[0];
        if (item.State == JobItemState.Completed && item.OutputPath is not null)
        {
            var output = await inspector.InspectAsync(item.OutputPath, MediaCategory.Image);
            Console.WriteLine($"  NCNN {scenario.Name}: {output.Width}x{output.Height} alpha={output.HasAlpha} " +
                              $"bytes={output.FileSizeBytes}");
        }
        Console.WriteLine($"  BENCH ncnn {scenario.Name} total={stopwatch.Elapsed.TotalSeconds:0.000}s " +
                          $"provider={scenario.Provider} scale={scenario.Scale} noise={scenario.Noise}");
    }
}

static async Task SwinIrAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    await models.InitializeAsync();
    var modelPath = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads", "swinir_realsr_x4.onnx");
    if (!File.Exists(modelPath))
    {
        Console.WriteLine($"SwinIR model not found: {modelPath}");
        return;
    }

    var install = await models.InstallFromFileAsync(modelPath, "swinir-real-sr-x4");
    Console.WriteLine($"Install swinir-real-sr-x4: success={install.Success} error={install.Error}");
    if (!install.Success) return;

    var outDir = Path.Combine(runRoot, "swinir");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "swinir-test.jpg"), MediaCategory.Image);
    var settings = new UpscaleSettings
    {
        Method = UpscaleMethod.Ai,
        ModelId = "swinir-real-sr-x4",
        ScaleFactor = 4,
        ExecutionProvider = ExecutionProviderKind.DirectML,
        OutputFormatId = "png",
        OutputDirectory = outDir,
        FileNameSuffix = "_swinir",
        TileSize = 0,
        TileOverlap = 0
    };
    var stopwatch = Stopwatch.StartNew();
    var job = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.UpscaleAi,
        Title = "SwinIR fixed-tile test",
        Settings = settings,
        Inputs = [new JobInput { Path = input.FilePath, Info = input }]
    });
    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(30));
    stopwatch.Stop();
    PrintJob(job);
    var item = job.Items[0];
    if (item.State == JobItemState.Completed && item.OutputPath is not null)
    {
        var output = await inspector.InspectAsync(item.OutputPath, MediaCategory.Image);
        Console.WriteLine($"  SwinIR output: {output.Width}x{output.Height} bytes={output.FileSizeBytes}");
    }
    Console.WriteLine($"  BENCH swinir total={stopwatch.Elapsed.TotalSeconds:0.000}s");
}

static async Task SpanAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    await models.InitializeAsync();
    var modelPath = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads", "span_4x_256.onnx");
    if (!File.Exists(modelPath))
    {
        Console.WriteLine($"SPAN model not found: {modelPath}");
        return;
    }

    var install = await models.InstallFromFileAsync(modelPath, "span-4x-nomos-uni");
    Console.WriteLine($"Install span-4x-nomos-uni: success={install.Success} error={install.Error}");
    if (!install.Success) return;

    var outDir = Path.Combine(runRoot, "span");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "swinir-test.jpg"), MediaCategory.Image);
    var settings = new UpscaleSettings
    {
        Method = UpscaleMethod.Ai,
        ModelId = "span-4x-nomos-uni",
        ScaleFactor = 4,
        ExecutionProvider = ExecutionProviderKind.DirectML,
        OutputFormatId = "png",
        OutputDirectory = outDir,
        FileNameSuffix = "_span"
    };
    var stopwatch = Stopwatch.StartNew();
    var job = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.UpscaleAi,
        Title = "SPAN fixed-tile test",
        Settings = settings,
        Inputs = [new JobInput { Path = input.FilePath, Info = input }]
    });
    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(10));
    stopwatch.Stop();
    PrintJob(job);
    var item = job.Items[0];
    if (item.State == JobItemState.Completed && item.OutputPath is not null)
    {
        var output = await inspector.InspectAsync(item.OutputPath, MediaCategory.Image);
        Console.WriteLine($"  SPAN output: {output.Width}x{output.Height} bytes={output.FileSizeBytes}");
    }
    Console.WriteLine($"  BENCH span total={stopwatch.Elapsed.TotalSeconds:0.000}s");
}

static async Task RestorationAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    await models.InitializeAsync();
    var downloads = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads");
    var scenarios = new (string ModelId, string File, string Suffix, ExecutionProviderKind Provider)[]
    {
        ("scunet-gan", "SCUNet-GAN.onnx", "_scunet", ExecutionProviderKind.DirectML),
        ("nafnet-gopro", "NAFNet-GoPro-width64.onnx", "_nafnet", ExecutionProviderKind.DirectML)
    };
    var outDir = Path.Combine(runRoot, "restoration");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "swinir-test.jpg"), MediaCategory.Image);

    foreach (var scenario in scenarios)
    {
        var install = await models.InstallFromFileAsync(Path.Combine(downloads, scenario.File), scenario.ModelId);
        Console.WriteLine($"Install {scenario.ModelId}: success={install.Success} error={install.Error}");
        if (!install.Success) continue;
        var settings = new UpscaleSettings
        {
            Method = UpscaleMethod.Ai,
            ModelId = scenario.ModelId,
            ScaleFactor = 1,
            ExecutionProvider = scenario.Provider,
            OutputFormatId = "png",
            OutputDirectory = outDir,
            FileNameSuffix = scenario.Suffix
        };
        var stopwatch = Stopwatch.StartNew();
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.UpscaleAi,
            Title = $"Restoration {scenario.ModelId}",
            Settings = settings,
            Inputs = [new JobInput { Path = input.FilePath, Info = input }]
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(20));
        stopwatch.Stop();
        PrintJob(job);
        var item = job.Items[0];
        if (item.State == JobItemState.Completed && item.OutputPath is not null)
        {
            var output = await inspector.InspectAsync(item.OutputPath, MediaCategory.Image);
            Console.WriteLine($"  {scenario.ModelId} output: {output.Width}x{output.Height} bytes={output.FileSizeBytes}");
        }
        Console.WriteLine($"  BENCH {scenario.ModelId} total={stopwatch.Elapsed.TotalSeconds:0.000}s");
    }
}

static async Task AiBenchmarkAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    var downloads = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads");
    var install = await models.InstallFromFileAsync(Path.Combine(downloads, "real_esrgan_x4.onnx"), "realesrgan-x4plus");
    Console.WriteLine($"Install realesrgan-x4plus: success={install.Success} error={install.Error}");
    if (!install.Success) return;

    var outDir = Path.Combine(runRoot, "ai-benchmark");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var files = new[] { "photo-exif-orientation.jpg", "bitmap.bmp", "transparent.png", "illustration.webp" };
    var inputs = new List<JobInput>();
    foreach (var file in files)
    {
        var info = await inspector.InspectAsync(Path.Combine(fixtures, file), MediaCategory.Image);
        inputs.Add(new JobInput { Path = info.FilePath, Info = info });
    }

    var settings = new UpscaleSettings
    {
        Method = UpscaleMethod.Ai,
        ModelId = "realesrgan-x4plus",
        ScaleFactor = 4,
        ExecutionProvider = ExecutionProviderKind.DirectML,
        OutputFormatId = "png",
        OutputDirectory = outDir,
        FileNameSuffix = "_bench",
        TileSize = int.TryParse(Environment.GetEnvironmentVariable("PIXELFORGE_AI_TILE"), out var tile) ? tile : 128,
        TileOverlap = int.TryParse(Environment.GetEnvironmentVariable("PIXELFORGE_AI_OVERLAP"), out var overlap) ? overlap : 16
    };
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var job = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.UpscaleAi,
        Title = "AI batch benchmark",
        Settings = settings,
        Inputs = inputs
    });
    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(30));
    stopwatch.Stop();
    PrintJob(job);
    Console.WriteLine($"BENCH total={stopwatch.Elapsed.TotalSeconds:0.000}s perImage={stopwatch.Elapsed.TotalSeconds / inputs.Count:0.000}s " +
                      $"tile={settings.TileSize} overlap={settings.TileOverlap} " +
                      $"parallelism={Environment.GetEnvironmentVariable("PIXELFORGE_AI_ITEM_PARALLELISM") ?? "default"}");
}

static async Task OcrAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var ocrModels = services.GetRequiredService<IOcrModelManager>();
    await ocrModels.InitializeAsync();
    var downloads = new List<(string Tier, ExecutionProviderKind Provider)>
    {
        ("ppocrv6-tiny", ExecutionProviderKind.DirectML),
        ("ppocrv6-small", ExecutionProviderKind.Cpu)
    };
    var outDir = Path.Combine(runRoot, "ocr");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var pages = new List<JobInput>();
    foreach (var name in new[] { "ocr-sample-1.png", "ocr-sample-2.png", "ocr-two-column.png" })
    {
        var path = Path.Combine(fixtures, name);
        if (!File.Exists(path)) continue;
        var info = await inspector.InspectAsync(path, MediaCategory.Image);
        pages.Add(new JobInput { Path = info.FilePath, Info = info });
    }

    var tinyDownload = await ocrModels.DownloadAsync("ppocrv6-tiny");
    Console.WriteLine($"Install ppocrv6-tiny: success={tinyDownload.Success} error={tinyDownload.Error}");
    if (!tinyDownload.Success) return;
    var smallDownload = await ocrModels.DownloadAsync("ppocrv6-small");
    Console.WriteLine($"Install ppocrv6-small: success={smallDownload.Success} error={smallDownload.Error}");
    foreach (var (tier, provider) in downloads)
    {
        if (ocrModels.GetInstalled(tier) is not { IsValid: true }) continue;
        var settings = new OcrSettings
        {
            OcrModelId = tier,
            DocumentName = "OCR_" + tier,
            GeneratePdf = true,
            ExecutionProvider = provider,
            OutputDirectory = outDir,
            PagePaths = pages.Select(p => p.Path).ToList(),
            Preprocess = true
        };
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.OcrDocument,
            Title = $"OCR {tier}",
            Settings = settings,
            Inputs = pages
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(20));
        PrintJob(job);
        var output = job.Items.FirstOrDefault()?.OutputPath;
        if (output is not null && File.Exists(output))
        {
            var doc = await inspector.InspectAsync(output, MediaCategory.Document);
            Console.WriteLine($"DOCX verify: {doc.IsReadable} {doc.FormatId} {doc.FileSizeBytes} bytes");
            var pdf = Path.ChangeExtension(output, ".pdf");
            Console.WriteLine($"PDF exists: {File.Exists(pdf)}" +
                              (File.Exists(pdf) ? $" ({new FileInfo(pdf).Length} bytes)" : ""));
            using var archive = System.IO.Compression.ZipFile.OpenRead(output);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open());
                var xml = reader.ReadToEnd();
                var text = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ");
                Console.WriteLine("DOCX text: " + text[..Math.Min(260, text.Length)].Replace("\n", " "));
            }
        }
    }
}

static async Task AdvancedOcrAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var ocrModels = services.GetRequiredService<IOcrModelManager>();
    await ocrModels.InitializeAsync();
    var outDir = Path.Combine(runRoot, "advanced-ocr");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var pages = new List<JobInput>();
    foreach (var name in new[] { "ocr-sample-1.png", "ocr-sample-2.png", "ocr-two-column.png" })
    {
        var path = Path.Combine(fixtures, name);
        if (!File.Exists(path)) continue;
        var info = await inspector.InspectAsync(path, MediaCategory.Image);
        pages.Add(new JobInput { Path = info.FilePath, Info = info });
    }

    var ids = (Environment.GetEnvironmentVariable("PIXELFORGE_OCR_ADVANCED_MODELS") ?? "glm-ocr")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var id in ids)
    {
        var stopwatch = Stopwatch.StartNew();
        var install = await ocrModels.DownloadAsync(id);
        Console.WriteLine($"Install {id}: success={install.Success} error={install.Error}");
        if (!install.Success) continue;

        var settings = new OcrSettings
        {
            OcrModelId = id,
            DocumentName = "AdvancedOCR_" + id,
            GeneratePdf = true,
            ExecutionProvider = ExecutionProviderKind.Auto,
            OutputDirectory = outDir,
            PagePaths = pages.Select(p => p.Path).ToList(),
            Preprocess = true
        };
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.OcrDocument,
            Title = $"Advanced OCR {id}",
            Settings = settings,
            Inputs = pages
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(30));
        stopwatch.Stop();
        PrintJob(job);
        var output = job.Items.FirstOrDefault()?.OutputPath;
        if (output is not null && File.Exists(output))
        {
            var doc = await inspector.InspectAsync(output, MediaCategory.Document);
            Console.WriteLine($"  ADVANCED DOCX: {doc.IsReadable} {doc.FormatId} {doc.FileSizeBytes} bytes");
            var pdf = Path.ChangeExtension(output, ".pdf");
            Console.WriteLine($"  ADVANCED PDF: {File.Exists(pdf)}" +
                              (File.Exists(pdf) ? $" ({new FileInfo(pdf).Length} bytes)" : ""));
            using var archive = System.IO.Compression.ZipFile.OpenRead(output);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open());
                var xml = reader.ReadToEnd();
                var text = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ");
                var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                Console.WriteLine("  ADVANCED DOCX text: " + normalized[..Math.Min(360, normalized.Length)]);
                Console.WriteLine($"  ADVANCED DOCX has literal markdown heading marker: {normalized.Contains("##")}");
            }
        }
        Console.WriteLine($"  BENCH advanced-ocr {id} total={stopwatch.Elapsed.TotalSeconds:0.000}s");
    }
}

static async Task OcrBenchmarkAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var ocrModels = services.GetRequiredService<IOcrModelManager>();
    await ocrModels.InitializeAsync();
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var tiers = (Environment.GetEnvironmentVariable("PIXELFORGE_OCR_TIERS") ?? "ppocrv6-tiny,ppocrv6-small")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var providers = (Environment.GetEnvironmentVariable("PIXELFORGE_OCR_PROVIDERS") ?? "DirectML,Cpu")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var repeat = int.TryParse(Environment.GetEnvironmentVariable("PIXELFORGE_OCR_PAGE_REPEAT"), out var r)
        ? Math.Max(1, r) : 4;
    var runs = int.TryParse(Environment.GetEnvironmentVariable("PIXELFORGE_OCR_BENCH_RUNS"), out var n)
        ? Math.Clamp(n, 1, 3) : 2;
    var generatePdf = string.Equals(Environment.GetEnvironmentVariable("PIXELFORGE_OCR_BENCH_PDF"), "1",
        StringComparison.Ordinal);

    var sourcePages = new List<JobInput>();
    foreach (var name in new[] { "ocr-sample-1.png", "ocr-sample-2.png" })
    {
        var info = await inspector.InspectAsync(Path.Combine(fixtures, name), MediaCategory.Image);
        sourcePages.Add(new JobInput { Path = info.FilePath, Info = info });
    }
    var pages = Enumerable.Range(0, repeat).SelectMany(_ => sourcePages).ToList();

    foreach (var tier in tiers)
    {
        var download = await ocrModels.DownloadAsync(tier);
        Console.WriteLine($"Install {tier}: success={download.Success} error={download.Error}");
    }

    var outDir = Path.Combine(runRoot, "ocr-benchmark");
    Directory.CreateDirectory(outDir);
    foreach (var tier in tiers)
    {
        if (ocrModels.GetInstalled(tier) is not { IsValid: true })
        {
            Console.WriteLine($"BENCH ocr tier={tier} skipped=not-installed");
            continue;
        }
        foreach (var providerName in providers)
        {
            if (!Enum.TryParse<ExecutionProviderKind>(providerName, ignoreCase: true, out var provider))
            {
                Console.WriteLine($"BENCH ocr tier={tier} provider={providerName} skipped=unknown-provider");
                continue;
            }
            for (var run = 0; run < runs; run++)
            {
                var settings = new OcrSettings
                {
                    OcrModelId = tier,
                    DocumentName = $"OCR_bench_{tier}_{provider}_{run}",
                    GeneratePdf = generatePdf,
                    ExecutionProvider = provider,
                    OutputDirectory = outDir,
                    PagePaths = pages.Select(p => p.Path).ToList(),
                    Preprocess = true
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var job = await jobs.EnqueueAsync(new JobRequest
                {
                    Operation = OperationKind.OcrDocument,
                    Title = $"OCR benchmark {tier} {provider} run {run + 1}",
                    Settings = settings,
                    Inputs = pages
                });
                await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(30));
                watch.Stop();
                PrintJob(job);
                var item = job.Items.FirstOrDefault();
                var verification = item?.VerificationSummary ?? item?.Error ?? "";
                Console.WriteLine($"BENCH ocr tier={tier} provider={provider} run={run + 1} state={item?.State} " +
                                  $"pages={pages.Count} total={watch.Elapsed.TotalSeconds:0.000}s " +
                                  $"pagesPerSecond={pages.Count / Math.Max(0.001, watch.Elapsed.TotalSeconds):0.00} " +
                                  $"verification={verification}");
            }
        }
    }
}

static async Task VideoCancelAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    var downloads = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads");
    foreach (var (file, id) in new[]
             {
                 ("real_esrgan_x4.onnx", "realesrgan-x4plus"),
                 ("rife_v425lite.onnx", "rife-4.25-lite")
             })
    {
        var install = await models.InstallFromFileAsync(Path.Combine(downloads, file), id);
        Console.WriteLine($"Install {id}: success={install.Success} error={install.Error}");
        if (!install.Success) return;
    }

    var outDir = Path.Combine(runRoot, "video-cancel");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "video-upscale-long.mp4"), MediaCategory.Video);
    var job = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.VideoUpscale,
        Title = "cancel video upscale",
        Settings = new VideoUpscaleSettings
        {
            EnableSuperResolution = true,
            ImageModelId = "realesrgan-x4plus",
            ScaleFactor = 4,
            EnableInterpolation = true,
            InterpolationModelId = "rife-4.25-lite",
            FrameRateMultiplier = 2,
            ExecutionProvider = ExecutionProviderKind.DirectML,
            OutputDirectory = outDir,
            VideoBitrateKbps = 6000,
            TileSize = 128,
            TileOverlap = 16
        },
        Inputs = [new JobInput { Path = input.FilePath, Info = input }]
    });

    for (var i = 0; i < 600 && !job.Items.Any(item => item.State == JobItemState.Running); i++)
        await Task.Delay(100);
    await Task.Delay(1200);
    Console.WriteLine($"Cancelling job {job.Id} while items={string.Join(",", job.Items.Select(i => i.State))}");
    await jobs.CancelAsync(job.Id);
    await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(3));
    PrintJob(job);
    var committed = Directory.Exists(outDir)
        ? Directory.GetFiles(outDir, "*.mp4", SearchOption.TopDirectoryOnly).Length
        : 0;
    var temp = Directory.Exists(outDir)
        ? Directory.GetDirectories(outDir, ".pixelforge-*", SearchOption.TopDirectoryOnly).Length +
          Directory.GetFiles(outDir, ".pixelforge-*", SearchOption.TopDirectoryOnly).Length
        : 0;
    Console.WriteLine($"Cancellation cleanup: state={job.State}, committed={committed}, tempArtifacts={temp}");
}

static async Task AiAsync(IServiceProvider services, string fixtures, string runRoot)
{
    var models = services.GetRequiredService<IModelManager>();
    var model = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads", "real_esrgan_x4.onnx");
    Console.WriteLine($"Installing model from {model}");
    var install = await models.InstallFromFileAsync(model, "realesrgan-x4plus");
    Console.WriteLine($"Install success={install.Success} error={install.Error} details={install.Details}");
    if (!install.Success) return;

    var outDir = Path.Combine(runRoot, "ai-output");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var input = await inspector.InspectAsync(Path.Combine(fixtures, "bitmap.bmp"), MediaCategory.Image);
    foreach (var provider in new[] { ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu })
    {
        var settings = new UpscaleSettings
        {
            Method = UpscaleMethod.Ai,
            ModelId = "realesrgan-x4plus",
            ScaleFactor = 4,
            ExecutionProvider = provider,
            OutputFormatId = "png",
            OutputDirectory = outDir,
            FileNameSuffix = provider == ExecutionProviderKind.DirectML ? "_gpu" : "_cpu",
            TileSize = 128,
            TileOverlap = 16
        };
        var job = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.UpscaleAi,
            Title = $"AI {provider}",
            Settings = settings,
            Inputs = [new JobInput { Path = input.FilePath, Info = input }]
        });
        await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(10));
        PrintJob(job);
    }

    var animeModel = Path.Combine(FindRepoRoot(), "third_party", "models", "downloads",
        "realesr-animevideov3.onnx");
    var animeInstall = await models.InstallFromFileAsync(animeModel, "realesr-animevideov3");
    Console.WriteLine($"Install realesr-animevideov3: success={animeInstall.Success} error={animeInstall.Error} details={animeInstall.Details}");
    if (!animeInstall.Success) return;

    var anime = await jobs.EnqueueAsync(new JobRequest
    {
        Operation = OperationKind.UpscaleAi,
        Title = "AI AnimeVideo DirectML",
        Settings = new UpscaleSettings
        {
            Method = UpscaleMethod.Ai,
            ModelId = "realesr-animevideov3",
            ScaleFactor = 4,
            ExecutionProvider = ExecutionProviderKind.DirectML,
            OutputFormatId = "png",
            OutputDirectory = outDir,
            FileNameSuffix = "_anime_gpu",
            TileSize = 256,
            TileOverlap = 32
        },
        Inputs = [new JobInput { Path = input.FilePath, Info = input }]
    });
    await WaitForJobAsync(jobs, anime.Id, TimeSpan.FromMinutes(10));
    PrintJob(anime);
}

static async Task SoakAsync(IServiceProvider services, string fixtures, string runRoot, int minutes)
{
    var outDir = Path.Combine(runRoot, "soak");
    Directory.CreateDirectory(outDir);
    var inspector = services.GetRequiredService<IMediaInspector>();
    var jobs = services.GetRequiredService<IJobService>();
    var deadline = DateTime.Now.AddMinutes(minutes);
    var iterations = 0;
    var image = await inspector.InspectAsync(Path.Combine(fixtures, "photo-exif-orientation.jpg"), MediaCategory.Image);
    var audio = await inspector.InspectAsync(Path.Combine(fixtures, "tone.wav"), MediaCategory.Audio);
    while (DateTime.Now < deadline)
    {
        iterations++;
        var imageJob = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.ImageConvert,
            Title = "soak image",
            Settings = new ImageConversionSettings
            {
                OutputFormatId = iterations % 2 == 0 ? "webp" : "png",
                OutputDirectory = Path.Combine(outDir, "images"),
                Quality = 88,
                FileNameSuffix = $"_{iterations}"
            },
            Inputs = [new JobInput { Path = image.FilePath, Info = image }]
        });
        var audioJob = await jobs.EnqueueAsync(new JobRequest
        {
            Operation = OperationKind.AudioConvert,
            Title = "soak audio",
            Settings = new AudioConversionSettings
            {
                OutputFormatId = "mp3",
                CodecId = "mp3",
                EncoderId = "libmp3lame",
                BitrateKbps = 160,
                OutputDirectory = Path.Combine(outDir, "audio"),
                FileNameSuffix = $"_{iterations}"
            },
            Inputs = [new JobInput { Path = audio.FilePath, Info = audio }]
        });
        await WaitForJobAsync(jobs, imageJob.Id, TimeSpan.FromMinutes(5));
        await WaitForJobAsync(jobs, audioJob.Id, TimeSpan.FromMinutes(5));
        if (imageJob.State != JobState.Completed || audioJob.State != JobState.Completed)
            Console.WriteLine($"ITERATION {iterations} FAILED image={imageJob.State} audio={audioJob.State}");
        if (iterations % 5 == 0)
            Console.WriteLine($"Soak iteration {iterations} at {DateTime.Now:HH:mm:ss}; memory={GC.GetTotalMemory(false) / 1024 / 1024} MB");
    }
    Console.WriteLine($"Soak finished: {iterations} iterations");
}

static async Task WaitForJobAsync(IJobService jobs, Guid jobId, TimeSpan timeout)
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

static void PrintJob(ProcessingJob job)
{
    Console.WriteLine($"Job {job.Title}: {job.State} {job.BatchSummary ?? job.Error}");
    foreach (var item in job.Items)
    {
        Console.WriteLine($"  {item.FileName}: {item.State} {item.Progress:P0} -> {item.OutputPath} {item.Error}");
        if (item.Warnings.Count > 0) Console.WriteLine("    warnings: " + string.Join(" | ", item.Warnings));
        if (item.VerificationSummary is not null) Console.WriteLine("    verification: " + item.VerificationSummary);
    }
}
