using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PixelForge.Composition;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintRootHelp();
            return 0;
        }

        if (args[0] == "help")
        {
            PrintCommandHelp(args.Skip(1).ToList());
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        var parsed = CommandLine.Parse(args);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine(parsed.Error);
            PrintRootHelp();
            return 2;
        }

        if (parsed.Positionals.Count > 0 &&
            string.Equals(parsed.Positionals[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandHelp(parsed.Positionals.Skip(1).ToList());
            return 0;
        }

        if (parsed.Flags.Contains("version"))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        if (parsed.Flags.Contains("help"))
        {
            PrintCommandHelp(parsed.Positionals);
            return 0;
        }

        try
        {
            return await RunAsync(parsed);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintRootHelp();
            return 2;
        }
        catch (JobValidationException ex)
        {
            Console.Error.WriteLine("Validation failed:");
            foreach (var issue in ex.Validation.Issues)
                Console.Error.WriteLine($"  - {issue.Code}: {issue.Details}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (parsed.Flags.Contains("verbose")) Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(CommandLine cli)
    {
        if (cli.Positionals.Count > 0 &&
            string.Equals(cli.Positionals[0], "ocr", StringComparison.OrdinalIgnoreCase) &&
            cli.Flags.Contains("review"))
        {
            Console.Error.WriteLine("--review requires the desktop review dialog. Run OCR in PixelForge without --review, or review the DOCX afterward.");
            return 2;
        }

        if (cli.Positionals.Count == 0)
        {
            PrintRootHelp();
            return 2;
        }

        var command = cli.Positionals[0].ToLowerInvariant();
        if (command is not ("convert" or "upscale" or "ocr" or "tools"))
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintRootHelp();
            return 2;
        }

        string subcommand;
        if (command == "ocr" && cli.Positionals.Count == 1)
        {
            subcommand = "ocr";
        }
        else if (cli.Positionals.Count >= 2)
        {
            subcommand = cli.Positionals[1].ToLowerInvariant();
        }
        else
        {
            PrintRootHelp();
            return 2;
        }

        if (command == "upscale" && subcommand is not ("image" or "video"))
        {
            Console.Error.WriteLine($"Unknown upscale command: {subcommand}");
            return 2;
        }
        if (command == "tools" && subcommand is not ("audio" or "image" or "video"))
        {
            Console.Error.WriteLine($"Unknown tools category: {subcommand}");
            return 2;
        }
        if (command == "convert")
        {
            try { _ = ParseCategory(subcommand); }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }

        var dataDir = cli.Get("data-dir");
        if (!string.IsNullOrWhiteSpace(dataDir))
            Environment.SetEnvironmentVariable("PIXELFORGE_DATA_DIR", Path.GetFullPath(dataDir));

        var services = new ServiceCollection().AddPixelForge().BuildServiceProvider();
        await using (services)
        {
            await ServiceRegistration.InitializeAsync(services);
            var jobs = services.GetRequiredService<IJobService>();
            await jobs.StartAsync();
            var scanner = services.GetRequiredService<IFolderScanner>();
            var inspector = services.GetRequiredService<IMediaInspector>();
            var settingsService = services.GetRequiredService<ISettingsService>();
            var appPaths = services.GetRequiredService<IAppPaths>();

            var outputDirectory = cli.Get("output-dir") ?? cli.Get("output") ??
                                  (string.IsNullOrWhiteSpace(settingsService.Settings.General.DefaultOutputDirectory)
                                      ? appPaths.DefaultOutputDirectory
                                      : settingsService.Settings.General.DefaultOutputDirectory);
            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            var inputs = await CollectInputsAsync(cli, scanner, inspector, subcommand, default);
            if (inputs.Count == 0)
            {
                Console.Error.WriteLine("No input files matched the supplied paths.");
                return 2;
            }

            var request = BuildRequest(command, subcommand, cli, settingsService.Settings, outputDirectory, inputs);
            var validation = await jobs.ValidateAsync(request);
            if (!validation.IsValid)
            {
                Console.Error.WriteLine("Validation failed:");
                foreach (var issue in validation.Issues)
                    Console.Error.WriteLine($"  - {issue.Code}: {issue.Details}");
                return 2;
            }

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                var job = await jobs.EnqueueAsync(request, cts.Token);
                var completion = new TaskCompletionSource<ProcessingJob>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(object? sender, JobChangedEventArgs e)
                {
                    if (e.Job.Id != job.Id) return;
                    if (!cli.Flags.Contains("json"))
                        Console.Error.WriteLine($"{e.Job.StateText} · {e.Job.ProgressText}");
                    if (e.Job.IsTerminal) completion.TrySetResult(e.Job);
                }

                jobs.JobChanged += OnChanged;
                try
                {
                    if (job.IsTerminal) completion.TrySetResult(job);
                    if (cts.IsCancellationRequested) await jobs.CancelAsync(job.Id);
                    var completed = await completion.Task;
                    return WriteResult(completed, cli.Flags.Contains("json"));
                }
                finally
                {
                    jobs.JobChanged -= OnChanged;
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                await jobs.StopAsync();
            }
        }
    }

    private static async Task<List<JobInput>> CollectInputsAsync(CommandLine cli, IFolderScanner scanner,
        IMediaInspector inspector, string categoryText, CancellationToken cancellationToken)
    {
        var inputs = new List<JobInput>();
        foreach (var path in cli.GetAll("input"))
        {
            if (Directory.Exists(path))
            {
                var category = ParseCategory(categoryText);
                if (category == MediaCategory.Archive && IsArchiveCreate(cli))
                {
                    var fullPath = Path.GetFullPath(path);
                    inputs.Add(new JobInput
                    {
                        Path = fullPath,
                        Info = new MediaInfo
                        {
                            FilePath = fullPath,
                            FileName = new DirectoryInfo(fullPath).Name,
                            Extension = "",
                            Category = MediaCategory.Archive,
                            FormatId = "folder",
                            FormatName = "Folder",
                            EngineId = "archives"
                        }
                    });
                    continue;
                }

                var scan = await scanner.ScanAsync(new FolderScanRequest
                {
                    FolderPath = Path.GetFullPath(path),
                    Recursive = cli.Flags.Contains("recursive"),
                    PreserveFolderStructure = true,
                    TargetCategory = category
                }, null, cancellationToken);
                foreach (var file in scan.IncludedFiles)
                    inputs.Add(new JobInput
                    {
                        Path = file.Path,
                        Info = file.Info,
                        RelativeDirectory = file.RelativeDirectory
                    });
            }
            else if (File.Exists(path))
            {
                MediaInfo? info = null;
                try { info = await inspector.InspectAsync(Path.GetFullPath(path), null, cancellationToken); }
                catch { }
                inputs.Add(new JobInput { Path = Path.GetFullPath(path), Info = info });
            }
            else
            {
                throw new CliUsageException($"Input not found: {path}");
            }
        }

        foreach (var directory in cli.GetAll("input-dir"))
        {
            var category = ParseCategory(categoryText);
            var scan = await scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = Path.GetFullPath(directory),
                Recursive = cli.Flags.Contains("recursive"),
                PreserveFolderStructure = true,
                TargetCategory = category
            }, null, cancellationToken);
            foreach (var file in scan.IncludedFiles)
                inputs.Add(new JobInput
                {
                    Path = file.Path,
                    Info = file.Info,
                    RelativeDirectory = file.RelativeDirectory
                });
        }

        return inputs
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsArchiveCreate(CommandLine cli) =>
        !string.Equals(cli.Get("action"), "repackage", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(cli.Get("action"), "extract", StringComparison.OrdinalIgnoreCase);

    private static JobRequest BuildRequest(string command, string subcommand, CommandLine cli,
        AppSettings defaults, string outputDirectory, IReadOnlyList<JobInput> inputs)
    {
        var collision = ParseCollision(cli.Get("collision"));
        if (cli.Flags.Contains("overwrite")) collision = CollisionPolicy.Overwrite;
        var suffix = cli.Get("suffix") ?? "";

        if (command == "convert")
        {
            var category = ParseCategory(subcommand);
            OperationSettings settings = category switch
            {
                MediaCategory.Image => BuildImageSettings(cli, defaults, outputDirectory, collision, suffix),
                MediaCategory.Audio => BuildAudioSettings(cli, defaults, outputDirectory, collision, suffix),
                MediaCategory.Video => BuildVideoSettings(cli, defaults, outputDirectory, collision, suffix),
                MediaCategory.Document => new DocumentConversionSettings
                {
                    OutputFormatId = cli.Get("format") ?? "pdf",
                    OutputDirectory = outputDirectory,
                    CollisionPolicy = collision,
                    FileNameSuffix = suffix,
                    PreserveMetadata = !cli.Flags.Contains("strip-metadata")
                },
                MediaCategory.Ebook => new EbookConversionSettings
                {
                    OutputFormatId = cli.Get("format") ?? "epub",
                    OutputDirectory = outputDirectory,
                    CollisionPolicy = collision,
                    FileNameSuffix = suffix,
                    PreserveMetadata = !cli.Flags.Contains("strip-metadata")
                },
                MediaCategory.Archive => new ArchiveConversionSettings
                {
                    OutputFormatId = cli.Get("format") ?? "zip",
                    Action = ParseEnum(cli.Get("action"), ArchiveAction.Create),
                    CompressionLevel = ParseInt(cli.Get("compression"), 5),
                    OutputDirectory = outputDirectory,
                    CollisionPolicy = collision,
                    FileNameSuffix = suffix
                },
                _ => throw new ArgumentException($"Unsupported category: {subcommand}")
            };
            return new JobRequest
            {
                Operation = settings.Operation,
                Title = cli.Get("title") ?? $"CLI {subcommand} conversion",
                Settings = settings,
                Inputs = inputs
            };
        }

        if (command == "upscale" && subcommand == "image")
        {
            var method = ParseEnum(cli.Get("method"), UpscaleMethod.Ai);
            var settings = new UpscaleSettings
            {
                Method = method,
                Algorithm = ParseEnum(cli.Get("algorithm"), ResamplingAlgorithm.Lanczos),
                ModelId = cli.Get("model") ?? defaults.Upscaler.ModelId,
                ScaleFactor = ParseInt(cli.Get("scale"), defaults.Upscaler.ScaleFactor),
                ExecutionProvider = ParseEnum(cli.Get("provider"), ExecutionProviderKind.Auto),
                TileSize = ParseInt(cli.Get("tile"), defaults.Upscaler.TileSize),
                TileOverlap = ParseInt(cli.Get("overlap"), defaults.Upscaler.TileOverlap),
                NoiseLevel = cli.Get("noise") is { } noise ? ParseInt(noise, 0) : null,
                OutputFormatId = cli.Get("format") ?? FormatIds.Auto,
                OutputQuality = ParseInt(cli.Get("quality"), defaults.Upscaler.OutputQuality),
                OutputDirectory = outputDirectory,
                CollisionPolicy = collision,
                FileNameSuffix = suffix
            };
            return new JobRequest
            {
                Operation = settings.Operation,
                Title = cli.Get("title") ?? "CLI image upscale",
                Settings = settings,
                Inputs = inputs
            };
        }

        if (command == "upscale" && subcommand == "video")
        {
            var settings = new VideoUpscaleSettings
            {
                ImageModelId = cli.Get("image-model") ?? defaults.VideoUpscaler.ImageModelId,
                InterpolationModelId = cli.Get("interpolation-model") ?? defaults.VideoUpscaler.InterpolationModelId,
                ScaleFactor = ParseInt(cli.Get("scale"), defaults.VideoUpscaler.FrameRateMultiplier > 1 ? 4 : 4),
                EnableSuperResolution = !cli.Flags.Contains("no-super-resolution"),
                EnableInterpolation = cli.Flags.Contains("interpolate"),
                FrameRateMultiplier = ParseInt(cli.Get("fps-multiplier"), defaults.VideoUpscaler.FrameRateMultiplier),
                VideoBitrateKbps = ParseInt(cli.Get("bitrate"), defaults.VideoUpscaler.VideoBitrateKbps),
                ExecutionProvider = ParseEnum(cli.Get("provider"), ExecutionProviderKind.Auto),
                EnableTemporalStability = cli.Flags.Contains("temporal"),
                TemporalStabilityStrength = ParseInt(cli.Get("temporal-strength"), 50),
                OutputDirectory = outputDirectory,
                CollisionPolicy = collision,
                FileNameSuffix = suffix
            };
            return new JobRequest
            {
                Operation = OperationKind.VideoUpscale,
                Title = cli.Get("title") ?? "CLI video upscale",
                Settings = settings,
                Inputs = inputs
            };
        }

        if (command == "ocr")
        {
            if (cli.Flags.Contains("review"))
                throw new ArgumentException("--review requires the desktop review dialog. Run OCR in PixelForge without --review, or review the DOCX afterward.");
            var settings = new OcrSettings
            {
                OcrModelId = cli.Get("model") ?? defaults.Ocr.ModelId,
                GeneratePdf = cli.Flags.Contains("pdf"),
                ReviewBeforeExport = false,
                ExecutionProvider = ParseEnum(cli.Get("provider"), ExecutionProviderKind.Auto),
                DocumentName = cli.Get("name") ?? "ScannedDocument_OCR",
                OutputDirectory = outputDirectory,
                CollisionPolicy = collision,
                FileNameSuffix = suffix,
                PagePaths = inputs.Select(i => i.Path).ToList()
            };
            return new JobRequest
            {
                Operation = OperationKind.OcrDocument,
                Title = cli.Get("title") ?? "CLI OCR document",
                Settings = settings,
                Inputs = inputs
            };
        }

        if (command == "tools" && subcommand is "audio" or "image" or "video")
        {
            OperationSettings settings = subcommand switch
            {
                "audio" => BuildAudioToolSettings(cli, defaults, outputDirectory, collision, suffix),
                "image" => BuildImageToolSettings(cli, defaults, outputDirectory, collision, suffix),
                _ => BuildVideoToolSettings(cli, defaults, outputDirectory, collision, suffix)
            };
            return new JobRequest
            {
                Operation = settings.Operation,
                Title = cli.Get("title") ?? $"CLI {subcommand} tool",
                Settings = settings,
                Inputs = inputs
            };
        }

        throw new ArgumentException($"Unknown command: {command} {subcommand}");
    }

    private static ImageConversionSettings BuildImageSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix) => new()
    {
        OutputFormatId = cli.Get("format") ?? defaults.Converter.ImageFormatId,
        Quality = ParseInt(cli.Get("quality"), defaults.Converter.ImageQuality),
        FlattenAnimation = cli.Flags.Contains("flatten"),
        PreserveMetadata = !cli.Flags.Contains("strip-metadata"),
        OutputDirectory = outputDirectory,
        CollisionPolicy = collision,
        FileNameSuffix = suffix,
        ResizeWidth = cli.Get("width") is { } w ? ParseInt(w, 0) : null,
        ResizeHeight = cli.Get("height") is { } h ? ParseInt(h, 0) : null,
        Resampling = ParseEnum(cli.Get("resampling"), ResamplingAlgorithm.Lanczos)
    };

    private static AudioConversionSettings BuildAudioSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix)
    {
        var formatId = cli.Get("format") ?? defaults.Converter.AudioFormatId;
        var codecId = cli.Get("codec") ??
                      CodecCatalog.GetAudioCodecsForContainer(formatId)
                          .OrderBy(c => c.SortOrder)
                          .FirstOrDefault()?.Id ?? formatId;
        return new AudioConversionSettings
        {
            OutputFormatId = formatId,
            CodecId = codecId,
            EncoderId = cli.Get("encoder") ?? "",
            BitrateKbps = ParseInt(cli.Get("bitrate"), defaults.Converter.AudioBitrateKbps),
            SampleRate = ParseInt(cli.Get("sample-rate"), 0),
            Channels = ParseInt(cli.Get("channels"), 0),
            PreserveMetadata = !cli.Flags.Contains("strip-metadata"),
            OutputDirectory = outputDirectory,
            CollisionPolicy = collision,
            FileNameSuffix = suffix
        };
    }

    private static VideoConversionSettings BuildVideoSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix) => new()
    {
        ContainerFormatId = cli.Get("format") ?? cli.Get("container") ?? defaults.Converter.VideoContainerId,
        VideoCodecId = cli.Get("codec") ?? defaults.Converter.VideoCodecId,
        AudioCodecId = cli.Get("audio-codec") ?? defaults.Converter.VideoAudioCodecId,
        VideoBitrateKbps = ParseInt(cli.Get("bitrate"), 8000),
        AudioBitrateKbps = ParseInt(cli.Get("audio-bitrate"), 192),
        QualityValue = ParseDouble(cli.Get("quality"), 23),
        RateControlMode = cli.Get("bitrate") is null ? VideoRateControlMode.ConstantQuality : VideoRateControlMode.Bitrate,
        Preset = ParseEnum(cli.Get("preset"), ConversionPreset.Balanced),
        SubtitleMode = ParseEnum(cli.Get("subtitles"), SubtitleMode.Copy),
        OutputDirectory = outputDirectory,
        CollisionPolicy = collision,
        FileNameSuffix = suffix,
        PreserveMetadata = !cli.Flags.Contains("strip-metadata")
    };

    private static AudioToolSettings BuildAudioToolSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix)
    {
        var formatId = cli.Get("format") ?? "wav";
        var codecId = cli.Get("codec") ??
                      CodecCatalog.GetAudioCodecsForContainer(formatId)
                          .OrderBy(c => c.SortOrder)
                          .FirstOrDefault()?.Id ?? "pcm_s16le";
        return new AudioToolSettings
        {
            Tool = ParseEnum(cli.Get("tool"), AudioToolOperation.NormalizeLoudness),
            OutputFormatId = formatId,
            CodecId = codecId,
            EncoderId = cli.Get("encoder") ?? "",
            BitrateKbps = ParseInt(cli.Get("bitrate"), 192),
            SampleRate = ParseInt(cli.Get("sample-rate"), 0),
            Channels = ParseInt(cli.Get("channels"), 0),
            TargetLufs = ParseDouble(cli.Get("target-lufs"), -16),
            TruePeakDb = ParseDouble(cli.Get("true-peak"), -1.5),
            NoiseMethod = ParseEnum(cli.Get("noise-method"), NoiseReductionMethod.Spectral),
            NoiseReductionDb = ParseDouble(cli.Get("noise-db"), 12),
            TrimStartSeconds = ParseDouble(cli.Get("trim-start"), 0),
            TrimEndSeconds = ParseDouble(cli.Get("trim-end"), 0),
            OutputDirectory = outputDirectory,
            CollisionPolicy = collision,
            FileNameSuffix = suffix
        };
    }

    private static ImageToolSettings BuildImageToolSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix) => new()
    {
        Tool = ParseEnum(cli.Get("tool"), ImageToolOperation.Optimize),
        OutputFormatId = cli.Get("format") ?? "webp",
        Quality = ParseInt(cli.Get("quality"), 86),
        OutputDirectory = outputDirectory,
        CollisionPolicy = collision,
        FileNameSuffix = suffix
    };

    private static VideoToolSettings BuildVideoToolSettings(CommandLine cli, AppSettings defaults,
        string outputDirectory, CollisionPolicy collision, string suffix) => new()
    {
        Tool = ParseEnum(cli.Get("tool"), VideoToolOperation.Compress),
        ContainerFormatId = cli.Get("format") ?? "mp4",
        VideoCodecId = cli.Get("codec") ?? "h264",
        AudioCodecId = cli.Get("audio-codec") ?? "aac",
        VideoBitrateKbps = ParseInt(cli.Get("bitrate"), 8000),
        AudioBitrateKbps = ParseInt(cli.Get("audio-bitrate"), 128),
        TargetSizeMb = ParseDouble(cli.Get("target-size"), 25),
        TrimStartSeconds = ParseDouble(cli.Get("trim-start"), 0),
        TrimEndSeconds = ParseDouble(cli.Get("trim-end"), 0),
        OutputDirectory = outputDirectory,
        CollisionPolicy = collision,
        FileNameSuffix = suffix
    };

    private static int WriteResult(ProcessingJob job, bool json)
    {
        if (json)
        {
            var payload = new
            {
                jobId = job.Id,
                state = job.State.ToString(),
                operation = job.Operation.ToString(),
                success = job.State == JobState.Completed && job.FailedItems == 0,
                summary = job.BatchSummary,
                items = job.Items.Select(i => new
                {
                    input = i.InputPath,
                    output = i.OutputPath,
                    status = i.State.ToString(),
                    error = i.Error,
                    errorCategory = i.ErrorCategory,
                    requestedProvider = i.RequestedProvider,
                    actualProvider = i.ActualProvider,
                    warning = i.FallbackReason,
                    durationMs = i.Duration?.TotalMilliseconds
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"{job.StateText}: {job.Title}");
            foreach (var item in job.Items)
            {
                Console.WriteLine($"  {item.StateText,-10} {item.InputPath} -> {item.OutputPath ?? "(no output)"}");
                if (!string.IsNullOrWhiteSpace(item.Error)) Console.WriteLine($"    {item.Error}");
                if (!string.IsNullOrWhiteSpace(item.ActualProvider))
                    Console.WriteLine($"    provider: {item.ActualProvider}");
            }
            Console.WriteLine(job.BatchSummary ?? job.ProgressText);
        }

        return job.State == JobState.Completed && job.FailedItems == 0 ? 0 : 1;
    }

    private static MediaCategory ParseCategory(string text) => text.ToLowerInvariant() switch
    {
        "image" or "images" => MediaCategory.Image,
        "audio" => MediaCategory.Audio,
        "video" => MediaCategory.Video,
        "document" or "documents" => MediaCategory.Document,
        "ocr" => MediaCategory.Image,
        "ebook" or "ebooks" or "e-book" or "e-books" => MediaCategory.Ebook,
        "archive" or "archives" => MediaCategory.Archive,
        _ => throw new ArgumentException($"Unknown category: {text}")
    };

    private static CollisionPolicy ParseCollision(string? text) => text?.ToLowerInvariant() switch
    {
        "skip" => CollisionPolicy.Skip,
        "overwrite" => CollisionPolicy.Overwrite,
        _ => CollisionPolicy.UniqueName
    };

    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, true, out var value) ? value : fallback;

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string GetVersion()
    {
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"
            : informational.Split('+')[0];
    }

    private static void PrintRootHelp()
    {
        Console.WriteLine("""
            PixelForge 1.1 command line

            Usage:
              pixelforge convert <images|audio|video|documents|ebooks|archives> --input <path> [options]
              pixelforge upscale image --input <path> [options]
              pixelforge upscale video --input <path> [options]
              pixelforge ocr --input <page-image> [options]
              pixelforge tools <audio|image|video> --tool <id> --input <path> [options]

            Global options:
              --data-dir <dir>       Use an isolated PixelForge profile
              --output-dir <dir>     Destination directory
              --format <id>          Target format
              --collision <mode>     unique (default), skip, overwrite
              --suffix <text>        Output filename suffix
              --recursive            Include nested files for directory inputs
              --flatten              Keep one frame/page from animated or multipage images
              --json                 Machine-readable result on stdout
              --verbose              Print exception details
              -h, --help             Command help
              -v, --version          Print version

            Examples:
              pixelforge convert images --input photo.png --format webp --quality 90 --output-dir out
              pixelforge convert audio --input song.wav --format flac --output-dir out
              pixelforge upscale image --input art.png --method ai --model realesrgan-x4plus --scale 4 --output-dir out
              pixelforge upscale video --input clip.mp4 --scale 4 --temporal --output-dir out
              pixelforge ocr --input page-1.png --input page-2.png --pdf --output-dir out
            """);
    }

    private static void PrintCommandHelp(IReadOnlyList<string> positionals)
    {
        if (positionals.Count >= 2 && positionals[0] == "convert")
        {
            Console.WriteLine($"""
                pixelforge convert {positionals[1]} [options]

                Required:
                  --input <path>       File or directory; repeat for multiple inputs

                Common options:
                  --format <id>        Target format (default depends on category)
                  --quality <number>   Image/video quality
                  --bitrate <kbps>     Audio/video bitrate
                  --codec <id>         Audio/video codec
                  --encoder <id>       Specific encoder where multiple exist
                  --output-dir <dir>   Destination directory
                  --recursive          Include nested directory inputs
                  --collision <mode>   unique, skip, overwrite
                  --suffix <text>      Output filename suffix
                  --strip-metadata     Remove metadata where supported
                  --flatten            Keep one frame/page from animated or multipage images
                  --json               JSON result on stdout
                  --verbose            Print exception details

                Archive-only:
                  --action <create|repackage|extract>
                  --compression <0-19>
                """);
            return;
        }

        if (positionals.Count >= 2 && positionals[0] == "upscale" && positionals[1] == "image")
        {
            Console.WriteLine("""
                pixelforge upscale image [options]

                Required:
                  --input <path>              File or directory; repeat for multiple inputs

                Options:
                  --method <ai|traditional>   Upscaling method (default ai)
                  --model <id>                AI model id
                  --algorithm <name>          Traditional resampling algorithm
                  --scale <number>            Scale factor
                  --provider <auto|cpu|directml|vulkan>
                  --tile <px> --overlap <px>  Tiling controls
                  --noise <0-3>               NCNN denoise level
                  --format <id> --quality <n> Output format and quality
                  --output-dir <dir>          Destination directory
                  --collision <mode>          unique, skip, overwrite
                  --suffix <text>             Output filename suffix
                  --json                      JSON result on stdout
                  --verbose                   Print exception details
                """);
            return;
        }

        if (positionals.Count >= 2 && positionals[0] == "upscale" && positionals[1] == "video")
        {
            Console.WriteLine("""
                pixelforge upscale video [options]

                Required:
                  --input <path>              File or directory; repeat for multiple inputs

                Options:
                  --image-model <id>          Frame super-resolution model
                  --interpolation-model <id>  RIFE model
                  --interpolate               Enable frame interpolation
                  --fps-multiplier <2-4>      Frame-rate multiplier
                  --temporal                  Enable temporal stability
                  --temporal-strength <0-100>
                  --scale <number>            Super-resolution scale factor
                  --bitrate <kbps>            Output video bitrate
                  --provider <auto|cpu|directml|vulkan>
                  --no-super-resolution       Disable frame super-resolution
                  --output-dir <dir>          Destination directory
                  --collision <mode>          unique, skip, overwrite
                  --suffix <text>             Output filename suffix
                  --json                      JSON result on stdout
                  --verbose                   Print exception details
                """);
            return;
        }

        if (positionals.Count >= 1 && positionals[0] == "ocr")
        {
            Console.WriteLine("""
                pixelforge ocr [options]

                Required:
                  --input <page-image>     Repeat for each page, in order

                Options:
                  --model <id>             OCR model id
                  --pdf                    Also generate a PDF
                  --review                 Not available headless; use the desktop app
                  --provider <auto|cpu|directml>
                  --name <document-name>  DOCX base name
                  --output-dir <dir>      Destination directory
                  --json                  JSON result on stdout
                """);
            return;
        }

        if (positionals.Count >= 2 && positionals[0] == "tools" && positionals[1] == "audio")
        {
            Console.WriteLine("""
                pixelforge tools audio --tool <id> [options]

                Required:
                  --input <path>       File or directory; repeat for multiple inputs
                  --tool <id>          Audio tool id, for example NormalizeLoudness

                Options:
                  --format <id>        Target format (default wav)
                  --output-dir <dir>   Destination directory
                  --target-lufs <db> --true-peak <db>
                  --noise-method <spectral|rnnoise> --noise-db <db>
                  --trim-start <seconds> --trim-end <seconds>
                  --collision <mode> --suffix <text>
                  --json               JSON result on stdout
                  --verbose            Print exception details
                """);
            return;
        }

        if (positionals.Count >= 2 && positionals[0] == "tools" && positionals[1] == "image")
        {
            Console.WriteLine("""
                pixelforge tools image --tool <id> [options]

                Required:
                  --input <path>       File or directory; repeat for multiple inputs
                  --tool <id>          Image tool id, for example Optimize

                Options:
                  --format <id>        Target format
                  --quality <number>   Output quality where supported
                  --output-dir <dir>   Destination directory
                  --collision <mode> --suffix <text>
                  --json               JSON result on stdout
                  --verbose            Print exception details
                """);
            return;
        }

        if (positionals.Count >= 2 && positionals[0] == "tools" && positionals[1] == "video")
        {
            Console.WriteLine("""
                pixelforge tools video --tool <id> [options]

                Required:
                  --input <path>       File or directory; repeat for multiple inputs
                  --tool <id>          Video tool id, for example VideoTrim

                Options:
                  --format <id>        Target container/format
                  --bitrate <kbps> --audio-bitrate <kbps> --target-size <mb>
                  --trim-start <seconds> --trim-end <seconds>
                  --output-dir <dir>   Destination directory
                  --collision <mode> --suffix <text>
                  --json               JSON result on stdout
                  --verbose            Print exception details
                """);
            return;
        }

        PrintRootHelp();
    }
}

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class CommandLine
{
    private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "version", "json", "verbose", "recursive", "overwrite", "strip-metadata",
        "pdf", "review", "interpolate", "temporal", "no-super-resolution", "flatten"
    };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help") { result.Flags.Add("help"); continue; }
            if (arg is "-v" or "--version") { result.Flags.Add("version"); continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(arg);
                continue;
            }

            var token = arg[2..];
            string? value = null;
            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                value = token[(equals + 1)..];
                token = token[..equals];
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                result.Error = $"Invalid option: {arg}";
                return result;
            }

            if (FlagNames.Contains(token) && value is null)
            {
                result.Flags.Add(token);
                continue;
            }

            if (value is null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result.Error = $"Option --{token} requires a value.";
                    return result;
                }
                value = args[++i];
            }

            if (!result._options.TryGetValue(token, out var list))
                result._options[token] = list = [];
            list.Add(value);
        }

        return result;
    }

    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;
    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var values) ? values : [];
}
