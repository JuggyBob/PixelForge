using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

/// <summary>
/// Unified audio/image/video tools workspace. It intentionally reuses the same
/// file list, format picker, collision policy, validation and queue services as
/// the Converter instead of introducing a parallel processing path.
/// </summary>
public sealed partial class MediaToolsViewModel : ObservableObject, IDisposable
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
    private readonly IWaveformService _waveform;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _validationCts;
    private CancellationTokenSource? _waveformCts;
    private CancellationTokenSource? _previewCts;
    private bool _initialized;
    private bool _loadingFormats;
    private bool _disposed;

    public MediaToolsViewModel(ICapabilityRegistry registry, IMediaInspector inspector, IFilePickerService filePicker,
        IJobService jobs, IThumbnailService thumbnails, IDialogService dialogs, INotificationService notifications,
        ISettingsService settings, IAppPaths paths, INavigationService navigation, IAppLogger logger,
        IWaveformService waveform)
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
        _waveform = waveform;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        OutputDirectory = string.IsNullOrWhiteSpace(settings.Settings.General.DefaultOutputDirectory)
            ? paths.DefaultOutputDirectory
            : settings.Settings.General.DefaultOutputDirectory;
        SelectedCollision = CollisionOptions.FirstOrDefault(c => c.Policy == settings.Settings.Converter.CollisionPolicy)
                            ?? CollisionOptions[0];
        SelectedNoiseMethod = NoiseMethods[0];
        SelectedChannels = ChannelOptions[0];
        SelectedSampleRate = SampleRates[0];
        SelectedAudioBitrate = AudioBitrates[4];
        SelectedMetadataMode = MetadataModes[0];
        SelectedCompressionPreset = CompressionPresets[1];
        SelectedExecutionProvider = ExecutionProviders[0];
        SelectedWatermarkKind = WatermarkKinds[0];
        SelectedSubtitleMode = SubtitleModes[0];
        Files.CollectionChanged += (_, _) => RaiseFilesChanged();
        MergeFiles.CollectionChanged += (_, _) => RaiseChanged();
        ToolOptions = MediaToolCatalog.For(MediaCategory.Audio);
        SelectedTool = ToolOptions[0];
        SelectCategory(MediaCategory.Audio);
    }

    public ObservableCollection<MediaFileViewModel> Files { get; } = [];
    public ObservableCollection<MediaFileViewModel> MergeFiles { get; } = [];
    public ObservableCollection<FormatItemViewModel> Formats { get; } = [];
    public ObservableCollection<ValidationIssueViewModel> Issues { get; } = [];
    public IReadOnlyList<CollisionOption> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number", "Never replace existing files."),
        new(CollisionPolicy.Skip, "Skip existing files", "Leave existing outputs untouched."),
        new(CollisionPolicy.Overwrite, "Replace existing files", "Overwrite only after confirmation.")
    ];

    [ObservableProperty] private IReadOnlyList<MediaToolDefinition> _toolOptions = [];

    public IReadOnlyList<Option<NoiseReductionMethod>> NoiseMethods { get; } =
    [
        new(NoiseReductionMethod.Spectral, "Spectral denoise (general)"),
        new(NoiseReductionMethod.Rnnoise, "RNNoise (speech)")
    ];

    public IReadOnlyList<Option<AudioMetadataMode>> MetadataModes { get; } =
    [
        new(AudioMetadataMode.Preserve, "Preserve metadata"),
        new(AudioMetadataMode.Edit, "Edit metadata"),
        new(AudioMetadataMode.RemoveAll, "Remove all metadata")
    ];

    public IReadOnlyList<Option<VideoCompressionPreset>> CompressionPresets { get; } =
    [
        new(VideoCompressionPreset.MaximumQuality, "Maximum quality"),
        new(VideoCompressionPreset.Balanced, "Balanced (recommended)"),
        new(VideoCompressionPreset.SmallestFile, "Smallest file"),
        new(VideoCompressionPreset.WebOptimized, "Web optimised")
    ];

    public IReadOnlyList<Option<SubtitleToolMode>> SubtitleModes { get; } =
    [
        new(SubtitleToolMode.Extract, "Extract / convert to text"),
        new(SubtitleToolMode.Embed, "Embed source subtitle tracks"),
        new(SubtitleToolMode.Drop, "Drop subtitles")
    ];

    public IReadOnlyList<Option<int>> SampleRates { get; } =
    [
        new(0, "Source sample rate"), new(44100, "44.1 kHz"), new(48000, "48 kHz"),
        new(96000, "96 kHz"), new(192000, "192 kHz")
    ];

    public IReadOnlyList<Option<int>> ChannelOptions { get; } =
    [
        new(0, "Source channels"), new(1, "Mono"), new(2, "Stereo")
    ];

    public IReadOnlyList<Option<int>> AudioBitrates { get; } =
    [
        new(64, "64 kbps"), new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"),
        new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")
    ];

    public IReadOnlyList<Option<ExecutionProviderKind>> ExecutionProviders { get; } =
    [
        new(ExecutionProviderKind.Auto, "Automatic (GPU if available)"),
        new(ExecutionProviderKind.Cpu, "CPU only"),
        new(ExecutionProviderKind.DirectML, "DirectML GPU")
    ];

    public IReadOnlyList<Option<WatermarkKind>> WatermarkKinds { get; } =
    [
        new(WatermarkKind.None, "No watermark"),
        new(WatermarkKind.Text, "Text watermark"),
        new(WatermarkKind.Image, "Image watermark")
    ];

    public IReadOnlyList<string> WatermarkPositions { get; } =
        ["BottomRight", "BottomLeft", "TopRight", "TopLeft", "BottomCenter", "TopCenter", "Center"];

    public IReadOnlyList<string> ThumbnailFormats { get; } = ["jpg", "png", "webp"];

    public IReadOnlyList<string> SubtitleFormats { get; } = ["srt", "vtt", "ass"];

    public IReadOnlyList<string> CropAspectRatios { get; } = ["", "16:9", "4:3", "3:2", "1:1", "9:16"];

    public IReadOnlyList<Option<string>> VideoCodecOptions { get; } =
    [
        new("h264", "H.264 / AVC (compatible)"),
        new("hevc", "H.265 / HEVC (smaller)"),
        new("av1", "AV1 (smaller, slower)"),
        new("vp9", "VP9 (WebM)"),
        new("mpeg4", "MPEG-4 Part 2 (legacy)"),
        new("prores", "Apple ProRes (editing)")
    ];

    public IReadOnlyList<Option<string>> VideoAudioCodecOptions { get; } =
    [
        new("aac", "AAC"), new("mp3", "MP3"), new("opus", "Opus"),
        new("flac", "FLAC (lossless)"), new("copy", "Copy source audio"),
        new("none", "No audio")
    ];

    [ObservableProperty] private MediaCategory _category = MediaCategory.Audio;
    [ObservableProperty] private MediaToolDefinition _selectedTool;
    [ObservableProperty] private FormatItemViewModel? _selectedFormat;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private CollisionOption? _selectedCollision;
    [ObservableProperty] private string _fileNameSuffix = "";
    [ObservableProperty] private bool _preserveMetadata = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private bool _canAddToQueue;
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _statusText = "Choose a tool and add files.";
    [ObservableProperty] private ImageSource? _waveformImage;
    [ObservableProperty] private bool _showWaveform;
    [ObservableProperty] private bool _isWaveformLoading;
    [ObservableProperty] private string _waveformStatus = "";
    [ObservableProperty] private double _waveformDuration;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private string _compressionEstimate = "";

    // Audio
    [ObservableProperty] private double _targetLufs = -16;
    [ObservableProperty] private double _truePeakDb = -1.5;
    [ObservableProperty] private double _loudnessRange = 11;
    [ObservableProperty] private Option<NoiseReductionMethod> _selectedNoiseMethod;
    [ObservableProperty] private double _noiseReductionDb = 12;
    [ObservableProperty] private double _silenceThresholdDb = -40;
    [ObservableProperty] private double _minSilenceSeconds = 0.7;
    [ObservableProperty] private double _keepSilenceMarginSeconds = 0.15;
    [ObservableProperty] private double _trimStartSeconds;
    [ObservableProperty] private double _trimEndSeconds;
    [ObservableProperty] private Option<int> _selectedChannels;
    [ObservableProperty] private Option<int> _selectedSampleRate;
    [ObservableProperty] private Option<int> _selectedAudioBitrate;
    [ObservableProperty] private Option<AudioMetadataMode> _selectedMetadataMode;
    [ObservableProperty] private string _metaTitle = "";
    [ObservableProperty] private string _metaArtist = "";
    [ObservableProperty] private string _metaAlbum = "";
    [ObservableProperty] private string _metaAlbumArtist = "";
    [ObservableProperty] private string _metaGenre = "";
    [ObservableProperty] private string _metaYear = "";
    [ObservableProperty] private string _metaComment = "";
    [ObservableProperty] private string _metaComposer = "";
    [ObservableProperty] private string _metaTrack = "";
    [ObservableProperty] private string _metaDisc = "";
    [ObservableProperty] private string _coverArtPath = "";
    [ObservableProperty] private bool _removeCoverArt;
    [ObservableProperty] private double _audioStreamIndex;
    [ObservableProperty] private bool _copyAudioStream;

    // Image
    [ObservableProperty] private double _quality = 86;
    [ObservableProperty] private bool _lossless;
    [ObservableProperty] private double _pngCompression = 6;
    [ObservableProperty] private double _avifSpeed = 6;
    [ObservableProperty] private double _gifColors = 256;
    [ObservableProperty] private bool _preserveAlpha = true;
    [ObservableProperty] private string _backgroundColor = "transparent";
    [ObservableProperty] private Option<ExecutionProviderKind> _selectedExecutionProvider;
    [ObservableProperty] private double _denoiseStrength;
    [ObservableProperty] private double _sharpenAmount;
    [ObservableProperty] private double _sharpenRadius = 1;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private double _contrast = 100;
    [ObservableProperty] private double _saturation = 100;
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _gamma = 1;
    [ObservableProperty] private double? _resizeWidth;
    [ObservableProperty] private double? _resizeHeight;
    [ObservableProperty] private bool _preserveAspectRatio = true;
    [ObservableProperty] private double? _cropX;
    [ObservableProperty] private double? _cropY;
    [ObservableProperty] private double? _cropWidth;
    [ObservableProperty] private double? _cropHeight;
    [ObservableProperty] private string _cropAspectRatio = "";
    [ObservableProperty] private Option<WatermarkKind> _selectedWatermarkKind;
    [ObservableProperty] private string _watermarkText = "PixelForge";
    [ObservableProperty] private string _watermarkImagePath = "";
    [ObservableProperty] private string _watermarkPosition = "BottomRight";
    [ObservableProperty] private double _watermarkOpacity = 0.6;
    [ObservableProperty] private double _watermarkScale = 0.18;
    [ObservableProperty] private double _watermarkMargin = 24;
    [ObservableProperty] private string _imageMetaDescription = "";
    [ObservableProperty] private string _imageMetaCopyright = "";
    [ObservableProperty] private string _imageMetaArtist = "";
    [ObservableProperty] private string _imageMetaComment = "";
    [ObservableProperty] private bool _removeAllImageMetadata;

    // Video
    [ObservableProperty] private string _videoCodecId = "h264";
    [ObservableProperty] private string _audioCodecId = "aac";
    [ObservableProperty] private Option<VideoCompressionPreset> _selectedCompressionPreset;
    [ObservableProperty] private double _targetSizeMb = 25;
    [ObservableProperty] private double _videoAudioBitrateKbps = 128;
    [ObservableProperty] private bool _frameAccurateTrim = true;
    [ObservableProperty] private double _stabilizationStrength = 0.5;
    [ObservableProperty] private double _thumbnailTimeSeconds = 1;
    [ObservableProperty] private string _thumbnailFormatId = "jpg";
    [ObservableProperty] private double _videoFrameRate;
    [ObservableProperty] private double _gifStartSeconds;
    [ObservableProperty] private double _gifEndSeconds = 3;
    [ObservableProperty] private double _gifFps = 12;
    [ObservableProperty] private double _gifWidth = 640;
    [ObservableProperty] private string _extractAudioFormatId = "mp3";
    [ObservableProperty] private string _replacementAudioPath = "";
    [ObservableProperty] private double _audioOffsetSeconds;
    [ObservableProperty] private Option<SubtitleToolMode> _selectedSubtitleMode;
    [ObservableProperty] private string _subtitleFormatId = "srt";
    [ObservableProperty] private double _subtitleStreamIndex;
    [ObservableProperty] private string _videoMetaTitle = "";
    [ObservableProperty] private string _videoMetaArtist = "";
    [ObservableProperty] private string _videoMetaComment = "";
    [ObservableProperty] private string _videoMetaYear = "";
    [ObservableProperty] private bool _removeAllVideoMetadata;
    [ObservableProperty] private double _toneMapPeakNits = 100;

    public bool HasFiles => Files.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public string FileCountText => Files.Count == 1 ? "1 file" : $"{Files.Count} files";
    public bool CanQueue => HasFiles && !HasValidationErrors && SelectedFormat is not null;
    public bool IsAudioCategory => Category == MediaCategory.Audio;
    public bool IsImageCategory => Category == MediaCategory.Image;
    public bool IsVideoCategory => Category == MediaCategory.Video;
    public string SelectedToolId => SelectedTool.Id;
    public bool IsAudioNormalize => IsAudioCategory && SelectedTool.Id == "NormalizeLoudness";
    public bool IsAudioNoise => IsAudioCategory && SelectedTool.Id == "ReduceNoise";
    public bool IsAudioSilence => IsAudioCategory && SelectedTool.Id == "RemoveSilence";
    public bool IsAudioTrim => IsAudioCategory && SelectedTool.Id == "Trim";
    public bool IsAudioMerge => IsAudioCategory && SelectedTool.Id == "Merge";
    public bool IsAudioChannels => IsAudioCategory && SelectedTool.Id == "ConvertChannelsAndRate";
    public bool IsAudioMetadata => IsAudioCategory && SelectedTool.Id == "EditMetadata";
    public bool IsAudioExtract => IsAudioCategory && SelectedTool.Id == "ExtractFromVideo";
    public bool IsImageOptimize => IsImageCategory && SelectedTool.Id == "Optimize";
    public bool IsImageBatchEdit => IsImageCategory && SelectedTool.Id == "BatchEdit";
    public bool IsBackgroundRemoval => IsImageCategory && SelectedTool.Id == "RemoveBackground";
    public bool IsImageMetadata => IsImageCategory && SelectedTool.Id == "ImageMetadata";
    public bool IsVideoCompress => IsVideoCategory && SelectedTool.Id == "Compress";
    public bool IsVideoTrim => IsVideoCategory && SelectedTool.Id == "VideoTrim";
    public bool IsVideoMerge => IsVideoCategory && SelectedTool.Id == "VideoMerge";
    public bool IsVideoStabilize => IsVideoCategory && SelectedTool.Id == "Stabilize";
    public bool IsThumbnail => IsVideoCategory && SelectedTool.Id == "Thumbnail";
    public bool IsGif => IsVideoCategory && SelectedTool.Id == "Gif";
    public bool IsVideoExtractAudio => IsVideoCategory && SelectedTool.Id == "ExtractAudio";
    public bool IsVideoReplaceAudio => IsVideoCategory && SelectedTool.Id == "ReplaceAudio";
    public bool IsSubtitles => IsVideoCategory && SelectedTool.Id == "Subtitles";
    public bool IsVideoMetadata => IsVideoCategory && SelectedTool.Id == "VideoMetadata";
    public bool IsHdr => IsVideoCategory && SelectedTool.Id == "HdrToSdr";
    public bool IsResizeFrameRate => IsVideoCategory && SelectedTool.Id == "ResizeFrameRate";
    public bool IsMergeTool => IsAudioMerge || IsVideoMerge;
    public bool ShowWaveformMarkers => IsAudioTrim;
    public bool ShowAudioSettings => IsAudioCategory;
    public bool ShowImageSettings => IsImageCategory;
    public bool ShowVideoSettings => IsVideoCategory;
    public string InputHint => Category switch
    {
        MediaCategory.Image => "Image tools accept still images such as PNG, JPEG, WebP, TIFF and BMP.",
        MediaCategory.Video => "Video tools require video files. Audio-only files are rejected.",
        _ => "Audio tools accept audio files and can also read the audio track from a video."
    };
    public bool ShowAudioBitrate => IsAudioCategory;
    public bool ShowAudioChannelsRate => IsAudioChannels || IsAudioNormalize || IsAudioNoise || IsAudioSilence || IsAudioTrim || IsAudioExtract;
    public bool ShowImageQuality => IsImageOptimize || IsImageBatchEdit;
    public bool ShowImageOptimizeSettings => IsImageOptimize;
    public bool ShowImageBatchEditSettings => IsImageBatchEdit;
    public bool ShowVideoContainerSettings => IsVideoCompress || IsVideoTrim || IsVideoStabilize || IsVideoMetadata || IsHdr || IsResizeFrameRate || IsVideoReplaceAudio || IsSubtitles;
    public bool ShowVideoTrimSettings => IsVideoTrim;
    public bool ShowVideoResizeSettings => IsResizeFrameRate;
    public bool ShowVideoCompressSettings => IsVideoCompress;
    public bool ShowVideoStabilizeSettings => IsVideoStabilize;
    public bool ShowThumbnailSettings => IsThumbnail;
    public bool ShowGifSettings => IsGif;
    public bool ShowVideoExtractSettings => IsVideoExtractAudio;
    public bool ShowVideoReplaceSettings => IsVideoReplaceAudio;
    public bool ShowSubtitleSettings => IsSubtitles;
    public bool ShowVideoMetadataSettings => IsVideoMetadata;
    public bool ShowHdrSettings => IsHdr;
    public bool ShowWatermarkSettings => IsImageBatchEdit;
    public bool ShowCropSettings => IsImageBatchEdit;
    public bool ShowImageResizeSettings => IsImageBatchEdit;
    public bool ShowImageColorSettings => IsImageBatchEdit;

    public async Task InitializeAsync(MediaCategory? preferred = null)
    {
        if (_initialized) return;
        _initialized = true;
        if (!_registry.IsInitialized) await _registry.InitializeAsync();
        if (preferred is { } category && category is MediaCategory.Audio or MediaCategory.Image or MediaCategory.Video)
            SelectCategory(category);
        LoadFormats();
        RaiseAllVisibility();
    }

    public void SelectCategory(MediaCategory category)
    {
        if (category is not (MediaCategory.Audio or MediaCategory.Image or MediaCategory.Video)) return;
        if (Category != category)
        {
            Files.Clear();
            MergeFiles.Clear();
        }

        Category = category;
        OnPropertyChanged(nameof(IsAudioCategory));
        OnPropertyChanged(nameof(IsImageCategory));
        OnPropertyChanged(nameof(IsVideoCategory));
        OnPropertyChanged(nameof(InputHint));
        ToolOptions = MediaToolCatalog.For(category);
        var tool = ToolOptions.FirstOrDefault(t => t.Id == SelectedTool?.Id) ?? ToolOptions[0];
        if (!ReferenceEquals(SelectedTool, tool)) SelectedTool = tool;
        LoadFormats();
        RaiseAllVisibility();
        RaiseChanged();
    }

    [RelayCommand]
    private void SelectAudioCategory() => SelectCategory(MediaCategory.Audio);

    [RelayCommand]
    private void SelectImageCategory() => SelectCategory(MediaCategory.Image);

    [RelayCommand]
    private void SelectVideoCategory() => SelectCategory(MediaCategory.Video);

    private void LoadFormats()
    {
        if (SelectedTool is null) return;
        _loadingFormats = true;
        Formats.Clear();
        IEnumerable<MediaFormat> formats;
        switch (SelectedTool.Id)
        {
            case "Thumbnail":
                formats = _registry.GetOutputFormats(MediaCategory.Image)
                    .Where(f => ThumbnailFormats.Contains(f.Format.Id))
                    .Select(f => f.Format);
                break;
            case "ExtractAudio":
                formats = _registry.GetOutputFormats(MediaCategory.Audio).Select(f => f.Format);
                break;
            case "Subtitles" when SelectedSubtitleMode?.Value is SubtitleToolMode.Extract or SubtitleToolMode.Convert:
                formats = SubtitleFormats.Select(SubtitleFormat);
                break;
            default:
                formats = _registry.GetOutputFormats(Category).Select(f => f.Format);
                break;
        }

        foreach (var format in formats)
            Formats.Add(new FormatItemViewModel(format));
        SelectedFormat = Formats.FirstOrDefault(f => f.Category == Category)
                        ?? Formats.FirstOrDefault();
        _loadingFormats = false;
        OnPropertyChanged(nameof(SelectedFormat));
        OnPropertyChanged(nameof(CanQueue));
        RaiseChanged();
    }

    private static MediaFormat SubtitleFormat(string id) => new()
    {
        Id = id,
        Extension = "." + id,
        DisplayName = id.ToUpperInvariant() + " subtitles",
        Category = MediaCategory.Document,
        EngineId = EngineIds.FFmpegTools,
        CanRead = true,
        CanWrite = true,
        SupportsMetadata = true,
        SortOrder = id switch { "srt" => 10, "vtt" => 20, _ => 30 }
    };

    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        IsLoading = true;
        try
        {
            var infos = await _inspector.InspectManyAsync(paths);
            foreach (var info in infos)
            {
                if (!info.IsReadable)
                {
                    _notifications.Notify("File skipped", $"{info.FileName}: {info.Error}", NotificationSeverity.Warning);
                    continue;
                }

                if (!Accepts(info))
                {
                    _notifications.Notify("File skipped",
                        $"{info.FileName} does not match the {SelectedTool.Label} tool.",
                        NotificationSeverity.Warning);
                    continue;
                }

                if (Files.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Files.Add(new MediaFileViewModel(info, _thumbnails, RemoveFile));
            }

            if (Files.Count > 0)
            {
                var first = Files[0].Info;
                if (TrimEndSeconds <= 0 && first.DurationSeconds is > 0) TrimEndSeconds = first.DurationSeconds.Value;
                if (GifEndSeconds <= 0 || GifEndSeconds > (first.DurationSeconds ?? 3)) GifEndSeconds = Math.Min(3, first.DurationSeconds ?? 3);
                if (ThumbnailTimeSeconds <= 0) ThumbnailTimeSeconds = 0.5;
                if ((SelectedAudioFormatId() is null) && SelectedFormat is null) LoadFormats();
            }

            RaiseFilesChanged();
            _ = RefreshWaveformAsync();
        _ = RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import files into media tools");
            await _dialogs.ShowErrorAsync("Files could not be imported", ex.Message);
        }
        finally
        {
            IsLoading = false;
            RaiseChanged();
        }
    }

    public async Task AddMergeFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        IsLoading = true;
        try
        {
            var infos = await _inspector.InspectManyAsync(paths);
            foreach (var info in infos)
            {
                if (!info.IsReadable || !Accepts(info)) continue;
                if (Files.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                    MergeFiles.Any(f => string.Equals(f.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                MergeFiles.Add(new MediaFileViewModel(info, _thumbnails, RemoveMergeFile));
            }

            RaiseChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool Accepts(MediaInfo info) => Category switch
    {
        MediaCategory.Audio => info.Category is MediaCategory.Audio or MediaCategory.Video,
        MediaCategory.Image => info.Category == MediaCategory.Image,
        MediaCategory.Video => info.Category == MediaCategory.Video,
        _ => false
    };

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var files = await _filePicker.PickFilesAsync(Category);
        if (files.Count > 0) await AddFilesAsync(files);
    }

    [RelayCommand]
    private async Task BrowseMergeAsync()
    {
        var files = await _filePicker.PickFilesAsync(Category);
        if (files.Count > 0) await AddMergeFilesAsync(files);
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _filePicker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) OutputDirectory = folder;
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (Directory.Exists(OutputDirectory))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true }); }
            catch { }
        }
    }

    [RelayCommand]
    private async Task BrowseCoverArtAsync()
    {
        var files = await _filePicker.PickFilesAsync(MediaCategory.Image);
        if (files.Count > 0) CoverArtPath = files[0];
    }

    [RelayCommand]
    private async Task BrowseWatermarkImageAsync()
    {
        var files = await _filePicker.PickFilesAsync(MediaCategory.Image);
        if (files.Count > 0) WatermarkImagePath = files[0];
    }

    [RelayCommand]
    private async Task BrowseReplacementAudioAsync()
    {
        var files = await _filePicker.PickFilesAsync(MediaCategory.Audio);
        if (files.Count > 0) ReplacementAudioPath = files[0];
    }

    [RelayCommand]
    private void ClearFiles()
    {
        Files.Clear();
        MergeFiles.Clear();
        RaiseFilesChanged();
        _ = RefreshWaveformAsync();
        _ = RefreshPreviewAsync();
    }

    private void RemoveFile(MediaFileViewModel file)
    {
        Files.Remove(file);
        RaiseFilesChanged();
        _ = RefreshWaveformAsync();
        _ = RefreshPreviewAsync();
    }

    private void RemoveMergeFile(MediaFileViewModel file)
    {
        MergeFiles.Remove(file);
        RaiseChanged();
    }

    [RelayCommand]
    private void MoveMergeFileUp(MediaFileViewModel? file)
    {
        if (file is null) return;
        var index = MergeFiles.IndexOf(file);
        if (index > 0) MergeFiles.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveMergeFileDown(MediaFileViewModel? file)
    {
        if (file is null) return;
        var index = MergeFiles.IndexOf(file);
        if (index >= 0 && index < MergeFiles.Count - 1) MergeFiles.Move(index, index + 1);
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (!HasFiles)
        {
            await _dialogs.ShowMessageAsync("No files to process", "Add at least one file to this tool.");
            return;
        }

        var request = BuildJobRequest();
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
                string.Join("\n\n", validation.Warnings.Select(w => w.Message)), "Process anyway", "Go back");
            if (!proceed) return;
        }

        if (SelectedCollision?.Policy == CollisionPolicy.Overwrite)
        {
            var proceed = await _dialogs.ConfirmAsync("Replace existing files?",
                "The output folder may contain files with the same names. Replace mode only affects outputs after this confirmation. Source files are never touched.",
                "Use Replace mode", "Cancel", destructive: true);
            if (!proceed) return;
        }

        try
        {
            IsLoading = true;
            var job = await _jobs.EnqueueAsync(request);
            _notifications.Notify("Added to processing queue", job.Title, NotificationSeverity.Success);
            Files.Clear();
            MergeFiles.Clear();
            RaiseFilesChanged();
            _navigation.Navigate("Queue");
        }
        catch (JobValidationException ex)
        {
            SetValidation(ex.Validation);
            await _dialogs.ShowJobValidationAsync(ex.Validation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enqueue media tool job");
            await _dialogs.ShowErrorAsync("Could not add the job", ex.Message, ex.ToString());
        }
        finally
        {
            IsLoading = false;
            RaiseChanged();
        }
    }

    public JobRequest BuildJobRequest()
    {
        if (!MediaToolCatalog.IsCompatible(Category, SelectedTool?.Id))
        {
            throw new InvalidOperationException(
                $"The selected media tool is not compatible with the {Category} category.");
        }

        var settings = BuildSettings();
        var inputs = IsMergeTool
            ? Files.Take(1).Select(f => new JobInput { Path = f.FilePath, Info = f.Info }).ToList()
            : Files.Select(f => new JobInput { Path = f.FilePath, Info = f.Info }).ToList();
        return new JobRequest
        {
            Operation = Category switch
            {
                MediaCategory.Audio => OperationKind.AudioProcess,
                MediaCategory.Image => OperationKind.ImageProcess,
                _ => OperationKind.VideoProcess
            },
            Title = BuildTitle(),
            Settings = settings,
            Inputs = inputs,
            PreserveRelativePaths = false
        };
    }

    private OperationSettings BuildSettings()
    {
        if (Category == MediaCategory.Audio)
        {
            var tool = Enum.Parse<AudioToolOperation>(SelectedTool.Id);
            var audio = new AudioToolSettings
            {
                Tool = tool,
                OutputFormatId = SelectedFormat?.Id ?? "wav",
                BitrateKbps = (int)(SelectedAudioBitrate?.Value ?? 192),
                SampleRate = SelectedSampleRate?.Value ?? 0,
                Channels = SelectedChannels?.Value ?? 0,
                TargetLufs = TargetLufs,
                TruePeakDb = TruePeakDb,
                LoudnessRange = LoudnessRange,
                NoiseMethod = SelectedNoiseMethod?.Value ?? NoiseReductionMethod.Spectral,
                NoiseReductionDb = NoiseReductionDb,
                SilenceThresholdDb = SilenceThresholdDb,
                MinSilenceSeconds = MinSilenceSeconds,
                KeepSilenceMarginSeconds = KeepSilenceMarginSeconds,
                TrimStartSeconds = TrimStartSeconds,
                TrimEndSeconds = TrimEndSeconds,
                MetadataMode = SelectedMetadataMode?.Value ?? AudioMetadataMode.Preserve,
                MetaTitle = NullIfEmpty(MetaTitle),
                MetaArtist = NullIfEmpty(MetaArtist),
                MetaAlbum = NullIfEmpty(MetaAlbum),
                MetaAlbumArtist = NullIfEmpty(MetaAlbumArtist),
                MetaGenre = NullIfEmpty(MetaGenre),
                MetaYear = NullIfEmpty(MetaYear),
                MetaComment = NullIfEmpty(MetaComment),
                MetaComposer = NullIfEmpty(MetaComposer),
                MetaTrack = NullIfEmpty(MetaTrack),
                MetaDisc = NullIfEmpty(MetaDisc),
                CoverArtPath = NullIfEmpty(CoverArtPath),
                RemoveCoverArt = RemoveCoverArt,
                AudioStreamIndex = (int)AudioStreamIndex,
                CopyAudioStream = CopyAudioStream,
                MergeInputPaths = IsAudioMerge ? MergeFiles.Select(f => f.FilePath).ToList() : []
            };
            Common(audio);
            return audio;
        }

        if (Category == MediaCategory.Image)
        {
            var tool = SelectedTool.Id switch
            {
                "Optimize" => ImageToolOperation.Optimize,
                "RemoveBackground" => ImageToolOperation.RemoveBackground,
                "ImageMetadata" => ImageToolOperation.EditMetadata,
                _ => ImageToolOperation.BatchEdit
            };
            var image = new ImageToolSettings
            {
                Tool = tool,
                OutputFormatId = SelectedFormat?.Id ?? "webp",
                Quality = (int)Math.Clamp(Quality, 1, 100),
                Lossless = Lossless,
                PngCompression = (int)Math.Clamp(PngCompression, 0, 9),
                AvifSpeed = (int)Math.Clamp(AvifSpeed, 0, 10),
                GifColors = (int)Math.Clamp(GifColors, 2, 256),
                PreserveAlpha = PreserveAlpha,
                PreserveIccProfile = true,
                PreserveMetadata = PreserveMetadata,
                BackgroundColor = BackgroundColor,
                ExecutionProvider = SelectedExecutionProvider?.Value ?? ExecutionProviderKind.Auto,
                DenoiseStrength = DenoiseStrength,
                SharpenAmount = SharpenAmount,
                SharpenRadius = SharpenRadius,
                Brightness = Brightness,
                Contrast = Contrast,
                Saturation = Saturation,
                Exposure = Exposure,
                Gamma = Gamma,
                ResizeWidth = ResizeWidth is > 0 ? (int)Math.Round(ResizeWidth.Value) : null,
                ResizeHeight = ResizeHeight is > 0 ? (int)Math.Round(ResizeHeight.Value) : null,
                PreserveAspectRatio = PreserveAspectRatio,
                CropX = CropX is >= 0 ? (int)Math.Round(CropX.Value) : null,
                CropY = CropY is >= 0 ? (int)Math.Round(CropY.Value) : null,
                CropWidth = CropWidth is > 0 ? (int)Math.Round(CropWidth.Value) : null,
                CropHeight = CropHeight is > 0 ? (int)Math.Round(CropHeight.Value) : null,
                CropAspectRatio = NullIfEmpty(CropAspectRatio),
                WatermarkKind = SelectedWatermarkKind?.Value ?? WatermarkKind.None,
                WatermarkText = NullIfEmpty(WatermarkText),
                WatermarkImagePath = NullIfEmpty(WatermarkImagePath),
                WatermarkPosition = WatermarkPosition,
                WatermarkOpacity = WatermarkOpacity,
                WatermarkScale = WatermarkScale,
                WatermarkMargin = (int)Math.Round(WatermarkMargin),
                MetaDescription = NullIfEmpty(ImageMetaDescription),
                MetaCopyright = NullIfEmpty(ImageMetaCopyright),
                MetaArtist = NullIfEmpty(ImageMetaArtist),
                MetaComment = NullIfEmpty(ImageMetaComment),
                RemoveAllMetadata = RemoveAllImageMetadata
            };
            Common(image);
            return image;
        }

        var videoTool = SelectedTool.Id switch
        {
            "Compress" => VideoToolOperation.Compress,
            "VideoTrim" => VideoToolOperation.Trim,
            "VideoMerge" => VideoToolOperation.Merge,
            "Stabilize" => VideoToolOperation.Stabilize,
            "Thumbnail" => VideoToolOperation.Thumbnail,
            "Gif" => VideoToolOperation.Gif,
            "ExtractAudio" => VideoToolOperation.ExtractAudio,
            "ReplaceAudio" => VideoToolOperation.ReplaceAudio,
            "Subtitles" => VideoToolOperation.Subtitles,
            "VideoMetadata" => VideoToolOperation.EditMetadata,
            "HdrToSdr" => VideoToolOperation.HdrToSdr,
            _ => VideoToolOperation.ResizeFrameRate
        };
        var video = new VideoToolSettings
        {
            Tool = videoTool,
            ContainerFormatId = videoTool is VideoToolOperation.Subtitles &&
                                SelectedSubtitleMode?.Value is SubtitleToolMode.Extract or SubtitleToolMode.Convert
                ? "mp4"
                : (SelectedFormat?.Id ?? "mp4"),
            VideoCodecId = videoTool is VideoToolOperation.Thumbnail or VideoToolOperation.ExtractAudio
                ? "h264"
                : videoTool == VideoToolOperation.EditMetadata ? "copy" : VideoCodecId,
            AudioCodecId = videoTool is VideoToolOperation.Thumbnail or VideoToolOperation.ExtractAudio ? "aac" : AudioCodecId,
            CompressionPreset = SelectedCompressionPreset?.Value ?? VideoCompressionPreset.Balanced,
            TargetSizeMb = TargetSizeMb,
            AudioBitrateKbps = (int)VideoAudioBitrateKbps,
            TrimStartSeconds = TrimStartSeconds,
            TrimEndSeconds = TrimEndSeconds,
            FrameAccurateTrim = FrameAccurateTrim,
            MergeInputPaths = IsVideoMerge ? MergeFiles.Select(f => f.FilePath).ToList() : [],
            ResizeWidth = ResizeWidth is > 0 ? (int)Math.Round(ResizeWidth.Value) : null,
            ResizeHeight = ResizeHeight is > 0 ? (int)Math.Round(ResizeHeight.Value) : null,
            PreserveAspectRatio = PreserveAspectRatio,
            FrameRate = IsResizeFrameRate ? VideoFrameRate : 0,
            StabilizationStrength = StabilizationStrength,
            ThumbnailTimeSeconds = ThumbnailTimeSeconds,
            ThumbnailFormatId = SelectedFormat?.Id ?? ThumbnailFormatId,
            GifStartSeconds = GifStartSeconds,
            GifEndSeconds = GifEndSeconds,
            GifFps = (int)GifFps,
            GifWidth = (int)GifWidth,
            ExtractAudioFormatId = SelectedFormat?.Id ?? ExtractAudioFormatId,
            AudioStreamIndex = (int)AudioStreamIndex,
            CopyAudioStream = CopyAudioStream,
            ReplacementAudioPath = NullIfEmpty(ReplacementAudioPath),
            AudioOffsetSeconds = AudioOffsetSeconds,
            SubtitleMode = SelectedSubtitleMode?.Value ?? SubtitleToolMode.Extract,
            SubtitleStreamIndex = (int)SubtitleStreamIndex,
            SubtitleFormatId = SelectedTool.Id == "Subtitles" && SelectedSubtitleMode?.Value is SubtitleToolMode.Extract or SubtitleToolMode.Convert
                ? (SelectedFormat?.Id ?? SubtitleFormatId)
                : SubtitleFormatId,
            MetaTitle = NullIfEmpty(VideoMetaTitle),
            MetaArtist = NullIfEmpty(VideoMetaArtist),
            MetaComment = NullIfEmpty(VideoMetaComment),
            MetaYear = NullIfEmpty(VideoMetaYear),
            RemoveAllMetadata = RemoveAllVideoMetadata,
            ToneMapPeakNits = ToneMapPeakNits
        };
        Common(video);
        return video;
    }

    private void Common(OperationSettings settings)
    {
        settings.OutputDirectory = OutputDirectory;
        settings.CollisionPolicy = SelectedCollision?.Policy ?? CollisionPolicy.UniqueName;
        settings.FileNameSuffix = FileNameSuffix;
        settings.PreserveMetadata = PreserveMetadata;
    }

    private string? SelectedAudioFormatId() => Category == MediaCategory.Audio ? SelectedFormat?.Id : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string BuildTitle() => Files.Count == 0
        ? SelectedTool.Label
        : $"{SelectedTool.Label}: {Files[0].FileName}" + (Files.Count > 1 && !IsMergeTool ? $" + {Files.Count - 1}" : "");

    private void RaiseFilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountText));
        UpdateSummary();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_loadingFormats) return;
        UpdateSummary();
        OnPropertyChanged(nameof(CanQueue));
        ScheduleValidation();
    }

    private void RaiseAllVisibility()
    {
        foreach (var property in new[]
                 {
                     nameof(IsAudioCategory), nameof(IsImageCategory), nameof(IsVideoCategory),
                     nameof(IsAudioNormalize), nameof(IsAudioNoise), nameof(IsAudioSilence), nameof(IsAudioTrim),
                     nameof(IsAudioMerge), nameof(IsAudioChannels), nameof(IsAudioMetadata), nameof(IsAudioExtract),
                     nameof(IsImageOptimize), nameof(IsImageBatchEdit), nameof(IsBackgroundRemoval), nameof(IsImageMetadata),
                     nameof(IsVideoCompress), nameof(IsVideoTrim), nameof(IsVideoMerge), nameof(IsVideoStabilize),
                     nameof(IsThumbnail), nameof(IsGif), nameof(IsVideoExtractAudio), nameof(IsVideoReplaceAudio),
                     nameof(IsSubtitles), nameof(IsVideoMetadata), nameof(IsHdr), nameof(IsResizeFrameRate),
                     nameof(IsMergeTool), nameof(ShowAudioSettings), nameof(ShowImageSettings), nameof(ShowVideoSettings),
                     nameof(ShowAudioBitrate), nameof(ShowAudioChannelsRate), nameof(ShowImageQuality),
                     nameof(ShowImageOptimizeSettings), nameof(ShowImageBatchEditSettings), nameof(ShowVideoContainerSettings),
                     nameof(ShowVideoTrimSettings), nameof(ShowVideoResizeSettings), nameof(ShowVideoCompressSettings),
                     nameof(ShowVideoStabilizeSettings), nameof(ShowThumbnailSettings), nameof(ShowGifSettings),
                     nameof(ShowVideoExtractSettings), nameof(ShowVideoReplaceSettings), nameof(ShowSubtitleSettings),
                     nameof(ShowVideoMetadataSettings), nameof(ShowHdrSettings), nameof(ShowWatermarkSettings),
                     nameof(ShowCropSettings), nameof(ShowImageResizeSettings), nameof(ShowImageColorSettings)
                 })
            OnPropertyChanged(property);
    }

    private void UpdateSummary()
    {
        if (SelectedTool is null)
        {
            Summary = "Choose a tool and add files.";
            StatusText = "Choose a tool and add files.";
            CompressionEstimate = "";
            return;
        }

        Summary = Files.Count == 0
            ? $"{SelectedTool.Label} · add files to begin"
            : $"{FileCountText} → {SelectedFormat?.ExtensionUpper ?? "auto"} · {SelectedTool.Label}";
        StatusText = Files.Count == 0
            ? SelectedTool.Description
            : $"{SelectedTool.Description} Output: {OutputDirectory}";
        if (Files.Count == 0)
        {
            CompressionEstimate = "";
        }
        else if (IsVideoCompress && Files[0].Info.DurationSeconds is > 0)
        {
            var duration = Files[0].Info.DurationSeconds!.Value;
            var bits = Math.Max(1, TargetSizeMb) * 1024 * 1024 * 8 * 0.98;
            var videoKbps = Math.Max(100, (int)Math.Round(bits / duration / 1000 - VideoAudioBitrateKbps - 24));
            CompressionEstimate = $"Estimated video bitrate ≈ {videoKbps:N0} kbps for {duration:0.0}s · preset {SelectedCompressionPreset?.Label ?? "Balanced"}";
        }
        else if (IsImageOptimize)
        {
            CompressionEstimate = $"Source size: {Files[0].SizeText}. Output size appears in History after processing.";
        }
        else
        {
            CompressionEstimate = "";
        }
    }

    private async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        if (Category is not (MediaCategory.Image or MediaCategory.Video) || Files.Count == 0)
        {
            ShowPreview = false;
            PreviewImage = null;
            PreviewStatus = "";
            return;
        }

        var file = Files[0];
        try
        {
            var path = await _thumbnails.GetThumbnailPathAsync(file.FilePath, file.Info.Category, 640);
            if (token.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowPreview = false;
                PreviewStatus = "Preview unavailable for this file.";
                return;
            }

            void Apply()
            {
                PreviewImage = new BitmapImage { UriSource = new Uri(path) };
                ShowPreview = true;
                PreviewStatus = Category == MediaCategory.Image
                    ? "Source image preview. Before/after comparison is available from History after processing."
                    : "Representative video frame preview. Use the thumbnail tool for a full-size frame.";
            }

            if (_dispatcher.HasThreadAccess) Apply();
            else _dispatcher.TryEnqueue(Apply);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Preview generation failed: {Message}", ex.Message);
            ShowPreview = false;
        }
    }

    private async Task RefreshWaveformAsync()
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = new CancellationTokenSource();
        var token = _waveformCts.Token;
        if (!IsAudioCategory || Files.Count == 0)
        {
            ShowWaveform = false;
            WaveformImage = null;
            IsWaveformLoading = false;
            WaveformStatus = "";
            return;
        }

        var file = Files[0];
        WaveformDuration = file.Info.DurationSeconds ?? 0;
        IsWaveformLoading = true;
        WaveformStatus = "Generating waveform from the source signal…";
        try
        {
            var path = await _waveform.GenerateAsync(file.FilePath, token);
            if (token.IsCancellationRequested) return;
            if (path is null || !File.Exists(path))
            {
                ShowWaveform = false;
                WaveformImage = null;
                WaveformStatus = "No audio waveform could be generated for this file.";
                return;
            }

            void Apply()
            {
                WaveformImage = new BitmapImage { UriSource = new Uri(path) };
                ShowWaveform = true;
                WaveformStatus = $"Real waveform · {FormatDuration(WaveformDuration)}";
            }

            if (_dispatcher.HasThreadAccess) Apply();
            else _dispatcher.TryEnqueue(Apply);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Waveform preview failed: {Message}", ex.Message);
            ShowWaveform = false;
            WaveformStatus = "Waveform preview unavailable.";
        }
        finally
        {
            if (!token.IsCancellationRequested) IsWaveformLoading = false;
        }
    }

    private static string FormatDuration(double seconds) => seconds > 0
        ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "duration unavailable";

    private void ScheduleValidation()
    {
        if (!_initialized) return;        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        _ = ValidateAfterDelayAsync(_validationCts.Token);
    }

    private async Task ValidateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !HasFiles) return;
            IsValidating = true;
            var validation = await _jobs.ValidateAsync(BuildJobRequest(), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            SetValidation(validation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Media tools validation failed: {Message}", ex.Message);
        }
        finally
        {
            IsValidating = false;
        }
    }

    private void SetValidation(ValidationResult? validation)
    {
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
            ValidationSummary = validation.Summary;
        }

        OnPropertyChanged(nameof(CanQueue));
        OnPropertyChanged(nameof(HasIssues));
        UpdateCanQueue();
    }

    private void UpdateCanQueue()
    {
        CanAddToQueue = HasFiles && !HasValidationErrors && SelectedFormat is not null && !IsLoading;
    }

    partial void OnSelectedToolChanged(MediaToolDefinition value)
    {
        if (value is null) return;
        if (value.Category != Category)
        {
            var fallback = ToolOptions.FirstOrDefault();
            if (fallback is not null && !ReferenceEquals(SelectedTool, fallback)) SelectedTool = fallback;
            return;
        }

        LoadFormats();
        RaiseAllVisibility();
        OnPropertyChanged(nameof(ShowWaveformMarkers));
        _ = RefreshWaveformAsync();
        _ = RefreshPreviewAsync();
        RaiseChanged();
    }

    partial void OnSelectedFormatChanged(FormatItemViewModel? value)
    {
        if (_loadingFormats) return;
        RaiseChanged();
    }

    partial void OnSelectedSubtitleModeChanged(Option<SubtitleToolMode> value)
    {
        LoadFormats();
        RaiseAllVisibility();
        RaiseChanged();
    }

    partial void OnOutputDirectoryChanged(string value) => RaiseChanged();
    partial void OnSelectedCollisionChanged(CollisionOption? value) => RaiseChanged();
    partial void OnFileNameSuffixChanged(string value) => RaiseChanged();
    partial void OnPreserveMetadataChanged(bool value) => RaiseChanged();
    partial void OnQualityChanged(double value) => RaiseChanged();
    partial void OnTargetLufsChanged(double value) => RaiseChanged();
    partial void OnTrimStartSecondsChanged(double value) => RaiseChanged();
    partial void OnTrimEndSecondsChanged(double value) => RaiseChanged();
    partial void OnTargetSizeMbChanged(double value) => RaiseChanged();
    partial void OnGifStartSecondsChanged(double value) => RaiseChanged();
    partial void OnGifEndSecondsChanged(double value) => RaiseChanged();
    partial void OnGifFpsChanged(double value) => RaiseChanged();
    partial void OnGifWidthChanged(double value) => RaiseChanged();
    partial void OnThumbnailTimeSecondsChanged(double value) => RaiseChanged();
    partial void OnSelectedMetadataModeChanged(Option<AudioMetadataMode> value) => RaiseChanged();
    partial void OnSelectedCompressionPresetChanged(Option<VideoCompressionPreset> value) => RaiseChanged();
    partial void OnAudioStreamIndexChanged(double value) => RaiseChanged();
    partial void OnSubtitleStreamIndexChanged(double value) => RaiseChanged();
    partial void OnSubtitleFormatIdChanged(string value) => RaiseChanged();
    partial void OnVideoCodecIdChanged(string value) => RaiseChanged();
    partial void OnAudioCodecIdChanged(string value) => RaiseChanged();
    partial void OnVideoAudioBitrateKbpsChanged(double value) => RaiseChanged();
    partial void OnSelectedExecutionProviderChanged(Option<ExecutionProviderKind> value) => RaiseChanged();
    partial void OnSelectedWatermarkKindChanged(Option<WatermarkKind> value) => RaiseChanged();
    partial void OnRemoveAllImageMetadataChanged(bool value) => RaiseChanged();
    partial void OnRemoveAllVideoMetadataChanged(bool value) => RaiseChanged();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
    }
}
