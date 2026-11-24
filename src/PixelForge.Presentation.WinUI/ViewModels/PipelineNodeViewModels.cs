using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Application.Pipelines;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public enum PipelineNodeStatus
{
    NotConfigured = 0,
    Ready = 1,
    Invalid = 2,
    Running = 3,
    Completed = 4,
    Failed = 5
}

public abstract partial class PipelineNodeViewModel : ObservableObject
{
    protected PipelineNodeViewModel(PipelineNodeDefinition definition)
    {
        Id = definition.Id;
        Kind = definition.Kind;
        _x = definition.X;
        _y = definition.Y;
        _isExpanded = definition.IsExpanded;
    }

    public string Id { get; }
    public PipelineNodeKind Kind { get; }
    public abstract string ActionId { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private PipelineNodeStatus _status;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _iconGlyph = "";

    public event EventHandler? ConfigurationChanged;

    public bool IsBoundary => Kind is PipelineNodeKind.Input or PipelineNodeKind.Output;

    public abstract PipelineNodeDefinition ToDefinition();

    public virtual void RefreshLocalization()
    {
    }

    protected void RaiseConfigurationChanged()
    {
        RefreshSummary();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void RefreshSummary()
    {
    }

    partial void OnIsExpandedChanged(bool value)
    {
        // The canvas listens to this through PropertyChanged and repositions
        // connection anchors after the control has finished measuring.
    }

    public string StatusTextKey => Status switch
    {
        PipelineNodeStatus.NotConfigured => "Not configured",
        PipelineNodeStatus.Ready => "Ready",
        PipelineNodeStatus.Invalid => "Needs attention",
        PipelineNodeStatus.Running => "Running",
        PipelineNodeStatus.Completed => "Completed",
        PipelineNodeStatus.Failed => "Failed",
        _ => "Ready"
    };
}

public sealed partial class PipelineInputNodeViewModel : PipelineNodeViewModel
{
    private readonly IMediaInspector _inspector;
    private readonly IThumbnailService _thumbnails;

    public PipelineInputNodeViewModel(PipelineNodeDefinition definition, IMediaInspector inspector,
        IThumbnailService thumbnails) : base(definition)
    {
        _inspector = inspector;
        _thumbnails = thumbnails;
        IconGlyph = "\uE8B7";
        FolderPath = definition.Input.FolderPath;
        Recursive = definition.Input.Recursive;
        foreach (var path in definition.Input.Paths)
            _ = AddPathAsync(path, notify: false);
        RefreshLocalization();
    }

    public override string ActionId => "input";
    public ObservableCollection<PipelineInputFileViewModel> Files { get; } = [];

    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _preserveFolderStructure = true;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private string _fileCountText = "";
    [ObservableProperty] private string _previewDetail = "";

    public bool HasFiles => Files.Count > 0;
    public bool IsFolderMode => !string.IsNullOrWhiteSpace(FolderPath);

    public async Task AddPathsAsync(IEnumerable<string> paths, bool notify = true)
    {
        foreach (var path in paths)
            await AddPathAsync(path, notify);
        if (notify) RaiseConfigurationChanged();
        await RefreshPreviewAsync();
    }

    private async Task AddPathAsync(string path, bool notify)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (Files.Any(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase))) return;
        var info = await _inspector.InspectAsync(fullPath);
        var item = new PipelineInputFileViewModel(info);
        Files.Add(item);
        RefreshCounts();
        FileCountText = Files.Count == 0
            ? Loc.T("No files selected")
            : Loc.P("Count.File", Files.Count);
        if (notify) RaiseConfigurationChanged();
        await item.LoadThumbnailAsync(_thumbnails);
        await RefreshPreviewAsync();
    }

    public void SetFolder(string? folderPath, bool recursive)
    {
        FolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath;
        Recursive = recursive;
        RaiseConfigurationChanged();
    }

    public void ClearFiles()
    {
        Files.Clear();
        Preview = null;
        PreviewDetail = "";
        RefreshCounts();
        RaiseConfigurationChanged();
    }

    public void RemoveFile(PipelineInputFileViewModel file)
    {
        if (Files.Remove(file))
        {
            RefreshCounts();
            _ = RefreshPreviewAsync();
            RaiseConfigurationChanged();
        }
    }

    partial void OnRecursiveChanged(bool value) => RaiseConfigurationChanged();

    partial void OnPreserveFolderStructureChanged(bool value) => RaiseConfigurationChanged();

    private void RefreshCounts()
    {
        FileCountText = Files.Count == 0
            ? Loc.T("No files selected")
            : Loc.P("Count.File", Files.Count);
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(IsFolderMode));
    }

    private async Task RefreshPreviewAsync()
    {
        var first = Files.FirstOrDefault();
        if (first is null)
        {
            Preview = null;
            PreviewDetail = "";
            return;
        }

        Preview = first.Thumbnail;
        PreviewDetail = first.Detail;
        if (Preview is null)
            await first.LoadThumbnailAsync(_thumbnails);
        Preview = first.Thumbnail;
        PreviewDetail = first.Detail;
    }

    public override PipelineNodeDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = PipelineNodeKind.Input,
        ActionId = "",
        X = X,
        Y = Y,
        IsExpanded = IsExpanded,
        Input = new PipelineInputConfiguration
        {
            Paths = Files.Select(f => f.Path).ToList(),
            FolderPath = FolderPath,
            Recursive = Recursive,
            PreserveFolderStructure = PreserveFolderStructure
        }
    };

    public override void RefreshLocalization()
    {
        Title = Loc.T("Input");
        RefreshCounts();
        RefreshSummary();
    }

    protected override void RefreshSummary()
    {
        Summary = IsFolderMode
            ? Loc.F("{0} · folder", FileCountText)
            : FileCountText;
    }
}

public sealed partial class PipelineInputFileViewModel : ObservableObject
{
    public PipelineInputFileViewModel(MediaInfo info)
    {
        Info = info;
        Path = info.FilePath;
        FileName = info.FileName;
        Detail = BuildDetail(info);
        Category = info.Category;
        TypeGlyph = info.Category switch
        {
            MediaCategory.Image => "\uEB9F",
            MediaCategory.Audio => "\uE8D6",
            MediaCategory.Video => "\uE714",
            MediaCategory.Document => "\uE8A5",
            MediaCategory.Ebook => "\uE736",
            MediaCategory.Archive => "\uE8B7",
            _ => "\uE8A5"
        };
    }

    public MediaInfo Info { get; }
    public string Path { get; }
    public string FileName { get; }
    public string Detail { get; }
    public MediaCategory Category { get; }
    public string TypeGlyph { get; }
    [ObservableProperty] private ImageSource? _thumbnail;

    public async Task LoadThumbnailAsync(IThumbnailService thumbnails)
    {
        if (Thumbnail is not null) return;
        try
        {
            var path = await thumbnails.GetThumbnailPathAsync(Path, Category, 160);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            Thumbnail = new BitmapImage { UriSource = new Uri(path) };
        }
        catch
        {
            // A preview is nice-to-have; validation still uses MediaInfo.
        }
    }

    private static string BuildDetail(MediaInfo info)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.DisplayDimensions) && info.DisplayDimensions != "-")
            parts.Add(info.DisplayDimensions);
        if (info.FileSizeBytes > 0)
            parts.Add(LocalizationService.FormatBytes(info.FileSizeBytes));
        if (info.DurationSeconds is > 0)
            parts.Add(TimeSpan.FromSeconds(info.DurationSeconds.Value).ToString(@"m\:ss"));
        if (!string.IsNullOrWhiteSpace(info.FormatId))
            parts.Add(info.FormatId!.ToUpperInvariant());
        return string.Join(" · ", parts);
    }
}

public sealed partial class PipelineOutputNodeViewModel : PipelineNodeViewModel
{
    private readonly IFilePickerService _picker;

    public PipelineOutputNodeViewModel(PipelineNodeDefinition definition, IFilePickerService picker,
        string defaultDirectory) : base(definition)
    {
        _picker = picker;
        IconGlyph = "\uE8B7";
        Directory = string.IsNullOrWhiteSpace(definition.Output.Directory)
            ? defaultDirectory
            : definition.Output.Directory;
        FileNameSuffix = definition.Output.FileNameSuffix;
        KeepIntermediateFiles = definition.Output.KeepIntermediateFiles;
        PreserveFolderStructure = definition.Output.PreserveFolderStructure;
        SelectedCollision = CollisionOptions.FirstOrDefault(o => o.Value == definition.Output.CollisionPolicy)
                            ?? CollisionOptions[0];
        RefreshLocalization();
    }

    public override string ActionId => "output";

    public ObservableCollection<Option<CollisionPolicy>> CollisionOptions { get; } =
    [
        new(CollisionPolicy.UniqueName, "Add unique number"),
        new(CollisionPolicy.Skip, "Skip existing files"),
        new(CollisionPolicy.Overwrite, "Replace existing files")
    ];

    [ObservableProperty] private string _directory = "";
    [ObservableProperty] private string _fileNameSuffix = "";
    [ObservableProperty] private Option<CollisionPolicy>? _selectedCollision;
    [ObservableProperty] private bool _keepIntermediateFiles;
    [ObservableProperty] private bool _preserveFolderStructure = true;
    [ObservableProperty] private bool _isFolderOutput;
    [ObservableProperty] private string _outputSummary = "";

    [RelayCommand]
    private async Task ChooseDirectoryAsync()
    {
        var folder = await _picker.PickFolderAsync(Directory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory = folder;
            RaiseConfigurationChanged();
        }
    }

    public override PipelineNodeDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = PipelineNodeKind.Output,
        ActionId = "",
        X = X,
        Y = Y,
        IsExpanded = IsExpanded,
        Output = new PipelineOutputConfiguration
        {
            Directory = Directory,
            FileNameSuffix = FileNameSuffix,
            CollisionPolicy = SelectedCollision?.Value ?? CollisionPolicy.UniqueName,
            KeepIntermediateFiles = KeepIntermediateFiles,
            PreserveFolderStructure = PreserveFolderStructure
        }
    };

    public override void RefreshLocalization()
    {
        Title = Loc.T("Output");
        foreach (var option in CollisionOptions) option.RefreshLocalization();
        RefreshSummary();
    }

    protected override void RefreshSummary()
    {
        var folder = string.IsNullOrWhiteSpace(Directory)
            ? Loc.T("Choose a folder")
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
        var suffix = string.IsNullOrWhiteSpace(FileNameSuffix) ? "" : " · " + FileNameSuffix;
        var policy = SelectedCollision?.DisplayLabel ?? "";
        OutputSummary = $"{folder}{suffix} · {policy}";
        Summary = OutputSummary;
    }

    partial void OnDirectoryChanged(string value) => RaiseConfigurationChanged();
    partial void OnFileNameSuffixChanged(string value) => RaiseConfigurationChanged();
    partial void OnSelectedCollisionChanged(Option<CollisionPolicy>? value) => RaiseConfigurationChanged();
    partial void OnKeepIntermediateFilesChanged(bool value) => RaiseConfigurationChanged();
    partial void OnPreserveFolderStructureChanged(bool value) => RaiseConfigurationChanged();
}

public sealed partial class PipelineActionNodeViewModel : PipelineNodeViewModel
{
    private readonly IOperationSettingsSerializer _serializer;
    private readonly PipelineActionDescriptor _descriptor;

    public PipelineActionNodeViewModel(PipelineNodeDefinition definition, PipelineActionDescriptor descriptor,
        OperationSettings settings, IOperationSettingsSerializer serializer) : base(definition)
    {
        _descriptor = descriptor;
        _serializer = serializer;
        Settings = settings;
        IconGlyph = descriptor.IconGlyph;
        RefreshLocalization();
    }

    public override string ActionId => _descriptor.Id;
    public PipelineActionDescriptor Descriptor => _descriptor;
    public OperationSettings Settings { get; }
    public IReadOnlyList<PipelineFieldDescriptor> Fields => _descriptor.Fields;
    public bool IsTerminal => _descriptor.IsTerminal;

    public void NotifySettingsChanged()
    {
        RaiseConfigurationChanged();
    }

    public override PipelineNodeDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = PipelineNodeKind.Action,
        ActionId = ActionId,
        X = X,
        Y = Y,
        IsExpanded = IsExpanded,
        ConfigurationJson = _serializer.Serialize(Settings)
    };

    public override void RefreshLocalization()
    {
        Title = Loc.T(_descriptor.TitleKey);
        RefreshSummary();
    }

    protected override void RefreshSummary()
    {
        Summary = PipelineNodeSummary.Build(_descriptor, Settings);
    }
}

/// <summary>
/// Read-only stand-in for an action that exists in a saved pipeline but is not
/// registered by this build. The node keeps its exact identifier, position and
/// configuration so that loading and re-saving never silently discards data.
/// </summary>
public sealed partial class PipelineUnavailableNodeViewModel : PipelineNodeViewModel
{
    private readonly string _actionId;
    private readonly string? _configurationJson;

    public PipelineUnavailableNodeViewModel(PipelineNodeDefinition definition) : base(definition)
    {
        _actionId = definition.ActionId;
        _configurationJson = definition.ConfigurationJson;
        IconGlyph = "\uE7BA";
        RefreshLocalization();
    }

    public override string ActionId => _actionId;
    public string? ConfigurationJson => _configurationJson;
    public string UnavailableDetail => _actionId;

    public override PipelineNodeDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = PipelineNodeKind.Action,
        ActionId = _actionId,
        X = X,
        Y = Y,
        IsExpanded = IsExpanded,
        ConfigurationJson = _configurationJson
    };

    public override void RefreshLocalization()
    {
        Title = Loc.T("Unsupported operation");
        RefreshSummary();
    }

    protected override void RefreshSummary() => Summary = _actionId;
}

/// <summary>
/// Builds the compact localized line shown under a collapsed node. Values are
/// technical identifiers where necessary; labels and separators are localized.
/// </summary>
internal static class PipelineNodeSummary
{
    public static string Build(PipelineActionDescriptor descriptor, OperationSettings settings)
    {
        var parts = new List<string>();
        switch (settings)
        {
            case ImageConversionSettings image:
                parts.Add((image.OutputFormatId ?? "png").ToUpperInvariant());
                if (image.ResizeWidth is > 0 || image.ResizeHeight is > 0)
                    parts.Add(Loc.F("{0} × {1}", image.ResizeWidth?.ToString() ?? "?", image.ResizeHeight?.ToString() ?? "?"));
                if (image.Quality is > 0 and < 100) parts.Add(Loc.F("Quality {0}", image.Quality));
                if (image.ResizeMode != ImageResizeMode.None) parts.Add(Loc.T(image.ResizeMode.ToString()));
                break;
            case UpscaleSettings upscale:
                parts.Add(Loc.F("{0}×", upscale.ScaleFactor));
                if (upscale.Method == UpscaleMethod.Traditional)
                {
                    parts.Add(Loc.T(upscale.Algorithm switch
                    {
                        ResamplingAlgorithm.NearestNeighbor => "Nearest neighbour",
                        ResamplingAlgorithm.Bilinear => "Bilinear",
                        ResamplingAlgorithm.Bicubic => "Bicubic",
                        ResamplingAlgorithm.MitchellNetravali => "Mitchell-Netravali",
                        _ => "Lanczos"
                    }));
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(upscale.ModelId)) parts.Add(upscale.ModelId);
                    parts.Add(Loc.T(upscale.ExecutionProvider.ToString()));
                }
                break;
            case ImageToolSettings imageTool:
                parts.Add(Loc.T(imageTool.Tool switch
                {
                    ImageToolOperation.Optimize => "Optimise",
                    ImageToolOperation.BatchEdit => "Crop and adjust",
                    ImageToolOperation.RemoveBackground => "Remove background",
                    _ => "Edit metadata"
                }));
                if (imageTool.Tool == ImageToolOperation.Optimize) parts.Add(Loc.F("Quality {0}", imageTool.Quality));
                if (imageTool.Tool == ImageToolOperation.BatchEdit &&
                    (imageTool.ResizeWidth is > 0 || imageTool.ResizeHeight is > 0))
                    parts.Add(Loc.F("{0} × {1}", imageTool.ResizeWidth?.ToString() ?? "?",
                        imageTool.ResizeHeight?.ToString() ?? "?"));
                break;
            case VideoConversionSettings video:
                parts.Add((video.ContainerFormatId ?? "mp4").ToUpperInvariant());
                parts.Add(video.VideoCodecId);
                if (video.Width is > 0 || video.Height is > 0)
                    parts.Add(Loc.F("{0} × {1}", video.Width?.ToString() ?? "?",
                        video.Height?.ToString() ?? "?"));
                parts.Add(Loc.T(video.Preset.ToString()));
                break;
            case VideoToolSettings videoTool:
                parts.Add(Loc.T(videoTool.Tool switch
                {
                    VideoToolOperation.Compress => "Compress",
                    VideoToolOperation.Trim => "Trim",
                    VideoToolOperation.ResizeFrameRate => "Resize and frame rate",
                    VideoToolOperation.Stabilize => "Stabilise",
                    VideoToolOperation.HdrToSdr => "HDR to SDR",
                    VideoToolOperation.Thumbnail => "Video thumbnail",
                    VideoToolOperation.Gif => "Video to GIF",
                    _ => "Video tools"
                }));
                if (videoTool.Tool == VideoToolOperation.Trim)
                    parts.Add(Loc.F("{0}–{1} s", videoTool.TrimStartSeconds.ToString("0.##"),
                        videoTool.TrimEndSeconds.ToString("0.##")));
                if (videoTool.Tool == VideoToolOperation.ResizeFrameRate &&
                    (videoTool.ResizeWidth is > 0 || videoTool.ResizeHeight is > 0))
                    parts.Add(Loc.F("{0} × {1}", videoTool.ResizeWidth?.ToString() ?? "?",
                        videoTool.ResizeHeight?.ToString() ?? "?"));
                if (videoTool.Tool == VideoToolOperation.Gif)
                    parts.Add(Loc.F("{0} fps", videoTool.GifFps));
                break;
            case VideoUpscaleSettings upscale:
                parts.Add(Loc.F("{0}×", upscale.ScaleFactor));
                if (!string.IsNullOrWhiteSpace(upscale.ImageModelId)) parts.Add(upscale.ImageModelId);
                if (upscale.EnableInterpolation) parts.Add(Loc.T("Frame interpolation"));
                break;
            case AudioConversionSettings audio:
                parts.Add((audio.OutputFormatId ?? "mp3").ToUpperInvariant());
                parts.Add(Loc.F("{0} kbps", audio.BitrateKbps));
                break;
            case AudioToolSettings audioTool:
                parts.Add(Loc.T(audioTool.Tool switch
                {
                    AudioToolOperation.NormalizeLoudness => "Normalise loudness",
                    AudioToolOperation.ReduceNoise => "Reduce noise",
                    AudioToolOperation.RemoveSilence => "Remove silence",
                    AudioToolOperation.Trim => "Trim",
                    AudioToolOperation.ConvertChannelsAndRate => "Channels and rate",
                    _ => "Extract from video"
                }));
                if (audioTool.Tool == AudioToolOperation.Trim)
                    parts.Add(Loc.F("{0}–{1} s", audioTool.TrimStartSeconds.ToString("0.##"),
                        audioTool.TrimEndSeconds.ToString("0.##")));
                break;
            case DocumentConversionSettings document:
                parts.Add((document.OutputFormatId ?? "pdf").ToUpperInvariant());
                break;
            case EbookConversionSettings ebook:
                parts.Add((ebook.OutputFormatId ?? "epub").ToUpperInvariant());
                break;
            case OcrSettings ocr:
                parts.Add(ocr.OcrModelId);
                parts.Add(Loc.T(ocr.Preprocess ? "Preprocess page images" : "No preprocessing"));
                break;
            case ArchiveConversionSettings archive:
                parts.Add(Loc.T(archive.Action == ArchiveAction.Extract ? "Extract" : "Compress"));
                if (archive.Action != ArchiveAction.Extract)
                    parts.Add((archive.OutputFormatId ?? "zip").ToUpperInvariant());
                break;
        }

        if (parts.Count == 0) parts.Add(Loc.T(descriptor.TitleKey));
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
