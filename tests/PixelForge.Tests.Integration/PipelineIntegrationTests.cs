using System.Collections.Concurrent;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Application.Capabilities;
using PixelForge.Application.Jobs;
using PixelForge.Application.Media;
using PixelForge.Application.Models;
using PixelForge.Application.Pipelines;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.Archives;
using PixelForge.Engines.FFmpeg;
using PixelForge.Engines.Images;
using PixelForge.Engines.Upscaling;
using PixelForge.Infrastructure.FileSystem;
using PixelForge.Infrastructure.Persistence;
using PixelForge.Infrastructure.ProcessExecution;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class PipelineIntegrationTests
{
    [Fact]
    public async Task Image_pipeline_runs_through_the_real_queue_and_writes_the_final_file()
    {
        using var paths = new TempAppPaths();
        var harness = await PipelineHarness.CreateAsync(paths);
        try
        {
            var input = Path.Combine(paths.RootDirectory, "source.png");
            using (var image = new MagickImage(MagickColors.Teal, 96, 64))
                image.Write(input);

            var definition = CreateImagePipeline(paths, "Basic image pipeline", input);
            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.Completed, completed.State);
            var output = Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.webp").Single();
            using var outputImage = new MagickImage(output);
            Assert.Equal(96u, outputImage.Width);
            Assert.Equal(64u, outputImage.Height);

            var history = await harness.History.GetRecentAsync(20);
            var steps = history.Where(h => h.PipelineId == definition.Id).ToList();
            Assert.NotEmpty(steps);
            Assert.All(steps, h =>
            {
                Assert.Equal("Basic image pipeline", h.PipelineName);
                Assert.Equal(1, h.PipelineStep);
                Assert.Equal(1, h.PipelineStepCount);
            });
            Assert.False(Directory.Exists(Path.Combine(paths.TempDirectory, "pipelines", run.Id.ToString("N"))));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Image_pipeline_isolates_batch_files_and_names_outputs_safely()
    {
        using var paths = new TempAppPaths();
        var harness = await PipelineHarness.CreateAsync(paths);
        try
        {
            var firstFolder = Path.Combine(paths.RootDirectory, "first");
            var secondFolder = Path.Combine(paths.RootDirectory, "second");
            Directory.CreateDirectory(firstFolder);
            Directory.CreateDirectory(secondFolder);
            var first = Path.Combine(firstFolder, "photo.png");
            var second = Path.Combine(secondFolder, "photo.png");
            using (var image = new MagickImage(MagickColors.Orange, 48, 32))
                image.Write(first);
            using (var image = new MagickImage(MagickColors.Purple, 32, 48))
                image.Write(second);

            var definition = CreateImagePipeline(paths, "Batch image pipeline", first, second);
            definition.OutputNode!.Output.CollisionPolicy = CollisionPolicy.UniqueName;
            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.Completed, completed.State);
            Assert.Equal(2, completed.FinishedFiles);
            var outputs = Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.webp").ToList();
            Assert.Equal(2, outputs.Count);
            Assert.Equal(2, harness.Jobs.Jobs.SelectMany(j => j.Items).Count(i => i.State == JobItemState.Completed));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelling_a_pipeline_cancels_the_active_job_and_leaves_no_final_output()
    {
        using var paths = new TempAppPaths();
        var image = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var blocking = new BlockingEngine(image);
        var harness = await PipelineHarness.CreateAsync(paths, blocking);
        try
        {
            var first = Path.Combine(paths.RootDirectory, "cancel-a.png");
            var second = Path.Combine(paths.RootDirectory, "cancel-b.png");
            using (var source = new MagickImage(MagickColors.Blue, 80, 60))
            {
                source.Write(first);
                source.Write(second);
            }

            var definition = CreateImagePipeline(paths, "Cancel image pipeline", first, second);
            var run = await harness.Runner.StartAsync(definition);
            var job = Assert.Single(harness.Jobs.Jobs, j => j.PipelineId == definition.Id);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline &&
                   !(job.State == JobState.Running &&
                     job.Items.Any(i => i.State == JobItemState.Running)))
                await Task.Delay(20);

            // Both the parent job and its active item must be running before we
            // cancel; otherwise the assertion would not cover the job-level state.
            Assert.Equal(JobState.Running, job.State);
            Assert.Contains(job.Items, i => i.State == JobItemState.Running);

            await harness.Runner.CancelAsync(run.Id);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.Cancelled, completed.State);
            Assert.Equal(JobState.Cancelled, job.State);
            Assert.Empty(Directory.EnumerateFiles(paths.DefaultOutputDirectory));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelling_a_pending_job_keeps_it_cancelled_and_never_invokes_the_engine()
    {
        using var paths = new TempAppPaths();
        var image = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var firstBlocker = Path.Combine(paths.RootDirectory, "blocker-a.png");
        var secondBlocker = Path.Combine(paths.RootDirectory, "blocker-b.png");
        var cancelledInput = Path.Combine(paths.RootDirectory, "cancel-while-pending.png");
        using (var source = new MagickImage(MagickColors.Silver, 24, 24))
        {
            source.Write(firstBlocker);
            source.Write(secondBlocker);
            source.Write(cancelledInput);
        }

        var gated = new GatedCountingEngine(image, firstBlocker, secondBlocker);
        var harness = await PipelineHarness.CreateAsync(paths, gated);
        try
        {
            // Both queue slots are occupied, so the third job stays Pending.
            var blockers = new[]
            {
                await harness.Jobs.EnqueueAsync(CreateImageJob(firstBlocker, gated.Id)),
                await harness.Jobs.EnqueueAsync(CreateImageJob(secondBlocker, gated.Id))
            };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline && gated.InvokedPaths.Count < 2)
                await Task.Delay(20);
            Assert.Equal(2, gated.InvokedPaths.Count);
            Assert.All(blockers, b => Assert.Equal(JobState.Running, b.State));

            var pending = await harness.Jobs.EnqueueAsync(CreateImageJob(cancelledInput, gated.Id));
            Assert.Equal(JobState.Pending, pending.State);

            await harness.Jobs.CancelAsync(pending.Id);

            Assert.Equal(JobState.Cancelled, pending.State);
            Assert.All(pending.Items, i => Assert.Equal(JobItemState.Cancelled, i.State));

            // Draining the blockers lets the dispatcher revisit the queue; a
            // job cancelled while pending must not be started afterwards.
            gated.Release();
            var drainDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < drainDeadline && blockers.Any(b => !b.IsTerminal))
                await Task.Delay(20);
            await Task.Delay(200);

            var afterDrain = harness.Jobs.GetJob(pending.Id);
            Assert.NotNull(afterDrain);
            Assert.Equal(JobState.Cancelled, afterDrain!.State);
            Assert.DoesNotContain(cancelledInput, gated.InvokedPaths);
        }
        finally
        {
            gated.Release();
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_failed_batch_item_does_not_stop_the_remaining_files()
    {
        using var paths = new TempAppPaths();
        var image = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var harness = await PipelineHarness.CreateAsync(paths, new FailOneEngine(image));
        try
        {
            var good = Path.Combine(paths.RootDirectory, "good.png");
            var bad = Path.Combine(paths.RootDirectory, "bad.png");
            using (var source = new MagickImage(MagickColors.Green, 70, 50))
            {
                source.Write(good);
                source.Write(bad);
            }

            var definition = CreateImagePipeline(paths, "Failure isolation", good, bad);
            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.CompletedWithFailures, completed.State);
            Assert.Equal(1, completed.FailedFiles);
            var outputs = Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.webp").ToList();
            Assert.Single(outputs);
            Assert.Contains("good", Path.GetFileName(outputs[0]), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Folder_input_runs_archive_creation_as_the_terminal_step()
    {
        using var paths = new TempAppPaths();
        var harness = await PipelineHarness.CreateAsync(paths);
        try
        {
            var folder = Path.Combine(paths.RootDirectory, "package-me");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "readme.txt"), "PixelForge pipeline test");

            var definition = PipelineDefinition.CreateEmpty("Archive folder");
            definition.InputNode!.Input.FolderPath = folder;
            definition.OutputNode!.Output.Directory = paths.DefaultOutputDirectory;
            var archive = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = PipelineActionIds.ArchiveCreate,
                X = 340,
                Y = 280,
                ConfigurationJson = new OperationSettingsSerializer().Serialize(
                    new ArchiveConversionSettings { Action = ArchiveAction.Create, OutputFormatId = "zip" })
            };
            definition.Nodes.Insert(1, archive);
            definition.Connections.Clear();
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = definition.InputNode.Id,
                ToNodeId = archive.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = archive.Id,
                ToNodeId = definition.OutputNode.Id
            });

            var (plan, validation) = await harness.Validator.BuildPlanAsync(definition);
            Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(i => i.Code)));
            Assert.NotNull(plan);
            Assert.Equal(PipelineMediaType.Folder, plan!.InputType);
            Assert.Equal(folder, Assert.Single(plan.InputPaths));
            Assert.Equal(PipelineMediaType.Archive, plan.OutputType);

            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);
            Assert.Equal(PipelineRunState.Completed, completed.State);
            var output = Assert.Single(Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.zip"));
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Skip_collision_for_every_file_finishes_as_completed()
    {
        using var paths = new TempAppPaths();
        var harness = await PipelineHarness.CreateAsync(paths);
        try
        {
            var input = Path.Combine(paths.RootDirectory, "existing.png");
            using (var image = new MagickImage(MagickColors.Red, 64, 64))
                image.Write(input);
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultOutputDirectory, "existing.webp"), "already here");

            var definition = CreateImagePipeline(paths, "Skip everything", input);
            definition.OutputNode!.Output.CollisionPolicy = CollisionPolicy.Skip;
            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.Completed, completed.State);
            Assert.Equal(1, completed.SkippedFiles);
            Assert.Equal(0, completed.FailedFiles);
            Assert.Equal("already here", await File.ReadAllTextAsync(
                Path.Combine(paths.DefaultOutputDirectory, "existing.webp")));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Multi_step_pipeline_chains_each_committed_output()
    {
        using var paths = new TempAppPaths();
        var harness = await PipelineHarness.CreateAsync(paths);
        try
        {
            var input = Path.Combine(paths.RootDirectory, "steps.jpg");
            using (var image = new MagickImage(MagickColors.Gold, 200, 120))
                image.Write(input, MagickFormat.Jpeg);

            var serializer = new OperationSettingsSerializer();
            var definition = PipelineDefinition.CreateEmpty("Two steps");
            definition.InputNode!.Input.Paths = [input];
            definition.OutputNode!.Output.Directory = paths.DefaultOutputDirectory;
            var resize = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = PipelineActionIds.ImageResize,
                X = 340,
                Y = 280,
                ConfigurationJson = serializer.Serialize(new ImageConversionSettings
                {
                    OutputFormatId = "png",
                    ResizeWidth = 50,
                    ResizeHeight = 30,
                    ResizeMode = ImageResizeMode.Stretch
                })
            };
            var convert = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = PipelineActionIds.ImageConvert,
                X = 600,
                Y = 280,
                ConfigurationJson = serializer.Serialize(new ImageConversionSettings
                {
                    OutputFormatId = "webp",
                    Quality = 85
                })
            };
            definition.Nodes.Insert(1, resize);
            definition.Nodes.Insert(2, convert);
            definition.Connections.Clear();
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = definition.InputNode.Id,
                ToNodeId = resize.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = resize.Id,
                ToNodeId = convert.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = convert.Id,
                ToNodeId = definition.OutputNode.Id
            });

            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);
            Assert.Equal(PipelineRunState.Completed, completed.State);
            Assert.Equal(2, completed.StepCount);
            var output = Assert.Single(Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.webp"));
            using var outputImage = new MagickImage(output);
            Assert.Equal(50u, outputImage.Width);
            Assert.Equal(30u, outputImage.Height);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Audio_job_reaches_running_state_and_writes_m4a_through_the_queue()
    {
        using var paths = new TempAppPaths();
        var ffmpeg = new FFmpegEngine(new ProcessRunner(), paths, NullLogger<FFmpegEngine>.Instance);
        var harness = await PipelineHarness.CreateAsync(paths, additionalEngine: ffmpeg);
        try
        {
            var source = RepoPaths.Fixture("tone.mp3");
            Assert.True(File.Exists(source), $"Fixture missing: {source}");
            var states = new ConcurrentQueue<JobState>();
            var title = "MP3 to M4A " + Guid.NewGuid().ToString("N");
            harness.JobService.JobChanged += (_, e) =>
            {
                if (string.Equals(e.Job.Title, title, StringComparison.Ordinal))
                    states.Enqueue(e.Job.State);
            };

            var job = await harness.Jobs.EnqueueAsync(new JobRequest
            {
                Operation = OperationKind.AudioConvert,
                Title = title,
                Settings = new AudioConversionSettings
                {
                    OutputFormatId = "m4a",
                    CodecId = "aac",
                    EncoderId = "aac",
                    BitrateKbps = 192,
                    OutputDirectory = paths.DefaultOutputDirectory,
                    CollisionPolicy = CollisionPolicy.UniqueName
                },
                Inputs = [new JobInput { Path = source }]
            });

            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline && !job.IsTerminal)
                await Task.Delay(20);

            Assert.True(job.IsTerminal, "The audio job did not finish in time.");
            Assert.Equal(JobState.Completed, job.State);
            Assert.Contains(JobState.Running, states);
            var output = Assert.Single(Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.m4a"));
            var info = await ffmpeg.TryInspectAsync(output, MediaCategory.Audio);
            Assert.NotNull(info);
            Assert.Equal("aac", info!.AudioCodec?.ToLowerInvariant());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Traditional_upscale_pipeline_runs_through_the_real_queue()
    {
        using var paths = new TempAppPaths();
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.LoadAsync();
        using var rife = new RifeInterpolator(NullLogger<RifeInterpolator>.Instance);
        var processRunner = new ProcessRunner();
        var ncnn = new NcnnRuntimeManager(paths, processRunner, NullLogger<NcnnRuntimeManager>.Instance);
        await ncnn.InitializeAsync();
        using var upscaler = new UpscalingEngine(paths, settings,
            NullLogger<UpscalingEngine>.Instance, rife, ncnn, processRunner);
        var harness = await PipelineHarness.CreateAsync(paths, additionalEngine: upscaler);
        try
        {
            var input = Path.Combine(paths.RootDirectory, "upscale-source.png");
            using (var image = new MagickImage(MagickColors.Cyan, 64, 48))
                image.Write(input);

            var serializer = new OperationSettingsSerializer();
            var definition = PipelineDefinition.CreateEmpty("Traditional upscale");
            definition.InputNode!.Input.Paths = [input];
            definition.OutputNode!.Output.Directory = paths.DefaultOutputDirectory;
            definition.OutputNode.Output.CollisionPolicy = CollisionPolicy.UniqueName;
            var upscale = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = PipelineActionIds.ImageUpscaleTraditional,
                X = 340,
                Y = 280,
                ConfigurationJson = serializer.Serialize(new UpscaleSettings
                {
                    Method = UpscaleMethod.Traditional,
                    Algorithm = ResamplingAlgorithm.Lanczos,
                    ScaleFactor = 2,
                    OutputFormatId = "png"
                })
            };
            definition.Nodes.Insert(1, upscale);
            definition.Connections.Clear();
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = definition.InputNode.Id,
                ToNodeId = upscale.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = upscale.Id,
                ToNodeId = definition.OutputNode.Id
            });

            var run = await harness.Runner.StartAsync(definition);
            var completed = await harness.WaitForRunAsync(run.Id);

            Assert.Equal(PipelineRunState.Completed, completed.State);
            var output = Assert.Single(Directory.EnumerateFiles(paths.DefaultOutputDirectory, "*.png"));
            using var outputImage = new MagickImage(output);
            Assert.Equal(128u, outputImage.Width);
            Assert.Equal(96u, outputImage.Height);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private static PipelineDefinition CreateImagePipeline(TempAppPaths paths, string name, params string[] inputs)
    {
        var definition = PipelineDefinition.CreateEmpty(name);
        definition.InputNode!.Input.Paths = [.. inputs];
        definition.OutputNode!.Output.Directory = paths.DefaultOutputDirectory;
        definition.OutputNode.Output.CollisionPolicy = CollisionPolicy.UniqueName;

        var convert = new PipelineNodeDefinition
        {
            Kind = PipelineNodeKind.Action,
            ActionId = PipelineActionIds.ImageConvert,
            X = 340,
            Y = 280,
            ConfigurationJson = new OperationSettingsSerializer().Serialize(new ImageConversionSettings
            {
                OutputFormatId = "webp",
                Quality = 80
            })
        };
        definition.Nodes.Insert(1, convert);
        definition.Connections.Clear();
        definition.Connections.Add(new PipelineConnectionDefinition
        {
            FromNodeId = definition.InputNode.Id,
            ToNodeId = convert.Id
        });
        definition.Connections.Add(new PipelineConnectionDefinition
        {
            FromNodeId = convert.Id,
            ToNodeId = definition.OutputNode.Id
        });
        return definition;
    }

    private static JobRequest CreateImageJob(string input, string engineId) => new()
    {
        Operation = OperationKind.ImageConvert,
        Title = "Cancel while pending",
        EngineId = engineId,
        Settings = new ImageConversionSettings
        {
            OutputFormatId = "webp",
            Quality = 80,
            OutputDirectory = Path.GetDirectoryName(input)!,
            CollisionPolicy = CollisionPolicy.UniqueName
        },
        Inputs = [new JobInput { Path = input }]
    };

    private sealed class PipelineHarness : IAsyncDisposable
    {
        private PipelineHarness()
        {
        }

        public required IJobService Jobs { get; init; }
        public required JobService JobService { get; init; }
        public required PipelineRunService Runner { get; init; }
        public required PipelineValidator Validator { get; init; }
        public required IHistoryRepository History { get; init; }

        public static async Task<PipelineHarness> CreateAsync(TempAppPaths paths,
            IProcessingEngine? engineOverride = null, IProcessingEngine? additionalEngine = null)
        {
            var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
            await settings.LoadAsync();
            settings.Settings.General.DefaultOutputDirectory = paths.DefaultOutputDirectory;
            settings.Settings.Processing.MaxConcurrentJobs = 2;

            var serializer = new OperationSettingsSerializer();
            var fileNames = new FileNameService();
            var outputPaths = new OutputPathService(fileNames);
            var image = new ImageEngine(NullLogger<ImageEngine>.Instance);
            var archive = new ArchiveEngine(new ProcessRunner(), paths,
                NullLogger<ArchiveEngine>.Instance);
            var engines = new List<IProcessingEngine> { engineOverride ?? image, archive };
            if (additionalEngine is not null) engines.Add(additionalEngine);
            var registry = new CapabilityRegistry(engines, NullLogger<CapabilityRegistry>.Instance, paths);
            await registry.InitializeAsync();
            var inspector = new MediaInspector(engines, NullLogger<MediaInspector>.Instance);
            var scanner = new FolderScanner(inspector, registry, NullLogger<FolderScanner>.Instance);
            var models = new EmptyModelManager();
            var ocrModels = new EmptyOcrModelManager();
            var db = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
            var jobRepository = new JobRepository(db, serializer, NullLogger<JobRepository>.Instance);
            var history = new HistoryRepository(db, NullLogger<HistoryRepository>.Instance);
            var jobValidator = new JobValidator(registry, engines, models, fileNames, outputPaths,
                NullLogger<JobValidator>.Instance);
            var jobService = new JobService(engines, jobRepository, history, serializer, registry, models, inspector,
                outputPaths, fileNames, settings, paths, jobValidator, NullLogger<JobService>.Instance);
            await jobService.StartAsync();
            var validator = new PipelineValidator(serializer, registry, models, ocrModels, inspector, scanner, fileNames);
            var runner = new PipelineRunService(jobService, validator, paths, fileNames, settings,
                NullLogger<PipelineRunService>.Instance);

            return new PipelineHarness
            {
                Jobs = jobService,
                JobService = jobService,
                Runner = runner,
                Validator = validator,
                History = history
            };
        }

        public async Task<PipelineRunSnapshot> WaitForRunAsync(Guid runId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var run = Runner.Runs.FirstOrDefault(r => r.Id == runId);
                if (run is { IsTerminal: true }) return run;
                await Task.Delay(40);
            }

            throw new TimeoutException("The pipeline run did not finish in time.");
        }

        public async ValueTask DisposeAsync()
        {
            await JobService.StopAsync();
        }
    }

    private sealed class BlockingEngine : IProcessingEngine
    {
        private readonly ImageEngine _inner;

        public BlockingEngine(ImageEngine inner) => _inner = inner;

        public string Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public MediaCategory[] Categories => _inner.Categories;

        public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default) =>
            _inner.ProbeAsync(cancellationToken);

        public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
            CancellationToken cancellationToken = default) => _inner.TryInspectAsync(path, categoryHint, cancellationToken);

        public Task<ValidationResult> ValidateAsync(ProcessingRequest request,
            CancellationToken cancellationToken = default) => _inner.ValidateAsync(request, cancellationToken);

        public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request,
            IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return await _inner.ProcessAsync(request, progress, cancellationToken);
        }

        public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
            CancellationToken cancellationToken = default) => _inner.VerifyOutputAsync(request, outputPath, cancellationToken);

        public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
            CancellationToken cancellationToken = default) =>
            _inner.CreateThumbnailAsync(inputPath, outputPath, maxSize, cancellationToken);
    }

    private sealed class GatedCountingEngine : IProcessingEngine
    {
        private readonly ImageEngine _inner;
        private readonly HashSet<string> _gatePaths;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private readonly List<string> _invokedPaths = [];

        public GatedCountingEngine(ImageEngine inner, params string[] gatePaths)
        {
            _inner = inner;
            _gatePaths = new HashSet<string>(gatePaths, StringComparer.OrdinalIgnoreCase);
        }

        public string Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public MediaCategory[] Categories => _inner.Categories;

        public List<string> InvokedPaths
        {
            get { lock (_sync) return _invokedPaths.ToList(); }
        }

        public void Release() => _release.TrySetResult();

        public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default) =>
            _inner.ProbeAsync(cancellationToken);

        public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
            CancellationToken cancellationToken = default) => _inner.TryInspectAsync(path, categoryHint, cancellationToken);

        public Task<ValidationResult> ValidateAsync(ProcessingRequest request,
            CancellationToken cancellationToken = default) => _inner.ValidateAsync(request, cancellationToken);

        public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request,
            IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default)
        {
            lock (_sync) _invokedPaths.Add(request.InputPath);
            if (_gatePaths.Contains(request.InputPath))
                await _release.Task.WaitAsync(cancellationToken);
            return await _inner.ProcessAsync(request, progress, cancellationToken);
        }

        public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
            CancellationToken cancellationToken = default) => _inner.VerifyOutputAsync(request, outputPath, cancellationToken);

        public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
            CancellationToken cancellationToken = default) =>
            _inner.CreateThumbnailAsync(inputPath, outputPath, maxSize, cancellationToken);
    }

    private sealed class FailOneEngine : IProcessingEngine
    {
        private readonly ImageEngine _inner;

        public FailOneEngine(ImageEngine inner) => _inner = inner;

        public string Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public MediaCategory[] Categories => _inner.Categories;

        public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default) =>
            _inner.ProbeAsync(cancellationToken);

        public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
            CancellationToken cancellationToken = default) => _inner.TryInspectAsync(path, categoryHint, cancellationToken);

        public Task<ValidationResult> ValidateAsync(ProcessingRequest request,
            CancellationToken cancellationToken = default) => _inner.ValidateAsync(request, cancellationToken);

        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request,
            IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default) =>
            Path.GetFileNameWithoutExtension(request.InputPath).StartsWith("bad", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(ProcessingResult.Fail("Simulated conversion failure."))
                : _inner.ProcessAsync(request, progress, cancellationToken);

        public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
            CancellationToken cancellationToken = default) => _inner.VerifyOutputAsync(request, outputPath, cancellationToken);

        public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
            CancellationToken cancellationToken = default) =>
            _inner.CreateThumbnailAsync(inputPath, outputPath, maxSize, cancellationToken);
    }

    private sealed class EmptyModelManager : IModelManager
    {
        public string ModelDirectory => "";
        public IReadOnlyList<ModelDescriptor> AvailableModels => [];
        public IReadOnlyList<InstalledModel> InstalledModels => [];
        public event EventHandler? ModelsChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public InstalledModel? GetInstalled(string modelId) => null;
        public ModelDescriptor? GetDescriptor(string modelId) => null;

        public Task<ModelDownloadResult> DownloadAsync(string modelId,
            IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModelDownloadResult> InstallFromFileAsync(string filePath, string? modelId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DiscardPartialAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool HasPartialDownload(string modelId) => false;
        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<InstalledModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InstalledModel?>(null);

        public Task<string?> ResolveModelPathAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetModelDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyOcrModelManager : IOcrModelManager
    {
        public string ModelDirectory => "";
        public IReadOnlyList<OcrModelDescriptor> AvailableModels => [];
        public IReadOnlyList<InstalledOcrModel> InstalledModels => [];
        public event EventHandler? ModelsChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ModelsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public InstalledOcrModel? GetInstalled(string modelId) => null;

        public Task<OcrModelDownloadResult> DownloadAsync(string modelId,
            IProgress<OcrModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstalledOcrModel?> ValidateAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InstalledOcrModel?>(null);

        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task DiscardPartialAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool HasPartialDownload(string modelId) => false;

        public Task<InstalledOcrModel?> ResolveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InstalledOcrModel?>(null);
    }
}
