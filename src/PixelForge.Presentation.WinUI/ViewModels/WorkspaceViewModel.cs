using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed record Option<T>(T Value, string Label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public override string ToString() => DisplayLabel;
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
}

public abstract partial class WorkspaceViewModel : ObservableObject
{
    protected readonly ICapabilityRegistry Registry;
    protected readonly ISettingsService Settings;
    protected readonly IAppPaths Paths;
    protected readonly IFilePickerService FilePicker;
    private bool _loadingFormat;
    private ValidationResult? _lastValidation;
    private CancellationTokenSource? _formatDirectorySaveCts;

    protected WorkspaceViewModel(MediaCategory category, ICapabilityRegistry registry, ISettingsService settings,
        IAppPaths paths, IFilePickerService filePicker)
    {
        Category = category;
        Registry = registry;
        Settings = settings;
        Paths = paths;
        FilePicker = filePicker;
        Files.CollectionChanged += OnFilesCollectionChanged;
        OutputDirectory = string.IsNullOrWhiteSpace(settings.Settings.General.DefaultOutputDirectory)
            ? paths.DefaultOutputDirectory
            : settings.Settings.General.DefaultOutputDirectory;
        SelectedCollision = CollisionOptions.FirstOrDefault(c => c.Policy == settings.Settings.Converter.CollisionPolicy)
                            ?? CollisionOptions[0];
    }

    public MediaCategory Category { get; }
    public ObservableCollection<MediaFileViewModel> Files { get; } = [];
    public ObservableCollection<FormatItemViewModel> Formats { get; } = [];
    public ObservableCollection<FormatOutputOption> FormatOutputOptions { get; } = [];
    public ObservableCollection<ValidationIssueViewModel> Issues { get; } = [];
    public IReadOnlyList<CollisionOption> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number", "Never replace existing files."),
        new(CollisionPolicy.Skip, "Skip existing files", "Leave existing outputs untouched."),
        new(CollisionPolicy.Overwrite, "Replace existing files", "Overwrite only after confirmation.")
    ];

    /// <summary>
    /// Re-evaluates localized bindings and item labels in place. Collections,
    /// selected items and staged files are never replaced.
    /// </summary>
    public virtual void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in CollisionOptions) option.RefreshLocalization();
        foreach (var format in Formats) format.RefreshLocalization();
        foreach (var option in FormatOutputOptions) option.RefreshLocalization();
        RefreshSummary();
        if (_lastValidation is not null) SetValidation(_lastValidation);
    }

    public abstract OperationKind Operation { get; }
    public abstract string CategoryTitle { get; }
    public abstract string CategorySubtitle { get; }
    public abstract string CategoryGlyph { get; }

    [ObservableProperty] private FormatItemViewModel? _selectedFormat;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private string _fileNameSuffix = "";
    [ObservableProperty] private bool _preserveMetadata = true;
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private string _summary = "";

    public bool HasFiles => Files.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public string FileCountText => LocalizationService.P("Count.File", Files.Count);
    public bool HasMultiFrameFiles => Files.Any(f => f.Info.IsAnimated || f.Info.IsMultipage);
    public bool HasAnimatedFiles => Files.Any(f => f.Info.IsAnimated);
    public bool HasAlphaFiles => Files.Any(f => f.Info.HasAlpha);
    public bool CanQueue => HasFiles && !HasValidationErrors && SelectedFormat is not null;
    public string SelectedFormatExtension => SelectedFormat?.Extension ?? "";
    public string EffectiveOutputDirectory => ResolveOutputDirectory();

    public event EventHandler? Changed;

    public void LoadFormats(string? preferredFormatId)
    {
        _loadingFormat = true;
        var output = Registry.GetOutputFormats(Category);
        Formats.Clear();
        foreach (var item in output)
            Formats.Add(new FormatItemViewModel(item.Format));
        var preferred = Formats.FirstOrDefault(f => string.Equals(f.Id, preferredFormatId, StringComparison.OrdinalIgnoreCase))
                        ?? Formats.FirstOrDefault();
        SelectedFormat = preferred;
        RebuildFormatOutputOptions();
        _loadingFormat = false;
        OnFormatChanged();
        RefreshSummary();
    }

    public void AddFile(MediaInfo info, IThumbnailService thumbnails, MediaFileViewModel? existing = null,
        string? relativeDirectory = null)
    {
        if (Files.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase))) return;
        Files.Add(existing ?? new MediaFileViewModel(info, thumbnails, RemoveFile, relativeDirectory));
    }

    public void ClearFiles() => Files.Clear();

    public void RemoveFile(MediaFileViewModel file) => Files.Remove(file);

    public void SetValidation(ValidationResult? validation)
    {
        _lastValidation = validation;
        Issues.Clear();
        if (validation is null)
        {
            ValidationSummary = "";
            HasValidationErrors = false;
            HasValidationWarnings = false;
        }
        else
        {
            foreach (var issue in validation.Issues.Where(i => i.Severity != ValidationSeverity.Info))
                Issues.Add(new ValidationIssueViewModel(issue.Message, issue.Severity));
            HasValidationErrors = validation.Errors.Count > 0;
            HasValidationWarnings = validation.Warnings.Count > 0;
            ValidationSummary = LocalizationService.ValidationSummary(validation);
        }

        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(CanQueue));
    }

    public JobRequest BuildJobRequest(string? title = null) => new()
    {
        Operation = Operation,
        Title = title ?? BuildDefaultTitle(),
        Settings = BuildSettings(),
        Inputs = Files.Select(f => new JobInput
        {
            Path = f.FilePath,
            Info = f.Info,
            RelativeDirectory = f.RelativeDirectory
        }).ToList(),
        PreserveRelativePaths = Files.Any(f => !string.IsNullOrWhiteSpace(f.RelativeDirectory))
    };

    public abstract OperationSettings BuildSettings();
    public abstract bool Accepts(MediaInfo info);
    protected abstract string BuildSummary();
    protected abstract string BuildDefaultTitle();

    protected void CommonSettings(OperationSettings settings)
    {
        settings.OutputDirectory = ResolveOutputDirectory();
        settings.CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName;
        settings.FileNameSuffix = FileNameSuffix;
        settings.PreserveMetadata = PreserveMetadata;
    }

    private string ResolveOutputDirectory()
    {
        if (SelectedFormat is null) return OutputDirectory;
        var custom = FormatOutputOptions.FirstOrDefault(o =>
            string.Equals(o.Id, SelectedFormat.Id, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(custom?.Directory) ? OutputDirectory : custom!.Directory;
    }

    private void RebuildFormatOutputOptions()
    {
        FormatOutputOptions.Clear();
        var stored = Settings.Settings.Converter.FormatOutputDirectories;
        foreach (var format in Formats)
        {
            stored.TryGetValue(format.Id, out var directory);
            FormatOutputOptions.Add(new FormatOutputOption(format, directory ?? "",
                ChooseFormatOutputFolderAsync, OnFormatOutputDirectoryChanged));
        }
        OnPropertyChanged(nameof(EffectiveOutputDirectory));
    }

    private async Task ChooseFormatOutputFolderAsync(FormatOutputOption option)
    {
        var initial = string.IsNullOrWhiteSpace(option.Directory) ? OutputDirectory : option.Directory;
        var folder = await FilePicker.PickFolderAsync(initial);
        if (!string.IsNullOrWhiteSpace(folder)) option.Directory = folder;
    }

    private void OnFormatOutputDirectoryChanged(FormatOutputOption option)
    {
        var directory = option.Directory.Trim();
        if (directory.Length == 0)
            Settings.Settings.Converter.FormatOutputDirectories.Remove(option.Id);
        else
            Settings.Settings.Converter.FormatOutputDirectories[option.Id] = directory;

        if (string.Equals(SelectedFormat?.Id, option.Id, StringComparison.OrdinalIgnoreCase))
            OnPropertyChanged(nameof(EffectiveOutputDirectory));
        ScheduleFormatDirectorySave();
        RaiseChanged();
    }

    private void ScheduleFormatDirectorySave()
    {
        _formatDirectorySaveCts?.Cancel();
        _formatDirectorySaveCts?.Dispose();
        _formatDirectorySaveCts = new CancellationTokenSource();
        _ = SaveFormatDirectoriesAsync(_formatDirectorySaveCts.Token);
    }

    private async Task SaveFormatDirectoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(400, cancellationToken);
            await Settings.SaveAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit.
        }
    }

    protected virtual void OnFormatChanged()
    {
    }

    protected virtual void OnFilesChanged()
    {
    }

    protected void RaiseChanged()
    {
        if (_loadingFormat) return;
        OnPropertyChanged(nameof(SelectedFormatExtension));
        OnPropertyChanged(nameof(EffectiveOutputDirectory));
        OnPropertyChanged(nameof(CanQueue));
        RefreshSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected void RefreshSummary()
    {
        Summary = BuildSummary();
    }

    protected void InitCommon(FormatItemViewModel? preferred = null)
    {
        SelectedFormat = preferred ?? Formats.FirstOrDefault();
    }

    partial void OnSelectedFormatChanged(FormatItemViewModel? value)
    {
        if (_loadingFormat) return;
        OnFormatChanged();
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveOutputDirectory));
        RaiseChanged();
    }

    partial void OnSelectedCollisionChanged(CollisionOption? value) => RaiseChanged();
    partial void OnFileNameSuffixChanged(string value) => RaiseChanged();
    partial void OnPreserveMetadataChanged(bool value) => RaiseChanged();

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(HasMultiFrameFiles));
        OnPropertyChanged(nameof(HasAnimatedFiles));
        OnPropertyChanged(nameof(HasAlphaFiles));
        OnPropertyChanged(nameof(CanQueue));
        OnFilesChanged();
        RefreshSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearFilesCommand() => ClearFiles();
}

public sealed partial class FormatOutputOption : ObservableObject
{
    private readonly Func<FormatOutputOption, Task> _choose;
    private readonly Action<FormatOutputOption> _changed;

    public FormatOutputOption(FormatItemViewModel format, string directory,
        Func<FormatOutputOption, Task> choose, Action<FormatOutputOption> changed)
    {
        Format = format;
        _directory = directory;
        _choose = choose;
        _changed = changed;
    }

    public FormatItemViewModel Format { get; }
    public string Id => Format.Id;
    public string Label => $"{Format.ExtensionUpper} · {Format.DisplayName}";
    public string Extension => Format.ExtensionUpper;

    public void RefreshLocalization() => OnPropertyChanged(nameof(Label));

    [ObservableProperty] private string _directory = "";

    [RelayCommand]
    private Task ChooseAsync() => _choose(this);

    partial void OnDirectoryChanged(string value) => _changed(this);
}

public sealed partial class ImageWorkspaceViewModel : WorkspaceViewModel
{
    public ImageWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Image, registry, settings, paths, filePicker)
    {
        Quality = settings.Settings.Converter.ImageQuality;
        PreserveMetadata = settings.Settings.Converter.ImagePreserveMetadata;
        ResizeMode = new Option<ImageResizeMode>(ImageResizeMode.Fit, "Fit inside");
        Resampling = new Option<ResamplingAlgorithm>(ResamplingAlgorithm.Lanczos, "Lanczos");
        Transparency = new Option<TransparencyMode>(TransparencyMode.Preserve, "Preserve transparency");
        TiffCompression = "Lzw";
    }

    public override OperationKind Operation => OperationKind.ImageConvert;
    public override string CategoryTitle => LocalizationService.T("Images");
    public override string CategorySubtitle => "Convert, resize and optimise still images and animations.";
    public override string CategoryGlyph => "\uE91B";

    public IReadOnlyList<Option<ImageResizeMode>> ResizeModes { get; } =
    [
        new(ImageResizeMode.Fit, "Fit inside"),
        new(ImageResizeMode.Fill, "Fill and crop"),
        new(ImageResizeMode.Stretch, "Stretch"),
        new(ImageResizeMode.None, "Exact original")
    ];

    public IReadOnlyList<Option<ResamplingAlgorithm>> ResamplingOptions { get; } =
    [
        new(ResamplingAlgorithm.NearestNeighbor, "Nearest Neighbor"),
        new(ResamplingAlgorithm.Bilinear, "Bilinear"),
        new(ResamplingAlgorithm.Bicubic, "Bicubic"),
        new(ResamplingAlgorithm.Lanczos, "Lanczos"),
        new(ResamplingAlgorithm.MitchellNetravali, "Mitchell-Netravali")
    ];

    public IReadOnlyList<Option<TransparencyMode>> TransparencyOptions { get; } =
    [
        new(TransparencyMode.Preserve, "Preserve alpha"),
        new(TransparencyMode.Flatten, "Flatten onto colour")
    ];

    public IReadOnlyList<string> TiffCompressionOptions { get; } = ["Lzw", "Zip", "Jpeg", "None"];

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var option in ResizeModes) option.RefreshLocalization();
        foreach (var option in ResamplingOptions) option.RefreshLocalization();
        foreach (var option in TransparencyOptions) option.RefreshLocalization();
        ResizeMode.RefreshLocalization();
        Resampling.RefreshLocalization();
        Transparency.RefreshLocalization();
    }

    [ObservableProperty] private double _quality;
    [ObservableProperty] private double _pngCompression = 6;
    [ObservableProperty] private bool _losslessWebP;
    [ObservableProperty] private bool _progressiveJpeg = true;
    [ObservableProperty] private double _avifSpeed = 6;
    [ObservableProperty] private double _gifColors = 256;
    [ObservableProperty] private string _tiffCompression;
    [ObservableProperty] private string _icoSizes = "256,128,64,48,32,16";
    [ObservableProperty] private bool _enableResize;
    [ObservableProperty] private double? _resizeWidth;
    [ObservableProperty] private double? _resizeHeight;
    [ObservableProperty] private bool _preserveAspect = true;
    [ObservableProperty] private Option<ImageResizeMode> _resizeMode;
    [ObservableProperty] private Option<ResamplingAlgorithm> _resampling;
    [ObservableProperty] private Option<TransparencyMode> _transparency;
    [ObservableProperty] private string _backgroundColor = "#FFFFFF";
    [ObservableProperty] private bool _preserveAnimation = true;
    [ObservableProperty] private bool _flattenAnimation;
    [ObservableProperty] private bool _preserveIccProfile = true;

    public bool ShowQuality => SelectedFormat?.IsLossy == true && !(SelectedFormat.Id == "webp" && LosslessWebP);
    public bool ShowPngCompression => SelectedFormat?.Id == "png";
    public bool ShowLossless => SelectedFormat?.Id == "webp";
    public bool ShowProgressive => SelectedFormat?.Id == "jpg";
    public bool ShowAvifSpeed => SelectedFormat?.Id == "avif";
    public bool ShowGifColors => SelectedFormat?.Id == "gif";
    public bool ShowTiffCompression => SelectedFormat?.Id == "tif";
    public bool ShowIcoSizes => SelectedFormat?.Id == "ico";
    public bool ShowTransparency => SelectedFormat?.SupportsAlpha == true;
    public bool ShowBackgroundColor => Transparency?.Value == TransparencyMode.Flatten || SelectedFormat?.SupportsAlpha == false;
    public bool ShowAnimationControls => HasMultiFrameFiles;
    public bool ShowPreserveAnimation => ShowAnimationControls && SelectedFormat?.SupportsAnimation == true;
    public bool ShowIcc => SelectedFormat?.Format.SupportsIccProfile == true;
    public bool SupportsAlpha => SelectedFormat?.SupportsAlpha == true;
    public bool SupportsAnimation => SelectedFormat?.SupportsAnimation == true;

    protected override void OnFormatChanged()
    {
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(ShowPngCompression));
        OnPropertyChanged(nameof(ShowLossless));
        OnPropertyChanged(nameof(ShowProgressive));
        OnPropertyChanged(nameof(ShowAvifSpeed));
        OnPropertyChanged(nameof(ShowGifColors));
        OnPropertyChanged(nameof(ShowTiffCompression));
        OnPropertyChanged(nameof(ShowIcoSizes));
        OnPropertyChanged(nameof(ShowTransparency));
        OnPropertyChanged(nameof(ShowBackgroundColor));
        OnPropertyChanged(nameof(ShowAnimationControls));
        OnPropertyChanged(nameof(ShowPreserveAnimation));
        OnPropertyChanged(nameof(ShowIcc));
        OnPropertyChanged(nameof(SupportsAlpha));
        OnPropertyChanged(nameof(SupportsAnimation));
        RaiseChanged();
    }

    protected override void OnFilesChanged()
    {
        OnPropertyChanged(nameof(ShowAnimationControls));
        OnPropertyChanged(nameof(ShowPreserveAnimation));
        OnPropertyChanged(nameof(ShowBackgroundColor));
    }

    protected override string BuildSummary()
    {
        if (SelectedFormat is null) return "Choose an output format";
        var resize = EnableResize
            ? ResizeWidth is > 0 || ResizeHeight is > 0
                ? " · " + LocalizationService.F("resize {0} × {1}",
                    ResizeWidth?.ToString("0") ?? "auto", ResizeHeight?.ToString("0") ?? "auto")
                : " · " + LocalizationService.T("resize enabled")
            : "";
        var animation = HasMultiFrameFiles
            ? " · " + LocalizationService.T(FlattenAnimation ? "first frame"
                : SupportsAnimation ? "animation preserved" : "animation needs flattening")
            : "";
        return $"{FileCountText} → {SelectedFormat.ExtensionUpper}{resize}{animation}";
    }

    protected override string BuildDefaultTitle() =>
        LocalizationService.F("Convert {0} to {1}", FileCountText,
            SelectedFormat?.ExtensionUpper ?? LocalizationService.T("images"));

    public override bool Accepts(MediaInfo info) => info.Category == MediaCategory.Image;

    public override OperationSettings BuildSettings()
    {
        var settings = new ImageConversionSettings
        {
            OutputFormatId = SelectedFormat?.Id ?? "png",
            Quality = (int)Math.Clamp(Quality, 1, 100),
            CompressionLevel = (int)Math.Clamp(PngCompression, 0, 9),
            Lossless = LosslessWebP,
            Progressive = ProgressiveJpeg,
            ResizeWidth = EnableResize && ResizeWidth is > 0 ? (int)Math.Round(ResizeWidth.Value) : null,
            ResizeHeight = EnableResize && ResizeHeight is > 0 ? (int)Math.Round(ResizeHeight.Value) : null,
            ResizeMode = ResizeMode?.Value ?? ImageResizeMode.Fit,
            PreserveAspectRatio = PreserveAspect,
            Resampling = Resampling?.Value ?? ResamplingAlgorithm.Lanczos,
            TransparencyMode = Transparency?.Value ?? TransparencyMode.Preserve,
            BackgroundColor = BackgroundColor,
            PreserveAnimation = PreserveAnimation,
            FlattenAnimation = FlattenAnimation,
            PreserveIccProfile = PreserveIccProfile,
            IcoSizes = IcoSizes,
            TiffCompression = TiffCompression,
            GifColors = (int)Math.Clamp(GifColors, 2, 256),
            AvifSpeed = (int)Math.Clamp(AvifSpeed, 0, 10)
        };
        CommonSettings(settings);
        return settings;
    }

    partial void OnQualityChanged(double value) => RaiseChanged();
    partial void OnPngCompressionChanged(double value) => RaiseChanged();
    partial void OnLosslessWebPChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuality));
        RaiseChanged();
    }
    partial void OnProgressiveJpegChanged(bool value) => RaiseChanged();
    partial void OnAvifSpeedChanged(double value) => RaiseChanged();
    partial void OnGifColorsChanged(double value) => RaiseChanged();
    partial void OnTiffCompressionChanged(string value) => RaiseChanged();
    partial void OnIcoSizesChanged(string value) => RaiseChanged();
    partial void OnEnableResizeChanged(bool value) => RaiseChanged();
    partial void OnResizeWidthChanged(double? value) => RaiseChanged();
    partial void OnResizeHeightChanged(double? value) => RaiseChanged();
    partial void OnPreserveAspectChanged(bool value) => RaiseChanged();
    partial void OnResizeModeChanged(Option<ImageResizeMode> value) => RaiseChanged();
    partial void OnResamplingChanged(Option<ResamplingAlgorithm> value) => RaiseChanged();
    partial void OnTransparencyChanged(Option<TransparencyMode> value)
    {
        OnPropertyChanged(nameof(ShowBackgroundColor));
        RaiseChanged();
    }
    partial void OnPreserveAnimationChanged(bool value) => RaiseChanged();
    partial void OnFlattenAnimationChanged(bool value) => RaiseChanged();
    partial void OnPreserveIccProfileChanged(bool value) => RaiseChanged();
}
