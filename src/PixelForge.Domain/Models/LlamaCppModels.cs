namespace PixelForge.Domain.Models;

public sealed record LlamaCppRuntimeDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string InstallDirectoryName { get; init; }
    public required string ExecutableRelativePath { get; init; }
    public required string LicenseName { get; init; }
    public required string LicenseUrl { get; init; }
    public required string SourceUrl { get; init; }
    public IReadOnlyList<string> RequiredRelativePaths { get; init; } = [];
    public string? Notes { get; init; }
    public string DisplaySize => FileSizeBytes >= 1024L * 1024 * 1024
        ? $"{FileSizeBytes / 1024d / 1024 / 1024:0.0} GB"
        : $"{FileSizeBytes / 1024d / 1024:0.0} MB";
}

public sealed record InstalledLlamaCppRuntime
{
    public required LlamaCppRuntimeDescriptor Descriptor { get; init; }
    public required string InstallDirectory { get; init; }
    public required string ExecutablePath { get; init; }
    public required long SizeBytes { get; init; }
    public required string ActualSha256 { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public bool IsValid { get; init; }
    public string? ValidationError { get; init; }
}

public sealed record LlamaCppDownloadProgress
{
    public required string RuntimeId { get; init; }
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
    public required string Stage { get; init; }
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

public sealed record LlamaCppDownloadResult
{
    public required bool Success { get; init; }
    public InstalledLlamaCppRuntime? Runtime { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
}

public sealed record LlamaOcrResult
{
    public required bool Success { get; init; }
    public string Markdown { get; init; } = "";
    public string Provider { get; init; } = "llama.cpp";
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
    public TimeSpan Duration { get; init; }
}
