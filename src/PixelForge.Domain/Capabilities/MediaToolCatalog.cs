using System.ComponentModel;

namespace PixelForge.Domain.Capabilities;

/// <summary>
/// Metadata for a focused media tool. The category is the single source of
/// truth used to filter the tool picker and to reject incompatible requests.
/// </summary>
public sealed record MediaToolDefinition(string Id, string Label, MediaCategory Category, string Description)
    : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
    }

    public override string ToString() => Label;
}

public static class MediaToolCatalog
{
    public static IReadOnlyList<MediaToolDefinition> All { get; } =
    [
        new("NormalizeLoudness", "Loudness normalisation (LUFS)", MediaCategory.Audio, "Measure EBU R128 integrated loudness and normalise with true-peak protection."),
        new("ReduceNoise", "Noise reduction / voice cleanup", MediaCategory.Audio, "Reduce steady noise with spectral filtering or the bundled RNNoise speech model."),
        new("RemoveSilence", "Silence detection and removal", MediaCategory.Audio, "Detect silent regions and cut them while keeping a margin around speech."),
        new("Trim", "Trim audio", MediaCategory.Audio, "Cut a precise start/end range without changing the source."),
        new("Merge", "Merge audio", MediaCategory.Audio, "Join multiple audio files in order with a common sample rate and channel layout."),
        new("ConvertChannelsAndRate", "Channels and sample rate", MediaCategory.Audio, "Convert mono/stereo and choose a new sample rate with proper resampling."),
        new("EditMetadata", "Audio metadata and cover art", MediaCategory.Audio, "Edit title, artist, album, genre and embedded cover art."),
        new("ExtractFromVideo", "Extract audio from video", MediaCategory.Audio, "Copy the original audio stream when compatible, otherwise re-encode."),

        new("Optimize", "Compress and optimise", MediaCategory.Image, "Reduce file size with quality and lossless controls."),
        new("BatchEdit", "Batch edit", MediaCategory.Image, "Crop, resize, denoise, sharpen, adjust colour and watermark in a fixed order."),
        new("RemoveBackground", "Background removal", MediaCategory.Image, "Local u2netp segmentation with transparent PNG/WebP output."),
        new("ImageMetadata", "Image metadata", MediaCategory.Image, "Inspect and edit EXIF/IPTC fields, or remove all metadata."),

        new("Compress", "Compress to target size", MediaCategory.Video, "Estimate bitrate from duration, encode, and report when the target cannot be met."),
        new("VideoTrim", "Trim video", MediaCategory.Video, "Cut a range with stream copy or frame-accurate re-encoding."),
        new("VideoMerge", "Merge video", MediaCategory.Video, "Join videos with stream copy when safe, otherwise re-encode to common settings."),
        new("Stabilize", "Stabilise video", MediaCategory.Video, "Reduce handheld shake with FFmpeg deshake and local processing."),
        new("Thumbnail", "Generate thumbnail", MediaCategory.Video, "Extract a representative frame with a timestamp and image format."),
        new("Gif", "Create GIF", MediaCategory.Video, "Create a palette-optimised GIF from a selected video range."),
        new("ExtractAudio", "Extract audio track", MediaCategory.Video, "Select an audio stream and copy or re-encode it."),
        new("ReplaceAudio", "Replace audio track", MediaCategory.Video, "Replace the audio stream while preserving the video when possible."),
        new("Subtitles", "Subtitle tools", MediaCategory.Video, "Extract/convert a text subtitle track, embed tracks, or drop subtitles."),
        new("VideoMetadata", "Video metadata", MediaCategory.Video, "Edit container metadata and optionally remove all metadata."),
        new("HdrToSdr", "HDR to SDR", MediaCategory.Video, "Tone-map HDR video to SDR BT.709 with zscale and hable."),
        new("ResizeFrameRate", "Resolution and frame rate", MediaCategory.Video, "Scale with Lanczos and optionally convert the frame rate.")
    ];

    public static IReadOnlyList<MediaToolDefinition> For(MediaCategory category) =>
        All.Where(t => t.Category == category).ToList();

    public static MediaToolDefinition? DefaultFor(MediaCategory category) =>
        All.FirstOrDefault(t => t.Category == category);

    public static bool IsCompatible(MediaCategory category, string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId) &&
        All.Any(t => t.Category == category && string.Equals(t.Id, toolId, StringComparison.Ordinal));
}
