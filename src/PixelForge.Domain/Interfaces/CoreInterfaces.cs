using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface IProcessingEngine
{
    string Id { get; }
    string DisplayName { get; }
    MediaCategory[] Categories { get; }
    Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default);
    Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default);
    Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath, CancellationToken cancellationToken = default);
    Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize, CancellationToken cancellationToken = default);
}

public interface ICapabilityRegistry
{
    bool IsInitialized { get; }
    IReadOnlyList<EngineCapability> Engines { get; }
    IReadOnlyList<MediaFormat> ImageOutputFormats { get; }
    IReadOnlyList<MediaFormat> AudioOutputFormats { get; }
    IReadOnlyList<MediaFormat> VideoOutputFormats { get; }
    IReadOnlyList<MediaFormat> DocumentOutputFormats { get; }
    IReadOnlyList<MediaFormat> EbookOutputFormats { get; }
    IReadOnlyList<MediaFormat> ArchiveOutputFormats { get; }
    IReadOnlyList<AlgorithmCapability> ResamplingAlgorithms { get; }
    IReadOnlyList<ExecutionProviderInfo> ExecutionProviders { get; }
    event EventHandler? CapabilitiesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<FormatOption> GetOutputFormats(MediaCategory category);
    IReadOnlyList<FormatOption> GetOutputFormats(OperationKind operation);
    MediaFormat? FindFormat(string idOrExtension, MediaCategory? category = null);
    MediaFormat? FindFormatByExtension(string extension, MediaCategory? category = null);
    IReadOnlyList<CodecCapability> GetVideoCodecs(string containerId);
    IReadOnlyList<CodecCapability> GetAudioCodecs(string containerId, bool forVideo);
    IReadOnlyList<EncoderCapability> GetEncoders(string codecId, string containerId);
    EncoderCapability? FindEncoder(string encoderId);
    CodecCapability? FindCodec(string codecId);
    EngineCapability? GetEngine(string engineId);
    EngineStatusInfo? GetEngineStatus(string engineId);
    bool IsVideoCodecCompatible(string containerId, string codecId);
    bool IsAudioCodecCompatible(string containerId, string codecId);
    bool CanCopyVideo(string containerId, string codecId);
    bool CanCopyAudio(string containerId, string codecId);
}

public interface IMediaInspector
{
    Task<MediaInfo> InspectAsync(string path, MediaCategory? categoryHint = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaInfo>> InspectManyAsync(IEnumerable<string> paths, MediaCategory? categoryHint = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    bool IsSupportedExtension(string path, MediaCategory category);
    IReadOnlyList<string> GetSupportedExtensions(MediaCategory category);
}

public interface IFolderScanner
{
    Task<FolderScanResult> ScanAsync(FolderScanRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFrameUpscaler
{
    Task<FrameUpscaleResult> UpscaleFrameAsync(byte[] rgb24, int width, int height, string modelId,
        ExecutionProviderKind provider, int tileSize, int tileOverlap, CancellationToken cancellationToken = default);
}

public interface IFrameInterpolator
{
    Task<FrameInterpolationResult> InterpolateFrameAsync(string modelPath, byte[] firstRgb24, byte[] secondRgb24,
        int width, int height, double timestep, ExecutionProviderKind provider,
        CancellationToken cancellationToken = default);
    Task<ModelValidationResult> ValidateModelAsync(string modelPath, ModelDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

public sealed record FrameUpscaleResult
{
    public required byte[] Rgb24 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ExecutionProviderKind Provider { get; init; }
    public string? Warning { get; init; }
}

public sealed record FrameInterpolationResult
{
    public required byte[] Rgb24 { get; init; }
    public required ExecutionProviderKind Provider { get; init; }
    public string? Warning { get; init; }
}

public interface IOutputPathService
{
    string BuildOutputPath(string inputPath, string outputDirectory, string extension, string suffix,
        string? relativeDirectory, CollisionPolicy collisionPolicy, out string? adjustmentNote,
        string? preferredBaseName = null);
    string EnsureUniquePath(string desiredPath);
    bool IsPathInside(string childPath, string parentPath);
}

public interface IFileNameService
{
    string SanitizeFileName(string name);
    string SanitizeSuffix(string suffix);
    string BuildDefaultSuffix(OperationSettings settings);
    string WithSuffix(string fileNameWithoutExtension, string suffix);
    string NormalizeExtension(string extension);
}

public interface IAppPaths
{
    string RootDirectory { get; }
    string SettingsFile { get; }
    string DatabaseFile { get; }
    string LogsDirectory { get; }
    string TempDirectory { get; }
    string CacheDirectory { get; }
    string ThumbnailCacheDirectory { get; }
    string DefaultOutputDirectory { get; }
    string DefaultUpscaleOutputDirectory { get; }
    string DefaultModelDirectory { get; }
    void EnsureCreated();
}

public interface ISettingsService
{
    AppSettings Settings { get; }
    event EventHandler<AppSettings>? SettingsChanged;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    string SettingsPath { get; }
}

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit = 200, string? search = null,
        OperationKind? operation = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<HistoryExportDocument> ExportAsync(HistoryQuery query, string appVersion,
        CancellationToken cancellationToken = default);
    Task<HistoryImportResult> ImportAsync(HistoryExportDocument document,
        CancellationToken cancellationToken = default);
    Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> TrimAsync(int maxEntries, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public interface IJobRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProcessingJob job, CancellationToken cancellationToken = default);
    Task SaveItemAsync(ProcessingJob job, JobItem item, CancellationToken cancellationToken = default);
    Task<ProcessingJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcessingJob>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteTerminalAsync(CancellationToken cancellationToken = default);
    Task<int> DeleteByStatesAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken = default);
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}

public interface IOperationSettingsSerializer
{
    string Serialize(OperationSettings settings);
    OperationSettings Deserialize(OperationKind operation, string json);
}
