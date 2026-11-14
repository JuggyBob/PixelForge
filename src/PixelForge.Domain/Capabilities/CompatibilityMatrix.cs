namespace PixelForge.Domain.Capabilities;

public static class CompatibilityMatrix
{
    private static readonly Dictionary<string, HashSet<string>> VideoStreams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "av1", "mpeg4" },
        ["mkv"] = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "av1", "vp9", "vp8", "mpeg4", "prores" },
        ["mov"] = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "prores", "mpeg4" },
        ["webm"] = new(StringComparer.OrdinalIgnoreCase) { "vp8", "vp9", "av1" },
        ["avi"] = new(StringComparer.OrdinalIgnoreCase) { "mpeg4" },
        ["gif"] = new(StringComparer.OrdinalIgnoreCase) { "gif" }
    };

    private static readonly Dictionary<string, HashSet<string>> AudioStreams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "alac" },
        ["m4a"] = new(StringComparer.OrdinalIgnoreCase) { "aac", "alac" },
        ["mkv"] = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "flac", "opus", "vorbis", "pcm_s16le", "alac" },
        ["mov"] = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "pcm_s16le", "alac" },
        ["webm"] = new(StringComparer.OrdinalIgnoreCase) { "opus", "vorbis" },
        ["avi"] = new(StringComparer.OrdinalIgnoreCase) { "mp3", "pcm_s16le" },
        ["gif"] = new(StringComparer.OrdinalIgnoreCase) { },
        ["mp3"] = new(StringComparer.OrdinalIgnoreCase) { "mp3" },
        ["wav"] = new(StringComparer.OrdinalIgnoreCase) { "pcm_s16le" },
        ["flac"] = new(StringComparer.OrdinalIgnoreCase) { "flac" },
        ["aac"] = new(StringComparer.OrdinalIgnoreCase) { "aac" },
        ["ogg"] = new(StringComparer.OrdinalIgnoreCase) { "vorbis", "opus" },
        ["opus"] = new(StringComparer.OrdinalIgnoreCase) { "opus" }
    };

    public static bool IsVideoCodecCompatible(string containerId, string codecId) =>
        VideoStreams.TryGetValue(containerId, out var set) && set.Contains(codecId);

    public static bool IsAudioCodecCompatible(string containerId, string codecId) =>
        AudioStreams.TryGetValue(containerId, out var set) && set.Contains(codecId);

    public static bool CanCopyVideo(string containerId, string codecId) => IsVideoCodecCompatible(containerId, codecId);

    public static bool CanCopyAudio(string containerId, string codecId) => IsAudioCodecCompatible(containerId, codecId);

    public static IReadOnlySet<string> GetCompatibleVideoCodecs(string containerId) =>
        VideoStreams.TryGetValue(containerId, out var set) ? set : new HashSet<string>();

    public static IReadOnlySet<string> GetCompatibleAudioCodecs(string containerId) =>
        AudioStreams.TryGetValue(containerId, out var set) ? set : new HashSet<string>();

    public static string GetDefaultVideoCodec(string containerId) => containerId.ToLowerInvariant() switch
    {
        "webm" => "vp9",
        "avi" => "mpeg4",
        "gif" => "gif",
        _ => "h264"
    };

    public static string GetDefaultAudioCodec(string containerId) => containerId.ToLowerInvariant() switch
    {
        "webm" => "opus",
        "ogg" => "vorbis",
        "opus" => "opus",
        "flac" => "flac",
        "wav" => "pcm_s16le",
        "avi" => "mp3",
        "mp3" => "mp3",
        "aac" => "aac",
        "m4a" => "aac",
        _ => "aac"
    };

    public static bool ContainerSupportsAudio(string containerId) =>
        !string.Equals(containerId, "gif", StringComparison.OrdinalIgnoreCase);

    public static bool ContainerSupportsSubtitles(string containerId) => containerId.ToLowerInvariant() switch
    {
        "mp4" => true,
        "mkv" => true,
        "mov" => true,
        "avi" => false,
        "webm" => true,
        "gif" => false,
        _ => false
    };

    public static bool ContainerSupportsChapters(string containerId) => containerId.ToLowerInvariant() switch
    {
        "mp4" => true,
        "mkv" => true,
        "mov" => true,
        "webm" => true,
        "avi" => false,
        "gif" => false,
        _ => false
    };

    public static bool ContainerSupportsFastStart(string containerId) =>
        containerId.Equals("mp4", StringComparison.OrdinalIgnoreCase) ||
        containerId.Equals("mov", StringComparison.OrdinalIgnoreCase) ||
        containerId.Equals("m4a", StringComparison.OrdinalIgnoreCase);
}
