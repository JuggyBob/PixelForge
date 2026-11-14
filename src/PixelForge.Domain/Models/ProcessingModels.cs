namespace PixelForge.Domain.Models;

public sealed record ProcessingRequest
{
    public required Guid JobId { get; init; }
    public required Guid ItemId { get; init; }
    public required OperationKind Operation { get; init; }
    public required OperationSettings Settings { get; init; }
    public required string InputPath { get; init; }
    public required string TempOutputPath { get; init; }
    public required string OutputFormatId { get; init; }
    public required string TempDirectory { get; init; }
    public MediaInfo? InputInfo { get; init; }
    public string? RelativeDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

public sealed record ProcessingProgress
{
    public double Fraction { get; init; }
    public string? Message { get; init; }
    public string? Speed { get; init; }
    public double? EtaSeconds { get; init; }
    public int? CurrentFrame { get; init; }
    public int? TotalFrames { get; init; }

    public static ProcessingProgress Indeterminate(string? message = null) => new() { Fraction = -1, Message = message };
    public static ProcessingProgress Percent(double fraction, string? message = null) =>
        new() { Fraction = Math.Clamp(fraction, 0, 1), Message = message };
}

public sealed record ProcessingResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public MediaInfo? OutputInfo { get; init; }
    public string? Error { get; init; }
    public string? ErrorDetails { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public long? OutputBytes { get; init; }
    public VerificationResult? Verification { get; init; }
    public IReadOnlyList<string> AdditionalOutputPaths { get; init; } = [];
    public string? EngineLog { get; init; }
    public bool IsDirectoryOutput { get; init; }

    public static ProcessingResult Ok(string outputPath, MediaInfo? info, long outputBytes, TimeSpan duration,
        VerificationResult? verification = null, IReadOnlyList<string>? warnings = null) => new()
    {
        Success = true,
        OutputPath = outputPath,
        OutputInfo = info,
        OutputBytes = outputBytes,
        Duration = duration,
        Verification = verification,
        Warnings = warnings ?? []
    };

    public static ProcessingResult Fail(string error, string? details = null, TimeSpan duration = default,
        string? engineLog = null) => new()
    {
        Success = false,
        Error = error,
        ErrorDetails = details,
        Duration = duration,
        EngineLog = engineLog
    };
}

public sealed record FolderScanRequest
{
    public required string FolderPath { get; init; }
    public bool Recursive { get; init; }
    public bool PreserveFolderStructure { get; init; } = true;
    public MediaCategory TargetCategory { get; init; } = MediaCategory.Image;
    public bool RejectAnimated { get; init; }
    public bool RejectVector { get; init; }
    /// <summary>Optional output folder that must not be scanned recursively.</summary>
    public string? ExcludeDirectory { get; init; }
}

public sealed record ScannedFile
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public string? RelativeDirectory { get; init; }
    public long SizeBytes { get; init; }
    public MediaCategory DetectedCategory { get; init; }
    public bool IsSupported { get; init; }
    public bool IsValid { get; init; }
    public string? Reason { get; init; }
    public MediaInfo? Info { get; init; }
}

public sealed record FolderScanResult
{
    public required string RootPath { get; init; }
    public bool Recursive { get; init; }
    public MediaCategory TargetCategory { get; init; } = MediaCategory.Image;
    public IReadOnlyList<ScannedFile> Files { get; init; } = [];
    public IReadOnlyList<ScannedFile> IncludedFiles => Files.Where(f => f.IsSupported && f.IsValid).ToList();
    public int DetectedCount => Files.Count;
    public int IncludedCount => IncludedFiles.Count;
    public int IneligibleCount => Files.Count(f => f.DetectedCategory != TargetCategory && !f.IsSupported);
    public int UnsupportedCount => Files.Count(f =>
        f.DetectedCategory == TargetCategory && !f.IsSupported);
    public int InvalidCount => Files.Count(f => f.IsSupported && !f.IsValid);
    public int NonImageSkippedCount => TargetCategory == MediaCategory.Image ? IneligibleCount : 0;
    public TimeSpan Duration { get; init; }
    public string? Warning { get; init; }
    public string CategoryNoun => TargetCategory switch
    {
        MediaCategory.Image => "image",
        MediaCategory.Audio => "audio file",
        MediaCategory.Video => "video",
        MediaCategory.Document => "document",
        MediaCategory.Ebook => "e-book",
        MediaCategory.Archive => "archive",
        _ => "file"
    };
    public string Summary =>
        $"{IncludedCount} {CategoryNoun}{(IncludedCount == 1 ? "" : "s")} ready · " +
        $"{IneligibleCount} other file{(IneligibleCount == 1 ? "" : "s")} ignored · " +
        $"{UnsupportedCount} unsupported · {InvalidCount} invalid";
}

public sealed record DiscoveredFile
{
    public required string Path { get; init; }
    public MediaInfo? Info { get; init; }
    public string? Error { get; init; }
    public MediaCategory DetectedCategory { get; init; }
    public bool IsSupported { get; init; }
    public string? Reason { get; init; }
}
