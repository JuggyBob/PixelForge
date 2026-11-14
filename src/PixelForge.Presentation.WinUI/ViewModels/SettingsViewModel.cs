using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace PixelForge.Presentation.ViewModels;

public enum SettingsSection
{
    General,
    Converter,
    Ocr,
    Upscaler,
    Processing,
    Storage,
    Diagnostics,
    About
}

public sealed record SettingsSectionOption(SettingsSection Value, string Label, string Glyph)
{
    public override string ToString() => Label;
}

public sealed record AccentSwatch(string Name, string Hex);

public sealed record CodecChoice(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IAppPaths _paths;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IHistoryRepository _history;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ICapabilityRegistry _registry;
    private readonly IModelManager _models;
    private readonly IOcrModelManager _ocrModels;
    private readonly IThumbnailService _thumbnails;
    private readonly INavigationService _navigation;
    private readonly IModelPanelService _modelPanel;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _saveCts;
    private Task? _diagnosticsTask;
    private DateTimeOffset _diagnosticsLoadedAt;
    private bool _initialized;
    private bool _loading;
    private bool _syncingAccent;
    private bool _disposed;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, IAppPaths paths,
        IFilePickerService filePicker, IDialogService dialogs, INotificationService notifications,
        IHistoryRepository history, IDiagnosticsService diagnostics, ICapabilityRegistry registry,
        IModelManager models, IOcrModelManager ocrModels, IThumbnailService thumbnails,
        INavigationService navigation, IModelPanelService modelPanel, IAppLogger logger)
    {
        _settings = settings;
        _theme = theme;
        _paths = paths;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _notifications = notifications;
        _history = history;
        _diagnostics = diagnostics;
        _registry = registry;
        _models = models;
        _ocrModels = ocrModels;
        _thumbnails = thumbnails;
        _navigation = navigation;
        _modelPanel = modelPanel;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _loading = true;
        Sections =
        [
            new(SettingsSection.General, "General", "\uE713"),
            new(SettingsSection.Converter, "Converter", "\uE8AB"),
            new(SettingsSection.Ocr, "OCR", "\uE8A5"),
            new(SettingsSection.Upscaler, "Upscaler", "\uE71E"),
            new(SettingsSection.Processing, "Processing", "\uE9D9"),
            new(SettingsSection.Storage, "Storage", "\uE8B7"),
            new(SettingsSection.Diagnostics, "Diagnostics", "\uE9D2"),
            new(SettingsSection.About, "About", "\uE946")
        ];
        SelectedSection = Sections[0];
        foreach (var font in _theme.AvailableFonts) AvailableFonts.Add(font);
        LoadFromSettings();
        _loading = false;
        _models.ModelsChanged += OnModelsChanged;
    }

    public IReadOnlyList<SettingsSectionOption> Sections { get; }
    public ObservableCollection<string> AvailableFonts { get; } = [];
    public ObservableCollection<FormatItemViewModel> ImageFormats { get; } = [];
    public ObservableCollection<FormatItemViewModel> AudioFormats { get; } = [];
    public ObservableCollection<FormatItemViewModel> VideoFormats { get; } = [];
    public ObservableCollection<CodecChoice> VideoCodecs { get; } = [];
    public ObservableCollection<ModelDescriptor> AvailableModels { get; } = [];
    public ObservableCollection<ModelDescriptor> VideoModels { get; } = [];
    public ObservableCollection<ModelDescriptor> VideoInterpolationModels { get; } = [];
    public ObservableCollection<OcrModelDescriptor> OcrModels { get; } = [];
    public IReadOnlyList<Option<ExecutionProviderKind>> OcrProviders { get; } =
    [
        new(ExecutionProviderKind.Auto, "Automatic (DirectML for PP-OCR, Vulkan for advanced OCR)"),
        new(ExecutionProviderKind.DirectML, "DirectML GPU (PP-OCR)"),
        new(ExecutionProviderKind.Vulkan, "Vulkan GPU (advanced OCR)"),
        new(ExecutionProviderKind.Cpu, "CPU only")
    ];
    public ObservableCollection<ProviderChoice> Providers { get; } = [];
    public ObservableCollection<UpscaleFormatChoice> UpscaleFormats { get; } = [];
    public ObservableCollection<Option<int>> VideoFrameRates { get; } =
    [
        new(2, "2� (double)"), new(3, "3� (triple)"), new(4, "4� (quadruple)")
    ];
    public ObservableCollection<Option<ResamplingAlgorithm>> Algorithms { get; } =
    [
        new(ResamplingAlgorithm.NearestNeighbor, "Nearest Neighbor"),
        new(ResamplingAlgorithm.Bilinear, "Bilinear"),
        new(ResamplingAlgorithm.Bicubic, "Bicubic"),
        new(ResamplingAlgorithm.Lanczos, "Lanczos"),
        new(ResamplingAlgorithm.MitchellNetravali, "Mitchell-Netravali")
    ];
    public ObservableCollection<Option<int>> ScaleOptions { get; } = [new(2, "2×"), new(3, "3×"), new(4, "4×")];
    public ObservableCollection<Option<int>> AudioBitrateOptions { get; } =
    [
        new(64, "64 kbps"), new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"),
        new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")
    ];
    public ObservableCollection<Option<ConversionPreset>> VideoPresets { get; } =
    [
        new(ConversionPreset.Fast, "Fast"), new(ConversionPreset.Balanced, "Balanced"), new(ConversionPreset.HighQuality, "High quality")
    ];
    public ObservableCollection<Option<ThemeMode>> ThemeOptions { get; } =
    [
        new(ThemeMode.System, "Follow Windows"), new(ThemeMode.Light, "Light"), new(ThemeMode.Dark, "Dark")
    ];
    public ObservableCollection<Option<UpscaleMethod>> UpscaleMethods { get; } =
    [
        new(UpscaleMethod.Traditional, "Traditional interpolation"), new(UpscaleMethod.Ai, "AI reconstruction")
    ];
    public ObservableCollection<Option<ExecutionProviderKind>> ProviderKinds { get; } =
    [
        new(ExecutionProviderKind.Auto, "Automatic"), new(ExecutionProviderKind.Cpu, "CPU"),
        new(ExecutionProviderKind.DirectML, "DirectML (GPU)"), new(ExecutionProviderKind.Vulkan, "Vulkan (NCNN)")
    ];
    public ObservableCollection<CollisionOption> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number", "Never replace existing files."),
        new(CollisionPolicy.Skip, "Skip existing files", "Leave existing outputs untouched."),
        new(CollisionPolicy.Overwrite, "Replace existing files", "Require confirmation.")
    ];
    public IReadOnlyList<AccentSwatch> AccentSwatches { get; } =
    [
        new("Forge", "#D2693A"), new("Ember", "#C2410C"), new("Amber", "#B45309"),
        new("Teal", "#0F766E"), new("Ocean", "#0B6E99"), new("Indigo", "#4F46E5"),
        new("Violet", "#7C3AED"), new("Rose", "#BE123C"), new("Forest", "#15803D"), new("Slate", "#475569")
    ];
    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    [ObservableProperty] private SettingsSectionOption _selectedSection;
    [ObservableProperty] private Option<ThemeMode> _selectedTheme = new(ThemeMode.System, "Follow Windows");
    [ObservableProperty] private string _accentHex = "#D2693A";
    [ObservableProperty] private Color _accentColor = Color.FromArgb(255, 210, 105, 58);
    [ObservableProperty] private double _accentRed = 210;
    [ObservableProperty] private double _accentGreen = 105;
    [ObservableProperty] private double _accentBlue = 58;
    [ObservableProperty] private string _selectedFont = "Segoe UI Variable Text";
    [ObservableProperty] private string _selectedMonoFont = "JetBrains Mono";
    [ObservableProperty] private double _fontScalePercent = 100;
    [ObservableProperty] private string _defaultOutputDirectory = "";
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _confirmOverwrite = true;
    [ObservableProperty] private bool _rememberLastPage = true;
    [ObservableProperty] private bool _openOutputAfterJob;
    [ObservableProperty] private string _saveStatus = "Settings are saved locally";

    [ObservableProperty] private OcrModelDescriptor? _selectedOcrModel;
    [ObservableProperty] private Option<ExecutionProviderKind> _selectedOcrProvider = new(ExecutionProviderKind.Auto, "Automatic (DirectML for PP-OCR, Vulkan for advanced OCR)");
    [ObservableProperty] private bool _ocrGeneratePdf;
    [ObservableProperty] private bool _ocrPreprocess = true;
    [ObservableProperty] private string _ocrOutputDirectory = "";
    [ObservableProperty] private string _ocrProviderStatus = "";
    [ObservableProperty] private FormatItemViewModel? _selectedImageFormat;
    [ObservableProperty] private double _imageQuality = 90;
    [ObservableProperty] private FormatItemViewModel? _selectedAudioFormat;
    [ObservableProperty] private Option<int> _selectedAudioBitrate = new(192, "192 kbps");
    [ObservableProperty] private FormatItemViewModel? _selectedVideoFormat;
    [ObservableProperty] private CodecChoice? _selectedVideoCodec;
    [ObservableProperty] private Option<ConversionPreset> _selectedVideoPreset = new(ConversionPreset.Balanced, "Balanced");
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private bool _converterPreserveMetadata = true;

    [ObservableProperty] private Option<UpscaleMethod> _selectedUpscaleMethod = new(UpscaleMethod.Traditional, "Traditional interpolation");
    [ObservableProperty] private Option<ResamplingAlgorithm>? _selectedUpscaleAlgorithm;
    [ObservableProperty] private Option<ExecutionProviderKind>? _selectedProviderKind;
    [ObservableProperty] private ModelDescriptor? _selectedUpscaleModel;
    [ObservableProperty] private Option<int>? _selectedUpscaleScale;
    [ObservableProperty] private double _upscaleTileSize;
    [ObservableProperty] private double _upscaleOverlap = 32;
    [ObservableProperty] private UpscaleFormatChoice? _selectedUpscaleFormat;
    [ObservableProperty] private string _upscaleSuffix = "";
    [ObservableProperty] private bool _upscaleIncludeSubfolders;
    [ObservableProperty] private bool _upscalePreserveStructure = true;
    [ObservableProperty] private string _modelDirectory = "";
    [ObservableProperty] private string _upscaleOutputDirectory = "";
    [ObservableProperty] private string _modelStatusText = "";

    [ObservableProperty] private bool _videoEnableSuperResolution = true;
    [ObservableProperty] private ModelDescriptor? _selectedVideoModel;
    [ObservableProperty] private bool _videoEnableInterpolation;
    [ObservableProperty] private ModelDescriptor? _selectedVideoInterpolationModel;
    [ObservableProperty] private Option<int>? _selectedVideoFrameRate;
    [ObservableProperty] private Option<ExecutionProviderKind>? _selectedVideoProviderKind;
    [ObservableProperty] private double _videoBitrateKbps = 12000;
    [ObservableProperty] private string _videoSuffix = "_upscaled";
    [ObservableProperty] private bool _videoIncludeSubfolders = true;
    [ObservableProperty] private CollisionOption? _selectedVideoCollision;
    [ObservableProperty] private string _videoOutputDirectory = "";
    [ObservableProperty] private string _videoModelStatus = "";
    [ObservableProperty] private string _videoInterpolationStatus = "";

    [ObservableProperty] private double _maxConcurrentJobs = 2;
    [ObservableProperty] private double _gpuConcurrency = 1;
    [ObservableProperty] private string _tempDirectory = "";
    [ObservableProperty] private bool _cleanupTempOnExit = true;
    [ObservableProperty] private bool _keepTempOnFailure;
    [ObservableProperty] private string _logLevel = "Information";

    [ObservableProperty] private double _historyRetentionDays = 180;
    [ObservableProperty] private double _maxHistoryEntries = 5000;
    [ObservableProperty] private bool _saveJobHistory = true;

    [ObservableProperty] private bool _isDiagnosticsLoading;
    [ObservableProperty] private string _diagnosticsReport = "";
    [ObservableProperty] private string _systemSummary = "";
    [ObservableProperty] private string _engineSummary = "";
    [ObservableProperty] private string _codecSummary = "";
    [ObservableProperty] private string _aboutVersion = "";

    public bool IsGeneral => SelectedSection.Value == SettingsSection.General;
    public bool IsConverter => SelectedSection.Value == SettingsSection.Converter;
    public bool IsOcr => SelectedSection.Value == SettingsSection.Ocr;
    public bool IsUpscaler => SelectedSection.Value == SettingsSection.Upscaler;
    public bool IsProcessing => SelectedSection.Value == SettingsSection.Processing;
    public bool IsStorage => SelectedSection.Value == SettingsSection.Storage;
    public bool IsDiagnostics => SelectedSection.Value == SettingsSection.Diagnostics;
    public bool IsAbout => SelectedSection.Value == SettingsSection.About;
    public string FontPreview => "PixelForge — convert, transcode and upscale locally. 0123456789";
    public string LogsPath => _paths.LogsDirectory;
    public string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
    public string MonoPreview => "ffmpeg -crf 23 · h264_amf · ONNX Runtime";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            _loading = true;
            try
            {
                LoadModels();
                LoadOcrModels();
                LoadFromSettings();
            }
            finally
            {
                _loading = false;
            }

            if (SelectedSection.Value == SettingsSection.Diagnostics &&
                (string.IsNullOrWhiteSpace(DiagnosticsReport) ||
                 DateTimeOffset.Now - _diagnosticsLoadedAt >= TimeSpan.FromMinutes(5)))
                await RefreshDiagnosticsAsync();
            return;
        }

        _initialized = true;
        _loading = true;
        try
        {
            if (!_registry.IsInitialized) await _registry.InitializeAsync();
            await _models.InitializeAsync();
            await _ocrModels.InitializeAsync();
            AvailableFonts.Clear();
            foreach (var font in _theme.AvailableFonts) AvailableFonts.Add(font);
            SelectedFont = "";
            SelectedMonoFont = "";
            LoadFormats();
            LoadModels();
            LoadOcrModels();
            LoadFromSettings();
            AboutVersion = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            if (SelectedSection.Value == SettingsSection.Diagnostics)
                await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings initialization failed");
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadFromSettings()
    {
        var g = _settings.Settings.General;
        SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Value == g.Theme) ?? ThemeOptions[0];
        AccentHex = g.AccentColorHex;
        SelectedFont = _theme.AvailableFonts.Contains(g.FontFamily, StringComparer.OrdinalIgnoreCase) ? g.FontFamily : "Segoe UI Variable Text";
        SelectedMonoFont = _theme.AvailableFonts.Contains(g.MonoFontFamily, StringComparer.OrdinalIgnoreCase) ? g.MonoFontFamily : "JetBrains Mono";
        FontScalePercent = Math.Round(g.FontScale * 100);
        DefaultOutputDirectory = g.DefaultOutputDirectory;
        ShowNotifications = g.ShowNotifications;
        ConfirmOverwrite = g.ConfirmOverwrite;
        RememberLastPage = g.RememberLastPage;
        OpenOutputAfterJob = g.OpenOutputAfterJob;

        var c = _settings.Settings.Converter;
        ImageQuality = c.ImageQuality;
        ConverterPreserveMetadata = c.ImagePreserveMetadata;
        SelectedAudioBitrate = AudioBitrateOptions.FirstOrDefault(o => o.Value == c.AudioBitrateKbps) ?? AudioBitrateOptions[4];
        SelectedVideoPreset = VideoPresets.FirstOrDefault(o => o.Value == c.VideoPreset) ?? VideoPresets[1];
        SelectedCollision = CollisionOptions.FirstOrDefault(o => o.Policy == c.CollisionPolicy) ?? CollisionOptions[0];

        var o = _settings.Settings.Ocr;
        SelectedOcrModel = OcrModels.FirstOrDefault(m => m.Id == o.ModelId) ?? OcrModels.FirstOrDefault();
        SelectedOcrProvider = OcrProviders.FirstOrDefault(p => p.Value == o.ExecutionProvider) ?? OcrProviders[0];
        OcrGeneratePdf = o.GeneratePdf;
        OcrPreprocess = o.Preprocess;
        OcrOutputDirectory = o.DefaultOutputDirectory;
        UpdateOcrStatus();

        var u = _settings.Settings.Upscaler;
        SelectedUpscaleMethod = UpscaleMethods.FirstOrDefault(o => o.Value == u.Method) ?? UpscaleMethods[0];
        SelectedUpscaleAlgorithm = Algorithms.FirstOrDefault(a => a.Value == u.Algorithm) ?? Algorithms[3];
        SelectedProviderKind = ProviderKinds.FirstOrDefault(p => p.Value == u.ExecutionProvider) ?? ProviderKinds[0];
        SelectedUpscaleScale = ScaleOptions.FirstOrDefault(s => s.Value == u.ScaleFactor) ?? ScaleOptions[2];
        UpscaleTileSize = u.TileSize;
        UpscaleOverlap = u.TileOverlap > 0 ? u.TileOverlap : 32;
        UpscaleSuffix = u.FileNameSuffix;
        UpscaleIncludeSubfolders = u.IncludeSubfolders;
        UpscalePreserveStructure = u.PreserveFolderStructure;
        ModelDirectory = u.ModelDirectory;
        UpscaleOutputDirectory = u.DefaultOutputDirectory;

        var v = _settings.Settings.VideoUpscaler;
        VideoEnableSuperResolution = v.EnableSuperResolution;
        VideoEnableInterpolation = v.EnableInterpolation;
        SelectedVideoFrameRate = VideoFrameRates.FirstOrDefault(o => o.Value == v.FrameRateMultiplier) ?? VideoFrameRates[0];
        SelectedVideoProviderKind = ProviderKinds.FirstOrDefault(p => p.Value == v.ExecutionProvider) ?? ProviderKinds[0];
        VideoBitrateKbps = v.VideoBitrateKbps;
        VideoSuffix = v.FileNameSuffix;
        VideoIncludeSubfolders = v.IncludeSubfolders;
        SelectedVideoCollision = CollisionOptions.FirstOrDefault(o => o.Policy == v.CollisionPolicy) ?? CollisionOptions[0];
        VideoOutputDirectory = v.DefaultOutputDirectory;
        SelectedVideoModel = VideoModels.FirstOrDefault(m => m.Id == v.ImageModelId) ?? SelectedVideoModel;
        SelectedVideoInterpolationModel = VideoInterpolationModels.FirstOrDefault(m => m.Id == v.InterpolationModelId)
                                          ?? SelectedVideoInterpolationModel;
        UpdateVideoModelStatus();

        var p = _settings.Settings.Processing;
        MaxConcurrentJobs = p.MaxConcurrentJobs;
        GpuConcurrency = p.GpuConcurrency;
        TempDirectory = p.TempDirectory;
        CleanupTempOnExit = p.CleanupTempOnExit;
        KeepTempOnFailure = p.KeepTempOnFailure;
        LogLevel = LogLevels.Contains(p.LogLevel) ? p.LogLevel : "Information";

        var s = _settings.Settings.Storage;
        HistoryRetentionDays = s.HistoryRetentionDays;
        MaxHistoryEntries = s.MaxHistoryEntries;
        SaveJobHistory = s.SaveJobHistory;
    }

    private void LoadFormats()
    {
        ImageFormats.Clear();
        foreach (var f in _registry.GetOutputFormats(MediaCategory.Image)) ImageFormats.Add(new FormatItemViewModel(f.Format));
        AudioFormats.Clear();
        foreach (var f in _registry.GetOutputFormats(MediaCategory.Audio)) AudioFormats.Add(new FormatItemViewModel(f.Format));
        VideoFormats.Clear();
        foreach (var f in _registry.GetOutputFormats(MediaCategory.Video)) VideoFormats.Add(new FormatItemViewModel(f.Format));
        UpscaleFormats.Clear();
        UpscaleFormats.Add(new UpscaleFormatChoice(FormatIds.Auto, "Same as source", "", true, true));
        foreach (var f in _registry.ImageOutputFormats)
            UpscaleFormats.Add(new UpscaleFormatChoice(f.Id, f.DisplayName, f.NormalizedExtension, f.IsLossy, false));

        SelectedImageFormat = ImageFormats.FirstOrDefault(f => f.Id == _settings.Settings.Converter.ImageFormatId) ?? ImageFormats.FirstOrDefault();
        SelectedAudioFormat = AudioFormats.FirstOrDefault(f => f.Id == _settings.Settings.Converter.AudioFormatId) ?? AudioFormats.FirstOrDefault();
        SelectedVideoFormat = VideoFormats.FirstOrDefault(f => f.Id == _settings.Settings.Converter.VideoContainerId) ?? VideoFormats.FirstOrDefault();
        SelectedUpscaleFormat = UpscaleFormats.FirstOrDefault(f => f.Id == _settings.Settings.Upscaler.OutputFormatId) ?? UpscaleFormats[0];
        LoadVideoCodecs();
    }

    private void LoadVideoCodecs()
    {
        var previous = SelectedVideoCodec?.Id ?? _settings.Settings.Converter.VideoCodecId;
        VideoCodecs.Clear();
        if (SelectedVideoFormat is not null)
        {
            foreach (var codec in _registry.GetVideoCodecs(SelectedVideoFormat.Id))
                VideoCodecs.Add(new CodecChoice(codec.Id, codec.DisplayName));
        }
        SelectedVideoCodec = VideoCodecs.FirstOrDefault(c => c.Id == previous) ?? VideoCodecs.FirstOrDefault();
    }

    private void LoadModels()
    {
        var previous = SelectedUpscaleModel?.Id ?? _settings.Settings.Upscaler.ModelId;
        var previousVideo = SelectedVideoModel?.Id ?? _settings.Settings.VideoUpscaler.ImageModelId;
        var previousInterpolation = SelectedVideoInterpolationModel?.Id ??
                                    _settings.Settings.VideoUpscaler.InterpolationModelId;

        AvailableModels.Clear();
        VideoModels.Clear();
        VideoInterpolationModels.Clear();
        foreach (var descriptor in _models.AvailableModels.OrderBy(m => m.SortOrder))
        {
            if (descriptor.Task == ModelTask.ImageUpscale && descriptor.SupportsStillImage)
                AvailableModels.Add(descriptor);
            if (descriptor.Task == ModelTask.ImageUpscale && descriptor.SupportsVideoFrames)
                VideoModels.Add(descriptor);
            if (descriptor.Task == ModelTask.FrameInterpolation && descriptor.SupportsVideoFrames)
                VideoInterpolationModels.Add(descriptor);
        }

        SelectedUpscaleModel = AvailableModels.FirstOrDefault(m => m.Id == previous) ?? AvailableModels.FirstOrDefault();
        SelectedVideoModel = VideoModels.FirstOrDefault(m => m.Id == previousVideo) ?? VideoModels.FirstOrDefault();
        SelectedVideoInterpolationModel = VideoInterpolationModels.FirstOrDefault(m => m.Id == previousInterpolation)
                                          ?? VideoInterpolationModels.FirstOrDefault();
        UpdateModelStatus();
        UpdateVideoModelStatus();
    }

    private void LoadOcrModels()
    {
        var previous = SelectedOcrModel?.Id ?? _settings.Settings.Ocr.ModelId;
        OcrModels.Clear();
        foreach (var descriptor in _ocrModels.AvailableModels.OrderBy(m => m.SortOrder))
            OcrModels.Add(descriptor);
        SelectedOcrModel = OcrModels.FirstOrDefault(m => m.Id == previous)
                           ?? OcrModels.FirstOrDefault(m => m.Id == "ppocrv6-small")
                           ?? OcrModels.FirstOrDefault();
        UpdateOcrStatus();
    }

    private void UpdateOcrStatus()
    {
        var directMl = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML);
        var installed = SelectedOcrModel is null ? null : _ocrModels.GetInstalled(SelectedOcrModel.Id);
        var model = SelectedOcrModel is null
            ? "No OCR model is available."
            : installed is { IsValid: true }
                ? $"{SelectedOcrModel.Name} · installed · {SelectedOcrModel.DisplaySize}"
                : $"{SelectedOcrModel.Name} · {SelectedOcrModel.DisplaySize} · download required";
        var provider = SelectedOcrModel?.Backend == OcrBackendKind.LlamaCpp
            ? SelectedOcrProvider?.Value == ExecutionProviderKind.Cpu
                ? "llama.cpp CPU"
                : "llama.cpp Vulkan preferred · CPU fallback"
            : SelectedOcrProvider?.Value switch
            {
                ExecutionProviderKind.Cpu => "CPU only",
                ExecutionProviderKind.Vulkan => "Vulkan requested · PP-OCRv6 falls back to CPU",
                ExecutionProviderKind.DirectML when directMl?.IsAvailable == true => "DirectML requested",
                ExecutionProviderKind.DirectML => "DirectML requested but unavailable; CPU fallback will be reported",
                _ when directMl?.IsAvailable == true => "Automatic · DirectML preferred · CPU fallback",
                _ => "Automatic · CPU fallback"
            };
        OcrProviderStatus = $"{model} · {provider}";
    }

    private void UpdateVideoModelStatus()
    {
        var image = SelectedVideoModel is null ? null : _models.GetInstalled(SelectedVideoModel.Id);
        VideoModelStatus = SelectedVideoModel is null
            ? "No image upscaling model is available for video."
            : image is { IsValid: true }
                ? $"{SelectedVideoModel.Name} · installed"
                : $"{SelectedVideoModel.Name} · download required before jobs can run";

        var interpolation = SelectedVideoInterpolationModel is null
            ? null
            : _models.GetInstalled(SelectedVideoInterpolationModel.Id);
        VideoInterpolationStatus = SelectedVideoInterpolationModel is null
            ? "No frame interpolation model is available."
            : interpolation is { IsValid: true }
                ? $"{SelectedVideoInterpolationModel.Name} · installed"
                : $"{SelectedVideoInterpolationModel.Name} · download required before interpolation can run";
    }

    private void UpdateModelStatus()
    {
        if (SelectedUpscaleModel is null)
        {
            ModelStatusText = "No models available";
            return;
        }
        var installed = _models.GetInstalled(SelectedUpscaleModel.Id);
        ModelStatusText = installed is { IsValid: true }
            ? $"{SelectedUpscaleModel.Name} · installed · {installed.ActualSizeBytes / 1024d / 1024:0.0} MB"
            : $"{SelectedUpscaleModel.Name} · not installed";
    }

    private void OnModelsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnModelsChanged(sender, e));
            return;
        }
        LoadModels();
    }

    public async Task RefreshDiagnosticsAsync(bool force = false)
    {
        if (!force &&
            !string.IsNullOrWhiteSpace(DiagnosticsReport) &&
            DateTimeOffset.Now - _diagnosticsLoadedAt < TimeSpan.FromMinutes(5))
            return;

        if (_diagnosticsTask is { IsCompleted: false })
        {
            await _diagnosticsTask;
            return;
        }

        _diagnosticsTask = RefreshDiagnosticsCoreAsync();
        try
        {
            await _diagnosticsTask;
        }
        finally
        {
            _diagnosticsTask = null;
        }
    }

    private async Task RefreshDiagnosticsCoreAsync()
    {
        IsDiagnosticsLoading = true;
        try
        {
            var diagnostics = await _diagnostics.BuildAsync();
            DiagnosticsReport = await _diagnostics.BuildReportAsync(diagnostics);
            SystemSummary = $"{diagnostics.OperatingSystem}\n{diagnostics.CpuName} · {diagnostics.LogicalCoreCount} logical cores · {diagnostics.TotalMemoryBytes / 1024d / 1024 / 1024:0.0} GB RAM";
            var gpu = diagnostics.Gpus.FirstOrDefault();
            if (gpu is not null) SystemSummary += $"\n{gpu.Name} · driver {gpu.DriverVersion}";
            EngineSummary = string.Join("\n", diagnostics.Engines.Select(e =>
                $"{e.DisplayName}: {(e.IsAvailable ? "Ready" : "Unavailable")} {(e.Version is null ? "" : "· " + e.Version)}"));
            CodecSummary = "Video codecs: " + string.Join(", ", diagnostics.AvailableVideoCodecs) + "\nAudio codecs: " + string.Join(", ", diagnostics.AvailableAudioCodecs) +
                           "\nImage outputs: " + string.Join(" ", diagnostics.AvailableImageFormats);
            AboutVersion = diagnostics.AppVersion.StartsWith("Version ", StringComparison.OrdinalIgnoreCase)
                ? diagnostics.AppVersion
                : "Version " + diagnostics.AppVersion;
            _diagnosticsLoadedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Diagnostics refresh failed: {Message}", ex.Message);
            DiagnosticsReport = ex.ToString();
        }
        finally
        {
            IsDiagnosticsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SetAccentAsync(string hex)
    {
        if (!IsValidHex(hex)) return;
        AccentHex = hex;
        await SaveNowAsync();
        _notifications.Notify("Accent updated", "The accent colour was applied across the app.", NotificationSeverity.Success);
    }

    [RelayCommand]
    private async Task ApplyAccentHexAsync()
    {
        if (!IsValidHex(AccentHex))
        {
            await _dialogs.ShowMessageAsync("Invalid colour", "Enter a HEX colour such as #D2693A or #FFD2693A.");
            return;
        }
        await SaveNowAsync();
        _notifications.Notify("Accent updated", AccentHex, NotificationSeverity.Success);
    }

    [RelayCommand]
    private async Task ResetFontAsync()
    {
        SelectedFont = "Segoe UI Variable Text";
        SelectedMonoFont = "JetBrains Mono";
        FontScalePercent = 100;
        await SaveNowAsync();
    }

    [RelayCommand]
    private async Task ChooseDefaultOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(DefaultOutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) { DefaultOutputDirectory = folder; await SaveNowAsync(); }
    }

    [RelayCommand]
    private async Task ChooseUpscaleOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(UpscaleOutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) { UpscaleOutputDirectory = folder; await SaveNowAsync(); }
    }

    [RelayCommand]
    private async Task ChooseVideoOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(VideoOutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) { VideoOutputDirectory = folder; await SaveNowAsync(); }
    }

    [RelayCommand]
    private void ResetVideoDefaults()
    {
        var defaults = new VideoUpscalerDefaults();
        _loading = true;
        try
        {
            VideoEnableSuperResolution = defaults.EnableSuperResolution;
            VideoEnableInterpolation = defaults.EnableInterpolation;
            SelectedVideoFrameRate = VideoFrameRates.FirstOrDefault(o => o.Value == defaults.FrameRateMultiplier)
                                     ?? VideoFrameRates[0];
            SelectedVideoProviderKind = ProviderKinds.FirstOrDefault(p => p.Value == defaults.ExecutionProvider)
                                        ?? ProviderKinds[0];
            VideoBitrateKbps = defaults.VideoBitrateKbps;
            VideoSuffix = defaults.FileNameSuffix;
            VideoIncludeSubfolders = defaults.IncludeSubfolders;
            SelectedVideoCollision = CollisionOptions.FirstOrDefault(o => o.Policy == defaults.CollisionPolicy)
                                     ?? CollisionOptions[0];
            VideoOutputDirectory = Path.Combine(_paths.DefaultOutputDirectory, "Video Upscaled");
            SelectedVideoModel = VideoModels.FirstOrDefault(m => m.Id == defaults.ImageModelId) ?? SelectedVideoModel;
            SelectedVideoInterpolationModel = VideoInterpolationModels.FirstOrDefault(m => m.Id == defaults.InterpolationModelId)
                                              ?? SelectedVideoInterpolationModel;
        }
        finally
        {
            _loading = false;
        }

        UpdateVideoModelStatus();
        ApplyAndSave();
    }

    [RelayCommand]
    private async Task ChooseModelFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(ModelDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (await _dialogs.ConfirmAsync("Change model storage folder?",
                "Existing models are not moved automatically. You can re-download them into the new folder.",
                "Change folder", "Cancel"))
        {
            await _models.SetModelDirectoryAsync(folder);
            ModelDirectory = folder;
            UpdateModelStatus();
        }
    }

    [RelayCommand]
    private async Task ChooseTempFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(TempDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) { TempDirectory = folder; await SaveNowAsync(); }
    }

    [RelayCommand]
    private void ManageModels() => _modelPanel.Show(ModelPanelFilter.All);

    [RelayCommand]
    private void ManageOcrModels() => _modelPanel.Show(ModelPanelFilter.Ocr);

    [RelayCommand]
    private async Task ChooseOcrOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OcrOutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) { OcrOutputDirectory = folder; await SaveNowAsync(); }
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the folder", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenNoticesAsync()
    {
        var path = NoticesPath;
        try
        {
            if (Directory.Exists(path))
            {
                await _dialogs.ShowErrorAsync("Cannot open third-party notices",
                    $"A folder named THIRD-PARTY-NOTICES.md exists at:\n{path}\n\n" +
                    "PixelForge will not delete or overwrite it. Rename the folder or reinstall the application to restore the notices document.");
                return;
            }

            if (!File.Exists(path))
            {
                await _dialogs.ShowErrorAsync("Third-party notices are missing",
                    $"The notices document was not found at:\n{path}\n\nReinstall or repair PixelForge to restore it.");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not open third-party notices: {Message}", ex.Message);
            await _dialogs.ShowErrorAsync("Cannot open third-party notices",
                $"{ex.Message}\n\nThe document is located at:\n{path}");
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear operation history?",
                "Delete all history rows from the local database? Your media files are not touched.",
                "Clear history", "Cancel", destructive: true)) return;
        await _history.ClearAsync();
        await _dialogs.ShowMessageAsync("History cleared", "Operation history has been removed.");
    }

    [RelayCommand]
    private async Task ClearThumbnailCacheAsync()
    {
        await _thumbnails.ClearCacheAsync();
        await _dialogs.ShowMessageAsync("Cache cleared", "Cached thumbnails have been removed.");
    }

    [RelayCommand]
    private async Task CleanupTempAsync()
    {
        try
        {
            if (Directory.Exists(TempDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(TempDirectory, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            await _dialogs.ShowMessageAsync("Temporary files cleaned", "Stale temporary files were removed where possible.");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Cleanup failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshDiagnosticsRelayAsync() => await RefreshDiagnosticsAsync(force: true);

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DiagnosticsReport)) await RefreshDiagnosticsAsync(force: true);
            var package = new DataPackage();
            package.SetText(DiagnosticsReport);
            Clipboard.SetContent(package);
            await _dialogs.ShowMessageAsync("Diagnostics copied", "The report is on the clipboard.");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not copy diagnostics", ex.Message);
        }
    }

    private async Task SaveNowAsync()
    {
        if (_loading) return;
        try
        {
            await _settings.SaveAsync();
            SaveStatus = "Saved just now · settings are local";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings save failed");
            SaveStatus = "Could not save settings";
        }
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        _ = SaveDebouncedAsync(_saveCts.Token);
    }

    private async Task SaveDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            if (!cancellationToken.IsCancellationRequested) await SaveNowAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit.
        }
    }

    private void ApplyGeneral()
    {
        var g = _settings.Settings.General;
        g.Theme = SelectedTheme?.Value ?? ThemeMode.System;
        if (IsValidHex(AccentHex)) g.AccentColorHex = AccentHex;
        g.FontFamily = SelectedFont;
        g.MonoFontFamily = SelectedMonoFont;
        g.FontScale = Math.Clamp(FontScalePercent / 100.0, 0.85, 1.35);
        g.DefaultOutputDirectory = DefaultOutputDirectory;
        g.ShowNotifications = ShowNotifications;
        g.ConfirmOverwrite = ConfirmOverwrite;
        g.RememberLastPage = RememberLastPage;
        g.OpenOutputAfterJob = OpenOutputAfterJob;
    }

    private void ApplyConverter()
    {
        var c = _settings.Settings.Converter;
        if (SelectedImageFormat is not null) c.ImageFormatId = SelectedImageFormat.Id;
        c.ImageQuality = (int)Math.Clamp(ImageQuality, 1, 100);
        if (SelectedAudioFormat is not null) c.AudioFormatId = SelectedAudioFormat.Id;
        c.AudioBitrateKbps = SelectedAudioBitrate?.Value ?? 192;
        if (SelectedVideoFormat is not null) c.VideoContainerId = SelectedVideoFormat.Id;
        if (SelectedVideoCodec is not null) c.VideoCodecId = SelectedVideoCodec.Id;
        c.VideoPreset = SelectedVideoPreset?.Value ?? ConversionPreset.Balanced;
        c.CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName;
        c.ImagePreserveMetadata = ConverterPreserveMetadata;
    }

    private void ApplyUpscaler()
    {
        var u = _settings.Settings.Upscaler;
        u.Method = SelectedUpscaleMethod?.Value ?? UpscaleMethod.Traditional;
        u.Algorithm = SelectedUpscaleAlgorithm?.Value ?? ResamplingAlgorithm.Lanczos;
        u.ExecutionProvider = SelectedProviderKind?.Value ?? ExecutionProviderKind.Auto;
        if (SelectedUpscaleModel is not null) u.ModelId = SelectedUpscaleModel.Id;
        u.ScaleFactor = SelectedUpscaleScale?.Value ?? 4;
        u.TileSize = (int)Math.Clamp(UpscaleTileSize, 0, 2048);
        u.TileOverlap = (int)Math.Clamp(UpscaleOverlap, 0, 512);
        u.OutputFormatId = SelectedUpscaleFormat?.Id ?? FormatIds.Auto;
        u.FileNameSuffix = UpscaleSuffix;
        u.IncludeSubfolders = UpscaleIncludeSubfolders;
        u.PreserveFolderStructure = UpscalePreserveStructure;
        u.ModelDirectory = ModelDirectory;
        u.DefaultOutputDirectory = UpscaleOutputDirectory;
    }

    private void ApplyOcrDefaults()
    {
        var o = _settings.Settings.Ocr;
        if (SelectedOcrModel is not null) o.ModelId = SelectedOcrModel.Id;
        o.ExecutionProvider = SelectedOcrProvider?.Value ?? ExecutionProviderKind.Auto;
        o.GeneratePdf = OcrGeneratePdf;
        o.Preprocess = OcrPreprocess;
        o.DefaultOutputDirectory = OcrOutputDirectory;
    }

    private void ApplyVideoDefaults()
    {
        var v = _settings.Settings.VideoUpscaler;
        v.EnableSuperResolution = VideoEnableSuperResolution;
        v.EnableInterpolation = VideoEnableInterpolation;
        if (SelectedVideoModel is not null) v.ImageModelId = SelectedVideoModel.Id;
        if (SelectedVideoInterpolationModel is not null) v.InterpolationModelId = SelectedVideoInterpolationModel.Id;
        v.ExecutionProvider = SelectedVideoProviderKind?.Value ?? ExecutionProviderKind.Auto;
        v.FrameRateMultiplier = SelectedVideoFrameRate?.Value ?? 2;
        v.VideoBitrateKbps = (int)Math.Clamp(VideoBitrateKbps, 100, 200_000);
        v.FileNameSuffix = VideoSuffix;
        v.IncludeSubfolders = VideoIncludeSubfolders;
        v.CollisionPolicy = SelectedVideoCollision?.Policy ?? CollisionPolicy.UniqueName;
        v.DefaultOutputDirectory = VideoOutputDirectory;
    }

    private void ApplyProcessing()
    {
        var p = _settings.Settings.Processing;
        p.MaxConcurrentJobs = (int)Math.Clamp(MaxConcurrentJobs, 1, 8);
        p.GpuConcurrency = (int)Math.Clamp(GpuConcurrency, 1, 2);
        p.TempDirectory = TempDirectory;
        p.CleanupTempOnExit = CleanupTempOnExit;
        p.KeepTempOnFailure = KeepTempOnFailure;
        p.LogLevel = LogLevel;
        _settings.Settings.Storage.HistoryRetentionDays = (int)Math.Clamp(HistoryRetentionDays, 0, 3650);
        _settings.Settings.Storage.MaxHistoryEntries = (int)Math.Clamp(MaxHistoryEntries, 100, 100000);
        _settings.Settings.Storage.SaveJobHistory = SaveJobHistory;
    }

    private void ApplyAndSave()
    {
        if (_loading) return;
        ApplyGeneral();
        ApplyConverter();
        ApplyOcrDefaults();
        ApplyUpscaler();
        ApplyVideoDefaults();
        ApplyProcessing();
        ScheduleSave();
    }

    private static bool IsValidHex(string value)
    {
        var text = value.Trim().TrimStart('#');
        return text.Length is 6 or 8 && text.All(Uri.IsHexDigit);
    }

    private static bool TryParseHexColor(string value, out Color color)
    {
        color = default;
        var text = value.Trim().TrimStart('#');
        if (text.Length is not (6 or 8) || !text.All(Uri.IsHexDigit)) return false;
        try
        {
            if (text.Length == 6)
            {
                color = Color.FromArgb(255,
                    System.Convert.ToByte(text[..2], 16),
                    System.Convert.ToByte(text.Substring(2, 2), 16),
                    System.Convert.ToByte(text.Substring(4, 2), 16));
            }
            else
            {
                color = Color.FromArgb(
                    System.Convert.ToByte(text[..2], 16),
                    System.Convert.ToByte(text.Substring(2, 2), 16),
                    System.Convert.ToByte(text.Substring(4, 2), 16),
                    System.Convert.ToByte(text.Substring(6, 2), 16));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToHex(Color color) =>
        color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void SyncAccentComponents(string hex)
    {
        if (!TryParseHexColor(hex, out var color)) return;
        _syncingAccent = true;
        try
        {
            AccentColor = color;
            AccentRed = color.R;
            AccentGreen = color.G;
            AccentBlue = color.B;
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    public void SetAccentFromColor(Color color)
    {
        _syncingAccent = true;
        try
        {
            AccentColor = color;
            AccentRed = color.R;
            AccentGreen = color.G;
            AccentBlue = color.B;
            AccentHex = ToHex(color);
        }
        finally
        {
            _syncingAccent = false;
        }

        ApplyAndSave();
    }

    private void ApplyAccentRgb()
    {
        if (_syncingAccent) return;
        var red = (byte)Math.Clamp(double.IsNaN(AccentRed) ? 0 : Math.Round(AccentRed), 0, 255);
        var green = (byte)Math.Clamp(double.IsNaN(AccentGreen) ? 0 : Math.Round(AccentGreen), 0, 255);
        var blue = (byte)Math.Clamp(double.IsNaN(AccentBlue) ? 0 : Math.Round(AccentBlue), 0, 255);
        var color = Color.FromArgb(255, red, green, blue);
        _syncingAccent = true;
        try
        {
            AccentRed = red;
            AccentGreen = green;
            AccentBlue = blue;
            AccentColor = color;
            AccentHex = ToHex(color);
        }
        finally
        {
            _syncingAccent = false;
        }

        ApplyAndSave();
    }

    partial void OnSelectedSectionChanged(SettingsSectionOption value)
    {
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsConverter));
        OnPropertyChanged(nameof(IsOcr));
        OnPropertyChanged(nameof(IsUpscaler));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsStorage));
        OnPropertyChanged(nameof(IsDiagnostics));
        OnPropertyChanged(nameof(IsAbout));
        if (value.Value == SettingsSection.Diagnostics && string.IsNullOrWhiteSpace(DiagnosticsReport))
            _ = RefreshDiagnosticsAsync();
    }

    partial void OnSelectedOcrModelChanged(OcrModelDescriptor? value) => UpdateOcrStatus();
    partial void OnSelectedOcrProviderChanged(Option<ExecutionProviderKind> value) => UpdateOcrStatus();
    partial void OnOcrGeneratePdfChanged(bool value) => ApplyAndSave();
    partial void OnOcrPreprocessChanged(bool value) => ApplyAndSave();
    partial void OnOcrOutputDirectoryChanged(string value) => ApplyAndSave();

    partial void OnSelectedThemeChanged(Option<ThemeMode> value) => ApplyAndSave();
    partial void OnAccentHexChanged(string value)
    {
        SyncAccentComponents(value);
        ApplyAndSave();
    }

    partial void OnAccentRedChanged(double value) => ApplyAccentRgb();
    partial void OnAccentGreenChanged(double value) => ApplyAccentRgb();
    partial void OnAccentBlueChanged(double value) => ApplyAccentRgb();
    partial void OnSelectedFontChanged(string value) => ApplyAndSave();
    partial void OnSelectedMonoFontChanged(string value) => ApplyAndSave();
    partial void OnFontScalePercentChanged(double value) => ApplyAndSave();
    partial void OnDefaultOutputDirectoryChanged(string value) => ApplyAndSave();
    partial void OnShowNotificationsChanged(bool value) => ApplyAndSave();
    partial void OnConfirmOverwriteChanged(bool value) => ApplyAndSave();
    partial void OnRememberLastPageChanged(bool value) => ApplyAndSave();
    partial void OnOpenOutputAfterJobChanged(bool value) => ApplyAndSave();
    partial void OnSelectedImageFormatChanged(FormatItemViewModel? value) => ApplyAndSave();
    partial void OnImageQualityChanged(double value) => ApplyAndSave();
    partial void OnSelectedAudioFormatChanged(FormatItemViewModel? value) => ApplyAndSave();
    partial void OnSelectedAudioBitrateChanged(Option<int> value) => ApplyAndSave();
    partial void OnSelectedVideoFormatChanged(FormatItemViewModel? value)
    {
        if (!_loading) LoadVideoCodecs();
        ApplyAndSave();
    }
    partial void OnSelectedVideoCodecChanged(CodecChoice? value) => ApplyAndSave();
    partial void OnSelectedVideoPresetChanged(Option<ConversionPreset> value) => ApplyAndSave();
    partial void OnSelectedCollisionChanged(CollisionOption? value) => ApplyAndSave();
    partial void OnConverterPreserveMetadataChanged(bool value) => ApplyAndSave();
    partial void OnSelectedUpscaleMethodChanged(Option<UpscaleMethod> value) => ApplyAndSave();
    partial void OnSelectedUpscaleAlgorithmChanged(Option<ResamplingAlgorithm>? value) => ApplyAndSave();
    partial void OnSelectedProviderKindChanged(Option<ExecutionProviderKind>? value) => ApplyAndSave();
    partial void OnSelectedUpscaleModelChanged(ModelDescriptor? value)
    {
        UpdateModelStatus();
        ApplyAndSave();
    }
    partial void OnSelectedUpscaleScaleChanged(Option<int>? value) => ApplyAndSave();
    partial void OnUpscaleTileSizeChanged(double value) => ApplyAndSave();
    partial void OnUpscaleOverlapChanged(double value) => ApplyAndSave();
    partial void OnSelectedUpscaleFormatChanged(UpscaleFormatChoice? value) => ApplyAndSave();
    partial void OnUpscaleSuffixChanged(string value) => ApplyAndSave();
    partial void OnUpscaleIncludeSubfoldersChanged(bool value) => ApplyAndSave();
    partial void OnUpscalePreserveStructureChanged(bool value) => ApplyAndSave();
    partial void OnModelDirectoryChanged(string value) => ApplyAndSave();
    partial void OnUpscaleOutputDirectoryChanged(string value) => ApplyAndSave();
    partial void OnVideoEnableSuperResolutionChanged(bool value) => ApplyAndSave();
    partial void OnVideoEnableInterpolationChanged(bool value)
    {
        UpdateVideoModelStatus();
        ApplyAndSave();
    }
    partial void OnSelectedVideoModelChanged(ModelDescriptor? value)
    {
        UpdateVideoModelStatus();
        ApplyAndSave();
    }
    partial void OnSelectedVideoInterpolationModelChanged(ModelDescriptor? value)
    {
        UpdateVideoModelStatus();
        ApplyAndSave();
    }
    partial void OnSelectedVideoFrameRateChanged(Option<int>? value) => ApplyAndSave();
    partial void OnSelectedVideoProviderKindChanged(Option<ExecutionProviderKind>? value) => ApplyAndSave();
    partial void OnVideoBitrateKbpsChanged(double value) => ApplyAndSave();
    partial void OnVideoSuffixChanged(string value) => ApplyAndSave();
    partial void OnVideoIncludeSubfoldersChanged(bool value) => ApplyAndSave();
    partial void OnSelectedVideoCollisionChanged(CollisionOption? value) => ApplyAndSave();
    partial void OnVideoOutputDirectoryChanged(string value) => ApplyAndSave();
    partial void OnMaxConcurrentJobsChanged(double value) => ApplyAndSave();
    partial void OnGpuConcurrencyChanged(double value) => ApplyAndSave();
    partial void OnTempDirectoryChanged(string value) => ApplyAndSave();
    partial void OnCleanupTempOnExitChanged(bool value) => ApplyAndSave();
    partial void OnKeepTempOnFailureChanged(bool value) => ApplyAndSave();
    partial void OnLogLevelChanged(string value) => ApplyAndSave();
    partial void OnHistoryRetentionDaysChanged(double value) => ApplyAndSave();
    partial void OnMaxHistoryEntriesChanged(double value) => ApplyAndSave();
    partial void OnSaveJobHistoryChanged(bool value) => ApplyAndSave();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _models.ModelsChanged -= OnModelsChanged;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
    }
}
