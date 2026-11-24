using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed record WatchOperationOption(OperationKind Operation, MediaCategory Category, string Label)
    : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    public override string ToString() => DisplayLabel;
}

public sealed partial class WatchFolderItemViewModel : ObservableObject
{
    private WatchFolderRule _rule;
    private readonly WatchFoldersViewModel _owner;
    private bool _syncing;

    public WatchFolderItemViewModel(WatchFolderRule rule, WatchFoldersViewModel owner)
    {
        _rule = rule;
        _owner = owner;
        _isEnabled = rule.IsEnabled;
        _editName = rule.Name;
        _editInput = rule.InputDirectory;
        _editOutput = rule.OutputDirectory;
        _editIncludeSubdirectories = rule.IncludeSubdirectories;
        _editIgnoreProcessed = rule.IgnorePreviouslyProcessed;
        _editEnabled = rule.IsEnabled;
        var editModelId = rule.Settings is UpscaleSettings u ? u.ModelId :
            rule.Settings is VideoUpscaleSettings v ? v.ImageModelId : "";
        var editInterpolationModelId = rule.Settings is VideoUpscaleSettings v2
            ? v2.InterpolationModelId
            : "rife-4.25-lite";
        _editImageModel = owner.ImageModels.FirstOrDefault(c =>
            string.Equals(c.Id, editModelId, StringComparison.OrdinalIgnoreCase));
        _editInterpolationModel = owner.InterpolationModels.FirstOrDefault(c =>
            string.Equals(c.Id, editInterpolationModelId, StringComparison.OrdinalIgnoreCase));
        _editScale = rule.Settings is UpscaleSettings u2 ? u2.ScaleFactor :
            rule.Settings is VideoUpscaleSettings v3 ? v3.ScaleFactor : 4;
        _editEnableInterpolation = rule.Settings is VideoUpscaleSettings v4 && v4.EnableInterpolation;
        _editTemporalStability = rule.Settings is VideoUpscaleSettings v5 && v5.EnableTemporalStability;
        _editTemporalStrength = rule.Settings is VideoUpscaleSettings v6 ? v6.TemporalStabilityStrength : 50;
        _editAlgorithm = owner.Algorithms.FirstOrDefault(a =>
                             rule.Settings is UpscaleSettings us && a.Value == us.Algorithm)
                         ?? owner.Algorithms.First(a => a.Value == ResamplingAlgorithm.Lanczos);
    }

    public WatchFoldersViewModel Owner => _owner;
    public Guid Id => _rule.Id;
    public string Name => _rule.Name;
    public string InputDirectory => _rule.InputDirectory;
    public string OutputDirectory => _rule.OutputDirectory;
    public string OperationLabel => _rule.OperationLabel;
    public void RefreshLocalization() => OnPropertyChanged(string.Empty);
    public string StatusText => _rule.StatusText;
    public bool HasError => !string.IsNullOrWhiteSpace(_rule.LastError);
    public string ErrorText => _rule.LastError ?? "";
    public string Detail => LocalizationService.F("{0} · {1}", LocalizationService.T(_rule.OperationLabel),
        LocalizationService.T(_rule.IncludeSubdirectories ? "include subfolders" : "top folder only"));
    public bool IsUpscaleRule => _rule.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional or OperationKind.VideoUpscale;
    public bool IsVideoUpscale => _rule.Operation == OperationKind.VideoUpscale;
    public bool IsAiUpscale => _rule.Operation == OperationKind.UpscaleAi;
    public bool IsTraditionalUpscale => _rule.Operation == OperationKind.UpscaleTraditional;
    public ObservableCollection<WatchModelChoice> ImageModels => _owner.ImageModels;
    public ObservableCollection<WatchModelChoice> InterpolationModels => _owner.InterpolationModels;
    public IReadOnlyList<Option<ResamplingAlgorithm>> Algorithms => _owner.Algorithms;

    [ObservableProperty] private bool _isEditing;
    public string ChevronGlyph => IsEditing ? "\uE70E" : "\uE70D";

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));
    [ObservableProperty] private string _editName;
    [ObservableProperty] private string _editInput;
    [ObservableProperty] private string _editOutput;
    [ObservableProperty] private bool _editIncludeSubdirectories;
    [ObservableProperty] private bool _editIgnoreProcessed;
    [ObservableProperty] private bool _editEnabled;
    [ObservableProperty] private WatchModelChoice? _editImageModel;
    [ObservableProperty] private WatchModelChoice? _editInterpolationModel;
    [ObservableProperty] private Option<ResamplingAlgorithm> _editAlgorithm =
        new(ResamplingAlgorithm.Lanczos, "Lanczos");
    [ObservableProperty] private double _editScale;
    [ObservableProperty] private bool _editEnableInterpolation;
    [ObservableProperty] private bool _editTemporalStability;
    [ObservableProperty] private double _editTemporalStrength;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _owner.SetEnabledAsync(_rule.Id, value);
    }

    public void SyncEnabled(bool value)
    {
        _syncing = true;
        IsEnabled = value;
        _syncing = false;
    }

    public void ApplyRule(WatchFolderRule rule)
    {
        _rule = rule;
        SyncEnabled(rule.IsEnabled);
        ResetDraft();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(InputDirectory));
        OnPropertyChanged(nameof(OutputDirectory));
        OnPropertyChanged(nameof(OperationLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsUpscaleRule));
        OnPropertyChanged(nameof(IsVideoUpscale));
        OnPropertyChanged(nameof(IsAiUpscale));
        OnPropertyChanged(nameof(IsTraditionalUpscale));
    }

    [RelayCommand]
    private void ToggleEdit()
    {
        if (IsEditing) CancelEdit();
        else BeginEdit();
    }

    [RelayCommand]
    private void BeginEdit()
    {
        ResetDraft();
        _owner.BeginEdit(this);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        ResetDraft();
        _owner.CancelEdit(this);
    }

    private void ResetDraft()
    {
        EditName = _rule.Name;
        EditInput = _rule.InputDirectory;
        EditOutput = _rule.OutputDirectory;
        EditIncludeSubdirectories = _rule.IncludeSubdirectories;
        EditIgnoreProcessed = _rule.IgnorePreviouslyProcessed;
        EditEnabled = _rule.IsEnabled;
        var imageId = _rule.Settings is UpscaleSettings u ? u.ModelId :
            _rule.Settings is VideoUpscaleSettings v ? v.ImageModelId : null;
        var interpolationId = _rule.Settings is VideoUpscaleSettings v2 ? v2.InterpolationModelId : null;
        EditImageModel = ImageModels.FirstOrDefault(c =>
                             string.Equals(c.Id, imageId, StringComparison.OrdinalIgnoreCase))
                         ?? ImageModels.FirstOrDefault();
        EditInterpolationModel = InterpolationModels.FirstOrDefault(c =>
                                     string.Equals(c.Id, interpolationId, StringComparison.OrdinalIgnoreCase))
                                 ?? InterpolationModels.FirstOrDefault();
        EditScale = _rule.Settings is UpscaleSettings u2 ? u2.ScaleFactor :
            _rule.Settings is VideoUpscaleSettings v3 ? v3.ScaleFactor : 4;
        EditEnableInterpolation = _rule.Settings is VideoUpscaleSettings v4 && v4.EnableInterpolation;
        EditTemporalStability = _rule.Settings is VideoUpscaleSettings v5 && v5.EnableTemporalStability;
        EditTemporalStrength = _rule.Settings is VideoUpscaleSettings v6 ? v6.TemporalStabilityStrength : 50;
        EditAlgorithm = Algorithms.FirstOrDefault(a =>
                            _rule.Settings is UpscaleSettings us && a.Value == us.Algorithm)
                        ?? Algorithms.First(a => a.Value == ResamplingAlgorithm.Lanczos);
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        var updated = _rule.Clone();
        updated.Name = string.IsNullOrWhiteSpace(EditName) ? "Watched folder" : EditName.Trim();
        updated.InputDirectory = EditInput;
        updated.OutputDirectory = EditOutput;
        updated.IncludeSubdirectories = EditIncludeSubdirectories;
        updated.IgnorePreviouslyProcessed = EditIgnoreProcessed;
        updated.IsEnabled = EditEnabled;
        updated.Settings.OutputDirectory = EditOutput;
        if (updated.Settings is UpscaleSettings upscale)
        {
            upscale.ModelId = EditImageModel?.Id ?? upscale.ModelId;
            upscale.Algorithm = EditAlgorithm?.Value ?? upscale.Algorithm;
            upscale.ScaleFactor = (int)Math.Clamp(EditScale, 1, 16);
        }
        else if (updated.Settings is VideoUpscaleSettings video)
        {
            video.ImageModelId = EditImageModel?.Id ?? video.ImageModelId;
            video.InterpolationModelId = EditInterpolationModel?.Id ?? video.InterpolationModelId;
            video.ScaleFactor = (int)Math.Clamp(EditScale, 1, 16);
            video.EnableInterpolation = EditEnableInterpolation;
            video.EnableTemporalStability = EditTemporalStability;
            video.TemporalStabilityStrength = (int)Math.Clamp(EditTemporalStrength, 0, 100);
        }

        if (await _owner.UpdateRuleAsync(updated))
            IsEditing = false;
    }

    [RelayCommand]
    private void ManageImageModels() => _owner.ManageImageModelsCommand.Execute(null);

    [RelayCommand]
    private void ManageInterpolationModels() => _owner.ManageInterpolationModelsCommand.Execute(null);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(_rule.Id);

    [RelayCommand]
    private void OpenInput() => _owner.OpenFolder(_rule.InputDirectory);

    [RelayCommand]
    private void OpenOutput() => _owner.OpenFolder(_rule.OutputDirectory);
}

public sealed record WatchModelChoice(ModelDescriptor Descriptor, bool IsInstalled) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Status => IsInstalled ? "Installed" : "Download required";
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    public override string ToString() => $"{Descriptor.Name} - {Status}";
}

public sealed partial class WatchFoldersViewModel : ObservableObject, IDisposable
{
    private readonly IWatchFolderService _watch;
    private readonly ICapabilityRegistry _registry;
    private readonly IModelManager _models;
    private readonly IModelPanelService _modelPanel;
    private readonly IFilePickerService _picker;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _initialized;
    private bool _disposed;
    private bool _loadingFormats;

    public WatchFoldersViewModel(IWatchFolderService watch, ICapabilityRegistry registry, IModelManager models,
        IModelPanelService modelPanel, IFilePickerService picker,
        IAppPaths paths, ISettingsService settings, INotificationService notifications, IAppLogger logger)
    {
        _watch = watch;
        _registry = registry;
        _models = models;
        _modelPanel = modelPanel;
        _picker = picker;
        _paths = paths;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _watch.RulesChanged += OnRulesChanged;
        _models.ModelsChanged += OnModelsChanged;
        InputDirectory = "";
        OutputDirectory = Path.Combine(_paths.DefaultOutputDirectory, "Watched");
        SelectedCategory = Categories[0];
        _selectedOperation = Operations[0];
        SelectedFormat = null;
        RefreshModels();
    }

    private void RefreshModels()
    {
        var previousImage = SelectedWatchModel?.Id;
        var previousInterpolation = SelectedWatchInterpolationModel?.Id;
        var installed = _models.InstalledModels
            .Where(m => m.IsValid)
            .Select(m => m.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var images = _models.AvailableModels
            .Where(m => m.Task is ModelTask.ImageUpscale or ModelTask.ImageRestore && m.SupportsStillImage)
            .OrderByDescending(m => installed.Contains(m.Id))
            .ThenBy(m => m.SortOrder)
            .Select(m => new WatchModelChoice(m, installed.Contains(m.Id)))
            .ToList();
        ImageModels.Clear();
        foreach (var choice in images) ImageModels.Add(choice);

        var interpolation = _models.AvailableModels
            .Where(m => m.Task == ModelTask.FrameInterpolation && m.SupportsVideoFrames)
            .OrderByDescending(m => installed.Contains(m.Id))
            .ThenBy(m => m.SortOrder)
            .Select(m => new WatchModelChoice(m, installed.Contains(m.Id)))
            .ToList();
        InterpolationModels.Clear();
        foreach (var choice in interpolation) InterpolationModels.Add(choice);

        SelectedWatchModel = ImageModels.FirstOrDefault(c =>
                              string.Equals(c.Id, previousImage ?? WatchModelId, StringComparison.OrdinalIgnoreCase))
                          ?? ImageModels.FirstOrDefault();
        SelectedWatchInterpolationModel = InterpolationModels.FirstOrDefault(c =>
                                              string.Equals(c.Id, previousInterpolation ?? WatchInterpolationModelId,
                                                  StringComparison.OrdinalIgnoreCase))
                                          ?? InterpolationModels.FirstOrDefault();
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

    [RelayCommand]
    private void ManageImageModels() => _modelPanel.Show(ModelPanelFilter.ImageUpscaling);

    [RelayCommand]
    private void ManageInterpolationModels() => _modelPanel.Show(ModelPanelFilter.FrameInterpolation);

    public ObservableCollection<WatchFolderItemViewModel> Rules { get; } = [];
    public ObservableCollection<WatchModelChoice> ImageModels { get; } = [];
    public ObservableCollection<WatchModelChoice> InterpolationModels { get; } = [];
    public ObservableCollection<FormatItemViewModel> Formats { get; } = [];
    public IReadOnlyList<Option<MediaCategory>> Categories { get; } =
    [
        new(MediaCategory.Image, "Images"),
        new(MediaCategory.Audio, "Audio"),
        new(MediaCategory.Video, "Video"),
        new(MediaCategory.Document, "Documents"),
        new(MediaCategory.Ebook, "E-books"),
        new(MediaCategory.Archive, "Archives")
    ];

    public IReadOnlyList<WatchOperationOption> Operations { get; } =
    [
        new(OperationKind.ImageConvert, MediaCategory.Image, "Image conversion"),
        new(OperationKind.AudioConvert, MediaCategory.Audio, "Audio conversion"),
        new(OperationKind.VideoConvert, MediaCategory.Video, "Video conversion"),
        new(OperationKind.DocumentConvert, MediaCategory.Document, "Document conversion"),
        new(OperationKind.EbookConvert, MediaCategory.Ebook, "E-book conversion"),
        new(OperationKind.ArchiveConvert, MediaCategory.Archive, "Archive conversion"),
        new(OperationKind.UpscaleTraditional, MediaCategory.Image, "Image upscaling (traditional)"),
        new(OperationKind.UpscaleAi, MediaCategory.Image, "Image upscaling (AI)"),
        new(OperationKind.VideoUpscale, MediaCategory.Video, "Video upscaling")
    ];

    public IReadOnlyList<Option<ExecutionProviderKind>> Providers { get; } =
    [
        new(ExecutionProviderKind.Auto, "Automatic"),
        new(ExecutionProviderKind.Cpu, "CPU"),
        new(ExecutionProviderKind.DirectML, "DirectML"),
        new(ExecutionProviderKind.Vulkan, "Vulkan")
    ];

    public IReadOnlyList<Option<int>> Scales { get; } = [new(2, "2x"), new(3, "3x"), new(4, "4x")];

    public IReadOnlyList<Option<ResamplingAlgorithm>> Algorithms { get; } =
    [
        new(ResamplingAlgorithm.NearestNeighbor, "Nearest Neighbor"),
        new(ResamplingAlgorithm.Bilinear, "Bilinear"),
        new(ResamplingAlgorithm.Bicubic, "Bicubic"),
        new(ResamplingAlgorithm.Lanczos, "Lanczos"),
        new(ResamplingAlgorithm.MitchellNetravali, "Mitchell–Netravali")
    ];

    public IReadOnlyList<Option<int>> AudioBitrates { get; } =
    [
        new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"),
        new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")
    ];

    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in Categories) option.RefreshLocalization();
        foreach (var option in Operations) option.RefreshLocalization();
        foreach (var option in Providers) option.RefreshLocalization();
        foreach (var option in Scales) option.RefreshLocalization();
        foreach (var option in Algorithms) option.RefreshLocalization();
        foreach (var option in AudioBitrates) option.RefreshLocalization();
        foreach (var format in Formats) format.RefreshLocalization();
        foreach (var rule in Rules) rule.RefreshLocalization();
        foreach (var model in ImageModels) model.RefreshLocalization();
        foreach (var model in InterpolationModels) model.RefreshLocalization();
        SelectedCategory.RefreshLocalization();
        SelectedOperation?.RefreshLocalization();
        WatchScale.RefreshLocalization();
        WatchAlgorithm.RefreshLocalization();
        WatchProvider.RefreshLocalization();
        SelectedAudioBitrate.RefreshLocalization();
        SelectedFormat?.RefreshLocalization();
        SelectedWatchModel?.RefreshLocalization();
        SelectedWatchInterpolationModel?.RefreshLocalization();
    }

    [ObservableProperty] private string _ruleName = "Watched folder";
    [ObservableProperty] private string _inputDirectory = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _includeSubdirectories = true;
    [ObservableProperty] private bool _ignorePreviouslyProcessed = true;
    [ObservableProperty] private bool _enableImmediately = true;
    [ObservableProperty] private Option<MediaCategory> _selectedCategory;
    [ObservableProperty] private WatchOperationOption? _selectedOperation;
    [ObservableProperty] private string _watchModelId = "realesrgan-x4plus";
    [ObservableProperty] private string _watchInterpolationModelId = "rife-4.25-lite";
    [ObservableProperty] private WatchModelChoice? _selectedWatchModel;
    [ObservableProperty] private WatchModelChoice? _selectedWatchInterpolationModel;
    [ObservableProperty] private Option<int> _watchScale = new(4, "4x");
    [ObservableProperty] private Option<ResamplingAlgorithm> _watchAlgorithm =
        new(ResamplingAlgorithm.Lanczos, "Lanczos");
    [ObservableProperty] private Option<ExecutionProviderKind> _watchProvider = new(ExecutionProviderKind.Auto, "Automatic");
    [ObservableProperty] private bool _watchEnableInterpolation;
    [ObservableProperty] private bool _watchTemporal;
    [ObservableProperty] private double _watchTemporalStrength = 50;
    [ObservableProperty] private FormatItemViewModel? _selectedFormat;
    [ObservableProperty] private double _imageQuality = 88;
    [ObservableProperty] private Option<int> _selectedAudioBitrate = new(192, "192 kbps");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Create a rule to watch a folder and queue new files automatically.";
    [ObservableProperty] private bool _hasError;

    public bool IsImageCategory => SelectedCategory?.Value == MediaCategory.Image;
    public bool IsAudioCategory => SelectedCategory?.Value == MediaCategory.Audio;
    public bool IsVideoCategory => SelectedCategory?.Value == MediaCategory.Video;
    public bool IsDocumentCategory => SelectedCategory?.Value is MediaCategory.Document or MediaCategory.Ebook;
    public bool IsArchiveCategory => SelectedCategory?.Value == MediaCategory.Archive;
    public bool IsUpscaleOperation => SelectedOperation?.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional or OperationKind.VideoUpscale;
    public bool IsVideoUpscale => SelectedOperation?.Operation == OperationKind.VideoUpscale;
    public bool IsImageUpscale => SelectedOperation?.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional;
    public bool IsAiUpscale => SelectedOperation?.Operation is OperationKind.UpscaleAi or OperationKind.VideoUpscale;
    public bool IsTraditionalUpscale => SelectedOperation?.Operation == OperationKind.UpscaleTraditional;
    public bool IsConversionOperation => !IsUpscaleOperation;
    public bool HasRules => Rules.Count > 0;
    public bool HasInputFolder => !string.IsNullOrWhiteSpace(InputDirectory) && Directory.Exists(InputDirectory);
    public string RunSummary => LocalizationService.P("Count.WatchRule", Rules.Count);

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!_registry.IsInitialized) await _registry.InitializeAsync();
        await _models.InitializeAsync();
        RefreshModels();
        await _watch.InitializeAsync();
        SelectedAudioBitrate = AudioBitrates[3];
        LoadFormats();
        await RefreshAsync();
    }

    private void LoadFormats()
    {
        if (SelectedCategory is null || !_registry.IsInitialized) return;
        _loadingFormats = true;
        Formats.Clear();
        if (SelectedOperation?.Operation == OperationKind.VideoUpscale)
        {
            SelectedFormat = null;
        }
        else
        {
            foreach (var option in _registry.GetOutputFormats(SelectedCategory.Value))
                Formats.Add(new FormatItemViewModel(option.Format));
            SelectedFormat = Formats.FirstOrDefault();
        }
        _loadingFormats = false;
        OnPropertyChanged(nameof(SelectedFormat));
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        try
        {
            var rules = await _watch.GetRulesAsync();
            void Apply()
            {
                var ordered = rules.OrderBy(r => r.Name).ToList();
                var targetIds = ordered.Select(r => r.Id).ToHashSet();
                foreach (var stale in Rules.Where(r => !targetIds.Contains(r.Id)).ToList())
                    Rules.Remove(stale);

                foreach (var rule in ordered)
                {
                    var item = Rules.FirstOrDefault(r => r.Id == rule.Id);
                    if (item is null) Rules.Add(new WatchFolderItemViewModel(rule, this));
                    else item.ApplyRule(rule);
                }

                for (var index = 0; index < ordered.Count; index++)
                {
                    var item = Rules.First(r => r.Id == ordered[index].Id);
                    var current = Rules.IndexOf(item);
                    if (current != index) Rules.Move(current, index);
                }

                OnPropertyChanged(nameof(HasRules));
                OnPropertyChanged(nameof(RunSummary));
            }

            if (_dispatcher.HasThreadAccess) Apply();
            else _dispatcher.TryEnqueue(Apply);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load watch folders: {Message}", ex.Message);
        }
    }

    private void OnRulesChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        var folder = await _picker.PickFolderAsync(InputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) InputDirectory = folder;
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var folder = await _picker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) OutputDirectory = folder;
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (SelectedOperation is null ||
            (SelectedOperation.Operation != OperationKind.VideoUpscale && SelectedFormat is null))
        {
            await SetErrorAsync("Choose an operation and output format.");
            return;
        }

        if (!Directory.Exists(InputDirectory))
        {
            await SetErrorAsync("Choose an existing input folder.");
            return;
        }

        IsBusy = true;
        try
        {
            var (operation, settings) = BuildRuleSettings();
            var rule = new WatchFolderRule
            {
                Name = string.IsNullOrWhiteSpace(RuleName) ? "Watched folder" : RuleName.Trim(),
                InputDirectory = InputDirectory,
                OutputDirectory = OutputDirectory,
                IncludeSubdirectories = IncludeSubdirectories,
                IgnorePreviouslyProcessed = IgnorePreviouslyProcessed,
                IsEnabled = EnableImmediately,
                Operation = operation,
                Settings = settings
            };
            await _watch.CreateAsync(rule);
            StatusMessage = LocalizationService.F("Watching {0} → {1}.",
                Path.GetFileName(InputDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetFileName(OutputDirectory.TrimEnd(Path.DirectorySeparatorChar)));
            HasError = false;
            _notifications.Notify("Watch rule created", rule.Name, NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (OperationKind Operation, OperationSettings Settings) BuildRuleSettings()
    {
        var operation = SelectedOperation!.Operation;
        var category = SelectedOperation.Category;
        var format = SelectedFormat?.Id ?? FormatIds.Auto;
        if (operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional)
        {
            return (operation, new UpscaleSettings
            {
                Method = operation == OperationKind.UpscaleAi ? UpscaleMethod.Ai : UpscaleMethod.Traditional,
                Algorithm = WatchAlgorithm?.Value ?? ResamplingAlgorithm.Lanczos,
                ModelId = SelectedWatchModel?.Id ?? WatchModelId,
                ScaleFactor = WatchScale?.Value ?? 4,
                ExecutionProvider = WatchProvider?.Value ?? ExecutionProviderKind.Auto,
                OutputFormatId = format,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            });
        }

        if (operation == OperationKind.VideoUpscale)
        {
            return (operation, new VideoUpscaleSettings
            {
                ImageModelId = SelectedWatchModel?.Id ?? WatchModelId,
                InterpolationModelId = SelectedWatchInterpolationModel?.Id ?? WatchInterpolationModelId,
                ScaleFactor = WatchScale?.Value ?? 4,
                EnableSuperResolution = true,
                EnableInterpolation = WatchEnableInterpolation,
                FrameRateMultiplier = 2,
                ExecutionProvider = WatchProvider?.Value ?? ExecutionProviderKind.Auto,
                EnableTemporalStability = WatchTemporal,
                TemporalStabilityStrength = (int)Math.Clamp(WatchTemporalStrength, 0, 100),
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            });
        }

        return category switch
        {
            MediaCategory.Image => (OperationKind.ImageConvert, new ImageConversionSettings
            {
                OutputFormatId = format,
                Quality = (int)Math.Clamp(ImageQuality, 1, 100),
                PreserveMetadata = true,
                PreserveIccProfile = true,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Audio => (OperationKind.AudioConvert, BuildAudioSettings(format)),
            MediaCategory.Video => (OperationKind.VideoConvert, BuildVideoSettings(format)),
            MediaCategory.Document => (OperationKind.DocumentConvert, new DocumentConversionSettings
            {
                OutputFormatId = format,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Ebook => (OperationKind.EbookConvert, new EbookConversionSettings
            {
                OutputFormatId = format,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Archive => (OperationKind.ArchiveConvert, new ArchiveConversionSettings
            {
                OutputFormatId = format,
                Action = ArchiveAction.Create,
                CompressionLevel = 5,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            _ => throw new InvalidOperationException("Choose a supported category.")
        };
    }

    private AudioConversionSettings BuildAudioSettings(string formatId)
    {
        var codec = _registry.GetAudioCodecs(formatId, false).FirstOrDefault();
        var encoder = codec?.Encoders.FirstOrDefault(e => e.IsAvailable);
        return new AudioConversionSettings
        {
            OutputFormatId = formatId,
            CodecId = codec?.Id ?? "aac",
            EncoderId = encoder?.Id ?? "aac",
            BitrateKbps = SelectedAudioBitrate?.Value ?? 192,
            OutputDirectory = OutputDirectory,
            CollisionPolicy = CollisionPolicy.UniqueName
        };
    }

    private VideoConversionSettings BuildVideoSettings(string formatId)
    {
        var (codec, audio) = formatId switch
        {
            "webm" => ("vp9", "opus"),
            "avi" => ("mpeg4", "mp3"),
            "gif" => ("gif", "none"),
            _ => ("h264", "aac")
        };
        var codecs = _registry.GetVideoCodecs(formatId);
        if (codecs.Count > 0 && codecs.All(c => c.Id != codec)) codec = codecs[0].Id;
        var audioCodecs = _registry.GetAudioCodecs(formatId, true);
        if (audioCodecs.Count > 0 && audioCodecs.All(c => c.Id != audio)) audio = audioCodecs[0].Id;
        return new VideoConversionSettings
        {
            ContainerFormatId = formatId,
            VideoCodecId = codec,
            AudioCodecId = audio,
            AudioBitrateKbps = 128,
            QualityValue = 23,
            Preset = ConversionPreset.Balanced,
            FastStart = true,
            OutputDirectory = OutputDirectory,
            CollisionPolicy = CollisionPolicy.UniqueName
        };
    }

    public void BeginEdit(WatchFolderItemViewModel item)
    {
        foreach (var rule in Rules)
            if (!ReferenceEquals(rule, item)) rule.IsEditing = false;
    }

    public void CancelEdit(WatchFolderItemViewModel item)
    {
        // Draft values live on the item; leaving edit mode discards them.
    }

    public async Task<bool> UpdateRuleAsync(WatchFolderRule rule)
    {
        try
        {
            await _watch.UpdateAsync(rule);
            StatusMessage = "Watch rule updated.";
            HasError = false;
            _notifications.Notify("Watch rule updated", rule.Name, NotificationSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
            return false;
        }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled)
    {
        try
        {
            await _watch.SetEnabledAsync(id, enabled);
            StatusMessage = enabled ? "Rule resumed." : "Rule paused.";
            HasError = false;
        }
        catch (Exception ex)
        {
            var item = Rules.FirstOrDefault(r => r.Id == id);
            item?.SyncEnabled(!enabled);
            await SetErrorAsync(ex.Message);
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        try
        {
            await _watch.DeleteAsync(id);
            _notifications.Notify("Watch rule removed", "Source files were not deleted.", NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
    }

    public void OpenFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notifications.Notify("Could not open folder", ex.Message, NotificationSeverity.Warning);
        }
    }

    private async Task SetErrorAsync(string message)
    {
        HasError = true;
        StatusMessage = message;
        await Task.CompletedTask;
    }

    partial void OnInputDirectoryChanged(string value) => OnPropertyChanged(nameof(HasInputFolder));
    partial void OnSelectedOperationChanged(WatchOperationOption? value)
    {
        if (value is null) return;
        SelectedCategory = Categories.FirstOrDefault(c => c.Value == value.Category) ?? Categories[0];
        OnPropertyChanged(nameof(IsUpscaleOperation));
        OnPropertyChanged(nameof(IsVideoUpscale));
        OnPropertyChanged(nameof(IsImageUpscale));
        OnPropertyChanged(nameof(IsAiUpscale));
        OnPropertyChanged(nameof(IsTraditionalUpscale));
        OnPropertyChanged(nameof(IsConversionOperation));
        LoadFormats();
    }

    partial void OnSelectedCategoryChanged(Option<MediaCategory> value)
    {
        if (value is null) return;
        LoadFormats();
        OnPropertyChanged(nameof(IsImageCategory));
        OnPropertyChanged(nameof(IsAudioCategory));
        OnPropertyChanged(nameof(IsVideoCategory));
        OnPropertyChanged(nameof(IsDocumentCategory));
        OnPropertyChanged(nameof(IsArchiveCategory));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watch.RulesChanged -= OnRulesChanged;
        _models.ModelsChanged -= OnModelsChanged;
    }
}
