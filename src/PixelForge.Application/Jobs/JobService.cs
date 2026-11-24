using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Jobs;

public sealed class JobService : IJobService
{
    private readonly IReadOnlyList<IProcessingEngine> _engines;
    private readonly IJobRepository _jobRepository;
    private readonly IHistoryRepository _historyRepository;
    private readonly IOperationSettingsSerializer _settingsSerializer;
    private readonly ICapabilityRegistry _registry;
    private readonly IModelManager _models;
    private readonly IMediaInspector _inspector;
    private readonly IOutputPathService _outputPaths;
    private readonly IFileNameService _fileNames;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly JobValidator _validator;
    private readonly ILogger<JobService> _logger;

    private readonly object _jobsLock = new();
    private readonly List<ProcessingJob> _jobs = [];
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellation = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _itemCancellation = new();
    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();
    private readonly object _slotLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _dispatcherTask;
    private int _runningCount;
    private bool _started;
    private volatile bool _shuttingDown;

    public JobService(IEnumerable<IProcessingEngine> engines, IJobRepository jobRepository,
        IHistoryRepository historyRepository, IOperationSettingsSerializer settingsSerializer,
        ICapabilityRegistry registry, IModelManager models, IMediaInspector inspector,
        IOutputPathService outputPaths, IFileNameService fileNames, ISettingsService settings,
        IAppPaths paths, JobValidator validator, ILogger<JobService> logger)
    {
        _engines = engines.ToList();
        _jobRepository = jobRepository;
        _historyRepository = historyRepository;
        _settingsSerializer = settingsSerializer;
        _registry = registry;
        _models = models;
        _inspector = inspector;
        _outputPaths = outputPaths;
        _fileNames = fileNames;
        _settings = settings;
        _paths = paths;
        _validator = validator;
        _logger = logger;
    }

    public IReadOnlyList<ProcessingJob> Jobs
    {
        get
        {
            lock (_jobsLock) return _jobs.ToList();
        }
    }

    public bool IsStarted => _started;

    public event EventHandler<JobChangedEventArgs>? JobChanged;
    public event EventHandler? QueueChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        var startupWatch = Stopwatch.StartNew();
        _paths.EnsureCreated();
        await _jobRepository.InitializeAsync(cancellationToken);
        await _historyRepository.InitializeAsync(cancellationToken);
        _logger.LogInformation("Startup: database ready at {Elapsed:0.0} ms", startupWatch.Elapsed.TotalMilliseconds);
        await _registry.InitializeAsync(cancellationToken);
        _logger.LogInformation("Startup: capability registry ready at {Elapsed:0.0} ms", startupWatch.Elapsed.TotalMilliseconds);
        await _models.InitializeAsync(cancellationToken);
        _logger.LogInformation("Startup: model index ready at {Elapsed:0.0} ms", startupWatch.Elapsed.TotalMilliseconds);

        var recovered = await _jobRepository.RecoverInterruptedAsync(cancellationToken);
        if (recovered > 0) _logger.LogWarning("Recovered {Count} interrupted jobs", recovered);

        var recent = await _jobRepository.GetRecentAsync(200, cancellationToken);
        lock (_jobsLock)
        {
            _jobs.Clear();
            _jobs.AddRange(recent);
        }

        TrimHistoryInBackground();
        _dispatcherTask = Task.Run(() => DispatcherLoopAsync(_shutdown.Token), CancellationToken.None);
        _started = true;
        _logger.LogInformation("Job service started with {Count} jobs loaded", recent.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        _shuttingDown = true;
        _started = false;

        var pendingIds = Jobs.Where(j => j.State is JobState.Pending or JobState.Preparing or JobState.Running)
            .Select(j => j.Id).ToList();
        foreach (var id in pendingIds)
        {
            if (_jobCancellation.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        try { _shutdown.Cancel(); } catch { }

        foreach (var job in Jobs.Where(j => j.State is JobState.Pending))
        {
            job.State = JobState.Interrupted;
            job.CompletedAt = DateTimeOffset.Now;
            job.Error ??= "The application closed before this job started.";
            foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
            {
                item.State = JobItemState.Cancelled;
                item.CompletedAt = DateTimeOffset.Now;
                item.Message = "Interrupted by application shutdown";
            }

            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
        }

        try
        {
            if (_dispatcherTask is not null)
                await _dispatcherTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            // Ignore shutdown timing issues.
        }

        try
        {
            var tasks = _activeTasks.Values.ToArray();
            if (tasks.Length > 0)
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Some jobs did not stop within the shutdown timeout");
        }

        foreach (var cts in _jobCancellation.Values)
        {
            try { cts.Dispose(); } catch { }
        }

        foreach (var cts in _itemCancellation.Values)
        {
            try { cts.Dispose(); } catch { }
        }

        _jobCancellation.Clear();
        _itemCancellation.Clear();
        _logger.LogInformation("Job service stopped");
    }

    public async Task<ValidationResult> ValidateAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        if (!_registry.IsInitialized) await _registry.InitializeAsync(cancellationToken);
        if (!_started) await _models.InitializeAsync(cancellationToken);
        return await _validator.ValidateAsync(request, cancellationToken);
    }

    public async Task<ProcessingJob> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("The processing queue has not been started.");

        var validation = await ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            throw new JobValidationException(validation);

        var settings = request.Settings.Clone();
        settings.OutputDirectory = Path.GetFullPath(settings.OutputDirectory);
        var job = new ProcessingJob
        {
            Operation = request.Operation,
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildTitle(request) : request.Title,
            State = JobState.Pending,
            Settings = settings,
            EngineId = request.EngineId ?? GetEngineId(request.Operation),
            OutputDirectory = settings.OutputDirectory,
            SourcePath = request.SourcePath,
            PreserveRelativePaths = request.PreserveRelativePaths,
            BatchSummary = request.BatchSummary,
            PipelineId = request.PipelineId,
            PipelineName = request.PipelineName,
            PipelineStep = request.PipelineStep,
            PipelineStepCount = request.PipelineStepCount
        };

        var sort = 0;
        if (request.Operation == OperationKind.OcrDocument)
        {
            var input = request.Inputs[0];
            var file = new FileInfo(input.Path);
            job.Items.Add(new JobItem
            {
                JobId = job.Id,
                InputPath = Path.GetFullPath(input.Path),
                FileName = request.Settings is OcrSettings ocr ? ocr.DocumentName : Path.GetFileName(input.Path),
                RelativeDirectory = input.RelativeDirectory,
                PreferredOutputName = input.PreferredOutputName,
                InputBytes = file.Exists ? file.Length : 0,
                InputInfo = input.Info,
                IsBatchChild = false,
                SortOrder = 0
            });
        }
        else
        {
            foreach (var input in request.Inputs)
            {
                var isDirectoryInput = Directory.Exists(input.Path);
                var file = isDirectoryInput ? null : new FileInfo(input.Path);
                job.Items.Add(new JobItem
                {
                    JobId = job.Id,
                    InputPath = Path.GetFullPath(input.Path),
                    FileName = Path.GetFileName(input.Path),
                    RelativeDirectory = input.RelativeDirectory,
                    PreferredOutputName = input.PreferredOutputName,
                    InputBytes = file is { Exists: true } ? file.Length : 0,
                    InputInfo = input.Info,
                    IsBatchChild = request.Inputs.Count > 1,
                    SortOrder = sort++
                });
            }
        }

        lock (_jobsLock) _jobs.Add(job);
        await TrySaveJobAsync(job);
        RaiseJobChanged(job, JobChangeKind.Added);
        RaiseQueueChanged();

        _queue.Writer.TryWrite(job.Id);
        _logger.LogInformation("Enqueued job {JobId} ({Operation}) with {Items} item(s)", job.Id, job.Operation, job.Items.Count);
        return job;
    }

    public ProcessingJob? GetJob(Guid jobId)
    {
        lock (_jobsLock) return _jobs.FirstOrDefault(j => j.Id == jobId);
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (job is null || job.IsTerminal) return;

        if (_jobCancellation.TryGetValue(jobId, out var cts))
        {
            foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
            {
                item.Message = "Cancelling…";
            }

            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
            try { cts.Cancel(); } catch { }
            _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
            return;
        }

        // The job has not started yet.
        job.State = JobState.Cancelled;
        job.CompletedAt = DateTimeOffset.Now;
        job.Error = "Cancelled before processing started.";
        foreach (var item in job.Items.Where(i => i.State == JobItemState.Pending))
        {
            item.State = JobItemState.Cancelled;
            item.CompletedAt = DateTimeOffset.Now;
            item.Message = "Cancelled before processing started";
        }

        await TrySaveJobAsync(job);
        RaiseJobChanged(job, JobChangeKind.Updated);
        RaiseQueueChanged();
    }

    public async Task CancelItemAsync(Guid jobId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (job is null) return;
        JobItem? item;
        lock (job) item = job.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return;

        if (item.State == JobItemState.Running)
        {
            if (_itemCancellation.TryGetValue(itemId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            return;
        }

        if (item.State != JobItemState.Pending) return;
        item.State = JobItemState.Cancelled;
        item.Progress = 1;
        item.CompletedAt = DateTimeOffset.Now;
        item.Message = "Cancelled by the user";
        if (job.State == JobState.Pending &&
            !job.Items.Any(i => i.State is JobItemState.Pending or JobItemState.Running))
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = DateTimeOffset.Now;
            job.Error = "All items were cancelled.";
        }

        await TrySaveJobAsync(job);
        RaiseJobChanged(job, JobChangeKind.Updated);
        RaiseQueueChanged();
    }

    public async Task RemoveItemAsync(Guid jobId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (job is null) return;
        JobItem? item;
        lock (job) item = job.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return;

        if (job.State == JobState.Pending)
        {
            bool removeJob;
            lock (job)
            {
                job.Items.Remove(item);
                removeJob = job.Items.Count == 0;
            }

            if (removeJob)
            {
                await RemovePendingAsync(jobId, cancellationToken);
                return;
            }

            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
            return;
        }

        if (item.State is JobItemState.Pending or JobItemState.Running)
        {
            // Once a batch is running, keep the child in the list but cancel it
            // safely. Siblings continue to process.
            await CancelItemAsync(jobId, itemId, cancellationToken);
            return;
        }

        if (!job.IsTerminal) return;
        lock (job) job.Items.Remove(item);
        await TrySaveJobAsync(job);
        RaiseJobChanged(job, JobChangeKind.Updated);
        RaiseQueueChanged();
    }

    public async Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (job is null || !job.CanRetry) return;

        var retryable = job.Items.Where(i => i.State is JobItemState.Failed or JobItemState.Cancelled).ToList();
        if (retryable.Count == 0)
        {
            job.Error = "There are no failed items to retry.";
            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
            return;
        }

        foreach (var item in retryable)
        {
            item.State = JobItemState.Pending;
            item.Progress = 0;
            item.Error = null;
            item.ErrorDetails = null;
            item.SkipReason = null;
            item.Message = null;
            item.StartedAt = null;
            item.CompletedAt = null;
            item.Duration = null;
            item.OutputPath = null;
            item.OutputBytes = null;
            item.OutputInfo = null;
            item.VerificationSummary = null;
            item.Warnings = [];
        }

        job.State = JobState.Pending;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Error = null;
        job.ErrorDetails = null;
        job.BatchSummary = null;

        await TrySaveJobAsync(job);
        RaiseJobChanged(job, JobChangeKind.Updated);
        if (job.State == JobState.Pending) _queue.Writer.TryWrite(job.Id);
        RaiseQueueChanged();
    }

    public async Task RemovePendingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        ProcessingJob? job;
        lock (_jobsLock) job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || job.State != JobState.Pending) return;

        job.State = JobState.Cancelled;
        job.CompletedAt = DateTimeOffset.Now;
        await _jobRepository.DeleteAsync(job.Id, cancellationToken);
        lock (_jobsLock) _jobs.Remove(job);
        RaiseJobChanged(job, JobChangeKind.Removed);
        RaiseQueueChanged();
    }

    public async Task ClearCompletedAsync(CancellationToken cancellationToken = default) =>
        await ClearStatesAsync([JobState.Completed], cancellationToken);

    public async Task ClearFailedAsync(CancellationToken cancellationToken = default) =>
        await ClearStatesAsync([JobState.Failed], cancellationToken);

    public async Task ClearTerminalAsync(CancellationToken cancellationToken = default) =>
        await ClearStatesAsync([JobState.Completed, JobState.Failed, JobState.Cancelled, JobState.Interrupted], cancellationToken);

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) =>
        await _jobRepository.RecoverInterruptedAsync(cancellationToken);

    public Task RequestOpenOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        var path = job?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open output directory {Path}", path);
        }

        return Task.CompletedTask;
    }

    private async Task ClearStatesAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken)
    {
        await _jobRepository.DeleteByStatesAsync(states, cancellationToken);
        List<ProcessingJob> removed;
        lock (_jobsLock)
        {
            removed = _jobs.Where(j => states.Contains(j.State)).ToList();
            foreach (var job in removed) _jobs.Remove(job);
        }

        foreach (var job in removed) RaiseJobChanged(job, JobChangeKind.Removed);
        RaiseQueueChanged();
    }

    private async Task DispatcherLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_queue.Reader.TryRead(out var jobId))
                {
                    var job = GetJob(jobId);
                    if (job is null || job.State != JobState.Pending) continue;

                    await WaitForSlotAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    job = GetJob(jobId);
                    if (job is null || job.State != JobState.Pending) continue;

                    lock (_slotLock) _runningCount++;
                    var task = Task.Run(() => RunJobGuardedAsync(jobId), CancellationToken.None);
                    _activeTasks[jobId] = task;
                    _ = task.ContinueWith(t =>
                    {
                        _activeTasks.TryRemove(jobId, out _);
                        lock (_slotLock)
                        {
                            _runningCount = Math.Max(0, _runningCount - 1);
                            Monitor.PulseAll(_slotLock);
                        }
                    }, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The job dispatcher stopped unexpectedly");
        }
    }

    private async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_slotLock)
            {
                var max = Math.Clamp(_settings.Settings.Processing.MaxConcurrentJobs, 1, 8);
                if (_runningCount < max) return;
                Monitor.Wait(_slotLock, 250);
            }
            await Task.Yield();
        }
    }

    private async Task RunJobGuardedAsync(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _jobCancellation[jobId] = cts;
        var started = Stopwatch.StartNew();
        try
        {
            // Close the race between the dispatcher's Pending check and CTS
            // registration: a CancelAsync call in that window marks the job
            // cancelled without a token to signal.
            if (cts.IsCancellationRequested || job.State != JobState.Pending)
            {
                job.State = _shuttingDown ? JobState.Interrupted : JobState.Cancelled;
                job.CompletedAt = DateTimeOffset.Now;
                if (_shuttingDown)
                    job.Error = "The application closed before the job completed.";
                else if (string.IsNullOrEmpty(job.Error))
                    job.Error = "Cancelled by the user.";
                foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
                {
                    item.State = JobItemState.Cancelled;
                    item.Progress = 1;
                    item.CompletedAt ??= DateTimeOffset.Now;
                    item.Message = job.Error;
                }

                await TrySaveJobAsync(job);
                RaiseJobChanged(job, JobChangeKind.Updated);
                RaiseQueueChanged();
                return;
            }

            job.State = JobState.Preparing;
            job.StartedAt = DateTimeOffset.Now;
            job.Error = null;
            job.ErrorDetails = null;
            await TrySaveJobAsync(job);
            cts.Token.ThrowIfCancellationRequested();
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();

            var engine = _engines.FirstOrDefault(e => e.Id == job.EngineId)
                ?? throw new InvalidOperationException($"Processing engine {job.EngineId} is not registered.");

            List<JobItem> items;
            lock (job)
            {
                items = job.Items.OrderBy(i => i.SortOrder)
                    .Where(i => i.State == JobItemState.Pending)
                    .ToList();
            }

            if (items.Count == 0)
            {
                job.State = JobState.Cancelled;
                job.CompletedAt = DateTimeOffset.Now;
                job.Error = "No items remained in the queue.";
                await TrySaveJobAsync(job);
                RaiseJobChanged(job, JobChangeKind.Updated);
                RaiseQueueChanged();
                return;
            }

            // AI upscaling is inference-bound. Running two items at once lets
            // one item decode/encode on the CPU while the other uses the GPU;
            // the upscaling engine still serializes access to the DirectML
            // session, so this does not create competing GPU workloads.
            var parallelism = job.Operation == OperationKind.UpscaleAi ? 2 : 1;
            if (job.Operation == OperationKind.UpscaleAi &&
                int.TryParse(Environment.GetEnvironmentVariable("PIXELFORGE_AI_ITEM_PARALLELISM"), out var requested))
                parallelism = Math.Clamp(requested, 1, 2);
            if (parallelism == 1)
            {
                foreach (var item in items)
                {
                    if (cts.IsCancellationRequested) break;
                    using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    _itemCancellation[item.Id] = itemCts;
                    try
                    {
                        await ProcessItemAsync(job, item, engine, itemCts.Token, cts.Token);
                    }
                    finally
                    {
                        _itemCancellation.TryRemove(item.Id, out _);
                    }
                }
            }
            else
            {
                using var itemGate = new SemaphoreSlim(parallelism, parallelism);
                var tasks = items.Select(async item =>
                {
                    await itemGate.WaitAsync(cts.Token);
                    using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    _itemCancellation[item.Id] = itemCts;
                    try
                    {
                        if (!cts.IsCancellationRequested && item.State == JobItemState.Pending)
                            await ProcessItemAsync(job, item, engine, itemCts.Token, cts.Token);
                    }
                    finally
                    {
                        _itemCancellation.TryRemove(item.Id, out _);
                        itemGate.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }

            started.Stop();
            job.CompletedAt = DateTimeOffset.Now;
            if (cts.IsCancellationRequested)
            {
                job.State = _shuttingDown ? JobState.Interrupted : JobState.Cancelled;
                job.Error = _shuttingDown
                    ? "The application closed before the job completed."
                    : "Cancelled by the user.";
                foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
                {
                    item.State = JobItemState.Cancelled;
                    item.Progress = 1;
                    item.CompletedAt ??= DateTimeOffset.Now;
                    item.Message = job.Error;
                }
            }
            else if (job.FailedItems > 0)
            {
                job.State = JobState.Failed;
                job.Error = $"{job.FailedItems} of {job.TotalItems} item(s) failed.";
                job.BatchSummary = BuildBatchSummary(job);
            }
            else if (job.CompletedItems == 0 && job.SkippedItems == 0 &&
                     job.CancelledItems == job.TotalItems && job.TotalItems > 0)
            {
                job.State = JobState.Cancelled;
                job.Error = "All items were cancelled.";
                job.BatchSummary = BuildBatchSummary(job);
            }
            else
            {
                job.State = JobState.Completed;
                job.BatchSummary = BuildBatchSummary(job);
            }

            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
            _logger.LogInformation("Job {JobId} {State} in {Duration:0.0}s: {Summary}",
                job.Id, job.State, started.Elapsed.TotalSeconds, job.BatchSummary ?? job.Error ?? "done");
        }
        catch (OperationCanceledException)
        {
            job.State = _shuttingDown ? JobState.Interrupted : JobState.Cancelled;
            job.CompletedAt = DateTimeOffset.Now;
            job.Error = _shuttingDown
                ? "The application closed before the job completed."
                : "Cancelled by the user.";
            foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
            {
                item.State = JobItemState.Cancelled;
                item.Progress = 1;
                item.CompletedAt ??= DateTimeOffset.Now;
                item.Message = job.Error;
            }

            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed unexpectedly", jobId);
            job.State = JobState.Failed;
            job.CompletedAt = DateTimeOffset.Now;
            job.Error = "The job failed unexpectedly. See the log for technical details.";
            job.ErrorDetails = ex.ToString();
            foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Running))
            {
                item.State = JobItemState.Failed;
                item.Error = job.Error;
                item.ErrorDetails = ex.ToString();
                item.CompletedAt ??= DateTimeOffset.Now;
            }
            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
        }
        finally
        {
            started.Stop();
            _jobCancellation.TryRemove(jobId, out _);
            if (_settings.Settings.Processing.CleanupTempOnExit) CleanupJobTempFiles(job);
            if (_settings.Settings.Storage.SaveJobHistory) TrimHistoryInBackground();
        }
    }

    private async Task ProcessItemAsync(ProcessingJob job, JobItem item, IProcessingEngine engine,
        CancellationToken cancellationToken, CancellationToken jobToken)
    {
        item.State = JobItemState.Running;
        item.Progress = 0;
        item.StartedAt = DateTimeOffset.Now;
        item.CompletedAt = null;
        item.Error = null;
        item.ErrorDetails = null;
        item.AdditionalOutputPaths.Clear();
        item.Message = "Preparing…";
        item.Attempt++;
        RaiseJobChanged(job, JobChangeKind.Updated);

        var itemStopwatch = Stopwatch.StartNew();
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectoryOutput = job.Settings is ArchiveConversionSettings { Action: ArchiveAction.Extract };
            var resolution = ResolveOutputFormat(job, item);
            if (resolution.Error is not null)
                throw new InvalidOperationException(resolution.Error);
            cancellationToken.ThrowIfCancellationRequested();
            var outputFormatId = resolution.FormatId
                ?? throw new InvalidOperationException("The output format could not be resolved.");
            var outputExtension = isDirectoryOutput
                ? ""
                : resolution.Extension ?? throw new InvalidOperationException("The output extension could not be resolved.");

            string finalPath;
            if (isDirectoryOutput)
            {
                var suffix = _fileNames.BuildDefaultSuffix(job.Settings);
                var baseName = _fileNames.SanitizeFileName(Path.GetFileNameWithoutExtension(
                    item.PreferredOutputName ?? item.InputPath)) + suffix;
                var parent = job.OutputDirectory;
                if (!string.IsNullOrWhiteSpace(item.RelativeDirectory))
                {
                    var invalid = Path.GetInvalidFileNameChars();
                    var parts = item.RelativeDirectory
                        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                        .Where(part => part is not "." and not "..")
                        .Select(part => new string(part
                            .Where(ch => ch >= 32 && Array.IndexOf(invalid, ch) < 0).ToArray()).Trim())
                        .Where(part => part.Length > 0)
                        .ToArray();
                    if (parts.Length > 0)
                        parent = Path.Combine(job.OutputDirectory, Path.Combine(parts));
                }

                if (!_outputPaths.IsPathInside(parent, job.OutputDirectory))
                    parent = job.OutputDirectory;

                var desired = Path.Combine(parent, baseName);
                if (!_outputPaths.IsPathInside(desired, job.OutputDirectory))
                    desired = Path.Combine(job.OutputDirectory, baseName);
                string? directoryNote = null;
                if (job.Settings.CollisionPolicy == CollisionPolicy.Skip && Directory.Exists(desired))
                {
                    item.State = JobItemState.Skipped;
                    item.Progress = 1;
                    item.SkipReason = "The output folder already exists.";
                    item.Message = item.SkipReason;
                    item.CompletedAt = DateTimeOffset.Now;
                    item.Duration = itemStopwatch.Elapsed;
                    await TrySaveJobAsync(job);
                    RaiseJobChanged(job, JobChangeKind.Updated);
                    return;
                }

                finalPath = EnsureUniqueDirectory(desired);
                if (job.Settings.CollisionPolicy == CollisionPolicy.Overwrite)
                    directoryNote = "Existing folders are never deleted; a unique folder name was used.";
                if (!string.IsNullOrWhiteSpace(directoryNote)) item.Message = directoryNote;
            }
            else
            {
                if (job.Operation == OperationKind.OcrDocument && job.Settings is OcrSettings ocr)
                {
                    var documentName = string.IsNullOrWhiteSpace(ocr.DocumentName)
                        ? item.PreferredOutputName ?? Path.GetFileNameWithoutExtension(item.InputPath)
                        : ocr.DocumentName;
                    var baseName = _fileNames.SanitizeFileName(documentName);
                    finalPath = Path.Combine(job.OutputDirectory, baseName + ".docx");
                }
                else
                {
                    finalPath = _outputPaths.BuildOutputPath(item.InputPath, job.OutputDirectory, outputExtension,
                        _fileNames.BuildDefaultSuffix(job.Settings), item.RelativeDirectory, job.Settings.CollisionPolicy,
                        out var adjustmentNote, item.PreferredOutputName);
                    if (!string.IsNullOrWhiteSpace(adjustmentNote)) item.Message = adjustmentNote;
                }

                if (job.Settings.CollisionPolicy == CollisionPolicy.Skip && File.Exists(finalPath))
                {
                    item.State = JobItemState.Skipped;
                    item.Progress = 1;
                    item.SkipReason = "The output file already exists.";
                    item.Message = item.SkipReason;
                    item.CompletedAt = DateTimeOffset.Now;
                    item.Duration = itemStopwatch.Elapsed;
                    await TrySaveJobAsync(job);
                    RaiseJobChanged(job, JobChangeKind.Updated);
                    return;
                }
            }

            var outputDirectory = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException("The output path has no directory.");
            Directory.CreateDirectory(outputDirectory);
            tempPath = isDirectoryOutput ? BuildTempDirectoryPath(outputDirectory, item.Id) : BuildTempPath(finalPath, item.Id);
            item.TempPath = tempPath;

            var request = new ProcessingRequest
            {
                JobId = job.Id,
                ItemId = item.Id,
                Operation = job.Operation,
                Settings = job.Settings,
                InputPath = item.InputPath,
                TempOutputPath = tempPath,
                OutputFormatId = outputFormatId,
                TempDirectory = outputDirectory,
                InputInfo = item.InputInfo,
                RelativeDirectory = item.RelativeDirectory
            };

            var lastUiUpdate = 0L;
            var lastPersist = 0L;
            var progress = new InlineProgress<ProcessingProgress>(p =>
            {
                if (p.Fraction >= 0) item.Progress = Math.Clamp(p.Fraction, 0, 0.999);
                if (!string.IsNullOrWhiteSpace(p.Message)) item.Message = p.Message;
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastUiUpdate) > 120)
                {
                    Interlocked.Exchange(ref lastUiUpdate, now);
                    RaiseJobChanged(job, JobChangeKind.Updated);
                }

                if (now - Interlocked.Read(ref lastPersist) > 2000)
                {
                    Interlocked.Exchange(ref lastPersist, now);
                    _ = PersistItemAsync(job, item);
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            job.State = JobState.Running;
            item.Message = "Processing…";
            RaiseJobChanged(job, JobChangeKind.Updated);
            RaiseQueueChanged();
            var result = await engine.ProcessAsync(request, progress, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            if (!result.Success)
            {
                item.State = JobItemState.Failed;
                item.Error = result.Error ?? "The engine reported a failure.";
                item.ErrorDetails = result.ErrorDetails ?? result.EngineLog;
                item.ErrorCategory = result.ErrorCategory;
                item.EngineLog = result.EngineLog;
                item.RequestedProvider = result.RequestedProvider;
                item.ActualProvider = result.ActualProvider;
                item.FallbackReason = result.FallbackReason;
                item.RuntimeVersion = result.RuntimeVersion;
                item.ModelId = result.ModelId;
                item.CompletedAt = DateTimeOffset.Now;
                item.Progress = 1;
                item.Message = "Failed";
                item.Duration = itemStopwatch.Elapsed;
                itemStopwatch.Stop();
                CleanupTemp(tempPath);
                AddHistory(job, item, JobItemState.Failed);
                await TrySaveJobAsync(job);
                RaiseJobChanged(job, JobChangeKind.Updated);
                return;
            }

            if (isDirectoryOutput)
            {
                if (!Directory.Exists(tempPath) ||
                    !Directory.EnumerateFiles(tempPath, "*", SearchOption.AllDirectories).Any())
                {
                    item.State = JobItemState.Failed;
                    item.Error = "The engine finished but produced no extracted files.";
                    item.CompletedAt = DateTimeOffset.Now;
                    item.Progress = 1;
                    item.Message = "Failed";
                    item.Duration = itemStopwatch.Elapsed;
                    itemStopwatch.Stop();
                    CleanupTemp(tempPath);
                    AddHistory(job, item, JobItemState.Failed);
                    await TrySaveJobAsync(job);
                    RaiseJobChanged(job, JobChangeKind.Updated);
                    return;
                }
            }
            else if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                item.State = JobItemState.Failed;
                item.Error = "The engine finished but produced no output file.";
                item.CompletedAt = DateTimeOffset.Now;
                item.Progress = 1;
                item.Message = "Failed";
                item.Duration = itemStopwatch.Elapsed;
                itemStopwatch.Stop();
                CleanupTemp(tempPath);
                AddHistory(job, item, JobItemState.Failed);
                await TrySaveJobAsync(job);
                RaiseJobChanged(job, JobChangeKind.Updated);
                return;
            }

            item.Message = "Verifying output…";
            var verification = result.Verification ?? await engine.VerifyOutputAsync(request, tempPath, cancellationToken);
            var outputCategory = job.Operation switch
            {
                OperationKind.AudioConvert or OperationKind.AudioProcess => MediaCategory.Audio,
                OperationKind.VideoConvert => MediaCategory.Video,
                OperationKind.VideoUpscale => MediaCategory.Video,
                OperationKind.DocumentConvert => MediaCategory.Document,
                OperationKind.OcrDocument => MediaCategory.Document,
                OperationKind.EbookConvert => MediaCategory.Ebook,
                OperationKind.ArchiveConvert => MediaCategory.Archive,
                OperationKind.VideoProcess when job.Settings is VideoToolSettings { Tool: VideoToolOperation.Thumbnail } => MediaCategory.Image,
                OperationKind.VideoProcess when job.Settings is VideoToolSettings { Tool: VideoToolOperation.ExtractAudio } => MediaCategory.Audio,
                OperationKind.VideoProcess when job.Settings is VideoToolSettings { Tool: VideoToolOperation.Subtitles, SubtitleMode: SubtitleToolMode.Extract } => MediaCategory.Document,
                OperationKind.VideoProcess => MediaCategory.Video,
                _ => MediaCategory.Image
            };
            MediaInfo inspected;
            if (isDirectoryOutput)
                inspected = result.OutputInfo ?? verification.ActualInfo
                    ?? throw new InvalidOperationException("The extraction engine did not describe its folder output.");
            else if (job.Operation == OperationKind.VideoProcess &&
                     job.Settings is VideoToolSettings { Tool: VideoToolOperation.Subtitles, SubtitleMode: SubtitleToolMode.Extract or SubtitleToolMode.Convert } &&
                     result.OutputInfo is not null)
                inspected = result.OutputInfo;
            else
                inspected = await _inspector.InspectAsync(tempPath, outputCategory, cancellationToken);
            var verificationOk = verification.IsValid && inspected.IsReadable &&
                                 (!isDirectoryOutput || Directory.Exists(tempPath));
            if (!verificationOk)
            {
                item.State = JobItemState.Failed;
                item.Error = verification.IsValid
                    ? $"The output could not be reopened: {inspected.Error}"
                    : verification.Summary;
                item.ErrorDetails = verification.Details;
                item.CompletedAt = DateTimeOffset.Now;
                item.Progress = 1;
                item.Message = "Verification failed";
                item.Duration = itemStopwatch.Elapsed;
                itemStopwatch.Stop();
                CleanupTemp(tempPath);
                AddHistory(job, item, JobItemState.Failed);
                await TrySaveJobAsync(job);
                RaiseJobChanged(job, JobChangeKind.Updated);
                return;
            }

            if (isDirectoryOutput)
            {
                if (Directory.Exists(finalPath))
                {
                    if (job.Settings.CollisionPolicy == CollisionPolicy.Skip)
                    {
                        item.State = JobItemState.Skipped;
                        item.Progress = 1;
                        item.SkipReason = "The output folder appeared while the job was running.";
                        item.Message = item.SkipReason;
                        item.CompletedAt = DateTimeOffset.Now;
                        item.Duration = itemStopwatch.Elapsed;
                        itemStopwatch.Stop();
                        CleanupTemp(tempPath);
                        await TrySaveJobAsync(job);
                        RaiseJobChanged(job, JobChangeKind.Updated);
                        return;
                    }

                    finalPath = EnsureUniqueDirectory(finalPath);
                }

                Directory.Move(tempPath, finalPath);
            }
            else
            {
                if (File.Exists(finalPath))
                {
                    if (job.Settings.CollisionPolicy == CollisionPolicy.Skip)
                    {
                        item.State = JobItemState.Skipped;
                        item.Progress = 1;
                        item.SkipReason = "The output file appeared while the job was running.";
                        item.Message = item.SkipReason;
                        item.CompletedAt = DateTimeOffset.Now;
                        item.Duration = itemStopwatch.Elapsed;
                        itemStopwatch.Stop();
                        CleanupTemp(tempPath);
                        await TrySaveJobAsync(job);
                        RaiseJobChanged(job, JobChangeKind.Updated);
                        return;
                    }

                    if (job.Settings.CollisionPolicy == CollisionPolicy.UniqueName)
                    {
                        finalPath = _outputPaths.EnsureUniquePath(finalPath);
                    }
                }

                File.Move(tempPath, finalPath, overwrite: job.Settings.CollisionPolicy == CollisionPolicy.Overwrite);
            }

            var additionalWarnings = new List<string>();
            foreach (var additional in result.AdditionalOutputPaths)
            {
                if (!File.Exists(additional)) continue;
                var extension = Path.GetExtension(additional);
                var desired = Path.ChangeExtension(finalPath, extension);
                if (File.Exists(desired) && job.Settings.CollisionPolicy != CollisionPolicy.Overwrite)
                    desired = _outputPaths.EnsureUniquePath(desired);
                try
                {
                    File.Move(additional, desired, overwrite: job.Settings.CollisionPolicy == CollisionPolicy.Overwrite);
                    item.AdditionalOutputPaths.Add(desired);
                    additionalWarnings.Add("Also generated: " + desired);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not commit additional output {Path}", additional);
                    additionalWarnings.Add("An additional output could not be saved: " + ex.Message);
                    CleanupTemp(additional);
                }
            }

            tempPath = null;
            item.TempPath = null;
            itemStopwatch.Stop();

            var outputBytes = isDirectoryOutput ? DirectoryBytes(finalPath) : new FileInfo(finalPath).Length;
            item.State = JobItemState.Completed;
            item.Progress = 1;
            item.OutputPath = finalPath;
            item.OutputBytes = outputBytes;
            item.OutputInfo = inspected with
            {
                FilePath = finalPath,
                FileName = Path.GetFileName(finalPath),
                FileSizeBytes = outputBytes
            };
            item.OutputFormatId = outputFormatId;
            item.VerificationSummary = verification.Summary;
            item.Warnings = result.Warnings.Concat(additionalWarnings).ToList();
            item.EngineLog = result.EngineLog;
            item.RequestedProvider = result.RequestedProvider;
            item.ActualProvider = result.ActualProvider;
            item.FallbackReason = result.FallbackReason;
            item.RuntimeVersion = result.RuntimeVersion;
            item.ModelId = result.ModelId;
            item.CompletedAt = DateTimeOffset.Now;
            item.Duration = itemStopwatch.Elapsed;
            item.Message = "Done";
            AddHistory(job, item, JobItemState.Completed);
            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
        }
        catch (OperationCanceledException)
        {
            itemStopwatch.Stop();
            item.State = JobItemState.Cancelled;
            item.Progress = 1;
            item.Message = _shuttingDown
                ? "Interrupted by application shutdown"
                : jobToken.IsCancellationRequested ? "Cancelled by the user" : "Cancelled";
            item.CompletedAt = DateTimeOffset.Now;
            item.Duration = itemStopwatch.Elapsed;
            CleanupTemp(tempPath);
            AddHistory(job, item, JobItemState.Cancelled);
            await TrySaveItemQuietAsync(job, item);
            RaiseJobChanged(job, JobChangeKind.Updated);
            if (jobToken.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            itemStopwatch.Stop();
            _logger.LogError(ex, "Item {Item} ({File}) failed in job {JobId}", item.Id, item.FileName, job.Id);
            item.State = JobItemState.Failed;
            item.Progress = 1;
            item.Error = ex.Message;
            item.ErrorDetails = ex.ToString();
            item.Message = "Failed";
            item.CompletedAt = DateTimeOffset.Now;
            item.Duration = itemStopwatch.Elapsed;
            CleanupTemp(tempPath);
            AddHistory(job, item, JobItemState.Failed);
            await TrySaveJobAsync(job);
            RaiseJobChanged(job, JobChangeKind.Updated);
        }
    }

    private (string? FormatId, string? Extension, string? Error) ResolveOutputFormat(ProcessingJob job, JobItem item)
    {
        switch (job.Settings)
        {
            case ImageConversionSettings image:
            {
                var format = _registry.FindFormat(image.OutputFormatId, MediaCategory.Image);
                return format is null || !format.CanWrite
                    ? (null, null, $"The selected image format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case AudioConversionSettings audio:
            {
                var format = _registry.FindFormat(audio.OutputFormatId, MediaCategory.Audio);
                return format is null || !format.CanWrite
                    ? (null, null, $"The selected audio format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case VideoConversionSettings video:
            {
                var format = _registry.FindFormat(video.ContainerFormatId, MediaCategory.Video);
                return format is null || !format.CanWrite
                    ? (null, null, $"The selected video container is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case VideoUpscaleSettings videoUpscale:
            {
                var format = _registry.FindFormat(videoUpscale.ContainerFormatId, MediaCategory.Video);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected video container is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case OcrSettings:
                return ("docx", ".docx", null);
            case UpscaleSettings upscale:
            {
                if (!string.Equals(upscale.OutputFormatId, FormatIds.Auto, StringComparison.OrdinalIgnoreCase))
                {
                    var chosen = _registry.FindFormat(upscale.OutputFormatId, MediaCategory.Image);
                    return chosen is null || !chosen.CanWrite
                        ? (null, null, "The selected output image format is not available.")
                        : (chosen.Id, chosen.NormalizedExtension, null);
                }

                var actual = item.InputInfo?.FormatId;
                var format = actual is null ? null : _registry.FindFormat(actual, MediaCategory.Image);
                if (format is { CanWrite: true }) return (format.Id, format.NormalizedExtension, null);
                return ("png", ".png", null);
            }
            case DocumentConversionSettings document:
            {
                var format = _registry.FindFormat(document.OutputFormatId, MediaCategory.Document);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected document format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case EbookConversionSettings ebook:
            {
                var format = _registry.FindFormat(ebook.OutputFormatId, MediaCategory.Ebook);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected e-book format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case ArchiveConversionSettings archive:
            {
                if (archive.Action == ArchiveAction.Extract)
                    return ("folder", "", null);
                var format = _registry.FindFormat(archive.OutputFormatId, MediaCategory.Archive);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected archive format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case AudioToolSettings audioTool:
            {
                var format = _registry.FindFormat(audioTool.OutputFormatId, MediaCategory.Audio);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected audio output format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case ImageToolSettings imageTool:
            {
                var format = _registry.FindFormat(imageTool.OutputFormatId, MediaCategory.Image);
                return format is null || !format.CanWrite
                    ? (null, null, "The selected image output format is not available.")
                    : (format.Id, format.NormalizedExtension, null);
            }
            case VideoToolSettings videoTool:
            {
                if (videoTool.Tool == VideoToolOperation.Thumbnail)
                {
                    var format = _registry.FindFormat(videoTool.ThumbnailFormatId, MediaCategory.Image);
                    return format is null || !format.CanWrite
                        ? (null, null, "The selected thumbnail format is not available.")
                        : (format.Id, format.NormalizedExtension, null);
                }
                if (videoTool.Tool == VideoToolOperation.ExtractAudio)
                {
                    var format = _registry.FindFormat(videoTool.ExtractAudioFormatId, MediaCategory.Audio);
                    return format is null || !format.CanWrite
                        ? (null, null, "The selected audio extraction format is not available.")
                        : (format.Id, format.NormalizedExtension, null);
                }
                if (videoTool.Tool == VideoToolOperation.Subtitles && videoTool.SubtitleMode == SubtitleToolMode.Extract)
                    return (videoTool.SubtitleFormatId, "." + videoTool.SubtitleFormatId, null);
                var container = videoTool.Tool == VideoToolOperation.Gif
                    ? "gif"
                    : videoTool.ContainerFormatId;
                var videoFormat = _registry.FindFormat(container, MediaCategory.Video);
                return videoFormat is null || !videoFormat.CanWrite
                    ? (null, null, "The selected video container is not available.")
                    : (videoFormat.Id, videoFormat.NormalizedExtension, null);
            }
            default:
                return (null, null, "Unsupported job settings.");
        }
    }

    private static string BuildTempPath(string finalPath, Guid itemId)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? Path.GetTempPath();
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $".pixelforge-{itemId:N}{extension}");
    }

    private static string BuildTempDirectoryPath(string outputDirectory, Guid itemId) =>
        Path.Combine(outputDirectory, $".pixelforge-{itemId:N}.tmp");

    private static string EnsureUniqueDirectory(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("Could not find a unique folder name for the extracted archive.");
    }

    private static long DirectoryBytes(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }

        return total;
    }

    private void AddHistory(ProcessingJob job, JobItem item, JobItemState status)
    {
        if (!_settings.Settings.Storage.SaveJobHistory) return;
        try
        {
            var entry = new HistoryEntry
            {
                JobId = job.Id,
                ItemId = item.Id,
                Operation = job.Operation,
                Title = job.Title,
                InputPath = item.InputPath,
                OutputPath = item.OutputPath,
                InputFileName = Path.GetFileName(item.InputPath),
                OutputFileName = item.OutputPath is null ? null : Path.GetFileName(item.OutputPath),
                InputBytes = item.InputBytes,
                OutputBytes = item.OutputBytes,
                FormatId = item.OutputFormatId,
                SettingsJson = _settingsSerializer.Serialize(job.Settings),
                DurationMs = item.Duration?.TotalMilliseconds ?? 0,
                Status = status,
                Error = item.Error ?? item.SkipReason,
                ErrorDetails = item.ErrorDetails,
                ErrorCategory = item.ErrorCategory,
                EngineLog = item.EngineLog,
                WarningsJson = item.Warnings.Count == 0 ? null : JsonSerializer.Serialize(item.Warnings),
                RequestedProvider = item.RequestedProvider,
                ActualProvider = item.ActualProvider,
                FallbackReason = item.FallbackReason,
                RuntimeVersion = item.RuntimeVersion,
                ModelId = item.ModelId,
                CreatedAt = DateTimeOffset.Now,
                VerificationSummary = item.VerificationSummary,
                PipelineId = job.PipelineId,
                PipelineName = job.PipelineName,
                PipelineStep = job.PipelineStep,
                PipelineStepCount = job.PipelineStepCount
            };
            _ = _historyRepository.AddAsync(entry).ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "Could not write history entry");
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prepare history entry");
        }
    }

    private async Task PersistItemAsync(ProcessingJob job, JobItem item)
    {
        try
        {
            await _jobRepository.SaveItemAsync(job, item);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist job progress");
        }
    }

    private async Task TrySaveJobAsync(ProcessingJob job)
    {
        try
        {
            await _jobRepository.SaveAsync(job);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist job {JobId}", job.Id);
        }
    }

    private async Task TrySaveItemQuietAsync(ProcessingJob job, JobItem item)
    {
        try
        {
            await _jobRepository.SaveItemAsync(job, item);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist item {ItemId}", item.Id);
        }
    }

    private void RaiseJobChanged(ProcessingJob job, JobChangeKind kind)
    {
        try
        {
            JobChanged?.Invoke(this, new JobChangedEventArgs { Job = job, Kind = kind });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A job-changed handler threw an exception");
        }
    }

    private void RaiseQueueChanged()
    {
        try
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A queue-changed handler threw an exception");
        }
    }

    private static string BuildBatchSummary(ProcessingJob job)
    {
        var completed = job.CompletedItems;
        var failed = job.FailedItems;
        var skipped = job.SkippedItems;
        var cancelled = job.CancelledItems;
        var parts = new List<string> { $"{completed} completed" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        return string.Join(" · ", parts);
    }

    private static string BuildTitle(JobRequest request)
    {
        var count = request.Inputs.Count;
        var noun = count == 1 ? "file" : "files";
        return request.Operation switch
        {
            OperationKind.ImageConvert when request.Settings is ImageConversionSettings image =>
                $"Convert {count} image{(count == 1 ? "" : "s")} to {image.OutputFormatId.ToUpperInvariant()}",
            OperationKind.AudioConvert when request.Settings is AudioConversionSettings audio =>
                $"Convert {count} audio {noun} to {audio.OutputFormatId.ToUpperInvariant()}",
            OperationKind.VideoConvert when request.Settings is VideoConversionSettings video =>
                $"Convert {count} video{(count == 1 ? "" : "s")} to {video.ContainerFormatId.ToUpperInvariant()}",
            OperationKind.UpscaleAi when request.Settings is UpscaleSettings upscale =>
                $"AI upscale {count} image{(count == 1 ? "" : "s")} · {upscale.ScaleFactor}×",
            OperationKind.UpscaleTraditional when request.Settings is UpscaleSettings upscale =>
                $"Upscale {count} image{(count == 1 ? "" : "s")} · {upscale.ScaleFactor}×",
            OperationKind.VideoUpscale when request.Settings is VideoUpscaleSettings videoUpscale =>
                $"Video upscale {count} video{(count == 1 ? "" : "s")}" +
                (videoUpscale.EnableSuperResolution ? $" · {videoUpscale.ScaleFactor}×" : "") +
                (videoUpscale.EnableInterpolation ? $" · {videoUpscale.FrameRateMultiplier}× fps" : ""),
            OperationKind.OcrDocument when request.Settings is OcrSettings ocr =>
                $"OCR · {ocr.DocumentName}",
            _ => $"Process {count} {noun}"
        };
    }

    private static string GetEngineId(OperationKind operation) => operation switch
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

    private void CleanupTemp(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        try
        {
            if (Directory.Exists(tempPath) && !_settings.Settings.Processing.KeepTempOnFailure)
                Directory.Delete(tempPath, recursive: true);
            else if (File.Exists(tempPath) && !_settings.Settings.Processing.KeepTempOnFailure)
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete temporary file {Path}", tempPath);
        }
    }

    private void CleanupJobTempFiles(ProcessingJob job)
    {
        foreach (var item in job.Items)
        {
            if (item.State is JobItemState.Completed) continue;
            if (!string.IsNullOrWhiteSpace(item.TempPath)) CleanupTemp(item.TempPath);
        }
    }

    private void TrimHistoryInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var storage = _settings.Settings.Storage;
                if (storage.HistoryRetentionDays > 0)
                    await _historyRepository.DeleteOlderThanAsync(DateTimeOffset.Now.AddDays(-storage.HistoryRetentionDays));
                await _historyRepository.TrimAsync(storage.MaxHistoryEntries);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "History maintenance failed");
            }
        });
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public InlineProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}
