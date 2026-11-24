using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PixelForge.Presentation.ViewModels;

public enum OcrSortMode
{
    FilenameNaturalAscending,
    FilenameNaturalDescending,
    CreatedOldest,
    CreatedNewest,
    ModifiedOldest,
    ModifiedNewest,
    CaptureDateOldest,
    CaptureDateNewest,
    FullPathAscending
}

public sealed record OcrSortChoice(OcrSortMode Value, string Label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    public override string ToString() => DisplayLabel;
}

public sealed partial class OcrPageItem : ObservableObject
{
    private readonly IThumbnailService _thumbnails;
    private readonly Action<OcrPageItem> _remove;
    private readonly Action<OcrPageItem, int> _reorder;
    private readonly DispatcherQueue _dispatcher;
    private bool _thumbnailRequested;

    public OcrPageItem(MediaInfo info, int pageNumber, IThumbnailService thumbnails,
        Action<OcrPageItem> remove, Action<OcrPageItem, int> reorder, DateTimeOffset? captureDate)
    {
        Info = info;
        PageNumber = pageNumber;
        _thumbnails = thumbnails;
        _remove = remove;
        _reorder = reorder;
        CaptureDate = captureDate;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = LoadThumbnailAsync();
    }

    public MediaInfo Info { get; }
    public string FilePath => Info.FilePath;
    public string FileName => Info.FileName;
    public DateTimeOffset? CaptureDate { get; }
    public DateTime CreatedUtc => File.Exists(FilePath) ? File.GetCreationTimeUtc(FilePath) : DateTime.MinValue;
    public DateTime ModifiedUtc => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _hasThumbnail;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;
    public string Dimensions => Info.Width is > 0 && Info.Height is > 0 ? $"{Info.Width} × {Info.Height}" : "";
    public string SizeText => Converters.FileSizeConverter.FormatBytes(Info.FileSizeBytes);
    public string Detail => string.Join(" · ", new[] { Dimensions, SizeText, Info.FormatId?.ToUpperInvariant() }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void MoveUp() => _reorder(this, -1);

    [RelayCommand]
    private void MoveDown() => _reorder(this, 1);

    private async Task LoadThumbnailAsync()
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        try
        {
            var path = await _thumbnails.GetThumbnailPathAsync(Info.FilePath, MediaCategory.Image, 160);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var bitmap = new BitmapImage { UriSource = new Uri(path) };
            if (_dispatcher.HasThreadAccess)
            {
                Thumbnail = bitmap;
                HasThumbnail = true;
            }
            else
            {
                _dispatcher.TryEnqueue(() =>
                {
                    Thumbnail = bitmap;
                    HasThumbnail = true;
                });
            }
        }
        catch
        {
            HasThumbnail = false;
        }
    }
}

public sealed partial class OcrModelItem : ObservableObject
{
    public OcrModelItem(OcrModelDescriptor descriptor, InstalledOcrModel? installed)
    {
        Descriptor = descriptor;
        Installed = installed;
    }

    public OcrModelDescriptor Descriptor { get; }
    public InstalledOcrModel? Installed { get; private set; }
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Tier => Descriptor.Tier;
    public string Architecture => Descriptor.Architecture;
    public string IntendedUse => Descriptor.IntendedUse;
    public string SizeText => Descriptor.DisplaySize;
    public string Languages => string.Join(", ", Descriptor.Languages);
    public string Notes => Descriptor.Notes ?? "";
    public bool IsInstalled => Installed is { IsValid: true };
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string StatusText => IsDownloading
        ? (IsCancelling ? "Cancelling…" : ProgressText)
        : HasError ? "Download failed"
        : IsInstalled ? "Installed" : "Download required";
    public bool CanDownload => !IsInstalled && !IsDownloading;
    public bool CanRemove => IsInstalled && !IsDownloading;
    public bool NeedsDownload => !IsInstalled && !IsDownloading;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _errorDetails = "";

    public void SetInstalled(InstalledOcrModel? installed)
    {
        Installed = installed;
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(NeedsDownload));
    }

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);

    public void Begin(string text)
    {
        IsDownloading = true;
        IsCancelling = false;
        Error = "";
        ErrorDetails = "";
        Progress = 0;
        ProgressText = text;
        NotifyState();
    }

    public void Report(OcrModelDownloadProgress value)
    {
        Progress = value.Fraction * 100;
        ProgressText = LocalizationService.F("{0} · {1} / {2} MB", LocalizationService.T(value.Stage),
            LocalizationService.FormatNumber(value.BytesReceived / 1024d / 1024, "0.0"),
            LocalizationService.FormatNumber(value.TotalBytes / 1024d / 1024, "0.0"));
        NotifyState();
    }

    public void Fail(string error, string? details)
    {
        IsDownloading = false;
        IsCancelling = false;
        Error = error;
        ErrorDetails = details ?? "";
        NotifyState();
    }

    public void Complete()
    {
        IsDownloading = false;
        IsCancelling = false;
        Progress = 100;
        ProgressText = "Installed";
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsCancelling));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(NeedsDownload));
    }
}

public sealed partial class OcrViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRegistry _registry;
    private readonly IMediaInspector _inspector;
    private readonly IFolderScanner _scanner;
    private readonly IThumbnailService _thumbnails;
    private readonly IFilePickerService _filePicker;
    private readonly IJobService _jobs;
    private readonly IOcrModelManager _ocrModels;
    private readonly IOcrReviewService _ocrReview;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly INavigationService _navigation;
    private readonly IModelPanelService _modelPanel;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<Guid> _trackedJobs = [];
    private CancellationTokenSource? _validationCts;
    private CancellationTokenSource? _modelCts;
    private bool _initialized;
    private ValidationResult? _lastValidation;
    private bool _hasScanResult;
    private int _scanAdded;
    private int _scanIncluded;
    private int _scanIneligible;
    private int _scanSkippedCount;
    private bool _documentNameEdited;
    private bool _updatingDocumentName;
    private string? _reviewedMarkdownPath;
    private string? _reviewedProvider;
    private string? _reviewedRuntimeVersion;
    private string? _reviewedSignature;
    private readonly Dictionary<Guid, string> _reviewFiles = [];
    private bool _disposed;

    public OcrViewModel(ICapabilityRegistry registry, IMediaInspector inspector, IFolderScanner scanner,
        IThumbnailService thumbnails, IFilePickerService filePicker, IJobService jobs, IOcrModelManager ocrModels,
        IOcrReviewService ocrReview, ISettingsService settings, IAppPaths paths, INavigationService navigation,
        IModelPanelService modelPanel, IDialogService dialogs, INotificationService notifications, IAppLogger logger)
    {
        _registry = registry;
        _inspector = inspector;
        _scanner = scanner;
        _thumbnails = thumbnails;
        _filePicker = filePicker;
        _jobs = jobs;
        _ocrModels = ocrModels;
        _ocrReview = ocrReview;
        _settings = settings;
        _paths = paths;
        _navigation = navigation;
        _modelPanel = modelPanel;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        OutputDirectory = string.IsNullOrWhiteSpace(settings.Settings.Ocr.DefaultOutputDirectory)
            ? Path.Combine(paths.DefaultOutputDirectory, "OCR")
            : settings.Settings.Ocr.DefaultOutputDirectory;
        GeneratePdf = settings.Settings.Ocr.GeneratePdf;
        Preprocess = settings.Settings.Ocr.Preprocess;
        ReviewBeforeExport = settings.Settings.Ocr.ReviewBeforeExport;
        SelectedCollision = CollisionOptions[0];
        SelectedSort = SortOptions[0];
        _jobs.JobChanged += OnJobChanged;
        _ocrModels.ModelsChanged += OnModelsChanged;
    }

    public ObservableCollection<OcrPageItem> Pages { get; } = [];
    public ObservableCollection<ScanSkipItem> Skipped { get; } = [];
    public ObservableCollection<OcrModelItem> Models { get; } = [];
    public IReadOnlyList<OcrSortChoice> SortOptions { get; } =
    [
        new(OcrSortMode.FilenameNaturalAscending, "Filename — natural ascending"),
        new(OcrSortMode.FilenameNaturalDescending, "Filename — natural descending"),
        new(OcrSortMode.CreatedOldest, "Date created — oldest first"),
        new(OcrSortMode.CreatedNewest, "Date created — newest first"),
        new(OcrSortMode.ModifiedOldest, "Date modified — oldest first"),
        new(OcrSortMode.ModifiedNewest, "Date modified — newest first"),
        new(OcrSortMode.CaptureDateOldest, "EXIF capture date — oldest first"),
        new(OcrSortMode.CaptureDateNewest, "EXIF capture date — newest first"),
        new(OcrSortMode.FullPathAscending, "Full path — ascending")
    ];
    public IReadOnlyList<CollisionOption> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number", "Never replace existing files."),
        new(CollisionPolicy.Skip, "Skip existing files", "Leave existing outputs untouched."),
        new(CollisionPolicy.Overwrite, "Replace existing files", "Overwrite only after confirmation.")
    ];

    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in SortOptions) option.RefreshLocalization();
        foreach (var option in CollisionOptions) option.RefreshLocalization();
        foreach (var model in Models) model.RefreshLocalization();
        UpdateModelStatus();
        UpdateProviderStatus();
        if (_lastValidation is not null) SetValidation(_lastValidation);
        if (_hasScanResult && !IsScanning)
            ScanSummary = Loc.F("{0} added · {1} supported · {2} other files ignored · {3} skipped",
                Loc.P("Count.Page", _scanAdded), _scanIncluded, _scanIneligible, _scanSkippedCount);
    }

    [ObservableProperty] private OcrModelItem? _selectedModel;
    [ObservableProperty] private OcrSortChoice? _selectedSort;
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private bool _scanRecursive = true;
    [ObservableProperty] private bool _generatePdf;
    [ObservableProperty] private bool _preprocess = true;
    [ObservableProperty] private bool _reviewBeforeExport;
    [ObservableProperty] private string _documentName = "ScannedDocument_OCR";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _scanSummary = "";
    [ObservableProperty] private bool _hasScanSummary;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPages;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private bool _canQueue;
    [ObservableProperty] private string _statusText = "Add scanned pages, photos or screenshots to begin.";
    [ObservableProperty] private string _modelStatus = "";
    public string ModelBadge => SelectedModel is null
        ? Loc.T("OCR · local inference")
        : $"{SelectedModel.Name} · local inference";
    [ObservableProperty] private string _providerStatus = "OCR runtime not initialized.";
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private string _jobStatus = "";
    [ObservableProperty] private string? _lastDocxPath;
    [ObservableProperty] private bool _hasOutput;

    public string PageCountText => LocalizationService.P("Count.Page", Pages.Count);
    public string FooterStatus => !HasPages
        ? Loc.T("Add scanned pages, photos or screenshots to begin.")
        : string.IsNullOrWhiteSpace(JobStatus)
            ? Loc.F("{0} ready to assemble into one document.", PageCountText)
            : JobStatus;
    public string ModelLanguageText => SelectedModel?.Descriptor.Backend == OcrBackendKind.LlamaCpp
        ? LocalizationService.F("Advanced OCR model. Documented languages: {0}. Page-level recognition is used; no translation is performed.", SelectedModel.Languages)
        : LocalizationService.T("The PP-OCRv6 recognizer uses a multilingual dictionary. English and Italian are validated; no translation is performed.");
    public bool HasSkipped => Skipped.Count > 0;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (!_registry.IsInitialized) await _registry.InitializeAsync();
            await _ocrModels.InitializeAsync();
            RefreshModels();
            UpdateProviderStatus();
            RefreshPageState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR initialization failed");
            await _dialogs.ShowErrorAsync("OCR unavailable", "The OCR workspace could not be initialized.", ex.ToString());
        }
    }

    public async Task AddPathsAsync(IReadOnlyList<string> paths, bool allowDuplicates)
    {
        if (paths.Count == 0) return;
        try
        {
            IsBusy = true;
            var distinct = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!allowDuplicates)
                distinct = distinct.Where(p => Pages.All(x => !string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase))).ToList();
            if (distinct.Count == 0)
            {
                await _dialogs.ShowMessageAsync("No new pages", "Those images are already in the document.");
                return;
            }

            var infos = await _inspector.InspectManyAsync(distinct, MediaCategory.Image);
            var added = 0;
            var skipped = new List<ScanSkipItem>();
            foreach (var info in infos)
            {
                if (!info.IsReadable || info.Category != MediaCategory.Image)
                {
                    skipped.Add(new ScanSkipItem(info.FileName, info.Error ?? "Not a readable image", false));
                    continue;
                }
                if (info.IsAnimated || info.IsMultipage)
                {
                    skipped.Add(new ScanSkipItem(info.FileName, "Animated and multi-page images cannot be used as a single OCR page.", true));
                    continue;
                }
                if (info.IsVector)
                {
                    skipped.Add(new ScanSkipItem(info.FileName, "Vector images must be rasterized before OCR.", true));
                    continue;
                }

                var captureDate = await TryGetCaptureDateAsync(info.FilePath);
                Pages.Add(new OcrPageItem(info, Pages.Count + 1, _thumbnails, RemovePage, Reorder, captureDate));
                added++;
            }

            AddSkipped(skipped);
            if (added > 0 && !_documentNameEdited)
            {
                var first = Path.GetFileNameWithoutExtension(Pages[0].FileName);
                SetDocumentName(SanitizeDocumentName(first) + "_OCR");
            }
            if (added > 0 && !string.IsNullOrWhiteSpace(ScanSummary))
                ScanSummary = LocalizationService.F("{0} added · {1} pages total",
                    LocalizationService.P("Count.Page", added), Pages.Count);
            RenumberPages();
            RefreshPageState();
            ScheduleValidation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not add OCR pages");
            await _dialogs.ShowErrorAsync("Could not add images", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddFolderAsync(string folder)
    {
        IsScanning = true;
        IsBusy = true;
        _hasScanResult = false;
        ScanSummary = Loc.T("Scanning folder…");
        HasScanSummary = true;
        try
        {
            var progress = new Progress<int>(count => _dispatcher.TryEnqueue(() =>
                ScanSummary = LocalizationService.P("Scan.CheckedFiles", count)));
            var result = await _scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = folder,
                Recursive = ScanRecursive,
                TargetCategory = MediaCategory.Image,
                RejectAnimated = true,
                RejectVector = true,
                ExcludeDirectory = OutputDirectory
            }, progress);
            foreach (var file in result.Files.Where(f => !f.IsSupported || !f.IsValid))
                Skipped.Add(new ScanSkipItem(file.RelativePath, file.Reason ?? "Skipped", !file.IsSupported));
            var included = result.IncludedFiles.Where(f => f.Info is not null).ToList();
            var sorted = SortFiles(included, SelectedSort?.Value ?? OcrSortMode.FilenameNaturalAscending);
            var known = Pages.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var file in sorted)
            {
                if (file.Info is null || known.Contains(file.Path)) continue;
                var captureDate = await TryGetCaptureDateAsync(file.Path);
                Pages.Add(new OcrPageItem(file.Info, Pages.Count + 1, _thumbnails, RemovePage, Reorder, captureDate));
                known.Add(file.Path);
                added++;
            }

            ScanSummary = LocalizationService.F("{0} added · {1} supported · {2} other files ignored · {3} skipped",
                LocalizationService.P("Count.Page", added), result.IncludedCount,
                result.IneligibleCount, Skipped.Count);
            _hasScanResult = true;
            _scanAdded = added;
            _scanIncluded = result.IncludedCount;
            _scanIneligible = result.IneligibleCount;
            _scanSkippedCount = Skipped.Count;
            if (!_documentNameEdited && Pages.Count > 0)
                SetDocumentName(SanitizeDocumentName(Path.GetFileNameWithoutExtension(Pages[0].FileName)) + "_OCR");
            RenumberPages();
            RefreshPageState();
            OnPropertyChanged(nameof(HasSkipped));
            if (added == 0)
                await _dialogs.ShowMessageAsync("No pages added",
                    "The folder contained no new supported images. Review the skipped list for details.");
            else
                _notifications.Notify("Folder scan complete", ScanSummary, NotificationSeverity.Success);
            ScheduleValidation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR folder scan failed");
            await _dialogs.ShowErrorAsync("Folder scan failed", ex.Message);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    public async Task PasteAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().Select(f => f.Path)
                    .Where(File.Exists).ToList();
                if (files.Count > 0)
                {
                    await AddPathsAsync(files, allowDuplicates: true);
                    return;
                }
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                var stream = await reference.OpenReadAsync();
                Directory.CreateDirectory(Path.Combine(_paths.TempDirectory, "ocr"));
                var path = Path.Combine(_paths.TempDirectory, "ocr", $"paste-{Guid.NewGuid():N}.png");
                await using (var target = File.Create(path))
                await using (var source = stream.AsStreamForRead())
                    await source.CopyToAsync(target);
                await AddPathsAsync([path], allowDuplicates: true);
                return;
            }

            await _dialogs.ShowMessageAsync("Clipboard has no image",
                "Copy an image, or copy one or more image files in File Explorer, then try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard paste failed");
            await _dialogs.ShowErrorAsync("Could not paste image", ex.Message);
        }
    }

    private void Reorder(OcrPageItem item, int offset)
    {
        var index = Pages.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Pages.Count) return;
        Pages.Move(index, target);
        RenumberPages();
        ScheduleValidation();
    }

    private void RemovePage(OcrPageItem item)
    {
        if (!Pages.Remove(item)) return;
        RenumberPages();
        RefreshPageState();
        ScheduleValidation();
    }

    [RelayCommand]
    private async Task AddImagesAsync()
    {
        var files = await _filePicker.PickUpscaleImagesAsync();
        await AddPathsAsync(files, allowDuplicates: false);
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) await AddFolderAsync(folder);
    }

    [RelayCommand]
    private async Task PasteImagesAsync() => await PasteAsync();

    [RelayCommand]
    private async Task ClearPagesAsync()
    {
        if (Pages.Count == 0 && Skipped.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear OCR pages?",
                "Remove all pages and scan results from this workspace? Source files are not deleted.",
                "Clear pages", "Keep", destructive: true)) return;
        Pages.Clear();
        Skipped.Clear();
        HasScanSummary = false;
        _hasScanResult = false;
        ScanSummary = "";
        _documentNameEdited = false;
        SetDocumentName("ScannedDocument_OCR");
        OnPropertyChanged(nameof(HasSkipped));
        RefreshPageState();
        ScheduleValidation();
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputDirectory = folder;
            ScheduleValidation();
        }
    }

    [RelayCommand]
    private void ManageModels() => _modelPanel.Show(ModelPanelFilter.Ocr);

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (SelectedModel is null || SelectedModel.IsDownloading) return;
        var cts = new CancellationTokenSource();
        var previous = _modelCts;
        _modelCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        var item = SelectedModel;
        item.Begin("Starting download…");
        ModelStatus = item.StatusText;
        try
        {
            var progress = new Progress<OcrModelDownloadProgress>(value =>
            {
                if (_dispatcher.HasThreadAccess) item.Report(value);
                else _dispatcher.TryEnqueue(() => item.Report(value));
                ModelStatus = item.ProgressText;
            });
            var result = await _ocrModels.DownloadAsync(item.Id, progress, cts.Token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                item.Complete();
                ModelStatus = $"{item.Name} installed";
                _notifications.Notify("OCR model installed", item.Name, NotificationSeverity.Success);
                RefreshPageState();
                ScheduleValidation();
            }
            else
            {
                item.Fail(result.Error ?? "Download failed", result.Details);
                ModelStatus = item.Error;
                if (!cts.IsCancellationRequested)
                    await _dialogs.ShowErrorAsync("OCR model download failed", item.Error, item.ErrorDetails);
            }
        }
        catch (OperationCanceledException)
        {
            item.Fail("Download cancelled", null);
            ModelStatus = "Download cancelled";
        }
        finally
        {
            if (ReferenceEquals(_modelCts, cts)) _modelCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelModelDownload()
    {
        var downloading = Models.FirstOrDefault(m => m.IsDownloading);
        if (downloading is not null) downloading.IsCancelling = true;
        _modelCts?.Cancel();
    }

    [RelayCommand]
    private async Task RemoveModelAsync()
    {
        if (SelectedModel is null || !SelectedModel.IsInstalled) return;
        if (!await _dialogs.ConfirmAsync("Remove OCR model?",
                $"Remove {SelectedModel.Name} from this PC? You can download it again later.",
                "Remove model", "Cancel", destructive: true)) return;
        var removed = await _ocrModels.RemoveAsync(SelectedModel.Id);
        if (removed)
        {
            SelectedModel.SetInstalled(null);
            ModelStatus = $"{SelectedModel.Name} removed";
            RefreshPageState();
            ScheduleValidation();
        }
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (Pages.Count == 0)
        {
            await _dialogs.ShowMessageAsync("No pages to process", "Add at least one image page first.");
            return;
        }
        if (SelectedModel is not { IsInstalled: true })
        {
            await _dialogs.ShowMessageAsync("OCR model required", "Download and select an OCR model first.");
            return;
        }
        if (ReviewBeforeExport && !HasCurrentReview)
        {
            var reviewed = await RunReviewAsync();
            if (!reviewed) return;
        }

        var request = BuildRequest();
        var validation = await _jobs.ValidateAsync(request);
        SetValidation(validation);
        if (!validation.IsValid)
        {
            await _dialogs.ShowJobValidationAsync(validation);
            return;
        }
        if (validation.Warnings.Count > 0)
        {
            var proceed = await _dialogs.ConfirmAsync("Review OCR warnings",
                string.Join("\n\n", validation.Warnings.Select(w => w.Message)),
                "Process anyway", "Go back");
            if (!proceed) return;
        }

        try
        {
            var job = await _jobs.EnqueueAsync(request);
            _trackedJobs.Add(job.Id);
            if (!string.IsNullOrWhiteSpace(_reviewedMarkdownPath))
                _reviewFiles[job.Id] = _reviewedMarkdownPath;
            JobStatus = LocalizationService.F("Queued · {0}", job.Title);
            _notifications.Notify("OCR added to queue", job.Title, NotificationSeverity.Success);
            Pages.Clear();
            _reviewedMarkdownPath = null;
            _reviewedProvider = null;
            _reviewedRuntimeVersion = null;
            _reviewedSignature = null;
            Skipped.Clear();
            HasScanSummary = false;
            _hasScanResult = false;
            ScanSummary = "";
            OnPropertyChanged(nameof(HasSkipped));
            RefreshPageState();
            ScheduleValidation();
            _navigation.Navigate("Queue");
        }
        catch (JobValidationException ex)
        {
            SetValidation(ex.Validation);
            await _dialogs.ShowJobValidationAsync(ex.Validation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue OCR job");
            await _dialogs.ShowErrorAsync("Could not add the OCR job", ex.Message, ex.ToString());
        }
    }

    private bool HasCurrentReview =>
        !string.IsNullOrWhiteSpace(_reviewedMarkdownPath) &&
        File.Exists(_reviewedMarkdownPath) &&
        string.Equals(_reviewedSignature, CurrentReviewSignature, StringComparison.Ordinal);

    private string CurrentReviewSignature => string.Join("|",
        Pages.Select(p => p.FilePath).Append(SelectedModel?.Id ?? "").Append(Preprocess ? "preprocess" : "raw"));

    private async Task<bool> RunReviewAsync()
    {
        IsBusy = true;
        StatusText = "Recognizing pages for review…";
        try
        {
            var settings = new OcrSettings
            {
                OcrModelId = SelectedModel?.Id ?? "ppocrv6-small",
                Preprocess = Preprocess,
                ExecutionProvider = _settings.Settings.Ocr.ExecutionProvider,
                PagePaths = Pages.Select(p => p.FilePath).ToList()
            };
            var progress = new Progress<ProcessingProgress>(value =>
                _dispatcher.TryEnqueue(() => StatusText = value.Message ?? "Recognizing pages for review…"));
            var result = await _ocrReview.RecognizeAsync(settings, progress);
            if (!result.Success)
            {
                await _dialogs.ShowErrorAsync("OCR review could not run",
                    result.Error ?? "The selected model could not recognize these pages.", result.ErrorDetails);
                return false;
            }

            var guidance = "Edit the recognized text before PixelForge builds the DOCX. " +
                           "Tables are preserved as Markdown. Cancel to return without queueing.";
            if (result.Warnings.Count > 0)
                guidance += "\n\n" + string.Join("\n", result.Warnings);
            var reviewed = await _dialogs.ReviewTextAsync("Review OCR text", guidance, result.Markdown,
                "Use reviewed text");
            if (reviewed is null) return false;

            var reviewDir = Path.Combine(_paths.TempDirectory, "ocr-review");
            Directory.CreateDirectory(reviewDir);
            var path = Path.Combine(reviewDir, $"review-{Guid.NewGuid():N}.md");
            await File.WriteAllTextAsync(path, reviewed, new UTF8Encoding(false));
            _reviewedMarkdownPath = path;
            _reviewedProvider = result.Provider;
            _reviewedRuntimeVersion = result.RuntimeVersion;
            _reviewedSignature = CurrentReviewSignature;
            StatusText = "Reviewed OCR text is ready to queue.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR review failed");
            await _dialogs.ShowErrorAsync("OCR review failed", ex.Message, ex.ToString());
            return false;
        }
        finally
        {
            IsBusy = false;
            UpdateCanQueue();
        }
    }

    private JobRequest BuildRequest()
    {
        var inputs = Pages.Select(p => new JobInput { Path = p.FilePath, Info = p.Info }).ToList();
        var settings = new OcrSettings
        {
            OcrModelId = SelectedModel?.Id ?? "ppocrv6-small",
            GeneratePdf = GeneratePdf,
            Preprocess = Preprocess,
            DocumentName = string.IsNullOrWhiteSpace(DocumentName) ? "ScannedDocument_OCR" : DocumentName.Trim(),
            OutputDirectory = OutputDirectory,
            CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName,
            ExecutionProvider = _settings.Settings.Ocr.ExecutionProvider,
            PagePaths = inputs.Select(i => i.Path).ToList(),
            ReviewBeforeExport = ReviewBeforeExport,
            ReviewedMarkdownPath = ReviewBeforeExport && HasCurrentReview ? _reviewedMarkdownPath : null,
            ReviewedProvider = ReviewBeforeExport && HasCurrentReview ? _reviewedProvider : null,
            ReviewedRuntimeVersion = ReviewBeforeExport && HasCurrentReview ? _reviewedRuntimeVersion : null
        };
        return new JobRequest
        {
            Operation = OperationKind.OcrDocument,
            Title = LocalizationService.F("OCR · {0}", settings.DocumentName),
            Settings = settings,
            Inputs = inputs
        };
    }

    private void RefreshModels()
    {
        var previous = SelectedModel?.Id;
        Models.Clear();
        foreach (var descriptor in OcrModelCatalog.All)
        {
            var installed = _ocrModels.GetInstalled(descriptor.Id);
            Models.Add(new OcrModelItem(descriptor, installed));
        }

        var preferredModel = !string.IsNullOrWhiteSpace(previous)
            ? previous
            : _settings.Settings.Ocr.ModelId;
        SelectedModel = Models.FirstOrDefault(m => m.Id == preferredModel && m.IsInstalled)
                        ?? Models.FirstOrDefault(m => m.Id == "ppocrv6-small" && m.IsInstalled)
                        ?? Models.FirstOrDefault(m => m.IsInstalled)
                        ?? Models.FirstOrDefault(m => m.Id == preferredModel)
                        ?? Models.FirstOrDefault();
        UpdateModelStatus();
    }

    public void RefreshRuntimeSettings() => UpdateProviderStatus();

    private void UpdateProviderStatus()
    {
        if (SelectedModel?.Descriptor.Backend == OcrBackendKind.LlamaCpp)
        {
            ProviderStatus = _settings.Settings.Ocr.ExecutionProvider == ExecutionProviderKind.Cpu
                ? Loc.T("Advanced OCR · llama.cpp CPU")
                : Loc.T("Advanced OCR · llama.cpp Vulkan preferred · CPU fallback");
            return;
        }

        var directMl = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML);
        ProviderStatus = _settings.Settings.Ocr.ExecutionProvider switch
        {
            ExecutionProviderKind.Cpu => Loc.T("CPU only · no GPU session will be requested"),
            ExecutionProviderKind.Vulkan => Loc.T("Vulkan requested · PP-OCRv6 falls back to CPU; advanced OCR uses llama.cpp Vulkan"),
            ExecutionProviderKind.DirectML when directMl?.IsAvailable == true =>
                Loc.T("DirectML requested · CPU fallback is reported if the session cannot initialize"),
            ExecutionProviderKind.DirectML =>
                Loc.T("DirectML requested · DirectML is unavailable, so CPU fallback will be reported"),
            _ when directMl?.IsAvailable == true =>
                Loc.T("Automatic · DirectML preferred for both detector and recognizer · CPU fallback"),
            _ => Loc.T("Automatic · CPU fallback")
        };
    }

    private void UpdateModelStatus()
    {
        if (SelectedModel is null)
        {
            ModelStatus = "No OCR model is available.";
            return;
        }
        ModelStatus = SelectedModel.IsInstalled
            ? LocalizationService.F("{0} · installed · {1}", SelectedModel.Name, SelectedModel.SizeText)
            : LocalizationService.F("{0} · {1} · download required", SelectedModel.Name, SelectedModel.SizeText);
        ScheduleValidation();
    }

    private void OnModelsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnModelsChanged(sender, e));
            return;
        }

        foreach (var item in Models)
            item.SetInstalled(_ocrModels.GetInstalled(item.Id));
        UpdateModelStatus();
    }

    private void RefreshPageState()
    {
        PageCount = Pages.Count;
        HasPages = Pages.Count > 0;
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(FooterStatus));
        UpdateCanQueue();
    }

    private void UpdateCanQueue()
    {
        CanQueue = Pages.Count > 0 && SelectedModel is { IsInstalled: true } && !HasValidationErrors && !IsBusy;
    }

    private void RenumberPages()
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            Pages[i].PageNumber = i + 1;
            Pages[i].CanMoveUp = i > 0;
            Pages[i].CanMoveDown = i < Pages.Count - 1;
        }
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(FooterStatus));
    }

    private void AddSkipped(IReadOnlyList<ScanSkipItem> skipped)
    {
        foreach (var item in skipped) Skipped.Add(item);
        OnPropertyChanged(nameof(HasSkipped));
    }

    private void ScheduleValidation()
    {
        if (_disposed || Pages.Count == 0) { SetValidation(null); return; }
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        _ = ValidateAsync(_validationCts.Token);
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            var validation = await _jobs.ValidateAsync(BuildRequest(), cancellationToken);
            if (!cancellationToken.IsCancellationRequested) SetValidation(validation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("OCR validation failed: {Message}", ex.Message);
        }
    }

    private void SetValidation(ValidationResult? validation)
    {
        _lastValidation = validation;
        HasValidationErrors = validation?.Errors.Count > 0;
        HasValidationWarnings = validation?.Warnings.Count > 0;
        ValidationSummary = validation is null
            ? ""
            : validation.IsValid
                ? LocalizationService.F("Ready · {0}",
                    LocalizationService.P("Count.Page", Pages.Count))
                : string.Join("\n", validation.Errors.Select(e => LocalizationService.T(e.Message)));
        UpdateCanQueue();
    }

    private void ApplySort()
    {
        if (Pages.Count == 0) return;
        var sorted = SortPages(Pages.ToList(), SelectedSort?.Value ?? OcrSortMode.FilenameNaturalAscending);
        for (var i = 0; i < sorted.Count; i++)
        {
            var target = Pages.IndexOf(sorted[i]);
            if (target != i) Pages.Move(target, i);
        }
        RenumberPages();
        ScheduleValidation();
    }

    private IReadOnlyList<OcrPageItem> SortPages(IReadOnlyList<OcrPageItem> pages, OcrSortMode mode)
    {
        return mode switch
        {
            OcrSortMode.FilenameNaturalAscending => pages.OrderBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.FilenameNaturalDescending => pages.OrderByDescending(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.CreatedOldest => pages.OrderBy(p => p.CreatedUtc).ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.CreatedNewest => pages.OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.ModifiedOldest => pages.OrderBy(p => p.ModifiedUtc).ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.ModifiedNewest => pages.OrderByDescending(p => p.ModifiedUtc).ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.CaptureDateOldest => pages.OrderBy(p => p.CaptureDate ?? DateTimeOffset.MinValue)
                .ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.CaptureDateNewest => pages.OrderByDescending(p => p.CaptureDate ?? DateTimeOffset.MinValue)
                .ThenBy(p => p.FileName, NaturalStringComparer.Instance).ToList(),
            _ => pages.OrderBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static IReadOnlyList<ScannedFile> SortFiles(IReadOnlyList<ScannedFile> files, OcrSortMode mode)
    {
        return mode switch
        {
            OcrSortMode.FilenameNaturalDescending => files.OrderByDescending(f => f.RelativePath, NaturalStringComparer.Instance).ToList(),
            OcrSortMode.CreatedOldest => files.OrderBy(f => SafeTime(f.Path, true)).ToList(),
            OcrSortMode.CreatedNewest => files.OrderByDescending(f => SafeTime(f.Path, true)).ToList(),
            OcrSortMode.ModifiedOldest => files.OrderBy(f => SafeTime(f.Path, false)).ToList(),
            OcrSortMode.ModifiedNewest => files.OrderByDescending(f => SafeTime(f.Path, false)).ToList(),
            OcrSortMode.FullPathAscending => files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => files.OrderBy(f => f.RelativePath, NaturalStringComparer.Instance).ToList()
        };
    }

    private static DateTime SafeTime(string path, bool created)
    {
        try { return created ? File.GetCreationTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static async Task<DateTimeOffset?> TryGetCaptureDateAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.RetrievePropertiesAsync(["System.Photo.DateTaken"]);
            if (properties.TryGetValue("System.Photo.DateTaken", out var value) && value is DateTimeOffset date)
                return date;
        }
        catch { }
        return null;
    }

    private void SetDocumentName(string value)
    {
        _updatingDocumentName = true;
        try
        {
            DocumentName = value;
        }
        finally
        {
            _updatingDocumentName = false;
        }
    }

    private static string SanitizeDocumentName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(invalid.Contains(c) ? '_' : c);
        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "ScannedDocument" : result;
    }

    partial void OnSelectedSortChanged(OcrSortChoice? value)
    {
        if (Pages.Count > 0) ApplySort();
    }

    partial void OnJobStatusChanged(string value) => OnPropertyChanged(nameof(FooterStatus));
    partial void OnIsBusyChanged(bool value) => UpdateCanQueue();

    partial void OnSelectedModelChanged(OcrModelItem? value)
    {
        UpdateModelStatus();
        UpdateProviderStatus();
        OnPropertyChanged(nameof(ModelBadge));
        OnPropertyChanged(nameof(ModelLanguageText));
        ScheduleValidation();
    }

    partial void OnDocumentNameChanged(string value)
    {
        if (!_updatingDocumentName) _documentNameEdited = true;
    }
    partial void OnGeneratePdfChanged(bool value) => ScheduleValidation();
    partial void OnPreprocessChanged(bool value) => ScheduleValidation();
    partial void OnOutputDirectoryChanged(string value) => ScheduleValidation();
    partial void OnSelectedCollisionChanged(CollisionOption? value) => ScheduleValidation();

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnJobChanged(sender, e));
            return;
        }
        if (!_trackedJobs.Contains(e.Job.Id)) return;
        JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T(e.Job.StateText), e.Job.Title);
        if (e.Job.IsTerminal && _reviewFiles.Remove(e.Job.Id, out var reviewPath))
        {
            try { if (File.Exists(reviewPath)) File.Delete(reviewPath); } catch { }
        }
        if (e.Job.State == JobState.Completed)
        {
            var output = e.Job.Items.FirstOrDefault(i => i.State == JobItemState.Completed)?.OutputPath;
            if (!string.IsNullOrWhiteSpace(output))
            {
                LastDocxPath = output;
                HasOutput = File.Exists(output);
                JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T("Completed"), Path.GetFileName(output));
            }
        }
        else if (e.Job.State == JobState.Failed)
        {
            JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T("Failed"), e.Job.Error);
        }
    }

    [RelayCommand]
    private void OpenDocx()
    {
        if (string.IsNullOrWhiteSpace(LastDocxPath) || !File.Exists(LastDocxPath)) return;
        OpenPath(LastDocxPath);
    }

    [RelayCommand]
    private void OpenPdf()
    {
        if (string.IsNullOrWhiteSpace(LastDocxPath)) return;
        var pdf = Path.ChangeExtension(LastDocxPath, ".pdf");
        if (File.Exists(pdf)) OpenPath(pdf);
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = string.IsNullOrWhiteSpace(LastDocxPath) ? OutputDirectory : Path.GetDirectoryName(LastDocxPath);
        if (!string.IsNullOrWhiteSpace(folder)) OpenPath(folder);
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        _ocrModels.ModelsChanged -= OnModelsChanged;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _modelCts?.Cancel();
        _modelCts?.Dispose();
        foreach (var path in _reviewFiles.Values)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        _reviewFiles.Clear();
    }
}

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var nx = x[startX..i].TrimStart('0');
                var ny = y[startY..j].TrimStart('0');
                if (nx.Length == 0) nx = "0";
                if (ny.Length == 0) ny = "0";
                if (nx.Length != ny.Length) return nx.Length.CompareTo(ny.Length);
                var digitCompare = string.CompareOrdinal(nx, ny);
                if (digitCompare != 0) return digitCompare;
                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
