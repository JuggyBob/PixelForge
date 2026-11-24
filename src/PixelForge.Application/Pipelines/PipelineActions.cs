using PixelForge.Domain;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Pipelines;

public enum PipelineActionCategory
{
    Images = 0,
    Upscaling = 1,
    Video = 2,
    Audio = 3,
    Documents = 4,
    Archives = 5,
    Utilities = 6
}

public enum PipelineFieldKind
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Choice = 4,
    Format = 5,
    VideoCodec = 6,
    AudioCodec = 7,
    Model = 8,
    OcrModel = 9,
    Slider = 10
}

public sealed record PipelineChoice(string Value, string LabelKey);

public sealed record PipelineFieldDescriptor
{
    public required string PropertyName { get; init; }
    public required string LabelKey { get; init; }
    public PipelineFieldKind Kind { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public double Step { get; init; } = 1;
    public IReadOnlyList<PipelineChoice> Choices { get; init; } = [];
    public MediaCategory? FormatCategory { get; init; }
    public ModelTask? ModelTask { get; init; }
    public string? DependsOnProperty { get; init; }
    public IReadOnlyList<string>? VisibleWhenValues { get; init; }
    public bool IsVideoCodec { get; init; }
    public string? Placeholder { get; init; }
}

public sealed record PipelineActionDescriptor
{
    public required string Id { get; init; }
    public required PipelineActionCategory Category { get; init; }
    public required string TitleKey { get; init; }
    public required string DescriptionKey { get; init; }
    public required string IconGlyph { get; init; }
    public required OperationKind Operation { get; init; }
    public required IReadOnlyList<PipelineMediaType> InputTypes { get; init; }
    public required PipelineMediaType OutputType { get; init; }
    public required Func<OperationSettings> CreateSettings { get; init; }
    public IReadOnlyList<PipelineFieldDescriptor> Fields { get; init; } = [];
    public bool IsTerminal { get; init; }
    public bool RequiresModel { get; init; }
}

public static class PipelineActionIds
{
    public const string ImageConvert = "image.convert";
    public const string ImageResize = "image.resize";
    public const string ImageUpscale = "image.upscale";
    public const string ImageUpscaleTraditional = "image.upscale.traditional";
    public const string ImageTools = "image.tools";
    public const string VideoConvert = "video.convert";
    public const string VideoTools = "video.tools";
    public const string VideoUpscale = "video.upscale";
    public const string VideoInterpolate = "video.interpolate";
    public const string VideoThumbnail = "video.thumbnail";
    public const string VideoGif = "video.gif";
    public const string AudioConvert = "audio.convert";
    public const string AudioTools = "audio.tools";
    public const string DocumentConvert = "document.convert";
    public const string EbookConvert = "ebook.convert";
    public const string Ocr = "ocr";
    public const string ArchiveCreate = "archive.create";
    public const string ArchiveRepackage = "archive.repackage";
    public const string ArchiveExtract = "archive.extract";
}

public static class PipelineActionRegistry
{
    private static readonly IReadOnlyList<PipelineActionDescriptor> ActionList = BuildActions();

    private static readonly IReadOnlyDictionary<string, PipelineActionDescriptor> ById =
        ActionList.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PipelineActionDescriptor> All => ActionList;

    public static PipelineActionDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : ById.GetValueOrDefault(id);

    public static IReadOnlyList<PipelineActionDescriptor> ForInputType(PipelineMediaType type) =>
        ActionList.Where(a => a.InputTypes.Contains(type)).ToList();

    public static PipelineMediaType ToPipelineType(MediaCategory category) => category switch
    {
        MediaCategory.Image => PipelineMediaType.Image,
        MediaCategory.Audio => PipelineMediaType.Audio,
        MediaCategory.Video => PipelineMediaType.Video,
        MediaCategory.Document => PipelineMediaType.Document,
        MediaCategory.Ebook => PipelineMediaType.Ebook,
        MediaCategory.Archive => PipelineMediaType.Archive,
        _ => PipelineMediaType.Unknown
    };

    public static MediaCategory ToMediaCategory(PipelineMediaType type) => type switch
    {
        PipelineMediaType.Image => MediaCategory.Image,
        PipelineMediaType.Audio => MediaCategory.Audio,
        PipelineMediaType.Video => MediaCategory.Video,
        PipelineMediaType.Document => MediaCategory.Document,
        PipelineMediaType.Ebook => MediaCategory.Ebook,
        PipelineMediaType.Archive => MediaCategory.Archive,
        _ => MediaCategory.Unknown
    };

    public static PipelineMediaType OutputTypeFor(PipelineActionDescriptor action, OperationSettings settings) =>
        action.Id switch
        {
            PipelineActionIds.VideoThumbnail or PipelineActionIds.VideoGif => PipelineMediaType.Image,
            PipelineActionIds.ArchiveExtract => PipelineMediaType.Folder,
            PipelineActionIds.Ocr => PipelineMediaType.Document,
            PipelineActionIds.EbookConvert => PipelineMediaType.Ebook,
            PipelineActionIds.DocumentConvert => PipelineMediaType.Document,
            PipelineActionIds.ArchiveCreate or PipelineActionIds.ArchiveRepackage => PipelineMediaType.Archive,
            PipelineActionIds.AudioConvert or PipelineActionIds.AudioTools => PipelineMediaType.Audio,
            PipelineActionIds.VideoConvert or PipelineActionIds.VideoTools
                or PipelineActionIds.VideoUpscale or PipelineActionIds.VideoInterpolate => PipelineMediaType.Video,
            _ => action.OutputType
        };

    private static IReadOnlyList<PipelineActionDescriptor> BuildActions() =>
    [
        new()
        {
            Id = PipelineActionIds.ImageConvert,
            Category = PipelineActionCategory.Images,
            TitleKey = "Convert image",
            DescriptionKey = "Resize, re-encode and optimise a still image or animation.",
            IconGlyph = "\uE91B",
            Operation = OperationKind.ImageConvert,
            InputTypes = [PipelineMediaType.Image],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new ImageConversionSettings(),
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Image),
                Number("Quality", "Quality", 1, 100),
                Number("ResizeWidth", "Width", 1, 32768),
                Number("ResizeHeight", "Height", 1, 32768),
                Choice("ResizeMode", "Resize mode", ("None", "None"), ("Fit", "Fit"), ("Fill", "Fill"), ("Stretch", "Stretch")),
                Choice("Resampling", "Resampling", ("NearestNeighbor", "Nearest neighbour"), ("Bilinear", "Bilinear"),
                    ("Bicubic", "Bicubic"), ("Lanczos", "Lanczos"), ("MitchellNetravali", "Mitchell-Netravali")),
                Bool("PreserveMetadata", "Preserve metadata"),
                Bool("PreserveAnimation", "Preserve animation"),
                Bool("FlattenAnimation", "Keep one frame"),
                Choice("TransparencyMode", "Transparency", ("Preserve", "Preserve"), ("Flatten", "Flatten")),
                Text("BackgroundColor", "Background colour")
            ]
        },
        new()
        {
            Id = PipelineActionIds.ImageResize,
            Category = PipelineActionCategory.Images,
            TitleKey = "Resize image",
            DescriptionKey = "Change image dimensions before the next operation.",
            IconGlyph = "\uE740",
            Operation = OperationKind.ImageConvert,
            InputTypes = [PipelineMediaType.Image],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new ImageConversionSettings
            {
                OutputFormatId = "png",
                Quality = 100,
                Lossless = true
            },
            Fields =
            [
                Number("ResizeWidth", "Width", 1, 32768),
                Number("ResizeHeight", "Height", 1, 32768),
                Choice("ResizeMode", "Resize mode", ("Fit", "Fit"), ("Fill", "Fill"), ("Stretch", "Stretch")),
                Choice("Resampling", "Resampling", ("NearestNeighbor", "Nearest neighbour"), ("Bilinear", "Bilinear"),
                    ("Bicubic", "Bicubic"), ("Lanczos", "Lanczos"), ("MitchellNetravali", "Mitchell-Netravali")),
                Bool("PreserveMetadata", "Preserve metadata"),
                Choice("TransparencyMode", "Transparency", ("Preserve", "Preserve"), ("Flatten", "Flatten")),
                Text("BackgroundColor", "Background colour")
            ]
        },
        new()
        {
            Id = PipelineActionIds.ImageUpscale,
            Category = PipelineActionCategory.Upscaling,
            TitleKey = "AI image upscaling",
            DescriptionKey = "Enlarge an image with an installed AI upscaling model.",
            IconGlyph = "\uE8A3",
            Operation = OperationKind.UpscaleAi,
            InputTypes = [PipelineMediaType.Image],
            OutputType = PipelineMediaType.Image,
            RequiresModel = true,
            CreateSettings = () => new UpscaleSettings
            {
                Method = UpscaleMethod.Ai,
                OutputFormatId = FormatIds.Auto
            },
            Fields =
            [
                Model("ModelId", "Model", ModelTask.ImageUpscale),
                Choice("ScaleFactor", "Scale", ("2", "2×"), ("3", "3×"), ("4", "4×")),
                Choice("ExecutionProvider", "Execution provider", ("Auto", "Automatic"), ("Cpu", "CPU"),
                    ("DirectML", "DirectML"), ("Vulkan", "Vulkan")),
                Bool("PreserveAlpha", "Preserve alpha"),
                Number("OutputQuality", "Output quality", 1, 100)
            ]
        },
        new()
        {
            Id = PipelineActionIds.ImageUpscaleTraditional,
            Category = PipelineActionCategory.Upscaling,
            TitleKey = "Traditional image upscaling",
            DescriptionKey = "Enlarge an image with a resampling algorithm.",
            IconGlyph = "\uE740",
            Operation = OperationKind.UpscaleTraditional,
            InputTypes = [PipelineMediaType.Image],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new UpscaleSettings
            {
                Method = UpscaleMethod.Traditional,
                Algorithm = ResamplingAlgorithm.Lanczos,
                OutputFormatId = FormatIds.Auto
            },
            Fields =
            [
                Choice("Algorithm", "Algorithm", ("NearestNeighbor", "Nearest neighbour"), ("Bilinear", "Bilinear"),
                    ("Bicubic", "Bicubic"), ("Lanczos", "Lanczos"), ("MitchellNetravali", "Mitchell-Netravali")),
                Choice("ScaleFactor", "Scale", ("2", "2×"), ("3", "3×"), ("4", "4×")),
                Bool("PreserveAlpha", "Preserve alpha"),
                Number("OutputQuality", "Output quality", 1, 100)
            ]
        },
        new()
        {
            Id = PipelineActionIds.ImageTools,
            Category = PipelineActionCategory.Images,
            TitleKey = "Image tools",
            DescriptionKey = "Crop, optimise, edit metadata or remove an image background.",
            IconGlyph = "\uE790",
            Operation = OperationKind.ImageProcess,
            InputTypes = [PipelineMediaType.Image],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new ImageToolSettings(),
            Fields =
            [
                Choice("Tool", "Operation", ("Optimize", "Optimise"), ("BatchEdit", "Crop and adjust"),
                    ("RemoveBackground", "Remove background"), ("EditMetadata", "Edit metadata")),
                Conditional(Number("Quality", "Quality", 1, 100), "Tool", "Optimize"),
                Conditional(Bool("Lossless", "Lossless"), "Tool", "Optimize"),
                Conditional(Number("PngCompression", "PNG compression", 0, 9), "Tool", "Optimize"),
                Conditional(Number("AvifSpeed", "AVIF speed", 0, 10), "Tool", "Optimize"),
                Conditional(Number("GifColors", "GIF colours", 2, 256), "Tool", "Optimize"),
                Conditional(Number("ResizeWidth", "Width", 1, 32768), "Tool", "BatchEdit"),
                Conditional(Number("ResizeHeight", "Height", 1, 32768), "Tool", "BatchEdit"),
                Conditional(Bool("PreserveAspectRatio", "Preserve aspect ratio"), "Tool", "BatchEdit"),
                Conditional(Number("Brightness", "Brightness", 0, 200), "Tool", "BatchEdit"),
                Conditional(Number("Contrast", "Contrast", 0, 200), "Tool", "BatchEdit"),
                Conditional(Number("Saturation", "Saturation", 0, 200), "Tool", "BatchEdit"),
                Conditional(Number("DenoiseStrength", "Denoise", 0, 1, 0.05), "Tool", "BatchEdit"),
                Conditional(Number("SharpenAmount", "Sharpen", 0, 5, 0.1), "Tool", "BatchEdit"),
                Conditional(Number("SharpenRadius", "Sharpen radius", 0.1, 10, 0.1), "Tool", "BatchEdit"),
                Conditional(Number("CropX", "Crop X", 0, 32768), "Tool", "BatchEdit"),
                Conditional(Number("CropY", "Crop Y", 0, 32768), "Tool", "BatchEdit"),
                Conditional(Number("CropWidth", "Crop width", 1, 32768), "Tool", "BatchEdit"),
                Conditional(Number("CropHeight", "Crop height", 1, 32768), "Tool", "BatchEdit"),
                Conditional(Text("CropAspectRatio", "Crop aspect ratio"), "Tool", "BatchEdit"),
                Conditional(Choice("WatermarkKind", "Watermark", ("None", "None"), ("Text", "Text"), ("Image", "Image")),
                    "Tool", "BatchEdit"),
                Conditional(Text("WatermarkText", "Watermark text"), "Tool", "BatchEdit"),
                Conditional(Text("WatermarkPosition", "Watermark position"), "Tool", "BatchEdit"),
                Conditional(Number("WatermarkOpacity", "Watermark opacity", 0, 1, 0.05), "Tool", "BatchEdit"),
                Conditional(Model("BackgroundModelId", "Background model", ModelTask.ImageRestore), "Tool", "RemoveBackground"),
                Conditional(Text("BackgroundColor", "Background colour"), "Tool", "RemoveBackground"),
                Conditional(Choice("ExecutionProvider", "Execution provider", ("Auto", "Automatic"), ("Cpu", "CPU"),
                    ("DirectML", "DirectML"), ("Vulkan", "Vulkan")), "Tool", "RemoveBackground"),
                Conditional(Text("MetaDescription", "Description"), "Tool", "EditMetadata"),
                Conditional(Text("MetaCopyright", "Copyright"), "Tool", "EditMetadata"),
                Conditional(Text("MetaArtist", "Artist"), "Tool", "EditMetadata"),
                Conditional(Text("MetaComment", "Comment"), "Tool", "EditMetadata"),
                Conditional(Bool("RemoveAllMetadata", "Remove all metadata"), "Tool", "EditMetadata")
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoConvert,
            Category = PipelineActionCategory.Video,
            TitleKey = "Video transcoding",
            DescriptionKey = "Re-encode a video container, codec, resolution or frame rate.",
            IconGlyph = "\uE714",
            Operation = OperationKind.VideoConvert,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Video,
            CreateSettings = () => new VideoConversionSettings(),
            Fields =
            [
                Format("ContainerFormatId", "Container", MediaCategory.Video),
                VideoCodec("VideoCodecId", "Video codec", "ContainerFormatId"),
                AudioCodec("AudioCodecId", "Audio codec", "ContainerFormatId"),
                Choice("RateControlMode", "Rate control", ("ConstantQuality", "Constant quality"), ("Bitrate", "Bitrate")),
                Number("QualityValue", "Quality", 0, 63),
                Number("VideoBitrateKbps", "Video bitrate", 100, 200000),
                Number("AudioBitrateKbps", "Audio bitrate", 32, 512),
                Number("Width", "Width", 1, 16384),
                Number("Height", "Height", 1, 16384),
                Choice("ResizeMode", "Resize mode", ("None", "None"), ("Fit", "Fit"), ("Crop", "Crop"), ("Stretch", "Stretch")),
                Bool("PreserveAspectRatio", "Preserve aspect ratio"),
                Number("FrameRate", "Frame rate", 0, 240, 0.001),
                Choice("Preset", "Quality preset", ("Fast", "Fast"), ("Balanced", "Balanced"), ("HighQuality", "High quality")),
                Bool("PreserveMetadata", "Preserve metadata"),
                Bool("FastStart", "Web fast start")
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoTools,
            Category = PipelineActionCategory.Video,
            TitleKey = "Video tools",
            DescriptionKey = "Trim, compress, resize, stabilise or tone-map a video.",
            IconGlyph = "\uE8B2",
            Operation = OperationKind.VideoProcess,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Video,
            CreateSettings = () => new VideoToolSettings(),
            Fields =
            [
                Choice("Tool", "Operation", ("Compress", "Compress"), ("Trim", "Trim"),
                    ("ResizeFrameRate", "Resize and frame rate"), ("Stabilize", "Stabilise"), ("HdrToSdr", "HDR to SDR")),
                Conditional(Format("ContainerFormatId", "Container", MediaCategory.Video), "Tool", "Compress", "Trim", "ResizeFrameRate", "Stabilize", "HdrToSdr"),
                Conditional(Number("TrimStartSeconds", "Start", 0, 86400, 0.01), "Tool", "Trim"),
                Conditional(Number("TrimEndSeconds", "End", 0, 86400, 0.01), "Tool", "Trim"),
                Conditional(Bool("FrameAccurateTrim", "Frame-accurate trim"), "Tool", "Trim"),
                Conditional(Choice("CompressionPreset", "Compression preset", ("MaximumQuality", "Maximum quality"),
                    ("Balanced", "Balanced"), ("SmallestFile", "Smallest file"), ("WebOptimized", "Web optimised")), "Tool", "Compress"),
                Conditional(Number("TargetSizeMb", "Target size (MB)", 1, 100000), "Tool", "Compress"),
                Conditional(Number("QualityValue", "Quality", 0, 63), "Tool", "Compress"),
                Conditional(Number("VideoBitrateKbps", "Video bitrate", 100, 200000), "Tool", "Compress"),
                Conditional(Number("AudioBitrateKbps", "Audio bitrate", 32, 512), "Tool", "Compress"),
                Conditional(Bool("AllowTwoPass", "Allow two-pass"), "Tool", "Compress"),
                Conditional(Bool("PreferHardware", "Prefer hardware"), "Tool", "Compress"),
                Conditional(Number("ResizeWidth", "Width", 1, 16384), "Tool", "ResizeFrameRate"),
                Conditional(Number("ResizeHeight", "Height", 1, 16384), "Tool", "ResizeFrameRate"),
                Conditional(Bool("PreserveAspectRatio", "Preserve aspect ratio"), "Tool", "ResizeFrameRate"),
                Conditional(Number("FrameRate", "Frame rate", 0, 240, 0.001), "Tool", "ResizeFrameRate"),
                Conditional(Number("StabilizationStrength", "Stabilisation", 0, 1, 0.05), "Tool", "Stabilize"),
                Conditional(Number("ToneMapPeakNits", "Tone-map peak", 80, 10000), "Tool", "HdrToSdr")
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoUpscale,
            Category = PipelineActionCategory.Video,
            TitleKey = "Video upscaling",
            DescriptionKey = "Upscale video frames with AI models and optional frame interpolation.",
            IconGlyph = "\uE8A3",
            Operation = OperationKind.VideoUpscale,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Video,
            RequiresModel = true,
            CreateSettings = () => new VideoUpscaleSettings(),
            Fields =
            [
                Model("ImageModelId", "Upscaling model", ModelTask.ImageUpscale),
                Choice("ScaleFactor", "Scale", ("2", "2×"), ("3", "3×"), ("4", "4×")),
                Bool("EnableInterpolation", "Frame interpolation"),
                Model("InterpolationModelId", "Interpolation model", ModelTask.FrameInterpolation),
                Choice("FrameRateMultiplier", "Frame rate multiplier", ("2", "2×"), ("3", "3×"), ("4", "4×")),
                Choice("ExecutionProvider", "Execution provider", ("Auto", "Automatic"), ("Cpu", "CPU"),
                    ("DirectML", "DirectML"), ("Vulkan", "Vulkan")),
                Bool("EnableTemporalStability", "Temporal stability"),
                Number("TemporalStabilityStrength", "Stability strength", 0, 100),
                AudioCodec("AudioCodecId", "Audio codec", "ContainerFormatId"),
                Number("AudioBitrateKbps", "Audio bitrate", 32, 512)
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoInterpolate,
            Category = PipelineActionCategory.Video,
            TitleKey = "Frame interpolation",
            DescriptionKey = "Generate smoother motion with an installed interpolation model.",
            IconGlyph = "\uE916",
            Operation = OperationKind.VideoUpscale,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Video,
            RequiresModel = true,
            CreateSettings = () => new VideoUpscaleSettings
            {
                EnableSuperResolution = false,
                EnableInterpolation = true
            },
            Fields =
            [
                Model("InterpolationModelId", "Interpolation model", ModelTask.FrameInterpolation),
                Choice("FrameRateMultiplier", "Frame rate multiplier", ("2", "2×"), ("3", "3×"), ("4", "4×")),
                Choice("ExecutionProvider", "Execution provider", ("Auto", "Automatic"), ("Cpu", "CPU"),
                    ("DirectML", "DirectML"), ("Vulkan", "Vulkan")),
                AudioCodec("AudioCodecId", "Audio codec", "ContainerFormatId"),
                Number("AudioBitrateKbps", "Audio bitrate", 32, 512)
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoThumbnail,
            Category = PipelineActionCategory.Video,
            TitleKey = "Video thumbnail",
            DescriptionKey = "Export a single still frame from a video.",
            IconGlyph = "\uEB9F",
            Operation = OperationKind.VideoProcess,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new VideoToolSettings
            {
                Tool = VideoToolOperation.Thumbnail,
                ThumbnailFormatId = "jpg"
            },
            Fields =
            [
                Number("ThumbnailTimeSeconds", "Time", 0, 86400, 0.01),
                Format("ThumbnailFormatId", "Format", MediaCategory.Image)
            ]
        },
        new()
        {
            Id = PipelineActionIds.VideoGif,
            Category = PipelineActionCategory.Video,
            TitleKey = "Video to GIF",
            DescriptionKey = "Export a short animated GIF from a video.",
            IconGlyph = "\uE91B",
            Operation = OperationKind.VideoProcess,
            InputTypes = [PipelineMediaType.Video],
            OutputType = PipelineMediaType.Image,
            CreateSettings = () => new VideoToolSettings { Tool = VideoToolOperation.Gif },
            Fields =
            [
                Number("GifStartSeconds", "Start", 0, 86400, 0.01),
                Number("GifEndSeconds", "End", 0, 86400, 0.01),
                Number("GifFps", "Frame rate", 1, 60),
                Number("GifWidth", "Width", 32, 4096)
            ]
        },
        new()
        {
            Id = PipelineActionIds.AudioConvert,
            Category = PipelineActionCategory.Audio,
            TitleKey = "Audio conversion",
            DescriptionKey = "Convert audio, or extract the audio track from a video.",
            IconGlyph = "\uE8D6",
            Operation = OperationKind.AudioConvert,
            InputTypes = [PipelineMediaType.Audio, PipelineMediaType.Video],
            OutputType = PipelineMediaType.Audio,
            CreateSettings = () => new AudioConversionSettings(),
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Audio),
                Number("BitrateKbps", "Bitrate", 32, 512),
                Number("Quality", "Quality", 0, 10),
                Number("SampleRate", "Sample rate", 0, 384000),
                Number("Channels", "Channels", 0, 8)
            ]
        },
        new()
        {
            Id = PipelineActionIds.AudioTools,
            Category = PipelineActionCategory.Audio,
            TitleKey = "Audio tools",
            DescriptionKey = "Trim, normalise, denoise or adjust audio channels and rate.",
            IconGlyph = "\uE9E9",
            Operation = OperationKind.AudioProcess,
            InputTypes = [PipelineMediaType.Audio, PipelineMediaType.Video],
            OutputType = PipelineMediaType.Audio,
            CreateSettings = () => new AudioToolSettings(),
            Fields =
            [
                Choice("Tool", "Operation", ("NormalizeLoudness", "Normalise loudness"), ("ReduceNoise", "Reduce noise"),
                    ("RemoveSilence", "Remove silence"), ("Trim", "Trim"), ("ConvertChannelsAndRate", "Channels and rate"),
                    ("ExtractFromVideo", "Extract from video")),
                Format("OutputFormatId", "Output format", MediaCategory.Audio),
                Conditional(Number("TrimStartSeconds", "Start", 0, 86400, 0.01), "Tool", "Trim"),
                Conditional(Number("TrimEndSeconds", "End", 0, 86400, 0.01), "Tool", "Trim"),
                Conditional(Number("TargetLufs", "Target loudness", -60, 0, 0.1), "Tool", "NormalizeLoudness"),
                Conditional(Number("TruePeakDb", "True peak", -20, 0, 0.1), "Tool", "NormalizeLoudness"),
                Conditional(Number("LoudnessRange", "Loudness range", 1, 30, 0.1), "Tool", "NormalizeLoudness"),
                Conditional(Choice("NoiseMethod", "Noise method", ("Spectral", "Spectral"), ("Rnnoise", "RNNoise")), "Tool", "ReduceNoise"),
                Conditional(Number("NoiseReductionDb", "Noise reduction", 0, 60, 0.5), "Tool", "ReduceNoise"),
                Conditional(Number("SilenceThresholdDb", "Silence threshold", -100, 0, 0.5), "Tool", "RemoveSilence"),
                Conditional(Number("MinSilenceSeconds", "Minimum silence", 0.05, 60, 0.05), "Tool", "RemoveSilence"),
                Conditional(Number("SampleRate", "Sample rate", 0, 384000), "Tool", "ConvertChannelsAndRate"),
                Conditional(Number("Channels", "Channels", 0, 8), "Tool", "ConvertChannelsAndRate"),
                Conditional(Number("BitrateKbps", "Bitrate", 32, 512), "Tool", "ConvertChannelsAndRate", "ExtractFromVideo"),
                Conditional(Number("AudioStreamIndex", "Audio stream", 0, 64), "Tool", "ExtractFromVideo"),
                Conditional(Bool("CopyAudioStream", "Copy audio stream"), "Tool", "ExtractFromVideo")
            ]
        },
        new()
        {
            Id = PipelineActionIds.DocumentConvert,
            Category = PipelineActionCategory.Documents,
            TitleKey = "Document conversion",
            DescriptionKey = "Convert documents between supported formats.",
            IconGlyph = "\uE8A5",
            Operation = OperationKind.DocumentConvert,
            InputTypes = [PipelineMediaType.Document],
            OutputType = PipelineMediaType.Document,
            CreateSettings = () => new DocumentConversionSettings(),
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Document),
                Text("Title", "Title"),
                Text("Author", "Author")
            ]
        },
        new()
        {
            Id = PipelineActionIds.EbookConvert,
            Category = PipelineActionCategory.Documents,
            TitleKey = "E-book conversion",
            DescriptionKey = "Convert e-books while preserving chapters and metadata.",
            IconGlyph = "\uE736",
            Operation = OperationKind.EbookConvert,
            InputTypes = [PipelineMediaType.Ebook],
            OutputType = PipelineMediaType.Ebook,
            CreateSettings = () => new EbookConversionSettings(),
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Ebook),
                Text("Title", "Title"),
                Text("Author", "Author"),
                Text("Language", "Language")
            ]
        },
        new()
        {
            Id = PipelineActionIds.Ocr,
            Category = PipelineActionCategory.Documents,
            TitleKey = "OCR",
            DescriptionKey = "Recognise text in an image or PDF and produce a Word document.",
            IconGlyph = "\uE8A5",
            Operation = OperationKind.OcrDocument,
            InputTypes = [PipelineMediaType.Image, PipelineMediaType.Document],
            OutputType = PipelineMediaType.Document,
            RequiresModel = true,
            CreateSettings = () => new OcrSettings(),
            Fields =
            [
                OcrModel("OcrModelId", "OCR model"),
                Bool("Preprocess", "Preprocess page images"),
                Bool("GeneratePdf", "Generate PDF companion"),
                Choice("ExecutionProvider", "Execution provider", ("Auto", "Automatic"), ("Cpu", "CPU"),
                    ("DirectML", "DirectML"), ("Vulkan", "Vulkan")),
                Text("DocumentName", "Document name")
            ]
        },
        new()
        {
            Id = PipelineActionIds.ArchiveCreate,
            Category = PipelineActionCategory.Archives,
            TitleKey = "Create archive",
            DescriptionKey = "Package files or folders into an archive.",
            IconGlyph = "\uE8B7",
            Operation = OperationKind.ArchiveConvert,
            InputTypes = [PipelineMediaType.Folder, PipelineMediaType.Archive],
            OutputType = PipelineMediaType.Archive,
            CreateSettings = () => new ArchiveConversionSettings { Action = ArchiveAction.Create },
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Archive),
                Slider("CompressionLevel", "Compression level", 0, 9),
                Bool("PreserveDirectoryStructure", "Preserve folder structure")
            ]
        },
        new()
        {
            Id = PipelineActionIds.ArchiveRepackage,
            Category = PipelineActionCategory.Archives,
            TitleKey = "Repackage archive",
            DescriptionKey = "Rebuild an archive in a different format or compression level.",
            IconGlyph = "\uE8B7",
            Operation = OperationKind.ArchiveConvert,
            InputTypes = [PipelineMediaType.Archive],
            OutputType = PipelineMediaType.Archive,
            CreateSettings = () => new ArchiveConversionSettings { Action = ArchiveAction.Repackage },
            Fields =
            [
                Format("OutputFormatId", "Output format", MediaCategory.Archive),
                Slider("CompressionLevel", "Compression level", 0, 9),
                Bool("PreserveDirectoryStructure", "Preserve folder structure")
            ]
        },
        new()
        {
            Id = PipelineActionIds.ArchiveExtract,
            Category = PipelineActionCategory.Archives,
            TitleKey = "Extract archive",
            DescriptionKey = "Extract an archive into a folder. This must be the final operation.",
            IconGlyph = "\uE8B7",
            Operation = OperationKind.ArchiveConvert,
            InputTypes = [PipelineMediaType.Archive],
            OutputType = PipelineMediaType.Folder,
            IsTerminal = true,
            CreateSettings = () => new ArchiveConversionSettings { Action = ArchiveAction.Extract },
            Fields = []
        }
    ];

    private static PipelineFieldDescriptor Text(string property, string label, string? placeholder = null) =>
        new() { PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Text, Placeholder = placeholder };

    private static PipelineFieldDescriptor Number(string property, string label, double min, double max,
        double step = 1) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Integer,
            Minimum = min, Maximum = max, Step = step
        };

    private static PipelineFieldDescriptor Slider(string property, string label, double min, double max,
        double step = 1) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Slider,
            Minimum = min, Maximum = max, Step = step
        };

    private static PipelineFieldDescriptor Bool(string property, string label) =>
        new() { PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Boolean };

    private static PipelineFieldDescriptor Choice(string property, string label,
        params (string Value, string LabelKey)[] choices) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Choice,
            Choices = choices.Select(c => new PipelineChoice(c.Value, c.LabelKey)).ToList()
        };

    private static PipelineFieldDescriptor Format(string property, string label, MediaCategory category) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Format, FormatCategory = category
        };

    private static PipelineFieldDescriptor Model(string property, string label, ModelTask task) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.Model, ModelTask = task
        };

    private static PipelineFieldDescriptor OcrModel(string property, string label) =>
        new() { PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.OcrModel };

    private static PipelineFieldDescriptor VideoCodec(string property, string label, string containerProperty) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.VideoCodec,
            DependsOnProperty = containerProperty, IsVideoCodec = true
        };

    private static PipelineFieldDescriptor AudioCodec(string property, string label, string containerProperty) =>
        new()
        {
            PropertyName = property, LabelKey = label, Kind = PipelineFieldKind.AudioCodec,
            DependsOnProperty = containerProperty
        };

    private static PipelineFieldDescriptor Conditional(PipelineFieldDescriptor field, string property,
        params string[] values) =>
        field with { DependsOnProperty = property, VisibleWhenValues = values };
}
