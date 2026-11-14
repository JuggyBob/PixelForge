namespace PixelForge.Domain.Models;

public sealed record MediaStreamInfo
{
    public required int Index { get; init; }
    public required string Type { get; init; }
    public string? Codec { get; init; }
    public string? CodecLongName { get; init; }
    public string? Profile { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public long? BitRate { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsAttachedPicture { get; init; }
}

public sealed record MediaInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public long FileSizeBytes { get; init; }
    public MediaCategory Category { get; init; }
    public string? FormatId { get; init; }
    public string? FormatName { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }
    public double? FrameRate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public long? AudioBitRate { get; init; }
    public long? VideoBitRate { get; init; }
    public long? TotalBitRate { get; init; }
    public bool HasAlpha { get; init; }
    public bool IsAnimated { get; init; }
    public bool IsMultipage { get; init; }
    public bool IsVector { get; init; }
    public int? FrameCount { get; init; }
    public int? SubtitleStreamCount { get; init; }
    public int? ChapterCount { get; init; }
    public int? RotationDegrees { get; init; }
    public string? ColorSpace { get; init; }
    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = [];
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public string? Error { get; init; }
    public string? EngineId { get; init; }

    public bool IsReadable => string.IsNullOrWhiteSpace(Error);
    public string DisplayDimensions => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "—";
}

public sealed record MediaFormat
{
    public required string Id { get; init; }
    public required string Extension { get; init; }
    public required string DisplayName { get; init; }
    public required MediaCategory Category { get; init; }
    public required string EngineId { get; init; }
    public string? TechnicalId { get; init; }
    public string? MuxerName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public bool CanRead { get; init; }
    public bool CanWrite { get; init; }
    public bool SupportsAnimation { get; init; }
    public bool SupportsMultipage { get; init; }
    public bool SupportsAlpha { get; init; }
    public bool SupportsMetadata { get; init; }
    public bool SupportsIccProfile { get; init; }
    public bool IsLossy { get; init; }
    public bool IsVector { get; init; }
    public bool IsContainer { get; init; }
    public int SortOrder { get; init; }
    public string? UnavailableReason { get; init; }

    public string NormalizedExtension => Extension.StartsWith('.') ? Extension : "." + Extension;
}
