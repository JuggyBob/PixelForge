using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Pipelines;

/// <summary>
/// Chains validated pipeline steps through the existing job queue. Each step is
/// an ordinary job, so validation, concurrency limits, cancellation, temp
/// files, verification, history and restart recovery remain owned by
/// <see cref="IJobService"/>. The coordinator only supplies the next job and
/// carries each committed output into the next step.
/// </summary>
public sealed class PipelineRunService : IPipelineRunService
{
    private sealed class StepOutput
    {
        public int Step { get; init; }
        public required string Path { get; init; }
    }

    private sealed class FileState
    {
        public required string OriginalPath { get; init; }
        public required string CurrentPath { get; set; }
        public string? RelativeDirectory { get; init; }
        public MediaInfo? Info { get; set; }
        public bool Failed { get; set; }
        public bool Skipped { get; set; }
        public List<StepOutput> Outputs { get; } = [];
        public List<StepOutput> AdditionalOutputs { get; } = [];
    }

    private sealed class RunState
    {
        public required PipelineRunSnapshot Snapshot { get; init; }
        public required PipelineExecutionPlan Plan { get; init; }
        public required List<FileState> Files { get; init; }
        public required string IntermediateRoot { get; init; }
        public int StepIndex { get; set; }
        public Guid? CurrentJobId { get; set; }
        public List<FileState> CurrentActiveFiles { get; set; } = [];
        public bool CancelRequested { get; set; }
        public bool HandlingTerminal { get; set; }
        public bool Finished { get; set; }
    }

    private readonly IJobService _jobs;
    private readonly PipelineValidator _validator;
    private readonly IAppPaths _paths;
    private readonly IFileNameService _fileNames;
    private readonly ISettingsService _settings;
    private readonly ILogger<PipelineRunService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = [];

    public PipelineRunService(IJobService jobs, PipelineValidator validator, IAppPaths paths,
        IFileNameService fileNames, ISettingsService settings, ILogger<PipelineRunService> logger)
    {
        _jobs = jobs;
        _validator = validator;
        _paths = paths;
        _fileNames = fileNames;
        _settings = settings;
        _logger = logger;
        _jobs.JobChanged += OnJobChanged;
    }

    public IReadOnlyList<PipelineRunSnapshot> Runs
    {
        get
        {
            lock (_gate)
                return _runs.Values
                    .OrderByDescending(r => r.Snapshot.StartedAt)
                    .Select(r => r.Snapshot.Clone())
                    .ToList();
        }
    }

    public event EventHandler? RunsChanged;

    public async Task<PipelineRunSnapshot> StartAsync(PipelineDefinition pipeline,
        CancellationToken cancellationToken = default)
    {
        var (plan, validation) = await _validator.BuildPlanAsync(pipeline, cancellationToken);
        if (plan is null)
            throw new PipelineValidationException(validation);

        var output = plan.Output;
        if (string.IsNullOrWhiteSpace(output.Directory))
            throw new PipelineValidationException(PipelineValidationResult.Invalid(
                new PipelineValidationIssue(PipelineIssueSeverity.Error, "OutputDirectoryMissing",
                    pipeline.OutputNode?.Id)));

        var outputDirectory = Path.GetFullPath(output.Directory);
        Directory.CreateDirectory(outputDirectory);

        var runId = Guid.NewGuid();
        var intermediateRoot = Path.Combine(_paths.TempDirectory, "pipelines", runId.ToString("N"));
        Directory.CreateDirectory(intermediateRoot);

        var inputNode = pipeline.InputNode;
        var inputConfig = inputNode?.Input ?? new PipelineInputConfiguration();
        var folderRoot = string.IsNullOrWhiteSpace(inputConfig.FolderPath)
            ? null
            : Path.GetFullPath(inputConfig.FolderPath);
        var files = new List<FileState>();
        foreach (var path in plan.InputPaths)
        {
            string? relativeDirectory = null;
            if (folderRoot is not null)
            {
                var relative = Path.GetRelativePath(folderRoot, path);
                var directory = Path.GetDirectoryName(relative);
                if (!string.IsNullOrWhiteSpace(directory) && directory != ".")
                    relativeDirectory = directory;
            }

            files.Add(new FileState
            {
                OriginalPath = path,
                CurrentPath = path,
                RelativeDirectory = relativeDirectory
            });
        }

        var snapshot = new PipelineRunSnapshot
        {
            Id = runId,
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            StepCount = plan.Steps.Count,
            TotalFiles = files.Count,
            State = PipelineRunState.Preparing
        };

        var run = new RunState
        {
            Snapshot = snapshot,
            Plan = plan,
            Files = files,
            IntermediateRoot = intermediateRoot
        };

        lock (_gate) _runs[runId] = run;
        RaiseChanged();
        _logger.LogInformation("Pipeline run {RunId} started for {Pipeline} with {Files} file(s)",
            runId, pipeline.Name, files.Count);

        await EnqueueStepAsync(run, 0, cancellationToken);
        return Snapshot(run);
    }

    public async Task CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        RunState? run;
        Guid? currentJob;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out run) || run.Finished) return;
            run.CancelRequested = true;
            run.Snapshot.Message = "Cancelling";
            currentJob = run.CurrentJobId;
        }

        RaiseChanged();
        if (currentJob is not null)
        {
            await _jobs.CancelAsync(currentJob.Value, cancellationToken);
            return;
        }

        await FinishAsync(run, PipelineRunState.Cancelled);
    }

    private async Task EnqueueStepAsync(RunState run, int stepIndex, CancellationToken cancellationToken)
    {
        if (run.Finished) return;
        if (run.CancelRequested)
        {
            await FinishAsync(run, PipelineRunState.Cancelled);
            return;
        }

        if (stepIndex >= run.Plan.Steps.Count)
        {
            await FinishAsync(run, ResultState(run));
            return;
        }

        var active = run.Files.Where(f => !f.Failed && !f.Skipped).ToList();
        if (active.Count == 0)
        {
            await FinishAsync(run, ResultState(run));
            return;
        }

        var step = run.Plan.Steps[stepIndex];
        var isFinal = stepIndex == run.Plan.Steps.Count - 1;
        string outputDirectory;
        if (isFinal)
        {
            outputDirectory = Path.GetFullPath(run.Plan.Output.Directory);
            Directory.CreateDirectory(outputDirectory);
        }
        else
        {
            outputDirectory = Path.Combine(run.IntermediateRoot, $"step-{stepIndex + 1:D2}");
            Directory.CreateDirectory(outputDirectory);
        }

        var settings = step.Settings.Clone();
        settings.OutputDirectory = outputDirectory;
        settings.CollisionPolicy = isFinal ? run.Plan.Output.CollisionPolicy : CollisionPolicy.UniqueName;
        settings.FileNameSuffix = isFinal ? run.Plan.Output.FileNameSuffix : "";

        if (settings is OcrSettings ocr)
            ocr.PagePaths = [active[0].CurrentPath];

        var inputs = active.Select(file => new JobInput
        {
            Path = file.CurrentPath,
            Info = file.Info,
            RelativeDirectory = run.Plan.Output.PreserveFolderStructure ? file.RelativeDirectory : null,
            PreferredOutputName = Path.GetFileNameWithoutExtension(file.OriginalPath)
        }).ToList();

        var request = new JobRequest
        {
            Operation = step.Action.Operation,
            Title = $"{run.Plan.Pipeline.Name} · {stepIndex + 1}/{run.Plan.Steps.Count}",
            Settings = settings,
            Inputs = inputs,
            PreserveRelativePaths = run.Plan.Output.PreserveFolderStructure,
            BatchSummary = run.Plan.Pipeline.Name,
            PipelineId = run.Plan.Pipeline.Id,
            PipelineName = run.Plan.Pipeline.Name,
            PipelineStep = stepIndex + 1,
            PipelineStepCount = run.Plan.Steps.Count
        };

        run.StepIndex = stepIndex;
        run.CurrentActiveFiles = active;
        run.Snapshot.State = PipelineRunState.Running;
        run.Snapshot.StepIndex = stepIndex + 1;
        run.Snapshot.Message = $"Step {stepIndex + 1}/{run.Plan.Steps.Count}";
        RaiseChanged();

        try
        {
            var job = await _jobs.EnqueueAsync(request, cancellationToken);
            if (run.CancelRequested)
            {
                await _jobs.CancelAsync(job.Id, CancellationToken.None);
            }

            bool alreadyTerminal;
            lock (_gate)
            {
                if (run.Finished) return;
                run.CurrentJobId = job.Id;
                run.Snapshot.JobIds.Add(job.Id);
                alreadyTerminal = job.IsTerminal;
                if (alreadyTerminal)
                    run.HandlingTerminal = true;
            }

            _logger.LogInformation("Pipeline run {RunId} queued step {Step}/{Count} as job {JobId}",
                run.Snapshot.Id, stepIndex + 1, run.Plan.Steps.Count, job.Id);
            RaiseChanged();
            if (alreadyTerminal)
                await HandleTerminalJobAsync(run, job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline run {RunId} could not queue step {Step}", run.Snapshot.Id, stepIndex + 1);
            run.Snapshot.Message = ex.Message;
            await FinishAsync(run, PipelineRunState.Failed);
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        var job = e.Job;
        RunState? run;
        lock (_gate)
        {
            run = _runs.Values.FirstOrDefault(r => r.CurrentJobId == job.Id && !r.Finished);
            if (run is null) return;

            if (!job.IsTerminal)
            {
                run.Snapshot.Progress = (run.StepIndex + Math.Clamp(job.Progress, 0, 1)) /
                                        Math.Max(1, run.Plan.Steps.Count);
                run.Snapshot.Message = $"Step {run.StepIndex + 1}/{run.Plan.Steps.Count}";
            }
            else
            {
                if (run.HandlingTerminal) return;
                run.HandlingTerminal = true;
            }
        }

        if (!job.IsTerminal)
        {
            RaiseChanged();
            return;
        }

        _ = HandleTerminalJobAsync(run, job);
    }

    private static PipelineRunState ResultState(RunState run) =>
        run.Files.All(f => f.Failed) ? PipelineRunState.Failed
        : run.Files.Any(f => f.Failed) ? PipelineRunState.CompletedWithFailures
        : PipelineRunState.Completed;

    private async Task HandleTerminalJobAsync(RunState run, ProcessingJob job)
    {
        try
        {
            if (run.Finished) return;
            var items = job.Items.OrderBy(i => i.SortOrder).ToList();
            var active = run.CurrentActiveFiles;
            for (var index = 0; index < active.Count; index++)
            {
                var file = active[index];
                var item = index < items.Count ? items[index] : null;
                if (item is null)
                {
                    file.Failed = true;
                    continue;
                }

                switch (item.State)
                {
                    case JobItemState.Completed when !string.IsNullOrWhiteSpace(item.OutputPath):
                        file.CurrentPath = item.OutputPath!;
                        file.Info = item.OutputInfo;
                        file.Outputs.Add(new StepOutput
                        {
                            Step = run.StepIndex + 1,
                            Path = item.OutputPath!
                        });
                        foreach (var additional in item.AdditionalOutputPaths)
                        {
                            file.AdditionalOutputs.Add(new StepOutput
                            {
                                Step = run.StepIndex + 1,
                                Path = additional
                            });
                        }
                        break;
                    case JobItemState.Skipped:
                        file.Skipped = true;
                        break;
                    default:
                        file.Failed = true;
                        break;
                }
            }

            run.Snapshot.FinishedFiles = run.Files.Count(f => f.Failed || f.Skipped ||
                f.Outputs.Any(o => o.Step == run.Plan.Steps.Count));
            run.Snapshot.FailedFiles = run.Files.Count(f => f.Failed);
            run.Snapshot.SkippedFiles = run.Files.Count(f => f.Skipped);
            run.Snapshot.Progress = (run.StepIndex + 1.0) / Math.Max(1, run.Plan.Steps.Count);

            if (run.CancelRequested)
            {
                await FinishAsync(run, PipelineRunState.Cancelled);
                return;
            }

            var hasActive = run.Files.Any(f => !f.Failed && !f.Skipped);
            if (!hasActive)
            {
                await FinishAsync(run, ResultState(run));
                return;
            }

            if (run.StepIndex + 1 >= run.Plan.Steps.Count)
            {
                await FinishAsync(run, ResultState(run));
                return;
            }

            lock (_gate)
            {
                run.CurrentJobId = null;
                run.HandlingTerminal = false;
            }

            await EnqueueStepAsync(run, run.StepIndex + 1, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline run {RunId} failed while advancing", run.Snapshot.Id);
            run.Snapshot.Message = ex.Message;
            await FinishAsync(run, PipelineRunState.Failed);
        }
    }

    private async Task FinishAsync(RunState run, PipelineRunState state)
    {
        lock (_gate)
        {
            if (run.Finished) return;
            run.Finished = true;
            run.Snapshot.State = state;
            run.Snapshot.Progress = 1;
            run.Snapshot.CompletedAt = DateTimeOffset.Now;
            run.Snapshot.Message = state switch
            {
                PipelineRunState.Completed => "Completed",
                PipelineRunState.CompletedWithFailures => "Completed with failures",
                PipelineRunState.Cancelled => "Cancelled",
                PipelineRunState.Failed => "Failed",
                _ => run.Snapshot.Message
            };
        }

        var intermediatesCopied = false;
        try
        {
            if (run.Plan.Output.KeepIntermediateFiles)
            {
                await CopyIntermediateFilesAsync(run);
                intermediatesCopied = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep intermediate files for pipeline run {RunId}", run.Snapshot.Id);
        }

        var keepForDiagnostics = _settings.Settings.Processing.KeepTempOnFailure &&
                                 state is PipelineRunState.Failed or PipelineRunState.CompletedWithFailures;
        if ((!run.Plan.Output.KeepIntermediateFiles || intermediatesCopied) && !keepForDiagnostics)
        {
            try
            {
                if (Directory.Exists(run.IntermediateRoot))
                    Directory.Delete(run.IntermediateRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clean pipeline temp folder {Path}", run.IntermediateRoot);
            }
        }

        RaiseChanged();
        _logger.LogInformation("Pipeline run {RunId} finished as {State}", run.Snapshot.Id, state);
    }

    private async Task CopyIntermediateFilesAsync(RunState run)
    {
        var folderName = _fileNames.SanitizeFileName(run.Plan.Pipeline.Name) + " intermediates";
        var root = Path.Combine(run.Plan.Output.Directory, folderName);
        Directory.CreateDirectory(root);
        foreach (var file in run.Files)
        {
            foreach (var output in file.Outputs.Concat(file.AdditionalOutputs))
            {
                if (!File.Exists(output.Path)) continue;
                if (!output.Path.StartsWith(run.IntermediateRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var directory = string.IsNullOrWhiteSpace(file.RelativeDirectory)
                    ? root
                    : Path.Combine(root, file.RelativeDirectory);
                Directory.CreateDirectory(directory);
                var extension = Path.GetExtension(output.Path);
                var name = _fileNames.SanitizeFileName(Path.GetFileNameWithoutExtension(file.OriginalPath))
                           + $"-step-{output.Step:D2}" + extension;
                var destination = Path.Combine(directory, name);
                var counter = 1;
                while (File.Exists(destination))
                    destination = Path.Combine(directory,
                        $"{Path.GetFileNameWithoutExtension(name)} ({counter++}){extension}");
                await Task.Run(() => File.Copy(output.Path, destination), CancellationToken.None);
            }
        }
    }

    private PipelineRunSnapshot Snapshot(RunState run)
    {
        lock (_gate) return run.Snapshot.Clone();
    }

    private void RaiseChanged() => RunsChanged?.Invoke(this, EventArgs.Empty);
}
