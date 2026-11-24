using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class VideoUpscalerViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRegistry _registry;
    private readonly IMediaInspector _inspector;
    private readonly IFolderScanner _scanner;
    private readonly IThumbnailService _thumbnails;
    private readonly IFilePickerService _filePicker;
    private readonly IJobService _jobs;
    private readonly IModelManager _models;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly IModelPanelService _modelPanel;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<Guid> _trackedJobs = [];
    private CancellationTokenSource? _validationCts;
    private CancellationTokenSource? _previewCts;
    private bool _initialized;
    private bool _disposed;
    private FolderScanResult? _lastScanResult;

    public VideoUpscalerViewModel(ICapabilityRegistry registry, IMediaInspector inspector, IFolderScanner scanner,
        IThumbnailService thumbnails, IFilePickerService filePicker, IJobService jobs, IModelManager models,
        IAppPaths paths, ISettingsService settings, INavigationService navigation, IModelPanelService modelPanel,
        IDialogService dialogs, INotificationService notifications, IAppLogger logger)
    {
        _registry = registry;
        _inspector = inspector;
        _scanner = scanner;
        _thumbnails = thumbnails;
        _filePicker = filePicker;
        _jobs = jobs;
        _models = models;
        _paths = paths;
        _settings = settings;
        _navigation = navigation;
        _modelPanel = modelPanel;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        OutputDirectory = Path.Combine(paths.DefaultOutputDirectory, "Video Upscaled");
        FileNameSuffix = "_upscaled";
        SelectedCollision = CollisionOptions[0];
        _jobs.JobChanged += OnJobChanged;
        _models.ModelsChanged += OnModelsChanged;
    }

    public ObservableCollection<MediaFileViewModel> Files { get; } = [];
    public ObservableCollection<ScanSkipItem> Skipped { get; } = [];
    public ObservableCollection<UpscaleModelItem> ImageModels { get; } = [];
    public ObservableCollection<UpscaleModelItem> InterpolationModels { get; } = [];
    public ObservableCollection<ProviderChoice> Providers { get; } = [];
    public ObservableCollection<Option<int>> ScaleOptions { get; } = [];
    public IReadOnlyList<Option<int>> FrameRateOptions { get; } =
    [
        new(2, "2× (double the frame rate)"),
        new(3, "3× (triple the frame rate)"),
        new(4, "4× (quadruple the frame rate)")
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
        foreach (var option in ScaleOptions) option.RefreshLocalization();
        foreach (var option in FrameRateOptions) option.RefreshLocalization();
        foreach (var option in CollisionOptions) option.RefreshLocalization();
        foreach (var provider in Providers) provider.RefreshLocalization();
        foreach (var model in ImageModels) model.RefreshLocalization();
        foreach (var model in InterpolationModels) model.RefreshLocalization();
        UpdateModelStatusText();
        RefreshSummary();
        if (SelectedProvider is not null) ProviderStatus = SelectedProvider.Detail;
        if (_lastScanResult is not null && !IsScanning)
            ScanSummary = LocalizationService.ScanSummary(_lastScanResult);
    }

    [ObservableProperty] private bool _enableSuperResolution = true;
    [ObservableProperty] private bool _enableInterpolation;
    [ObservableProperty] private bool _enableTemporalStability;
    [ObservableProperty] private double _temporalStabilityStrength = 50;
    [ObservableProperty] private UpscaleModelItem? _selectedImageModel;
    [ObservableProperty] private UpscaleModelItem? _selectedInterpolationModel;
    [ObservableProperty] private Option<int>? _selectedScale;
    [ObservableProperty] private Option<int>? _selectedFrameRate;
    [ObservableProperty] private ProviderChoice? _selectedProvider;
    [ObservableProperty] private double _videoBitrateKbps = 12000;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private string _fileNameSuffix = "_upscaled";
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private bool _hasFiles;
    [ObservableProperty] private bool _canQueue;
    [ObservableProperty] private bool _canCompare;
    [ObservableProperty] private string _summary = "Add a video to begin";
    [ObservableProperty] private string _statusText = "Super-resolution and frame interpolation run locally through the shared processing queue.";
    [ObservableProperty] private string _expectedOutput = "";
    [ObservableProperty] private string _imageModelStatus = "";
    [ObservableProperty] private string _interpolationModelStatus = "";
    [ObservableProperty] private string _providerStatus = "";
    [ObservableProperty] private string _scanSummary = "";
    [ObservableProperty] private bool _hasScanSummary;
    [ObservableProperty] private bool _hasScanSkipped;
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private string _jobStatus = "";
    [ObservableProperty] private string? _lastInputPath;
    [ObservableProperty] private string? _lastOutputPath;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (!_registry.IsInitialized) await _registry.InitializeAsync();
            await _models.InitializeAsync();
            LoadProviders();
            RefreshModels();
            ApplyDefaults();
            RefreshSummary();
            ScheduleValidation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video upscaler initialization failed");
            await _dialogs.ShowErrorAsync("Video Upscaler unavailable",
                "The video processing engines could not be initialized.", ex.ToString());
        }
    }

    public async Task AddVideosAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _lastScanResult = null;
        try
        {
            var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;
            var infos = await _inspector.InspectManyAsync(files, MediaCategory.Video);
            var added = 0;
            foreach (var info in infos)
            {
                if (!info.IsReadable || info.Category != MediaCategory.Video)
                {
                    _notifications.Notify("Video skipped", LocalizationService.F("{0}: not a readable video.", info.FileName),
                        NotificationSeverity.Warning);
                    continue;
                }
                if (Files.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Files.Add(new MediaFileViewModel(info, _thumbnails, RemoveFile));
                added++;
            }
            if (added == 0) return;
            RefreshFileState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import videos into the Video Upscaler");
            await _dialogs.ShowErrorAsync("Videos could not be imported", ex.Message, ex.ToString());
        }
    }

    public async Task AddFolderAsync(string folder)
    {
        if (IsScanning) return;
        _lastScanResult = null;
        IsScanning = true;
        HasScanSummary = true;
        ScanSummary = Loc.T("Scanning folder…");
        Skipped.Clear();
        HasScanSkipped = false;
        try
        {
            var progress = new Progress<int>(count =>
                _dispatcher.TryEnqueue(() => ScanSummary = LocalizationService.P("Scan.CheckedFiles", count)));
            var result = await _scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = folder,
                Recursive = IncludeSubfolders,
                PreserveFolderStructure = IncludeSubfolders,
                TargetCategory = MediaCategory.Video,
                ExcludeDirectory = OutputDirectory
            }, progress);

            var added = 0;
            foreach (var file in result.IncludedFiles)
            {
                if (file.Info is null) continue;
                if (Files.Any(f => string.Equals(f.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                Files.Add(new MediaFileViewModel(file.Info, _thumbnails, RemoveFile, file.RelativeDirectory));
                added++;
            }
            foreach (var file in result.Files.Where(f => !f.IsSupported || !f.IsValid))
                Skipped.Add(new ScanSkipItem(file.RelativePath, file.Reason ?? "Skipped", !file.IsSupported));
            HasScanSkipped = Skipped.Count > 0;
            ScanSummary = LocalizationService.ScanSummary(result);
            _lastScanResult = result;
            HasScanSummary = true;
            RefreshFileState();
            if (added > 0)
                _notifications.Notify("Folder scan complete", ScanSummary, NotificationSeverity.Success);
            else
                await _dialogs.ShowMessageAsync("No videos added",
                    "The folder did not contain readable videos supported by this page.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video folder scan failed");
            await _dialogs.ShowErrorAsync("Folder scan failed", ex.Message, ex.ToString());
        }
        finally
        {
            IsScanning = false;
            UpdateCanQueue();
        }
    }

    [RelayCommand]
    private async Task BrowseVideosAsync()
    {
        var files = await _filePicker.PickFilesAsync(MediaCategory.Video);
        if (files.Count > 0) await AddVideosAsync(files);
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) await AddFolderAsync(folder);
    }

    [RelayCommand]
    private async Task ClearFilesAsync()
    {
        if (Files.Count == 0 && Skipped.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear the video upscaler?",
                "Remove all imported videos and scan results? No files on disk will be deleted.",
                "Clear workspace", "Keep", destructive: true)) return;
        Files.Clear();
        Skipped.Clear();
        HasScanSkipped = false;
        HasScanSummary = false;
        _lastScanResult = null;
        ScanSummary = "";
        PreviewImage = null;
        ShowPreview = false;
        RefreshFileState();
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        OutputDirectory = folder;
        RefreshSummary();
    }

    [RelayCommand]
    private void OpenOutput()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = OutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the destination", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenInput()
    {
        var file = Files.FirstOrDefault();
        if (file is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the video", ex.Message);
        }
    }

    [RelayCommand]
    private void ManageModels() => _modelPanel.Show(ModelPanelFilter.VideoUpscaling);

    [RelayCommand]
    private void ManageInterpolationModels() => _modelPanel.Show(ModelPanelFilter.FrameInterpolation);

    [RelayCommand]
    private async Task CompareLastAsync()
    {
        if (!CanCompare || LastInputPath is null || LastOutputPath is null) return;
        try
        {
            var input = await _thumbnails.GetThumbnailPathAsync(LastInputPath, MediaCategory.Video, 1024);
            var output = await _thumbnails.GetThumbnailPathAsync(LastOutputPath, MediaCategory.Video, 1024);
            if (input is null || output is null)
            {
                await _dialogs.ShowMessageAsync("Comparison unavailable",
                    "A representative frame could not be extracted from one of the videos.");
                return;
            }
            _navigation.Navigate("Compare", new ComparisonRequest(input, output, "Video frame comparison"));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not prepare the comparison", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (Files.Count == 0)
        {
            await _dialogs.ShowMessageAsync("No videos to process", "Add at least one video first.");
            return;
        }
        if (!EnableSuperResolution && !EnableInterpolation)
        {
            await _dialogs.ShowMessageAsync("Choose an operation",
                "Enable super-resolution, frame interpolation, or both.");
            return;
        }
        if (EnableSuperResolution && SelectedImageModel?.IsInstalled != true)
        {
            await PromptForModel();
            return;
        }
        if (EnableInterpolation && SelectedInterpolationModel?.IsInstalled != true)
        {
            await PromptForModel();
            return;
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
            var proceed = await _dialogs.ConfirmAsync("Review warnings",
                string.Join("\n\n", validation.Warnings.Select(w => w.Message)),
                "Process anyway", "Go back");
            if (!proceed) return;
        }
        if (SelectedCollision?.Policy == CollisionPolicy.Overwrite)
        {
            var proceed = await _dialogs.ConfirmAsync("Replace existing files?",
                "The output folder may already contain files with the same names. Source files are never touched.",
                "Use Replace mode", "Cancel", destructive: true);
            if (!proceed) return;
        }

        try
        {
            var job = await _jobs.EnqueueAsync(request);
            _trackedJobs.Add(job.Id);
            _notifications.Notify("Added to processing queue", job.Title, NotificationSeverity.Success);
            Files.Clear();
            Skipped.Clear();
            HasScanSkipped = false;
            HasScanSummary = false;
            _lastScanResult = null;
            ScanSummary = "";
            PreviewImage = null;
            ShowPreview = false;
            JobStatus = Loc.T("Queued · waiting for processing");
            RefreshFileState();
            _navigation.Navigate("Queue");
        }
        catch (JobValidationException ex)
        {
            SetValidation(ex.Validation);
            await _dialogs.ShowJobValidationAsync(ex.Validation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enqueue video upscaling job");
            await _dialogs.ShowErrorAsync("Could not add the video", ex.Message, ex.ToString());
        }
    }

    public void ApplyDefaults()
    {
        var defaults = _settings.Settings.VideoUpscaler;

        EnableSuperResolution = defaults.EnableSuperResolution;
        EnableInterpolation = defaults.EnableInterpolation;
        EnableTemporalStability = defaults.EnableTemporalStability;
        TemporalStabilityStrength = defaults.TemporalStabilityStrength;

        SelectedImageModel = ImageModels.FirstOrDefault(m => m.Id == defaults.ImageModelId)
                             ?? SelectedImageModel
                             ?? ImageModels.FirstOrDefault();
        SelectedInterpolationModel = InterpolationModels.FirstOrDefault(m => m.Id == defaults.InterpolationModelId)
                                     ?? SelectedInterpolationModel
                                     ?? InterpolationModels.FirstOrDefault();
        SelectedFrameRate = FrameRateOptions.FirstOrDefault(o => o.Value == defaults.FrameRateMultiplier)
                            ?? FrameRateOptions[0];
        SelectedProvider = Providers.FirstOrDefault(p => p.Kind == defaults.ExecutionProvider && p.IsAvailable)
                           ?? Providers.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Auto)
                           ?? Providers.FirstOrDefault();
        VideoBitrateKbps = Math.Clamp(defaults.VideoBitrateKbps, 100, 200_000);
        FileNameSuffix = string.IsNullOrWhiteSpace(defaults.FileNameSuffix) ? "_upscaled" : defaults.FileNameSuffix;
        IncludeSubfolders = defaults.IncludeSubfolders;
        SelectedCollision = CollisionOptions.FirstOrDefault(c => c.Policy == defaults.CollisionPolicy)
                            ?? CollisionOptions[0];
        OutputDirectory = string.IsNullOrWhiteSpace(defaults.DefaultOutputDirectory)
            ? Path.Combine(_paths.DefaultOutputDirectory, "Video Upscaled")
            : defaults.DefaultOutputDirectory;

        RefreshModelStatus();
        RefreshSummary();
        ScheduleValidation();
    }

    private async Task PromptForModel()
    {
        var manage = await _dialogs.ConfirmAsync("AI model required",
            "Install the required model before starting this video job. Open Model Management now?",
            "Manage models", "Cancel");
        if (manage) _modelPanel.Show(ModelPanelFilter.VideoUpscaling);
    }

    private JobRequest BuildRequest()
    {
        var scale = SelectedImageModel?.Descriptor.ScaleFactor ?? 4;
        var settings = new VideoUpscaleSettings
        {
            OutputDirectory = OutputDirectory,
            CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName,
            FileNameSuffix = FileNameSuffix,
            ContainerFormatId = "mp4",
            VideoEncoderId = "",
            VideoBitrateKbps = (int)Math.Clamp(VideoBitrateKbps, 100, 200_000),
            AudioCodecId = "copy",
            EnableSuperResolution = EnableSuperResolution,
            ImageModelId = SelectedImageModel?.Id ?? "",
            ScaleFactor = scale,
            EnableInterpolation = EnableInterpolation,
            InterpolationModelId = SelectedInterpolationModel?.Id ?? "",
            FrameRateMultiplier = SelectedFrameRate?.Value ?? 2,
            EnableTemporalStability = EnableTemporalStability,
            TemporalStabilityStrength = (int)Math.Clamp(TemporalStabilityStrength, 0, 100),
            ExecutionProvider = SelectedProvider?.Kind ?? ExecutionProviderKind.Auto,
            TileSize = 0,
            TileOverlap = 0
        };
        return new JobRequest
        {
            Operation = OperationKind.VideoUpscale,
            Title = LocalizationService.F("Video upscale {0}", LocalizationService.P("Count.Video", Files.Count)),
            Settings = settings,
            Inputs = Files.Select(f => new JobInput
            {
                Path = f.FilePath,
                Info = f.Info,
                RelativeDirectory = f.RelativeDirectory
            }).ToList(),
            PreserveRelativePaths = Files.Any(f => !string.IsNullOrWhiteSpace(f.RelativeDirectory))
        };
    }

    private void RefreshModels()
    {
        var previousImage = SelectedImageModel?.Id;
        var previousInterp = SelectedInterpolationModel?.Id;
        ImageModels.Clear();
        InterpolationModels.Clear();
        var installed = _models.InstalledModels.ToDictionary(m => m.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _models.AvailableModels
                     .Where(m => m.Task == ModelTask.ImageUpscale && m.SupportsVideoFrames)
                     .OrderByDescending(m => installed.ContainsKey(m.Id)).ThenBy(m => m.SortOrder))
        {
            installed.TryGetValue(descriptor.Id, out var model);
            ImageModels.Add(new UpscaleModelItem(descriptor, model));
        }
        foreach (var descriptor in _models.AvailableModels
                     .Where(m => m.Task == ModelTask.FrameInterpolation && m.SupportsVideoFrames)
                     .OrderByDescending(m => installed.ContainsKey(m.Id)).ThenBy(m => m.SortOrder))
        {
            installed.TryGetValue(descriptor.Id, out var model);
            InterpolationModels.Add(new UpscaleModelItem(descriptor, model));
        }

        SelectedImageModel = ImageModels.FirstOrDefault(m => m.Id == previousImage && m.IsInstalled)
                             ?? ImageModels.FirstOrDefault(m => m.Id == "realesrgan-x4plus") 
                             ?? ImageModels.FirstOrDefault();
        SelectedInterpolationModel = InterpolationModels.FirstOrDefault(m => m.Id == previousInterp && m.IsInstalled)
                                     ?? InterpolationModels.FirstOrDefault(m => m.Id == "rife-4.25-lite")
                                     ?? InterpolationModels.FirstOrDefault();
        RefreshModelStatus();
        RefreshSummary();
        ScheduleValidation();
    }

    private void LoadProviders()
    {
        Providers.Clear();
        Providers.Add(new ProviderChoice(ExecutionProviderKind.Auto, "Automatic (GPU when available)", true,
            "DirectML when available, otherwise CPU."));
        foreach (var provider in _registry.ExecutionProviders.Where(p => p.Kind != ExecutionProviderKind.Vulkan))
        {
            Providers.Add(new ProviderChoice(provider.Kind, provider.DisplayName, provider.IsAvailable,
                provider.Details ?? ""));
        }
        SelectedProvider = Providers.FirstOrDefault(p => p.IsAvailable) ?? Providers.First();
        ProviderStatus = SelectedProvider?.Detail ?? "";
    }

    private void RefreshModelStatus()
    {
        UpdateModelStatusText();

        var scaleOptions = new List<Option<int>>();
        if (SelectedImageModel is not null)
        {
            var native = SelectedImageModel.Descriptor.ScaleFactor;
            scaleOptions.Add(new Option<int>(native, Loc.F("{0}× (native)", native)));
        }
        var previousScale = SelectedScale?.Value;
        ScaleOptions.Clear();
        foreach (var option in scaleOptions) ScaleOptions.Add(option);
        SelectedScale = ScaleOptions.FirstOrDefault(o => o.Value == previousScale) ?? ScaleOptions.FirstOrDefault();

        OnPropertyChanged(nameof(ShowImageSettings));
        OnPropertyChanged(nameof(ShowInterpolationSettings));
        UpdateCanQueue();
    }

    private void UpdateModelStatusText()
    {
        if (SelectedImageModel is null)
        {
            ImageModelStatus = Loc.T("No image upscaling model is installed. Open Manage to download one.");
        }
        else
        {
            ImageModelStatus = SelectedImageModel.IsInstalled
                ? Loc.F("{0} · {1} · installed", SelectedImageModel.Name, SelectedImageModel.ScaleText)
                : Loc.F("{0} · download required before queueing", SelectedImageModel.Name);
        }

        InterpolationModelStatus = SelectedInterpolationModel is null
            ? Loc.T("No frame interpolation model is installed. Open Manage to download RIFE.")
            : SelectedInterpolationModel.IsInstalled
                ? Loc.F("{0} · installed", SelectedInterpolationModel.Name)
                : Loc.F("{0} · download required before queueing", SelectedInterpolationModel.Name);
    }

    public bool ShowImageSettings => EnableSuperResolution;
    public bool ShowInterpolationSettings => EnableInterpolation;
    public bool ShowTemporalSettings => EnableSuperResolution || EnableInterpolation;

    private void RefreshFileState()
    {
        HasFiles = Files.Count > 0;
        OnPropertyChanged(nameof(HasFiles));
        RefreshPreview();
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    private void RefreshPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var file = Files.FirstOrDefault();
        if (file is null)
        {
            PreviewImage = null;
            ShowPreview = false;
            PreviewStatus = "";
            return;
        }

        _ = LoadPreviewAsync(file, token);
    }

    private async Task LoadPreviewAsync(MediaFileViewModel file, CancellationToken cancellationToken)
    {
        try
        {
            var path = await _thumbnails.GetThumbnailPathAsync(file.FilePath, MediaCategory.Video, 720);
            if (cancellationToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowPreview = false;
                PreviewStatus = Loc.T("Preview frame unavailable.");
                return;
            }

            void Apply()
            {
                PreviewImage = new BitmapImage { UriSource = new Uri(path) };
                ShowPreview = true;
                PreviewStatus = $"{file.FileName} · {file.Detail}";
            }
            if (_dispatcher.HasThreadAccess) Apply();
            else _dispatcher.TryEnqueue(Apply);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Video preview failed: {Message}", ex.Message);
            ShowPreview = false;
            PreviewStatus = Loc.T("Preview frame unavailable.");
        }
    }

    private void RefreshSummary()
    {
        if (Files.Count == 0)
        {
            Summary = Loc.T("Add a video to begin");
            ExpectedOutput = "";
            return;
        }

        var operations = new List<string>();
        if (EnableSuperResolution && SelectedImageModel is not null)
            operations.Add(Loc.F("AI {0}", SelectedImageModel.ScaleText));
        if (EnableInterpolation)
            operations.Add(Loc.F("{0}× frame interpolation", SelectedFrameRate?.Value ?? 2));
        if (operations.Count == 0) operations.Add(Loc.T("No operation enabled"));

        var first = Files[0].Info;
        var scale = EnableSuperResolution ? SelectedImageModel?.Descriptor.ScaleFactor ?? 4 : 1;
        var fps = (first.FrameRate ?? 30) * (EnableInterpolation ? SelectedFrameRate?.Value ?? 2 : 1);
        var width = first.Width is > 0 ? first.Width.Value * scale : 0;
        var height = first.Height is > 0 ? first.Height.Value * scale : 0;
        ExpectedOutput = width > 0 && height > 0
            ? $"{width} × {height} · {fps:0.##} fps"
            : $"{fps:0.##} fps";
        Summary = Loc.F("{0} · {1}", Loc.P("Count.Video", Files.Count), string.Join(" + ", operations));
        StatusText = Loc.F("{0} · {1}", operations[0], ExpectedOutput);
    }

    private void UpdateCanQueue()
    {
        var modelsReady = (!EnableSuperResolution || SelectedImageModel?.IsInstalled == true) &&
                          (!EnableInterpolation || SelectedInterpolationModel?.IsInstalled == true);
        CanQueue = Files.Count > 0 && modelsReady && (EnableSuperResolution || EnableInterpolation) &&
                   !IsScanning && !HasValidationErrors;
    }

    private void ScheduleValidation()
    {
        if (!_initialized) return;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        _ = ValidateAfterDelayAsync(_validationCts.Token);
    }

    private async Task ValidateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (Files.Count == 0)
            {
                SetValidation(null);
                return;
            }
            IsValidating = true;
            var validation = await _jobs.ValidateAsync(BuildRequest(), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            SetValidation(validation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Video upscaler validation failed: {Message}", ex.Message);
        }
        finally
        {
            IsValidating = false;
        }
    }

    private void SetValidation(ValidationResult? validation)
    {
        if (validation is null)
        {
            ValidationSummary = "";
            HasValidationErrors = false;
            HasValidationWarnings = false;
        }
        else
        {
            HasValidationErrors = validation.Errors.Count > 0;
            HasValidationWarnings = validation.Warnings.Count > 0;
            ValidationSummary = LocalizationService.ValidationSummary(validation);
        }
        UpdateCanQueue();
    }

    private void RemoveFile(MediaFileViewModel file)
    {
        Files.Remove(file);
        RefreshFileState();
    }

    private void OnModelsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnModelsChanged(sender, e));
            return;
        }
        RefreshModels();
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (_disposed || !_trackedJobs.Contains(e.Job.Id)) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnJobChanged(sender, e));
            return;
        }

        if (!e.Job.IsTerminal)
        {
            JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T(e.Job.StateText), e.Job.Progress.ToString("P0", LocalizationService.CurrentCulture));
            return;
        }

        var item = e.Job.Items.FirstOrDefault(i => i.OutputPath is not null);
        if (e.Job.State == JobState.Completed && item?.OutputPath is not null)
        {
            LastInputPath = item.InputPath;
            LastOutputPath = item.OutputPath;
            CanCompare = true;
            JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T("Completed"), item.VerificationSummary ?? Path.GetFileName(item.OutputPath));
            _ = RefreshCompletedPreviewAsync(item.OutputPath);
        }
        else
        {
            JobStatus = LocalizationService.F("{0} · {1}", LocalizationService.T(e.Job.StateText), e.Job.Error ?? item?.Error ?? LocalizationService.T("The video job did not complete."));
        }
    }

    private async Task RefreshCompletedPreviewAsync(string outputPath)
    {
        var path = await _thumbnails.GetThumbnailPathAsync(outputPath, MediaCategory.Video, 720);
        if (path is null) return;
        if (_dispatcher.HasThreadAccess)
        {
            PreviewImage = new BitmapImage { UriSource = new Uri(path) };
            ShowPreview = true;
            PreviewStatus = Loc.T("Last output frame");
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                PreviewImage = new BitmapImage { UriSource = new Uri(path) };
                ShowPreview = true;
                PreviewStatus = Loc.T("Last output frame");
            });
        }
    }

    partial void OnEnableSuperResolutionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowImageSettings));
        OnPropertyChanged(nameof(ShowTemporalSettings));
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    partial void OnEnableInterpolationChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInterpolationSettings));
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    partial void OnEnableTemporalStabilityChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTemporalSettings));
        ScheduleValidation();
    }

    partial void OnTemporalStabilityStrengthChanged(double value) => ScheduleValidation();

    partial void OnSelectedImageModelChanged(UpscaleModelItem? value)
    {
        RefreshModelStatus();
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    partial void OnSelectedInterpolationModelChanged(UpscaleModelItem? value)
    {
        RefreshModelStatus();
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    partial void OnSelectedFrameRateChanged(Option<int>? value)
    {
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnSelectedProviderChanged(ProviderChoice? value)
    {
        ProviderStatus = value?.Detail ?? "";
    }

    partial void OnSelectedCollisionChanged(CollisionOption? value) => ScheduleValidation();
    partial void OnFileNameSuffixChanged(string value) => ScheduleValidation();
    partial void OnVideoBitrateKbpsChanged(double value) => ScheduleValidation();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        _models.ModelsChanged -= OnModelsChanged;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
    }
}
