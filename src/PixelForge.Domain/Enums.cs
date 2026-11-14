namespace PixelForge.Domain;

public enum MediaCategory
{
    Unknown = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    Document = 4,
    Ebook = 5,
    Archive = 6
}

public enum OperationKind
{
    ImageConvert = 0,
    AudioConvert = 1,
    VideoConvert = 2,
    UpscaleTraditional = 3,
    UpscaleAi = 4,
    DocumentConvert = 5,
    EbookConvert = 6,
    ArchiveConvert = 7,
    AudioProcess = 8,
    ImageProcess = 9,
    VideoProcess = 10,
    VideoUpscale = 11,
    OcrDocument = 12
}

public enum JobState
{
    Pending = 0,
    Preparing = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Interrupted = 6
}

public enum JobItemState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5
}

public enum CollisionPolicy
{
    UniqueName = 0,
    Skip = 1,
    Overwrite = 2
}

public enum ResamplingAlgorithm
{
    NearestNeighbor = 0,
    Bilinear = 1,
    Bicubic = 2,
    Lanczos = 3,
    MitchellNetravali = 4
}

public enum ImageResizeMode
{
    None = 0,
    Fit = 1,
    Fill = 2,
    Stretch = 3
}

public enum VideoResizeMode
{
    None = 0,
    Fit = 1,
    Crop = 2,
    Stretch = 3
}

public enum TransparencyMode
{
    Preserve = 0,
    Flatten = 1
}

public enum SubtitleMode
{
    Copy = 0,
    Convert = 1,
    Drop = 2
}

public enum VideoRateControlMode
{
    ConstantQuality = 0,
    Bitrate = 1
}

public enum AudioRateControlMode
{
    ConstantBitrate = 0,
    VariableQuality = 1
}

public enum ConversionPreset
{
    Fast = 0,
    Balanced = 1,
    HighQuality = 2
}

public enum UpscaleMethod
{
    Traditional = 0,
    Ai = 1
}

public enum ModelTask
{
    ImageUpscale = 0,
    FrameInterpolation = 1,
    ImageRestore = 2
}

public enum CodecKind
{
    Video = 0,
    Audio = 1
}

public enum ExecutionProviderKind
{
    Auto = 0,
    Cpu = 1,
    DirectML = 2,
    Vulkan = 3
}

public enum ModelBackendKind
{
    OnnxRuntime = 0,
    NcnnVulkan = 1,
    Python = 2,
    LlamaCpp = 3
}

public enum OcrBackendKind
{
    PpOcr = 0,
    LlamaCpp = 1
}

[Flags]
public enum RateControlCapabilities
{
    None = 0,
    Bitrate = 1,
    ConstantQuality = 2,
    Quantizer = 4,
    Lossless = 8
}

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum ModelAvailability
{
    NotInstalled = 0,
    Installed = 1,
    Downloading = 2,
    UpdateAvailable = 3,
    Invalid = 4,
    Unavailable = 5
}

public enum ArchiveAction
{
    Create = 0,
    Repackage = 1,
    Extract = 2
}

public enum AudioToolOperation
{
    NormalizeLoudness = 0,
    ReduceNoise = 1,
    RemoveSilence = 2,
    Trim = 3,
    Merge = 4,
    ConvertChannelsAndRate = 5,
    EditMetadata = 6,
    ExtractFromVideo = 7
}

public enum NoiseReductionMethod
{
    Spectral = 0,
    Rnnoise = 1
}

public enum AudioMetadataMode
{
    Preserve = 0,
    Edit = 1,
    RemoveAll = 2
}

public enum ImageToolOperation
{
    Optimize = 0,
    BatchEdit = 1,
    RemoveBackground = 2,
    EditMetadata = 3
}

public enum WatermarkKind
{
    None = 0,
    Text = 1,
    Image = 2
}

public enum VideoToolOperation
{
    Compress = 0,
    Trim = 1,
    Merge = 2,
    Stabilize = 3,
    Thumbnail = 4,
    Gif = 5,
    ExtractAudio = 6,
    ReplaceAudio = 7,
    Subtitles = 8,
    EditMetadata = 9,
    HdrToSdr = 10,
    ResizeFrameRate = 11
}

public enum VideoCompressionPreset
{
    MaximumQuality = 0,
    Balanced = 1,
    SmallestFile = 2,
    WebOptimized = 3
}

public enum SubtitleToolMode
{
    Extract = 0,
    Convert = 1,
    Embed = 2,
    Drop = 3
}