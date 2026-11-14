namespace PixelForge.Domain.Models;

public sealed record ModelDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Architecture { get; init; }
    public required string IntendedUse { get; init; }
    public required int ScaleFactor { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string DownloadUrl { get; init; }
    public required string SourceUrl { get; init; }
    public required string LicenseName { get; init; }
    public required string LicenseUrl { get; init; }
    public string? SourceDescription { get; init; }
    public string? InputDescription { get; init; }
    public string? OutputDescription { get; init; }
    public bool SupportsDynamicInput { get; init; } = true;
    public int FixedInputWidth { get; init; }
    public int FixedInputHeight { get; init; }
    public int FixedTileOverlap { get; init; } = 8;
    public bool SupportsStillImage { get; init; } = true;
    public bool SupportsVideoFrames { get; init; } = true;
    public bool CpuOnly { get; init; }
    public string RuntimeBackend { get; init; } = "ONNX Runtime 1.24.4 · DirectML / CPU";
    public string ProviderSupport { get; init; } = "DirectML validated · CPU fallback";
    public string VerificationStatus { get; init; } = "ONNX graph and SHA-256 verified";
    public ModelBackendKind Backend { get; init; } = ModelBackendKind.OnnxRuntime;
    public string? RuntimeId { get; init; }
    public string? ModelDirectoryName { get; init; }
    public int DefaultNoiseLevel { get; init; }
    public IReadOnlyList<int> SupportedNoiseLevels { get; init; } = [];
    public IReadOnlyList<int> SupportedScales { get; init; } = [];
    public bool IsAnime { get; init; }
    public bool IsRecommended { get; init; }
    public ModelTask Task { get; init; } = ModelTask.ImageUpscale;
    public int InputSizeMultiple { get; init; } = 1;
    public int DefaultTileSize { get; init; } = 256;
    public int DefaultOverlap { get; init; } = 32;
    public int MinimumInputSize { get; init; } = 8;
    public IReadOnlyList<string> RequiredFiles { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Notes { get; init; }
    public int SortOrder { get; init; }
    public bool IsDownloadable => !string.IsNullOrWhiteSpace(DownloadUrl);
    public string DisplaySize => FileSizeBytes >= 1024L * 1024 * 1024
        ? $"{FileSizeBytes / 1024d / 1024 / 1024:0.0} GB"
        : $"{FileSizeBytes / 1024d / 1024:0.0} MB";
}

public sealed record InstalledModel
{
    public required ModelDescriptor Descriptor { get; init; }
    public required string FilePath { get; init; }
    public required long ActualSizeBytes { get; init; }
    public required string ActualSha256 { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public bool IsValid { get; init; }
    public string? ValidationError { get; init; }
    public int? VerifiedScaleFactor { get; init; }
    public string? VerifiedInputShape { get; init; }
    public string? VerifiedOutputShape { get; init; }
    public ModelAvailability Availability => IsValid ? ModelAvailability.Installed : ModelAvailability.Invalid;
    public string FileName => Path.GetFileName(FilePath);
}

public sealed record ModelDownloadProgress
{
    public required string ModelId { get; init; }
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
    public required string Stage { get; init; }
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

public sealed record ModelDownloadResult
{
    public required bool Success { get; init; }
    public InstalledModel? Model { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
}

public sealed record GpuInfo
{
    public required string Name { get; init; }
    public string? DriverVersion { get; init; }
    public long? AdapterMemoryBytes { get; init; }
    public string? Vendor { get; init; }
}

public sealed record SystemDiagnostics
{
    public required string AppVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required string CpuName { get; init; }
    public required int LogicalCoreCount { get; init; }
    public required long TotalMemoryBytes { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<EngineStatusInfo> Engines { get; init; } = [];
    public IReadOnlyList<ExecutionProviderInfo> ExecutionProviders { get; init; } = [];
    public IReadOnlyList<InstalledModel> InstalledModels { get; init; } = [];
    public IReadOnlyList<string> AvailableVideoCodecs { get; init; } = [];
    public IReadOnlyList<string> AvailableAudioCodecs { get; init; } = [];
    public IReadOnlyList<string> AvailableImageFormats { get; init; } = [];
    public required string SettingsPath { get; init; }
    public required string DatabasePath { get; init; }
    public required string LogsPath { get; init; }
    public required string ModelDirectory { get; init; }
    public required string TempDirectory { get; init; }
    public string? AiRuntimeVersion { get; init; }
}
