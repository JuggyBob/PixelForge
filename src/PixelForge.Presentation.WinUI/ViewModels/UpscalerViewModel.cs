using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed record ScanSkipItem(string FileName, string Reason, bool IsUnsupported);

public sealed class UpscaleModelItem
{
    public UpscaleModelItem(ModelDescriptor descriptor, InstalledModel? installed)
    {
        Descriptor = descriptor;
        Installed = installed;
    }

    public ModelDescriptor Descriptor { get; }
    public InstalledModel? Installed { get; }
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Version => Descriptor.Version;
    public string Architecture => Descriptor.Architecture;
    public string IntendedUse => Descriptor.IntendedUse;
    public string ScaleText => Descriptor.Task switch
    {
        ModelTask.FrameInterpolation => "Frame interpolation",
        ModelTask.ImageRestore => "Restoration · 1×",
        _ => $"{Descriptor.ScaleFactor}×"
    };
    public string SizeText => Descriptor.DisplaySize;
    public string Tags => string.Join(" · ", Descriptor.Tags);
    public bool IsInstalled => Installed is { IsValid: true };
    public bool NeedsValidation => Installed is { IsValid: false };
    public string StatusText => IsInstalled ? "Installed" : NeedsValidation ? "Needs validation" : "Not installed";
    public string Detail => IsInstalled
        ? $"{Descriptor.ScaleFactor}× · {Descriptor.DisplaySize} · installed"
        : $"{Descriptor.ScaleFactor}× · {Descriptor.DisplaySize} · download required";
    public override string ToString() => Name;
}

public sealed record ProviderChoice(ExecutionProviderKind Kind, string Label, bool IsAvailable, string Detail)
{
    public string DisplayName => IsAvailable ? Label : Label + " (unavailable)";
    public override string ToString() => DisplayName;
}

public sealed record UpscaleFormatChoice(string Id, string Label, string Extension, bool IsLossy, bool IsAuto)
{
    public override string ToString() => Label;
}

public sealed partial class UpscalerViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRegistry _registry;
    private readonly IMediaInspector _inspector;
    private readonly IFolderScanner _scanner;
    private readonly IThumbnailService _thumbnails;
    private readonly IFilePickerService _filePicker;
    private readonly IJobService _jobs;
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly INavigationService _navigation;
    private readonly IModelPanelService _modelPanel;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly HashSet<Guid> _trackedJobs = [];
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _validationCts;
    private bool _initialized;
    private bool _disposed;

    public UpscalerViewModel(ICapabilityRegistry registry, IMediaInspector inspector, IFolderScanner scanner,
        IThumbnailService thumbnails, IFilePickerService filePicker, IJobService jobs, IModelManager models,
        ISettingsService settings, IAppPaths paths, INavigationService navigation, IModelPanelService modelPanel,
        IDialogService dialogs, INotificationService notifications, IAppLogger logger)
    {
        _registry = registry;
        _inspector = inspector;
        _scanner = scanner;
        _thumbnails = thumbnails;
        _filePicker = filePicker;
        _jobs = jobs;
        _models = models;
        _settings = settings;
        _paths = paths;
        _navigation = navigation;
        _modelPanel = modelPanel;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _jobs.JobChanged += OnJobChanged;
        _models.ModelsChanged += OnModelsChanged;
    }

    public ObservableCollection<MediaFileViewModel> Files { get; } = [];
    public ObservableCollection<ScanSkipItem> Skipped { get; } = [];
    public ObservableCollection<UpscaleModelItem> Models { get; } = [];
    public ObservableCollection<ProviderChoice> Providers { get; } = [];
    public ObservableCollection<UpscaleFormatChoice> OutputFormats { get; } = [];
    public ObservableCollection<Option<ResamplingAlgorithm>> Algorithms { get; } =
    [
        new(ResamplingAlgorithm.NearestNeighbor, "Nearest Neighbor"),
        new(ResamplingAlgorithm.Bilinear, "Bilinear"),
        new(ResamplingAlgorithm.Bicubic, "Bicubic"),
        new(ResamplingAlgorithm.Lanczos, "Lanczos"),
        new(ResamplingAlgorithm.MitchellNetravali, "Mitchell-Netravali")
    ];
    public ObservableCollection<Option<int>> ScaleOptions { get; } =
    [
        new(2, "2×"),
        new(3, "3×"),
        new(4, "4×"),
        new(1, "1× (restoration)"),
        new(0, "Custom dimensions")
    ];
    public ObservableCollection<Option<int>> NoiseLevels { get; } = [];
    public ObservableCollection<CollisionOption> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number", "Never replace existing files."),
        new(CollisionPolicy.Skip, "Skip existing files", "Leave existing outputs untouched."),
        new(CollisionPolicy.Overwrite, "Replace existing files", "Overwrite after confirmation.")
    ];

    [ObservableProperty] private bool _isTraditional = true;
    [ObservableProperty] private bool _isAi;
    [ObservableProperty] private Option<ResamplingAlgorithm>? _selectedAlgorithm;
    [ObservableProperty] private Option<int>? _selectedScale;
    [ObservableProperty] private bool _useCustomSize;
    [ObservableProperty] private double? _customWidth;
    [ObservableProperty] private double? _customHeight;
    [ObservableProperty] private bool _preserveAspect = true;
    [ObservableProperty] private bool _autoTileSize = true;
    [ObservableProperty] private double _tileSize = 512;
    [ObservableProperty] private double _tileOverlap = 32;
    [ObservableProperty] private UpscaleModelItem? _selectedModel;
    [ObservableProperty] private Option<int>? _selectedNoiseLevel;
    [ObservableProperty] private ProviderChoice? _selectedProvider;
    [ObservableProperty] private UpscaleFormatChoice? _selectedOutputFormat;
    [ObservableProperty] private double _outputQuality = 95;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private string _fileNameSuffix = "";
    [ObservableProperty] private bool _preserveMetadata = true;
    [ObservableProperty] private bool _preserveAlpha = true;
    [ObservableProperty] private bool _preserveIccProfile = true;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private bool _preserveFolderStructure = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private bool _canQueue;
    [ObservableProperty] private string _scanSummary = "";
    [ObservableProperty] private bool _hasScanSummary;
    [ObservableProperty] private bool _hasSkipped;
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private string _modelStatus = "Choose an AI model";
    [ObservableProperty] private string _providerStatus = "Automatic provider selection";
    [ObservableProperty] private string _summary = "Import images to begin";
    [ObservableProperty] private string? _lastInputPath;
    [ObservableProperty] private string? _lastOutputPath;
    [ObservableProperty] private bool _canCompare;
    [ObservableProperty] private bool _hasFiles;

    public bool ShowAlgorithm => IsTraditional;
    public bool ShowAi => IsAi;
    public bool ShowCustomSize => SelectedScale?.Value == 0;
    public bool ShowTileControls => IsAi;
    public bool ShowNoiseLevel => IsAi && SelectedModel?.Descriptor.Backend == ModelBackendKind.NcnnVulkan &&
                                  NoiseLevels.Count > 0;
    public string NoiseLevelHint => SelectedNoiseLevel?.Value switch
    {
        -1 => "No denoising; closest to the original pixels.",
        0 => "Conservative restoration with minimal texture removal.",
        1 => "Light denoising.",
        2 => "Balanced denoising.",
        3 => "Strong denoising for noisy or heavily compressed sources.",
        _ => ""
    };
    public bool ShowOutputQuality => SelectedOutputFormat is { IsAuto: false, IsLossy: true } || SelectedOutputFormat is { IsAuto: true };
    public string ScaleHint => SelectedScale?.Value == 0
        ? "Exact output dimensions"
        : SelectedModel?.Descriptor.Task == ModelTask.ImageRestore
            ? SelectedScale?.Value == 1
                ? "Restoration model · output stays at the source resolution."
                : $"{SelectedModel.Name} is a 1× restoration model; a conventional {SelectedScale?.Value}× resize will be applied after restoration."
            : SelectedModel is { IsInstalled: true } model && !UseCustomSize && SelectedScale?.Value != model.Descriptor.ScaleFactor
                ? $"{model.Descriptor.Name} is {model.Descriptor.ScaleFactor}×; a secondary resize will produce {SelectedScale?.Value}×."
                : "Native scale";
    public bool HasSelectedModel => SelectedModel?.IsInstalled == true;
    public string SelectedModelDetail => SelectedModel is null
        ? "No model selected"
        : $"{SelectedModel.Architecture} · {SelectedModel.IntendedUse}";

    public async Task InitializeAsync(UpscalerImportRequest? request = null)
    {
        if (_initialized)
        {
            if (request is not null) await HandleImportRequestAsync(request);
            return;
        }

        _initialized = true;
        try
        {
            if (!_registry.IsInitialized) await _registry.InitializeAsync();
            await _models.InitializeAsync();
            LoadProviders();
            LoadOutputFormats();
            RefreshModels();
            ApplyDefaults();
            if (request is not null) await HandleImportRequestAsync(request);
            RefreshSummary();
            ScheduleValidation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upscaler initialization failed");
            await _dialogs.ShowErrorAsync("Upscaler unavailable", "The upscaling engines could not be initialized.", ex.ToString());
        }
    }

    private void ApplyDefaults()
    {
        var defaults = _settings.Settings.Upscaler;
        IsTraditional = defaults.Method == UpscaleMethod.Traditional;
        IsAi = !IsTraditional;
        SelectedAlgorithm = Algorithms.FirstOrDefault(a => a.Value == defaults.Algorithm) ?? Algorithms[3];
        SelectedScale = ScaleOptions.FirstOrDefault(s => s.Value == defaults.ScaleFactor)
                        ?? ScaleOptions.FirstOrDefault(s => s.Value == 4)
                        ?? ScaleOptions[0];
        IncludeSubfolders = defaults.IncludeSubfolders;
        PreserveFolderStructure = defaults.PreserveFolderStructure;
        OutputDirectory = string.IsNullOrWhiteSpace(defaults.DefaultOutputDirectory)
            ? _paths.DefaultUpscaleOutputDirectory
            : defaults.DefaultOutputDirectory;
        SelectedCollision = CollisionOptions.FirstOrDefault(c => c.Policy == _settings.Settings.Converter.CollisionPolicy) ?? CollisionOptions[0];
        FileNameSuffix = defaults.FileNameSuffix;
        OutputQuality = defaults.OutputQuality;
        PreserveMetadata = defaults.PreserveMetadata;
        AutoTileSize = defaults.TileSize <= 0;
        TileSize = defaults.TileSize > 0 ? defaults.TileSize : 512;
        TileOverlap = defaults.TileOverlap > 0 ? defaults.TileOverlap : 32;
        SelectedOutputFormat = OutputFormats.FirstOrDefault(f => string.Equals(f.Id, defaults.OutputFormatId, StringComparison.OrdinalIgnoreCase))
                                ?? OutputFormats[0];
        SelectedProvider = Providers.FirstOrDefault(p => p.Kind == defaults.ExecutionProvider)
                           ?? Providers.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Auto)
                           ?? Providers.FirstOrDefault();
        RefreshModelState();
        OnPropertyChanged(nameof(ShowAlgorithm));
        OnPropertyChanged(nameof(ShowAi));
        OnPropertyChanged(nameof(ShowCustomSize));
        OnPropertyChanged(nameof(ShowTileControls));
        OnPropertyChanged(nameof(ShowOutputQuality));
        OnPropertyChanged(nameof(ScaleHint));
    }

    private void LoadProviders()
    {
        var list = new List<ProviderChoice>
        {
            new(ExecutionProviderKind.Auto, "Automatic", true, "Choose the best available backend for the selected model")
        };
        foreach (var provider in _registry.ExecutionProviders.OrderBy(p => p.Kind))
        {
            list.Add(new ProviderChoice(provider.Kind,
                provider.Kind == ExecutionProviderKind.Cpu ? "CPU" : provider.DisplayName,
                provider.IsAvailable,
                provider.Details ?? ""));
        }

        Providers.Clear();
        foreach (var item in list) Providers.Add(item);
        SelectedProvider = Providers.FirstOrDefault(p => p.Kind == _settings.Settings.Upscaler.ExecutionProvider)
                           ?? Providers[0];
        UpdateProviderStatus();
    }

    private void LoadOutputFormats()
    {
        OutputFormats.Clear();
        OutputFormats.Add(new UpscaleFormatChoice("auto", "Same as source", "", true, true));
        foreach (var format in _registry.ImageOutputFormats)
            OutputFormats.Add(new UpscaleFormatChoice(format.Id, format.DisplayName, format.NormalizedExtension, format.IsLossy, false));
        SelectedOutputFormat = OutputFormats.FirstOrDefault(f => f.Id == _settings.Settings.Upscaler.OutputFormatId) ?? OutputFormats[0];
    }

    private void RefreshModels()
    {
        var previous = SelectedModel?.Id;
        Models.Clear();
        var installed = _models.InstalledModels.ToDictionary(m => m.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _models.AvailableModels
                     .Where(m => (m.Task == ModelTask.ImageUpscale || m.Task == ModelTask.ImageRestore) &&
                                 m.SupportsStillImage)
                     .OrderByDescending(m => installed.ContainsKey(m.Id)).ThenBy(m => m.SortOrder))
        {
            installed.TryGetValue(descriptor.Id, out var model);
            Models.Add(new UpscaleModelItem(descriptor, model));
        }

        var desired = previous ?? _settings.Settings.Upscaler.ModelId;
        SelectedModel = Models.FirstOrDefault(m => m.Id == desired && m.IsInstalled)
                        ?? Models.FirstOrDefault(m => m.IsInstalled)
                        ?? Models.FirstOrDefault(m => m.Id == desired)
                        ?? Models.FirstOrDefault();
        RefreshModelState();
    }

    private void RefreshModelState()
    {
        if (SelectedModel is null)
        {
            ModelStatus = "No AI model available";
        }
        else if (SelectedModel.IsInstalled)
        {
            ModelStatus = $"{SelectedModel.Name} · {SelectedModel.ScaleText} · installed";
        }
        else
        {
            ModelStatus = $"{SelectedModel.Name} · download required";
        }

        RefreshNoiseLevels();
        OnPropertyChanged(nameof(HasSelectedModel));
        OnPropertyChanged(nameof(SelectedModelDetail));
        OnPropertyChanged(nameof(ScaleHint));
        OnPropertyChanged(nameof(ShowNoiseLevel));
        OnPropertyChanged(nameof(NoiseLevelHint));
        UpdateProviderStatus();
        UpdateCanQueue();
    }

    private void RefreshNoiseLevels()
    {
        var previous = SelectedNoiseLevel?.Value;
        NoiseLevels.Clear();
        var descriptor = SelectedModel?.Descriptor;
        if (descriptor?.Backend == ModelBackendKind.NcnnVulkan)
        {
            foreach (var level in descriptor.SupportedNoiseLevels)
                NoiseLevels.Add(new Option<int>(level, level switch
                {
                    -1 => "No denoise",
                    0 => "Conservative / light",
                    1 => "Denoise 1×",
                    2 => "Denoise 2×",
                    3 => "Denoise 3×",
                    _ => $"Level {level}"
                }));
        }

        SelectedNoiseLevel = NoiseLevels.FirstOrDefault(o => o.Value == previous)
                             ?? NoiseLevels.FirstOrDefault(o => o.Value == descriptor?.DefaultNoiseLevel)
                             ?? NoiseLevels.FirstOrDefault();
        OnPropertyChanged(nameof(ShowNoiseLevel));
        OnPropertyChanged(nameof(NoiseLevelHint));
    }

    private void UpdateProviderStatus()
    {
        if (SelectedProvider is null) return;
        if (SelectedModel?.Descriptor.CpuOnly == true && SelectedProvider.Kind != ExecutionProviderKind.Cpu)
        {
            ProviderStatus = "CPU only for this model · DirectML is disabled after a verified native crash";
        }
        else if (SelectedProvider.Kind == ExecutionProviderKind.Auto)
        {
            if (SelectedModel?.Descriptor.Backend == ModelBackendKind.NcnnVulkan)
            {
                var vulkan = Providers.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Vulkan);
                ProviderStatus = vulkan?.IsAvailable == true
                    ? "Automatic · Vulkan preferred for this NCNN model"
                    : "Automatic · NCNN CPU fallback";
            }
            else
            {
                var dml = Providers.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML);
                ProviderStatus = dml?.IsAvailable == true
                    ? "Automatic · DirectML preferred"
                    : "Automatic · CPU fallback";
            }
        }
        else if (SelectedProvider.Kind == ExecutionProviderKind.Vulkan &&
                 SelectedModel?.Descriptor.Backend != ModelBackendKind.NcnnVulkan)
        {
            ProviderStatus = "Vulkan applies to NCNN models · this ONNX model uses DirectML/CPU";
        }
        else if (SelectedProvider.Kind == ExecutionProviderKind.DirectML &&
                 SelectedModel?.Descriptor.Backend == ModelBackendKind.NcnnVulkan)
        {
            ProviderStatus = "NCNN uses Vulkan instead of DirectML · the GPU path is still used";
        }
        else
        {
            ProviderStatus = SelectedProvider.IsAvailable
                ? SelectedProvider.Detail
                : $"{SelectedProvider.Label} is not available on this system.";
        }
        OnPropertyChanged(nameof(ProviderStatus));
    }

    private async Task HandleImportRequestAsync(UpscalerImportRequest request)
    {
        if (request.IsFolder)
        {
            await AddFolderAsync(request.Path);
        }
        else if (request.Files is { Count: > 0 })
        {
            await AddImagesAsync(request.Files);
        }
    }

    public async Task AddImagesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        await _importGate.WaitAsync();
        try
        {
            var candidates = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _importCts?.Cancel();
            _importCts?.Dispose();
            _importCts = new CancellationTokenSource();
            var token = _importCts.Token;
            var skipped = new List<ScanSkipItem>();
            var totalWatch = Stopwatch.StartNew();
            var inspectWatch = Stopwatch.StartNew();
            var gate = new SemaphoreSlim(4, 4);
            var processed = 0;

            IsScanning = true;
            ScanSummary = $"Inspecting {candidates.Count} image{(candidates.Count == 1 ? "" : "s")}…";
            HasScanSummary = true;
            UpdateFileState();

            async Task<(string Path, MediaInfo Info, long Milliseconds)> InspectOneAsync(string path)
            {
                await gate.WaitAsync(token);
                try
                {
                    var watch = Stopwatch.StartNew();
                    var info = await _inspector.InspectAsync(path, MediaCategory.Image, token);
                    return (path, info, watch.ElapsedMilliseconds);
                }
                finally
                {
                    gate.Release();
                }
            }

            var pending = candidates.Select(InspectOneAsync).ToList();
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var (_, info, milliseconds) = await completed;
                processed++;
                _logger.LogInformation("Upscaler import inspected {File} in {Elapsed} ms", info.FileName, milliseconds);

                if (!info.IsReadable)
                {
                    skipped.Add(new ScanSkipItem(info.FileName, info.Error ?? "Unreadable image", false));
                }
                else if (info.Category != MediaCategory.Image)
                {
                    skipped.Add(new ScanSkipItem(info.FileName, "Not an image", true));
                }
                else if (info.IsAnimated || info.IsMultipage)
                {
                    skipped.Add(new ScanSkipItem(info.FileName,
                        "Animated or multi-page images cannot be upscaled while preserving the original content.", true));
                }
                else if (info.IsVector)
                {
                    skipped.Add(new ScanSkipItem(info.FileName,
                        "Vector images must be rasterized in the image converter first.", true));
                }
                else if (Files.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(new ScanSkipItem(info.FileName, "Already in the list", false));
                }
                else
                {
                    Files.Add(new MediaFileViewModel(info, _thumbnails, RemoveFile));
                    UpdateFileState();
                }

                ScanSummary = $"Adding images… {processed}/{candidates.Count} · {Files.Count} ready";
            }

            foreach (var item in skipped) Skipped.Add(item);
            HasSkipped = Skipped.Count > 0;
            if (skipped.Count > 0)
            {
                ScanSummary = $"{(Files.Count == 1 ? "1 image" : $"{Files.Count} images")} ready · {skipped.Count} skipped";
                _notifications.Notify("Some images were skipped", $"{skipped.Count} file(s) could not be upscaled.", NotificationSeverity.Warning);
            }
            else if (Files.Count > 0)
            {
                ScanSummary = $"{(Files.Count == 1 ? "1 image" : $"{Files.Count} images")} ready";
            }

            _logger.LogInformation(
                "Upscaler import: {Count} candidates in {Total} ms (inspection {Inspection} ms, {Ready} ready, {Skipped} skipped)",
                candidates.Count, totalWatch.ElapsedMilliseconds, inspectWatch.ElapsedMilliseconds, Files.Count, skipped.Count);
        }
        catch (OperationCanceledException)
        {
            // A newer import or workspace clear superseded this one.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import images into the Image Upscaler");
            await _dialogs.ShowErrorAsync("Images could not be imported", ex.Message, ex.ToString());
        }
        finally
        {
            IsScanning = false;
            UpdateFileState();
            _importGate.Release();
        }
    }

    public async Task AddFolderAsync(string folder)
    {
        IsScanning = true;
        ScanSummary = "Scanning folder…";
        HasScanSummary = true;
        try
        {
            var progress = new Progress<int>(count =>
            {
                _dispatcher.TryEnqueue(() => ScanSummary = $"Scanning folder… {count} files checked");
            });
            var result = await _scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = folder,
                Recursive = IncludeSubfolders,
                PreserveFolderStructure = PreserveFolderStructure,
                TargetCategory = MediaCategory.Image,
                RejectAnimated = true,
                RejectVector = true
            }, progress);

            foreach (var file in result.Files.Where(f => !f.IsSupported || !f.IsValid))
                Skipped.Add(new ScanSkipItem(file.RelativePath, file.Reason ?? "Skipped", !file.IsSupported));
            foreach (var file in result.IncludedFiles)
            {
                if (file.Info is null) continue;
                if (Files.Any(f => string.Equals(f.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                Files.Add(new MediaFileViewModel(file.Info, _thumbnails, RemoveFile,
                    PreserveFolderStructure ? file.RelativeDirectory : null));
            }

            ScanSummary = result.Summary + (result.Warning is null ? "" : " · " + result.Warning);
            HasScanSummary = true;
            HasSkipped = Skipped.Count > 0;
            if (result.IncludedCount == 0)
            {
                await _dialogs.ShowMessageAsync("No images found",
                    "The folder scan did not find any supported, readable images. Review the skipped list for details.");
            }
            else
            {
                _notifications.Notify("Folder scan complete", result.Summary, NotificationSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder scan failed");
            await _dialogs.ShowErrorAsync("Folder scan failed", ex.Message, ex.ToString());
        }
        finally
        {
            IsScanning = false;
            UpdateFileState();
        }
    }

    [RelayCommand]
    private async Task BrowseImagesAsync()
    {
        var files = await _filePicker.PickUpscaleImagesAsync();
        if (files.Count > 0) await AddImagesAsync(files);
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
        if (await _dialogs.ConfirmAsync("Clear the upscaler workspace?",
                "Remove all imported images and scan results? No files on disk will be deleted.",
                "Clear workspace", "Keep", destructive: true))
        {
            _importCts?.Cancel();
            Files.Clear();
            Skipped.Clear();
            HasSkipped = false;
            ScanSummary = "";
            HasScanSummary = false;
            UpdateFileState();
        }
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputDirectory = folder;
            RefreshSummary();
        }
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            try { Directory.CreateDirectory(OutputDirectory); } catch { }
        }
        if (Directory.Exists(OutputDirectory))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true });
    }

    [RelayCommand]
    private void ManageModels() => _modelPanel.Show(ModelPanelFilter.ImageUpscaling);

    [RelayCommand]
    private void CompareLast()
    {
        if (CanCompare && LastInputPath is not null && LastOutputPath is not null)
            _navigation.Navigate("Compare", new ComparisonRequest(LastInputPath, LastOutputPath, "Upscaled result"));
    }

    [RelayCommand]
    private void SelectTraditional()
    {
        IsTraditional = true;
        IsAi = false;
        RefreshSummary();
        ScheduleValidation();
    }

    [RelayCommand]
    private void SelectAi()
    {
        IsTraditional = false;
        IsAi = true;
        RefreshSummary();
        ScheduleValidation();
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (Files.Count == 0)
        {
            await _dialogs.ShowMessageAsync("No images to upscale", "Add individual images or scan a folder first.");
            return;
        }

        if (IsAi && SelectedModel?.IsInstalled != true)
        {
            var manage = await _dialogs.ConfirmAsync("AI model required",
                "Install an AI model before starting AI upscaling. Open Model Management now?",
                "Manage models", "Cancel");
            if (manage) _modelPanel.Show(ModelPanelFilter.ImageUpscaling);
            return;
        }

        await ValidateAsync();
        if (HasValidationErrors)
        {
            await _dialogs.ShowErrorAsync("Review the upscaling settings", ValidationSummary);
            return;
        }

        if (HasValidationWarnings)
        {
            var proceed = await _dialogs.ConfirmAsync("Review warnings", ValidationSummary, "Process anyway", "Go back");
            if (!proceed) return;
        }

        if (SelectedCollision?.Policy == CollisionPolicy.Overwrite)
        {
            var proceed = await _dialogs.ConfirmAsync("Replace existing files?",
                "Existing output files may be replaced only for this batch. Source images are never modified.",
                "Use Replace mode", "Cancel", destructive: true);
            if (!proceed) return;
        }

        var settings = BuildSettings();
        var request = new JobRequest
        {
            Operation = IsAi ? OperationKind.UpscaleAi : OperationKind.UpscaleTraditional,
            Title = IsAi
                ? $"AI upscale {Files.Count} image{(Files.Count == 1 ? "" : "s")} · {settings.ScaleFactor}×"
                : $"Upscale {Files.Count} image{(Files.Count == 1 ? "" : "s")} · {settings.ScaleFactor}×",
            Settings = settings,
            Inputs = Files.Select(f => new JobInput
            {
                Path = f.FilePath,
                Info = f.Info,
                RelativeDirectory = f.RelativeDirectory
            }).ToList(),
            PreserveRelativePaths = PreserveFolderStructure
        };
        try
        {
            var job = await _jobs.EnqueueAsync(request);
            _trackedJobs.Add(job.Id);
            _notifications.Notify("Added to processing queue", job.Title, NotificationSeverity.Success);
            Files.Clear();
            Skipped.Clear();
            HasSkipped = false;
            HasScanSummary = false;
            ScanSummary = "";
            UpdateFileState();
            _navigation.Navigate("Queue");
        }
        catch (JobValidationException ex)
        {
            SetValidation(ex.Validation);
            await _dialogs.ShowJobValidationAsync(ex.Validation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enqueue upscaling job");
            await _dialogs.ShowErrorAsync("Could not add the upscaling job", ex.Message, ex.ToString());
        }
    }

    private void RemoveFile(MediaFileViewModel file)
    {
        Files.Remove(file);
        UpdateFileState();
    }

    private void UpdateFileState()
    {
        HasFiles = Files.Count > 0;
        OnPropertyChanged(nameof(HasFiles));
        RefreshSummary();
        UpdateCanQueue();
        ScheduleValidation();
    }

    private void RefreshSummary()
    {
        if (Files.Count == 0)
        {
            Summary = "Import images or scan a folder to begin";
            return;
        }

        var method = IsAi ? "AI" : SelectedAlgorithm?.Label ?? "Lanczos";
        var model = IsAi && SelectedModel is not null ? $" · {SelectedModel.Name}" : "";
        var scale = UseCustomSize || SelectedScale?.Value == 0
            ? $" · {(CustomWidth?.ToString("0") ?? "auto")}×{(CustomHeight?.ToString("0") ?? "auto")}"
            : $" · {SelectedScale?.Value}×";
        Summary = $"{Files.Count} image{(Files.Count == 1 ? "" : "s")} · {method}{model}{scale}";
        OnPropertyChanged(nameof(Summary));
    }

    private void UpdateCanQueue()
    {
        CanQueue = Files.Count > 0 && !IsScanning && !HasValidationErrors &&
                   (!IsAi || SelectedModel?.IsInstalled == true);
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
            await ValidateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Upscaler validation failed: {Message}", ex.Message);
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (Files.Count == 0 || SelectedOutputFormat is null || SelectedScale is null)
        {
            SetValidation(null);
            return;
        }

        IsValidating = true;
        try
        {
            var request = new JobRequest
            {
                Operation = IsAi ? OperationKind.UpscaleAi : OperationKind.UpscaleTraditional,
                Title = "validation",
                Settings = BuildSettings(),
                Inputs = Files.Select(f => new JobInput { Path = f.FilePath, Info = f.Info, RelativeDirectory = f.RelativeDirectory }).ToList(),
                PreserveRelativePaths = PreserveFolderStructure
            };
            var result = await _jobs.ValidateAsync(request, cancellationToken);
            SetValidation(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Upscaler validation failed: {Message}", ex.Message);
        }
        finally
        {
            IsValidating = false;
        }
    }

    private void SetValidation(ValidationResult? result)
    {
        HasValidationErrors = result?.Errors.Count > 0;
        HasValidationWarnings = result?.Warnings.Count > 0;
        ValidationSummary = result?.Summary ?? "";
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(HasValidationWarnings));
        OnPropertyChanged(nameof(ValidationSummary));
        UpdateCanQueue();
    }

    private UpscaleSettings BuildSettings()
    {
        var settings = new UpscaleSettings
        {
            Method = IsAi ? UpscaleMethod.Ai : UpscaleMethod.Traditional,
            Algorithm = SelectedAlgorithm?.Value ?? ResamplingAlgorithm.Lanczos,
            ModelId = SelectedModel?.Id ?? _settings.Settings.Upscaler.ModelId,
            ScaleFactor = SelectedScale?.Value == 0 ? 1 : SelectedScale?.Value ?? 4,
            CustomWidth = SelectedScale?.Value == 0 && CustomWidth is > 0 ? (int)Math.Round(CustomWidth.Value) : null,
            CustomHeight = SelectedScale?.Value == 0 && CustomHeight is > 0 ? (int)Math.Round(CustomHeight.Value) : null,
            PreserveAspectRatio = PreserveAspect,
            ExecutionProvider = SelectedProvider?.Kind ?? ExecutionProviderKind.Auto,
            TileSize = IsAi && !AutoTileSize ? (int)Math.Clamp(TileSize, 64, 2048) : 0,
            TileOverlap = IsAi ? (int)Math.Clamp(TileOverlap, 0, 512) : 0,
            NoiseLevel = SelectedNoiseLevel?.Value,
            OutputFormatId = SelectedOutputFormat?.Id ?? FormatIds.Auto,
            OutputQuality = (int)Math.Clamp(OutputQuality, 1, 100),
            PreserveAlpha = PreserveAlpha,
            PreserveIccProfile = PreserveIccProfile,
            OutputDirectory = OutputDirectory,
            CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName,
            FileNameSuffix = FileNameSuffix,
            PreserveMetadata = PreserveMetadata
        };
        return settings;
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
        ScheduleValidation();
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (_disposed || !_trackedJobs.Contains(e.Job.Id)) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnJobChanged(sender, e));
            return;
        }

        if (!e.Job.IsTerminal) return;
        var item = e.Job.Items.FirstOrDefault(i => i.State == JobItemState.Completed && i.OutputPath is not null);
        if (item?.OutputPath is not null)
        {
            LastInputPath = item.InputPath;
            LastOutputPath = item.OutputPath;
            CanCompare = true;
            OnPropertyChanged(nameof(CanCompare));
        }
    }

    partial void OnIsTraditionalChanged(bool value)
    {
        if (!value) return;
        OnPropertyChanged(nameof(ShowAlgorithm));
        OnPropertyChanged(nameof(ShowAi));
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnIsAiChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAi));
        OnPropertyChanged(nameof(ShowTileControls));
        OnPropertyChanged(nameof(ShowAlgorithm));
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnSelectedAlgorithmChanged(Option<ResamplingAlgorithm>? value)
    {
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnSelectedScaleChanged(Option<int>? value)
    {
        OnPropertyChanged(nameof(ShowCustomSize));
        OnPropertyChanged(nameof(ScaleHint));
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnCustomWidthChanged(double? value)
    {
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnCustomHeightChanged(double? value)
    {
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnUseCustomSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCustomSize));
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnSelectedModelChanged(UpscaleModelItem? value)
    {
        if (value?.Descriptor.Task == ModelTask.ImageRestore)
            SelectedScale = ScaleOptions.FirstOrDefault(o => o.Value == 1) ?? SelectedScale;
        RefreshModelState();
        ScheduleValidation();
    }

    partial void OnSelectedNoiseLevelChanged(Option<int>? value)
    {
        OnPropertyChanged(nameof(NoiseLevelHint));
        ScheduleValidation();
    }

    partial void OnSelectedProviderChanged(ProviderChoice? value)
    {
        UpdateProviderStatus();
        ScheduleValidation();
    }

    partial void OnSelectedOutputFormatChanged(UpscaleFormatChoice? value)
    {
        OnPropertyChanged(nameof(ShowOutputQuality));
        RefreshSummary();
        ScheduleValidation();
    }

    partial void OnAutoTileSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTileControls));
        ScheduleValidation();
    }

    partial void OnTileSizeChanged(double value) => ScheduleValidation();
    partial void OnTileOverlapChanged(double value) => ScheduleValidation();
    partial void OnOutputQualityChanged(double value) => ScheduleValidation();
    partial void OnOutputDirectoryChanged(string value) => ScheduleValidation();
    partial void OnSelectedCollisionChanged(CollisionOption? value) => ScheduleValidation();
    partial void OnFileNameSuffixChanged(string value) => ScheduleValidation();
    partial void OnPreserveMetadataChanged(bool value) => ScheduleValidation();
    partial void OnPreserveAlphaChanged(bool value) => ScheduleValidation();
    partial void OnIncludeSubfoldersChanged(bool value) => ScheduleValidation();
    partial void OnPreserveIccProfileChanged(bool value) => ScheduleValidation();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        _models.ModelsChanged -= OnModelsChanged;
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importGate.Dispose();
        _validationCts?.Cancel();
        _validationCts?.Dispose();
    }
}
