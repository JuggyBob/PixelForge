using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using Loc = PixelForge.Presentation.Localization.Loc;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class ConverterViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRegistry _registry;
    private readonly IMediaInspector _inspector;
    private readonly IFilePickerService _filePicker;
    private readonly IJobService _jobs;
    private readonly IThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly INavigationService _navigation;
    private readonly IAppLogger _logger;
    private readonly IFolderScanner _scanner;
    private readonly ConverterStagingSession _session;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _validationCts;
    private FolderScanResult? _lastScanResult;
    private int _lastScanDuplicates;
    private bool _initialized;
    private bool _updatingCategory;
    private bool _queueing;
    private bool _disposed;

    public ConverterViewModel(ICapabilityRegistry registry, IMediaInspector inspector, IFilePickerService filePicker,
        IJobService jobs, IThumbnailService thumbnails, IDialogService dialogs, INotificationService notifications,
        ISettingsService settings, IAppPaths paths, INavigationService navigation, IAppLogger logger,
        IFolderScanner scanner, ConverterStagingSession session)
    {
        _registry = registry;
        _inspector = inspector;
        _filePicker = filePicker;
        _jobs = jobs;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _notifications = notifications;
        _settings = settings;
        _paths = paths;
        _navigation = navigation;
        _logger = logger;
        _scanner = scanner;
        _session = session;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Image = session.Image;
        Audio = session.Audio;
        Video = session.Video;
        Documents = session.Documents;
        Ebooks = session.Ebooks;
        Archives = session.Archives;
        _session.Changed += OnStagingChanged;
        _registry.CapabilitiesChanged += OnCapabilitiesChanged;
        CurrentWorkspace = Image;
        IsImagesSelected = true;
    }

    public ImageWorkspaceViewModel Image { get; }
    public AudioWorkspaceViewModel Audio { get; }
    public VideoWorkspaceViewModel Video { get; }
    public DocumentWorkspaceViewModel Documents { get; }
    public EbookWorkspaceViewModel Ebooks { get; }
    public ArchiveWorkspaceViewModel Archives { get; }

    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        Image.RefreshLocalization();
        Audio.RefreshLocalization();
        Video.RefreshLocalization();
        Documents.RefreshLocalization();
        Ebooks.RefreshLocalization();
        Archives.RefreshLocalization();
        UpdateEngineStatus();
        UpdateSummary();
        if (!IsScanningFolder && _lastScanResult is not null)
        {
            var summary = LocalizationService.ScanSummary(_lastScanResult);
            if (_lastScanDuplicates > 0)
                summary += " · " + Loc.F("{0} already staged", _lastScanDuplicates);
            ScanSummary = summary;
        }
    }

    private void UpdateEngineStatus()
    {
        if (!_registry.IsInitialized)
            EngineStatus = Loc.T("Starting local engines…");
        else
            EngineStatus = Loc.T("Engines ready · local processing");
    }

    [ObservableProperty] private WorkspaceViewModel _currentWorkspace;
    [ObservableProperty] private MediaCategory _selectedCategory = MediaCategory.Image;
    [ObservableProperty] private bool _isImagesSelected = true;
    [ObservableProperty] private bool _isAudioSelected;
    [ObservableProperty] private bool _isVideoSelected;
    [ObservableProperty] private bool _isDocumentsSelected;
    [ObservableProperty] private bool _isEbooksSelected;
    [ObservableProperty] private bool _isArchivesSelected;
    [ObservableProperty] private bool _isEngineReady;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _canAddToQueue;
    [ObservableProperty] private bool _hasStagedFiles;
    [ObservableProperty] private string _engineStatus = "Starting local engines…";
    [ObservableProperty] private string _categorySummary = "";
    [ObservableProperty] private string _selectionHint = "Import files to begin";
    [ObservableProperty] private bool _isScanningFolder;
    [ObservableProperty] private bool _scanSubfolders = true;
    [ObservableProperty] private bool _hasScanSummary;
    [ObservableProperty] private bool _hasScanSkipped;
    [ObservableProperty] private string _scanSummary = "";

    public ObservableCollection<ScanSkipItem> ScanSkipped { get; } = [];

    public async Task InitializeAsync(MediaCategory? preferredCategory = null)
    {
        if (_initialized)
        {
            if (preferredCategory is { } existingCategory) SelectCategory(existingCategory);
            return;
        }
        _initialized = true;
        IsLoading = true;
        try
        {
            if (!_registry.IsInitialized) await _registry.InitializeAsync();
            LoadFormats();
            IsEngineReady = _registry.Engines.Any(e => e.EngineId == EngineIds.Images && e.IsAvailable);
            UpdateEngineStatus();
            if (preferredCategory is { } category) SelectCategory(category);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Converter initialization failed");
            EngineStatus = Loc.T("Engine initialization failed");
            await _dialogs.ShowErrorAsync("Converter unavailable",
                "The processing engines could not be initialized.", ex.ToString());
        }
        finally
        {
            IsLoading = false;
            UpdateCanQueue();
        }
    }

    private void LoadFormats(bool force = false)
    {
        if (_session.FormatsLoaded && !force) return;
        Image.LoadFormats(_settings.Settings.Converter.ImageFormatId);
        Audio.LoadFormats(_settings.Settings.Converter.AudioFormatId);
        Video.LoadFormats(_settings.Settings.Converter.VideoContainerId);
        Documents.LoadFormats("pdf");
        Ebooks.LoadFormats("epub");
        Archives.LoadFormats("zip");
        _session.FormatsLoaded = true;
    }

    public async Task AddFilesAsync(IReadOnlyList<string> paths, MediaCategory? preferredCategory = null)
    {
        if (paths.Count == 0) return;
        if (!_registry.IsInitialized) await InitializeAsync(preferredCategory);
        IsLoading = true;
        try
        {
            var distinct = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) return;
            var infos = await _inspector.InspectManyAsync(distinct);
            var unreadable = infos.Where(i => !i.IsReadable).ToList();
            if (unreadable.Count > 0)
            {
                await _dialogs.ShowErrorAsync("Some files could not be imported",
                    string.Join("\n", unreadable.Take(8).Select(i => $"{i.FileName}: {i.Error}")));
            }

            var readable = infos.Where(i => i.IsReadable).ToList();
            if (readable.Count == 0) return;
            var preferred = preferredCategory ?? SelectedCategory;
            var addedByCategory = new Dictionary<MediaCategory, int>();
            var skipped = new List<string>();
            foreach (var info in readable)
            {
                WorkspaceViewModel? workspace = preferred switch
                {
                    MediaCategory.Archive when Archives.Accepts(info) => Archives,
                    MediaCategory.Document when Documents.Accepts(info) => Documents,
                    MediaCategory.Ebook when Ebooks.Accepts(info) => Ebooks,
                    _ => info.Category switch
                    {
                        MediaCategory.Image => Image,
                        MediaCategory.Audio => Audio,
                        MediaCategory.Video when preferred == MediaCategory.Audio && info.AudioCodec is not null => Audio,
                        MediaCategory.Video => Video,
                        MediaCategory.Document => Documents,
                        MediaCategory.Ebook => Ebooks,
                        MediaCategory.Archive => Archives,
                        _ => null
                    }
                };
                if (workspace is null || !workspace.Accepts(info))
                {
                    skipped.Add(info.FileName);
                    continue;
                }

                var before = workspace.Files.Count;
                workspace.AddFile(info, _thumbnails);
                if (workspace.Files.Count > before)
                    addedByCategory[workspace.Category] = addedByCategory.GetValueOrDefault(workspace.Category) + 1;
            }

            if (addedByCategory.Count == 0)
            {
                await _dialogs.ShowMessageAsync("No compatible files",
                    skipped.Count > 0
                        ? "The selected files do not match this converter workspace. Try another category."
                        : "No new files were added.");
                return;
            }

            var target = preferred;
            if (!addedByCategory.ContainsKey(target))
                target = addedByCategory.Keys.First();
            if (addedByCategory.Count > 1)
            {
                _notifications.Notify("Files organised by type",
                    "Mixed files were placed in their matching converter workspaces.", NotificationSeverity.Info);
            }

            if (skipped.Count > 0)
            {
                _notifications.Notify("Some files were skipped",
                    LocalizationService.P("Converter.IncompatibleFiles", skipped.Count),
                    NotificationSeverity.Warning);
            }

            SelectCategory(target);
            UpdateSummary();
            ScheduleValidation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File import failed");
            await _dialogs.ShowErrorAsync("Files could not be imported", ex.Message);
        }
        finally
        {
            IsLoading = false;
            UpdateCanQueue();
        }
    }

    public void SelectCategory(MediaCategory category)
    {
        if (_updatingCategory) return;
        _updatingCategory = true;
        try
        {
            SelectedCategory = category;
            IsImagesSelected = category == MediaCategory.Image;
            IsAudioSelected = category == MediaCategory.Audio;
            IsVideoSelected = category == MediaCategory.Video;
            IsDocumentsSelected = category == MediaCategory.Document;
            IsEbooksSelected = category == MediaCategory.Ebook;
            IsArchivesSelected = category == MediaCategory.Archive;
            CurrentWorkspace = category switch
            {
                MediaCategory.Audio => Audio,
                MediaCategory.Video => Video,
                MediaCategory.Document => Documents,
                MediaCategory.Ebook => Ebooks,
                MediaCategory.Archive => Archives,
                _ => Image
            };
            UpdateSummary();
            ScheduleValidation();
        }
        finally
        {
            _updatingCategory = false;
        }
    }

    [RelayCommand]
    private void SelectImages() => SelectCategory(MediaCategory.Image);

    [RelayCommand]
    private void SelectAudio() => SelectCategory(MediaCategory.Audio);

    [RelayCommand]
    private void SelectVideo() => SelectCategory(MediaCategory.Video);

    [RelayCommand]
    private void SelectDocuments() => SelectCategory(MediaCategory.Document);

    [RelayCommand]
    private void SelectEbooks() => SelectCategory(MediaCategory.Ebook);

    [RelayCommand]
    private void SelectArchives() => SelectCategory(MediaCategory.Archive);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var files = await _filePicker.PickFilesAsync(SelectedCategory);
        if (files.Count > 0) await AddFilesAsync(files, SelectedCategory);
    }

    public async Task AddFolderAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        if (Archives.SelectedAction.Value != ArchiveAction.Create)
        {
            await _dialogs.ShowMessageAsync("Choose Create archive",
                "Folders can be packaged only with the Create archive action. Switch the archive action to Create and try again.");
            return;
        }

        SelectCategory(MediaCategory.Archive);
        var folder = new DirectoryInfo(path);
        var info = new MediaInfo
        {
            FilePath = folder.FullName,
            FileName = folder.Name,
            Extension = "",
            FileSizeBytes = 0,
            Category = MediaCategory.Archive,
            FormatId = "folder",
            FormatName = "Folder",
            EngineId = EngineIds.Archives,
            Tags = new Dictionary<string, string> { ["IsFolder"] = "true" }
        };
        var before = Archives.Files.Count;
        Archives.AddFile(info, _thumbnails);
        if (Archives.Files.Count == before)
        {
            await _dialogs.ShowMessageAsync("Folder already added", "This folder is already in the archive list.");
            return;
        }

        UpdateSummary();
        ScheduleValidation();
    }

    public async Task AddFolderAsCategoryAsync(string path, MediaCategory category)
    {
        if (!Directory.Exists(path)) return;
        if (!_registry.IsInitialized) await InitializeAsync(category);
        SelectCategory(category);
        var workspace = _session.WorkspaceFor(category);
        var result = await _scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = Path.GetFullPath(path),
            Recursive = true,
            PreserveFolderStructure = true,
            TargetCategory = category,
            ExcludeDirectory = workspace.OutputDirectory
        });
        var files = result.IncludedFiles
            .Where(f => f.Info is not null)
            .Select(f => f.Path)
            .ToList();
        if (files.Count > 0)
            await AddFilesAsync(files, category);
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(CurrentWorkspace?.OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) await AddFolderAsync(folder);
    }

    [RelayCommand]
    private async Task ClearFilesAsync()
    {
        if (!CurrentWorkspace.HasFiles) return;
        if (await _dialogs.ConfirmAsync("Clear imported files?",
                $"Remove {CurrentWorkspace.FileCountText} from this workspace? No files on disk will be deleted.",
                "Clear files", "Keep files", destructive: true))
        {
            CurrentWorkspace.ClearFiles();
            UpdateSummary();
            ScheduleValidation();
        }
    }

    [RelayCommand]
    private async Task RemoveFileAsync(MediaFileViewModel? file)
    {
        if (file is null) return;
        CurrentWorkspace.RemoveFile(file);
        UpdateSummary();
        ScheduleValidation();
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(CurrentWorkspace.OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            CurrentWorkspace.OutputDirectory = folder;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void OpenOutput()
    {
        var folder = CurrentWorkspace.OutputDirectory;
        if (!Directory.Exists(folder))
        {
            try { Directory.CreateDirectory(folder); } catch { }
        }

        if (Directory.Exists(folder))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not open output folder: {Message}", ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task ClearAllFilesAsync()
    {
        var total = _session.TotalFileCount;
        if (total == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear all staged files?",
                Loc.F("Remove {0} from every Converter category? No files on disk will be deleted.",
                    CountLabel(total, "file", "files")),
                "Clear all", "Keep files", destructive: true)) return;
        _session.ClearAll();
        ScanSkipped.Clear();
        HasScanSkipped = false;
        HasScanSummary = false;
        ScanSummary = "";
        _lastScanResult = null;
        _lastScanDuplicates = 0;
        UpdateSummary();
        ScheduleValidation();
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        if (IsScanningFolder) return;
        var category = SelectedCategory;
        var workspace = _session.WorkspaceFor(category);
        var folder = await _filePicker.PickFolderAsync(workspace.OutputDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;

        IsScanningFolder = true;
        HasScanSummary = true;
        ScanSummary = Loc.T("Scanning folder…");
        ScanSkipped.Clear();
        HasScanSkipped = false;
        try
        {
            var progress = new Progress<int>(count =>
            {
                _dispatcher.TryEnqueue(() => ScanSummary =
                    LocalizationService.P("Scan.CheckedFiles", count));
            });
            var result = await _scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = folder,
                Recursive = ScanSubfolders,
                PreserveFolderStructure = ScanSubfolders,
                TargetCategory = category,
                ExcludeDirectory = workspace.OutputDirectory
            }, progress);

            var added = 0;
            var duplicates = 0;
            foreach (var file in result.IncludedFiles)
            {
                if (file.Info is null) continue;
                var before = workspace.Files.Count;
                workspace.AddFile(file.Info, _thumbnails, relativeDirectory: file.RelativeDirectory);
                if (workspace.Files.Count > before) added++;
                else duplicates++;
            }

            foreach (var file in result.Files.Where(f => !f.IsSupported || !f.IsValid))
                ScanSkipped.Add(new ScanSkipItem(file.RelativePath, file.Reason ?? "Skipped", !file.IsSupported));
            HasScanSkipped = ScanSkipped.Count > 0;

            var summary = LocalizationService.ScanSummary(result);
            if (duplicates > 0)
                summary += " · " + Loc.F("{0} already staged", duplicates);
            _lastScanResult = result;
            _lastScanDuplicates = duplicates;
            ScanSummary = summary;
            HasScanSummary = true;

            if (added > 0)
            {
                _notifications.Notify("Folder scan complete", summary, NotificationSeverity.Success);
            }
            else
            {
                var reason = ScanSkipped.Count > 0
                    ? string.Join("\n", ScanSkipped.Take(8).Select(s => $"{s.FileName}: {s.Reason}"))
                    : "No supported, readable files were found in the selected folder.";
                await _dialogs.ShowMessageAsync($"No {FolderCategoryNoun(category)} files added", reason);
            }

            UpdateSummary();
            ScheduleValidation();
        }
        catch (OperationCanceledException)
        {
            ScanSummary = Loc.T("Folder scan cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Converter folder scan failed");
            ScanSummary = Loc.T("Folder scan failed.");
            await _dialogs.ShowErrorAsync("Folder scan failed", ex.Message, ex.ToString());
        }
        finally
        {
            IsScanningFolder = false;
            UpdateCanQueue();
        }
    }

    private static string FolderCategoryNoun(MediaCategory category) => category switch
    {
        MediaCategory.Image => "image",
        MediaCategory.Audio => "audio",
        MediaCategory.Video => "video",
        MediaCategory.Document => "document",
        MediaCategory.Ebook => "e-book",
        MediaCategory.Archive => "archive",
        _ => "media"
    };

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (_queueing || IsLoading) return;
        var staged = _session.WorkspacesWithFiles;
        if (staged.Count == 0)
        {
            await _dialogs.ShowMessageAsync("No files to process",
                "Import files into at least one Converter category before queueing.");
            return;
        }

        _queueing = true;
        IsLoading = true;
        var valid = new List<(WorkspaceViewModel Workspace, JobRequest Request, ValidationResult Validation)>();
        var invalid = new List<(WorkspaceViewModel Workspace, ValidationResult Validation)>();
        try
        {
            foreach (var workspace in staged)
            {
                var request = workspace.BuildJobRequest();
                var validation = await _jobs.ValidateAsync(request);
                workspace.SetValidation(validation);
                if (validation.IsValid) valid.Add((workspace, request, validation));
                else invalid.Add((workspace, validation));
            }

            if (valid.Count == 0)
            {
                await _dialogs.ShowJobValidationAsync(invalid[0].Validation);
                if (invalid.Count > 1)
                {
                    var details = string.Join("\n\n", invalid.Skip(1).Select(i =>
                        i.Workspace.CategoryTitle + ": " + LocalizationService.ValidationSummary(i.Validation)));
                    await _dialogs.ShowMessageAsync("Other categories also need attention", details);
                }
                return;
            }

            var warnings = valid.SelectMany(v => v.Validation.Warnings.Select(w =>
                v.Workspace.CategoryTitle + ": " + LocalizationService.T(w.Message))).ToList();
            if (warnings.Count > 0)
            {
                var proceed = await _dialogs.ConfirmAsync("Review warnings",
                    string.Join("\n\n", warnings), "Process anyway", "Go back");
                if (!proceed) return;
            }

            if (valid.Any(v => v.Workspace.SelectedCollision?.Policy == CollisionPolicy.Overwrite))
            {
                var proceed = await _dialogs.ConfirmAsync("Replace existing files?",
                    "One or more categories use Replace mode. PixelForge will overwrite only generated output files after this confirmation. Source files are never touched.",
                    "Use Replace mode", "Cancel", destructive: true);
                if (!proceed) return;
            }

            var queued = new List<WorkspaceViewModel>();
            var queuedFiles = 0;
            var enqueueFailures = new List<string>();
            foreach (var (workspace, request, _) in valid)
            {
                var fileCount = workspace.Files.Count;
                try
                {
                    await _jobs.EnqueueAsync(request);
                    queued.Add(workspace);
                    queuedFiles += fileCount;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not enqueue {Category} converter job", workspace.CategoryTitle);
                    enqueueFailures.Add($"{workspace.CategoryTitle}: {ex.Message}");
                }
            }

            if (queued.Count > 0)
            {
                _session.MarkQueued(queued);
                _notifications.Notify("Added to processing queue",
                    Loc.F("{0} across {1}.",
                        CountLabel(queuedFiles, "file", "files"),
                        CountLabel(queued.Count, "category", "categories")),
                    NotificationSeverity.Success);
            }

            var notQueued = new List<string>();
            notQueued.AddRange(invalid.Select(i =>
                i.Workspace.CategoryTitle + ": " + LocalizationService.ValidationSummary(i.Validation)));
            notQueued.AddRange(enqueueFailures);
            if (notQueued.Count > 0)
            {
                var message = "These staged files were left in the Converter so you can fix them and retry:\n\n" +
                              string.Join("\n\n", notQueued);
                if (queued.Count > 0)
                    await _dialogs.ShowMessageAsync("Some files were not queued", message);
                else
                    await _dialogs.ShowErrorAsync("Could not add the jobs", message);
            }

            UpdateSummary();
            ScheduleValidation();
            if (queued.Count > 0) _navigation.Navigate("Queue");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not add converter jobs");
            await _dialogs.ShowErrorAsync("Could not add the jobs", ex.Message, ex.ToString());
        }
        finally
        {
            _queueing = false;
            IsLoading = false;
            UpdateCanQueue();
        }
    }

    private void OnStagingChanged(object? sender, EventArgs e)
    {
        UpdateSummary();
        ScheduleValidation();
        UpdateCanQueue();
    }

    private void OnCapabilitiesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnCapabilitiesChanged(sender, e));
            return;
        }

        LoadFormats(force: true);
        EngineStatus = "Engines ready · local processing";
        UpdateSummary();
    }

    private void ScheduleValidation()
    {
        if (!_initialized) return;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var token = _validationCts.Token;
        _ = ValidateAfterDelayAsync(token);
    }

    private async Task ValidateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            var workspace = CurrentWorkspace;
            if (!workspace.HasFiles)
            {
                workspace.SetValidation(null);
                UpdateCanQueue();
                return;
            }

            workspace.IsValidating = true;
            var validation = await _jobs.ValidateAsync(workspace.BuildJobRequest(), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(workspace, CurrentWorkspace)) return;
            workspace.SetValidation(validation);
            UpdateCanQueue();
        }
        catch (OperationCanceledException)
        {
            // A newer edit superseded this validation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Live validation failed: {Message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(CurrentWorkspace, CurrentWorkspace))
                CurrentWorkspace.IsValidating = false;
        }
    }

    private void UpdateCanQueue()
    {
        HasStagedFiles = _session.HasFiles;
        CanAddToQueue = HasStagedFiles && !IsLoading && !_queueing;
        var total = _session.TotalFileCount;
        var categories = _session.Workspaces.Count(w => w.Files.Count > 0);
        SelectionHint = total == 0
            ? Loc.T("Import files to begin")
            : categories > 1
                ? Loc.F("{0} staged · {1} categories", CountLabel(total, "file", "files"), categories)
                : Loc.F("{0} staged", CountLabel(total, "file", "files"));
    }

    private void UpdateSummary()
    {
        CategorySummary = BuildCategorySummary();
        HasStagedFiles = _session.HasFiles;
        var total = _session.TotalFileCount;
        var categories = _session.Workspaces.Count(w => w.Files.Count > 0);
        SelectionHint = total == 0
            ? Loc.T("Import files to begin")
            : categories > 1
                ? Loc.F("{0} staged · {1} categories", CountLabel(total, "file", "files"), categories)
                : Loc.F("{0} staged", CountLabel(total, "file", "files"));
    }

    private string BuildCategorySummary() =>
        string.Join(" · ",
            CountLabel(Image.Files.Count, "image", "images"),
            CountLabel(Audio.Files.Count, "audio", "audio"),
            CountLabel(Video.Files.Count, "video", "videos"),
            CountLabel(Documents.Files.Count, "doc", "docs"),
            CountLabel(Ebooks.Files.Count, "e-book", "e-books"),
            CountLabel(Archives.Files.Count, "archive", "archives"));


    private static string CountLabel(int count, string singular, string plural)
    {
        var key = singular switch
        {
            "file" => "Count.File",
            "category" => "Count.Category",
            "image" => "Count.Image",
            "audio" => "Count.Audio",
            "video" => "Count.Video",
            "doc" => "Count.Document",
            "e-book" => "Count.Ebook",
            "archive" => "Count.Archive",
            _ => "Count." + singular
        };
        return LocalizationService.P(key, count);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.CapabilitiesChanged -= OnCapabilitiesChanged;
        _session.Changed -= OnStagingChanged;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
    }
}
