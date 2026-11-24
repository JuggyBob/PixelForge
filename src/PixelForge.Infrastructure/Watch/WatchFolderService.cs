using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Watch;

/// <summary>
/// Persistent watch folders that enqueue ordinary central-queue jobs. Polling is used
/// deliberately: it behaves consistently across network shares, survives restarts and
/// lets us wait for files to become stable before touching them.
/// </summary>
public sealed class WatchFolderService : IWatchFolderService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IJobService _jobs;
    private readonly IMediaInspector _inspector;
    private readonly IOutputPathService _outputPaths;
    private readonly IOperationSettingsSerializer _serializer;
    private readonly IAppPaths _paths;
    private readonly ILogger<WatchFolderService> _logger;
    private readonly object _rulesLock = new();
    private readonly List<WatchFolderRule> _rules = [];
    private readonly Dictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcessedFile> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, QueuedFile> _queued = new();
    private readonly object _queuedLock = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _initializeTask;
    private readonly object _initializeLock = new();
    private bool _initialized;
    private bool _disposed;

    public WatchFolderService(IJobService jobs, IMediaInspector inspector, IOutputPathService outputPaths,
        IOperationSettingsSerializer serializer, IAppPaths paths, ILogger<WatchFolderService> logger)
    {
        _jobs = jobs;
        _inspector = inspector;
        _outputPaths = outputPaths;
        _serializer = serializer;
        _paths = paths;
        _logger = logger;
        _jobs.JobChanged += OnJobChanged;
    }

    public IReadOnlyList<WatchFolderRule> Rules
    {
        get
        {
            lock (_rulesLock) return _rules.Select(r => r.Clone()).ToList();
        }
    }

    public event EventHandler? RulesChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_initializeLock)
        {
            if (_initialized) return _initializeTask ?? Task.CompletedTask;
            _initialized = true;
            _initializeTask = InitializeCoreAsync(cancellationToken);
            return _initializeTask;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public Task<IReadOnlyList<WatchFolderRule>> GetRulesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Rules);

    public async Task<WatchFolderRule> CreateAsync(WatchFolderRule rule, CancellationToken cancellationToken = default)
    {
        ValidateRule(rule);
        rule.Id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
        rule.CreatedAt = DateTimeOffset.Now;
        lock (_rulesLock)
        {
            if (_rules.Any(r => r.Id == rule.Id)) throw new InvalidOperationException("A watch rule with this ID already exists.");
            _rules.Add(rule.Clone());
        }

        await SaveRulesAsync(cancellationToken);
        RaiseChanged();
        return rule.Clone();
    }

    public async Task UpdateAsync(WatchFolderRule rule, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            ValidateRule(rule);
            var stateCleared = false;
            lock (_rulesLock)
            {
                var index = _rules.FindIndex(r => r.Id == rule.Id);
                if (index < 0) throw new InvalidOperationException("The watch rule was not found.");
                var previous = _rules[index];
                _rules[index] = rule.Clone();
                var identityChanged = previous.Operation != rule.Operation ||
                                      !string.Equals(previous.InputDirectory, rule.InputDirectory, StringComparison.OrdinalIgnoreCase) ||
                                      !string.Equals(previous.OutputDirectory, rule.OutputDirectory, StringComparison.OrdinalIgnoreCase);
                if (identityChanged || !rule.IgnorePreviouslyProcessed)
                {
                    var prefix = rule.Id.ToString("N") + "|";
                    foreach (var key in _pending.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        _pending.Remove(key);
                    foreach (var key in _processed.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        _processed.Remove(key);
                    _processedThisSession.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    stateCleared = true;
                }
            }

            await SaveRulesAsync(cancellationToken);
            if (stateCleared || !rule.IgnorePreviouslyProcessed) await SaveStateAsync(cancellationToken);
            RaiseChanged();
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_rulesLock)
        {
            _rules.RemoveAll(r => r.Id == id);
            var prefix = id.ToString("N") + "|";
            foreach (var key in _pending.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _pending.Remove(key);
            foreach (var key in _processed.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _processed.Remove(key);
            _processedThisSession.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        await SaveRulesAsync(cancellationToken);
        await SaveStateAsync(cancellationToken);
        RaiseChanged();
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_rulesLock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) throw new InvalidOperationException("The watch rule was not found.");
            rule.IsEnabled = enabled;
            if (!enabled) rule.LastError = null;
        }

        await SaveRulesAsync(cancellationToken);
        RaiseChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try { _loopCts?.Cancel(); } catch { }
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch { }
        }

        await SaveStateAsync(cancellationToken);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await ScanAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch folder loop stopped unexpectedly");
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!_jobs.IsStarted) return;
        if (!await _scanGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            List<WatchFolderRule> rules;
            lock (_rulesLock) rules = _rules.Where(r => r.IsEnabled).Select(r => r.Clone()).ToList();
            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ScanRuleAsync(rule, cancellationToken);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task ScanRuleAsync(WatchFolderRule rule, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rule.InputDirectory)) return;
        var extensions = GetInputExtensions(rule).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptsAnyFile = rule.Operation == OperationKind.ArchiveConvert &&
                             rule.Settings is ArchiveConversionSettings { Action: ArchiveAction.Create };
        IEnumerable<string> files;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = rule.IncludeSubdirectories,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            files = Directory.EnumerateFiles(rule.InputDirectory, "*", options);
        }
        catch (Exception ex)
        {
            await SetErrorAsync(rule, "The input folder could not be read: " + ex.Message, cancellationToken);
            return;
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!acceptsAnyFile && !extensions.Contains(Path.GetExtension(path))) continue;
            if (IsUnderOutput(rule, path)) continue;
            if (!IsStable(rule, path, out var signature)) continue;
            var key = ProcessedKey(rule.Id, path);
            if (_processed.TryGetValue(key, out var processed) && processed.Matches(signature))
            {
                if (rule.IgnorePreviouslyProcessed || _processedThisSession.Contains(key)) continue;
            }
            await TryProcessAsync(rule, path, signature, key, cancellationToken);
        }
    }

    private async Task TryProcessAsync(WatchFolderRule rule, string path, FileSignature signature, string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var categoryHint = CategoryHint(rule.Operation, rule.Settings);
            var info = await _inspector.InspectAsync(path, categoryHint, cancellationToken);
            if (!info.IsReadable || !Accepts(rule, info))
            {
                MarkProcessed(key, signature);
                await SetErrorAsync(rule, info.IsReadable
                    ? $"{Path.GetFileName(path)} does not match this rule's input types."
                    : $"{Path.GetFileName(path)}: {info.Error}", cancellationToken);
                return;
            }

            var settings = rule.Settings.Clone();
            settings.OutputDirectory = rule.OutputDirectory;
            settings.CollisionPolicy = CollisionPolicy.UniqueName;
            var relative = Path.GetRelativePath(rule.InputDirectory, Path.GetDirectoryName(path) ?? rule.InputDirectory);
            var request = new JobRequest
            {
                Operation = rule.Operation,
                Title = $"{rule.Name}: {Path.GetFileName(path)}",
                Settings = settings,
                Inputs =
                [
                    new JobInput
                    {
                        Path = path,
                        Info = info,
                        RelativeDirectory = rule.IncludeSubdirectories && relative != "." ? relative : null
                    }
                ],
                PreserveRelativePaths = rule.IncludeSubdirectories
            };
            var validation = await _jobs.ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                if (!IsTransientValidation(validation)) MarkProcessed(key, signature);
                await SetErrorAsync(rule, $"{Path.GetFileName(path)}: {validation.Summary}", cancellationToken);
                return;
            }

            var job = await _jobs.EnqueueAsync(request, cancellationToken);
            lock (_queuedLock) _queued[job.Id] = new QueuedFile(key, signature);
            if (job.IsTerminal)
                OnJobChanged(this, new JobChangedEventArgs { Job = job, Kind = JobChangeKind.Updated });
            lock (_rulesLock)
            {
                var stored = _rules.FirstOrDefault(r => r.Id == rule.Id);
                if (stored is not null)
                {
                    stored.LastTriggeredAt = DateTimeOffset.Now;
                    stored.LastError = null;
                }
            }

            _logger.LogInformation("Watch folder {Rule} queued {File} as job {JobId}", rule.Name, path, job.Id);
            await SaveRulesAsync(cancellationToken);
            await SaveStateAsync(cancellationToken);
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watch folder could not queue {File}", path);
            await SetErrorAsync(rule, ex.Message, cancellationToken);
        }
    }

    private static bool IsTransientValidation(ValidationResult validation) =>
        validation.Issues.Any(issue =>
            issue.Code.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            issue.Code.Contains("runtime", StringComparison.OrdinalIgnoreCase) ||
            issue.Code.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            issue.Code.Contains("download", StringComparison.OrdinalIgnoreCase) ||
            issue.Code is "video.upscale.scale" or "video.upscale.none" or "engine.unavailable" ||
            (issue.Details?.Contains("not installed", StringComparison.OrdinalIgnoreCase) ?? false));

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (e.Kind != JobChangeKind.Updated || !e.Job.IsTerminal) return;
        QueuedFile? queued;
        lock (_queuedLock)
        {
            if (!_queued.TryGetValue(e.Job.Id, out queued)) return;
            _queued.Remove(e.Job.Id);
        }

        if (e.Job.State != JobState.Completed || e.Job.FailedItems > 0 || e.Job.CancelledItems > 0)
            return;

        MarkProcessed(queued.Key, queued.Signature);
        _ = SaveStateAsync(CancellationToken.None);
    }

    private bool IsStable(WatchFolderRule rule, string path, out FileSignature signature)
    {
        signature = default;
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        var now = DateTime.UtcNow;
        var current = new FileSignature(info.Length, info.LastWriteTimeUtc);
        var key = rule.Id.ToString("N") + "|" + Path.GetFullPath(path);
        if (!_pending.TryGetValue(key, out var pending) || !pending.Signature.Equals(current))
        {
            _pending[key] = new PendingFile(current, now);
            return false;
        }

        if (now - pending.FirstSeen < TimeSpan.FromSeconds(2.5)) return false;
        if (!CanOpenExclusive(path)) return false;
        var after = new FileInfo(path);
        if (!after.Exists || after.Length != current.Length || after.LastWriteTimeUtc != current.LastWriteUtc) return false;
        _pending.Remove(key);
        signature = current;
        return true;
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length == new FileInfo(path).Length;
        }
        catch
        {
            return false;
        }
    }

    private bool IsUnderOutput(WatchFolderRule rule, string path)
    {
        try { return _outputPaths.IsPathInside(path, rule.OutputDirectory); }
        catch { return false; }
    }

    private void MarkProcessed(string key, FileSignature signature)
    {
        _processed[key] = new ProcessedFile(signature);
        _processedThisSession.Add(key);
        if (_processed.Count > 5000)
        {
            foreach (var oldest in _processed.Keys.Take(_processed.Count - 5000).ToList())
                _processed.Remove(oldest);
        }
    }

    private async Task SetErrorAsync(WatchFolderRule rule, string error, CancellationToken cancellationToken)
    {
        lock (_rulesLock)
        {
            var stored = _rules.FirstOrDefault(r => r.Id == rule.Id);
            if (stored is not null) stored.LastError = error;
        }

        await SaveRulesAsync(cancellationToken);
        RaiseChanged();
    }

    private static bool Accepts(WatchFolderRule rule, MediaInfo info) => rule.Operation switch
    {
        OperationKind.ImageConvert or OperationKind.UpscaleAi or OperationKind.UpscaleTraditional =>
            info.Category == MediaCategory.Image,
        OperationKind.AudioConvert => info.Category is MediaCategory.Audio or MediaCategory.Video,
        OperationKind.VideoConvert or OperationKind.VideoUpscale => info.Category == MediaCategory.Video,
        OperationKind.DocumentConvert => info.Category is MediaCategory.Document or MediaCategory.Ebook,
        OperationKind.EbookConvert => info.Category is MediaCategory.Document or MediaCategory.Ebook,
        OperationKind.ArchiveConvert => info.Category == MediaCategory.Archive || rule.Settings is ArchiveConversionSettings { Action: ArchiveAction.Create },
        OperationKind.AudioProcess => info.Category is MediaCategory.Audio or MediaCategory.Video,
        OperationKind.ImageProcess => info.Category == MediaCategory.Image,
        OperationKind.VideoProcess => info.Category == MediaCategory.Video,
        _ => false
    };

    private static MediaCategory CategoryHint(OperationKind operation, OperationSettings settings) => operation switch
    {
        OperationKind.ImageConvert or OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => MediaCategory.Image,
        OperationKind.AudioConvert => MediaCategory.Audio,
        OperationKind.VideoConvert or OperationKind.VideoUpscale => MediaCategory.Video,
        OperationKind.DocumentConvert => MediaCategory.Document,
        OperationKind.EbookConvert => MediaCategory.Ebook,
        OperationKind.ArchiveConvert => settings is ArchiveConversionSettings { Action: ArchiveAction.Create }
            ? MediaCategory.Unknown
            : MediaCategory.Archive,
        OperationKind.AudioProcess => MediaCategory.Audio,
        OperationKind.ImageProcess => MediaCategory.Image,
        OperationKind.VideoProcess => MediaCategory.Video,
        _ => MediaCategory.Unknown
    };

    private static IReadOnlyList<string> GetInputExtensions(WatchFolderRule rule) => rule.Operation switch
    {
        OperationKind.DocumentConvert =>
            ContentFormatCatalog.GetInputExtensions(MediaCategory.Document)
                .Concat(ContentFormatCatalog.GetInputExtensions(MediaCategory.Ebook))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        OperationKind.EbookConvert =>
            ContentFormatCatalog.GetInputExtensions(MediaCategory.Ebook)
                .Concat(ContentFormatCatalog.GetInputExtensions(MediaCategory.Document))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        OperationKind.AudioProcess =>
            FormatCatalog.GetInputExtensions(MediaCategory.Audio)
                .Concat(FormatCatalog.GetInputExtensions(MediaCategory.Video))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        _ => FormatCatalog.GetInputExtensions(CategoryHint(rule.Operation, rule.Settings))
    };

    private void ValidateRule(WatchFolderRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.InputDirectory) || !Directory.Exists(rule.InputDirectory))
            throw new InvalidOperationException("Choose an existing input folder.");
        if (string.IsNullOrWhiteSpace(rule.OutputDirectory))
            throw new InvalidOperationException("Choose an output folder.");
        var input = Path.GetFullPath(rule.InputDirectory);
        var output = Path.GetFullPath(rule.OutputDirectory);
        if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The watched input folder cannot be a symbolic link or junction.");
        ValidateRuleRelationship(rule, input, output);
        Directory.CreateDirectory(rule.OutputDirectory);
        if (rule.Settings.Operation != rule.Operation)
            throw new InvalidOperationException("The rule settings do not match the selected operation.");
    }

    /// <summary>
    /// Validation used when loading persisted rules. Rules for temporarily
    /// unavailable drives/folders are kept so they resume when the path
    /// returns; only structural and security invariants are enforced.
    /// </summary>
    private static void ValidateLoadedRule(WatchFolderRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.InputDirectory) ||
            string.IsNullOrWhiteSpace(rule.OutputDirectory))
            throw new InvalidOperationException("The watch rule is missing a folder.");
        var input = Path.GetFullPath(rule.InputDirectory);
        var output = Path.GetFullPath(rule.OutputDirectory);
        if (Directory.Exists(input) &&
            (File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The watched input folder cannot be a symbolic link or junction.");
        ValidateRuleRelationship(rule, input, output);
        if (rule.Settings.Operation != rule.Operation)
            throw new InvalidOperationException("The rule settings do not match the selected operation.");
    }

    private static void ValidateRuleRelationship(WatchFolderRule rule, string input, string output)
    {
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The input and output folders must be different to prevent processing loops.");
        if (IsPathInside(input, output) || IsPathInside(output, input))
            throw new InvalidOperationException("Input and output folders must not be nested inside each other.");
        if (string.IsNullOrWhiteSpace(rule.Name)) rule.Name = "Watched folder";
        rule.InputDirectory = input;
        rule.OutputDirectory = output;
    }

    private static bool IsPathInside(string child, string parent)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProcessedKey(Guid ruleId, string path) =>
        ruleId.ToString("N") + "|" + Path.GetFullPath(path);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var rulesPath = RulesPath;
        if (File.Exists(rulesPath))
        {
            try
            {
                var document = JsonSerializer.Deserialize<RulesDocument>(await File.ReadAllTextAsync(rulesPath, cancellationToken), JsonOptions);
                if (document?.Rules is not null)
                {
                    lock (_rulesLock)
                    {
                        foreach (var persisted in document.Rules)
                        {
                            try
                            {
                                var settings = _serializer.Deserialize(persisted.Operation, persisted.SettingsJson);
                                var rule = new WatchFolderRule
                                {
                                    Id = persisted.Id,
                                    Name = persisted.Name,
                                    InputDirectory = persisted.InputDirectory,
                                    OutputDirectory = persisted.OutputDirectory,
                                    IncludeSubdirectories = persisted.IncludeSubdirectories,
                                    IgnorePreviouslyProcessed = persisted.IgnorePreviouslyProcessed,
                                    IsEnabled = persisted.IsEnabled,
                                    Operation = persisted.Operation,
                                    Settings = settings,
                                    CreatedAt = persisted.CreatedAt,
                                    LastTriggeredAt = persisted.LastTriggeredAt,
                                    LastError = persisted.LastError
                                };
                                ValidateLoadedRule(rule);
                                _rules.Add(rule);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Could not load watch rule {Rule}", persisted.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load watch folders");
            }
        }

        _logger.LogInformation("Loaded {Count} watch rules", _rules.Count);

        var statePath = StatePath;
        if (File.Exists(statePath))
        {
            try
            {
                var state = JsonSerializer.Deserialize<StateDocument>(await File.ReadAllTextAsync(statePath, cancellationToken), JsonOptions);
                if (state?.Entries is not null)
                {
                    foreach (var entry in state.Entries)
                        _processed[entry.Key] = new ProcessedFile(new FileSignature(entry.Length, new DateTime(entry.LastWriteTicks, DateTimeKind.Utc)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load watch folder state");
            }
        }
    }

    private async Task SaveRulesAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            RulesDocument document;
            lock (_rulesLock)
            {
                document = new RulesDocument
                {
                    Rules = _rules.Select(rule => new PersistedRule
                    {
                        Id = rule.Id,
                        Name = rule.Name,
                        InputDirectory = rule.InputDirectory,
                        OutputDirectory = rule.OutputDirectory,
                        IncludeSubdirectories = rule.IncludeSubdirectories,
                        IgnorePreviouslyProcessed = rule.IgnorePreviouslyProcessed,
                        IsEnabled = rule.IsEnabled,
                        Operation = rule.Operation,
                        SettingsJson = _serializer.Serialize(rule.Settings),
                        CreatedAt = rule.CreatedAt,
                        LastTriggeredAt = rule.LastTriggeredAt,
                        LastError = rule.LastError
                    }).ToList()
                };
            }

            Directory.CreateDirectory(_paths.RootDirectory);
            var path = RulesPath;
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save watch folders");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            StateDocument document;
            lock (_rulesLock)
            {
                document = new StateDocument
                {
                    Entries = _processed.Select(entry => new StateEntry
                    {
                        Key = entry.Key,
                        Length = entry.Value.Signature.Length,
                        LastWriteTicks = entry.Value.Signature.LastWriteUtc.Ticks
                    }).ToList()
                };
            }

            Directory.CreateDirectory(_paths.RootDirectory);
            var path = StatePath;
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save watch folder state");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RaiseChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);

    private string RulesPath => Path.Combine(_paths.RootDirectory, "watch-folders.json");
    private string StatePath => Path.Combine(_paths.RootDirectory, "watch-folders-state.json");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _scanGate.Dispose();
        _saveGate.Dispose();
    }

    private readonly record struct FileSignature(long Length, DateTime LastWriteUtc);
    private sealed record PendingFile(FileSignature Signature, DateTime FirstSeen);
    private sealed record QueuedFile(string Key, FileSignature Signature);
    private sealed record ProcessedFile(FileSignature Signature)
    {
        public bool Matches(FileSignature signature) => Signature.Equals(signature);
    }

    private sealed class RulesDocument
    {
        public List<PersistedRule> Rules { get; set; } = [];
    }

    private sealed class PersistedRule
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string InputDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public bool IncludeSubdirectories { get; set; }
        public bool IgnorePreviouslyProcessed { get; set; } = true;
        public bool IsEnabled { get; set; }
        public OperationKind Operation { get; set; }
        public string SettingsJson { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? LastTriggeredAt { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class StateDocument
    {
        public List<StateEntry> Entries { get; set; } = [];
    }

    private sealed class StateEntry
    {
        public string Key { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteTicks { get; set; }
    }
}
