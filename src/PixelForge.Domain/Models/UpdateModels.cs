namespace PixelForge.Domain.Models;

public sealed record UpdateAssetInfo
{
    public required string Name { get; init; }
    public required string DownloadUrl { get; init; }
    public long Size { get; init; }
    public bool IsInstaller { get; init; }
    public bool IsPortable { get; init; }
}

public sealed record UpdateReleaseInfo
{
    public required string TagName { get; init; }
    public required string Version { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public required string HtmlUrl { get; init; }
    public string Body { get; init; } = "";
    public bool IsPrerelease { get; init; }
    public IReadOnlyList<UpdateAssetInfo> Assets { get; init; } = [];
}

public enum UpdateCheckOutcome
{
    UpToDate,
    InternalBuildUpToDate,
    UpdateAvailable,
    PreReleaseAvailable,
    NoReleaseFound,
    NoCompatibleAsset,
    RateLimited,
    NetworkUnavailable,
    ServerError,
    InvalidMetadata,
    Failed
}

public sealed record UpdateCheckResult
{
    public required UpdateCheckOutcome Outcome { get; init; }
    public required string CurrentVersion { get; init; }
    public UpdateReleaseInfo? Release { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record HistoryQuery
{
    public int Limit { get; init; } = 200;
    public string? Search { get; init; }
    public OperationKind? Operation { get; init; }
    public JobItemState? Status { get; init; }
    public string? FormatId { get; init; }
    public string? ModelId { get; init; }
    public string? Provider { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public sealed record HistoryExportDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<HistoryEntry> Entries { get; init; } = [];
}

public sealed record HistoryImportResult
{
    public int Imported { get; init; }
    public int Skipped { get; init; }
    public int Invalid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
