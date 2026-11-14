using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelForge.Application.Capabilities;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<IProcessingEngine> _engines;
    private readonly ILogger<CapabilityRegistry> _logger;
    private readonly IAppPaths? _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initializationLock = new();
    private Task? _initializationTask;
    private RegistryState _state = RegistryState.Empty;

    public CapabilityRegistry(IEnumerable<IProcessingEngine> engines, ILogger<CapabilityRegistry> logger)
        : this(engines, logger, null)
    {
    }

    public CapabilityRegistry(IEnumerable<IProcessingEngine> engines, ILogger<CapabilityRegistry> logger,
        IAppPaths? paths)
    {
        _engines = engines.ToList();
        _logger = logger;
        _paths = paths;
    }

    public bool IsInitialized => _state.IsInitialized;
    public IReadOnlyList<EngineCapability> Engines => _state.EngineCapabilities;
    public IReadOnlyList<MediaFormat> ImageOutputFormats => _state.ImageOutputFormats;
    public IReadOnlyList<MediaFormat> AudioOutputFormats => _state.AudioOutputFormats;
    public IReadOnlyList<MediaFormat> VideoOutputFormats => _state.VideoOutputFormats;
    public IReadOnlyList<MediaFormat> DocumentOutputFormats => _state.DocumentOutputFormats;
    public IReadOnlyList<MediaFormat> EbookOutputFormats => _state.EbookOutputFormats;
    public IReadOnlyList<MediaFormat> ArchiveOutputFormats => _state.ArchiveOutputFormats;
    public IReadOnlyList<AlgorithmCapability> ResamplingAlgorithms => _state.Algorithms;
    public IReadOnlyList<ExecutionProviderInfo> ExecutionProviders => _state.ExecutionProviders;

    public event EventHandler? CapabilitiesChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_state.IsInitialized) return Task.CompletedTask;
        lock (_initializationLock)
        {
            if (_state.IsInitialized) return Task.CompletedTask;
            return _initializationTask ??= InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (TryLoadCache(out var cached))
            {
                _state = BuildState(cached);
                _logger.LogInformation("Capability registry restored from cache with {Count} engines", cached.Count);
                CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
                _ = RefreshInBackgroundAsync(cancellationToken);
                return;
            }

            await RefreshAsync(cancellationToken);
        }
        finally
        {
            lock (_initializationLock) _initializationTask = null;
        }
    }

    private async Task RefreshInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await RefreshAsync(cancellationToken);
            _logger.LogInformation("Capability registry refreshed in the background");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background capability refresh failed");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Probing {Count} processing engines", _engines.Count);
            var probes = _engines.Select(async engine =>
            {
                var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var capability = await engine.ProbeAsync(cancellationToken);
                    var probeMs = System.Diagnostics.Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;
                    if (probeMs >= 50)
                        _logger.LogInformation("Engine probe {Engine} completed in {Elapsed:0.0} ms", engine.Id, probeMs);
                    return capability;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Engine {Engine} capability probe failed", engine.Id);
                    return new EngineCapability
                    {
                        EngineId = engine.Id,
                        DisplayName = engine.DisplayName,
                        IsAvailable = false,
                        Error = ex.Message
                    };
                }
            }).ToList();

            var capabilities = await Task.WhenAll(probes);
            _state = BuildState(capabilities);
            SaveCache(capabilities);
            _logger.LogInformation("Capability registry ready: {Formats} output formats, {VideoCodecs} video codecs, {AudioCodecs} audio codecs",
                _state.ImageOutputFormats.Count + _state.AudioOutputFormats.Count + _state.VideoOutputFormats.Count,
                _state.VideoCodecs.Count, _state.AudioCodecs.Count);
        }
        finally
        {
            _gate.Release();
        }

        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FormatOption> GetOutputFormats(MediaCategory category)
    {
        var formats = category switch
        {
            MediaCategory.Image => _state.ImageOutputFormats,
            MediaCategory.Audio => _state.AudioOutputFormats,
            MediaCategory.Video => _state.VideoOutputFormats,
            MediaCategory.Document => _state.DocumentOutputFormats,
            MediaCategory.Ebook => _state.EbookOutputFormats,
            MediaCategory.Archive => _state.ArchiveOutputFormats,
            _ => []
        };
        return formats.OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .Select(f => new FormatOption { Format = f })
            .ToList();
    }

    public IReadOnlyList<FormatOption> GetOutputFormats(OperationKind operation) =>
        GetOutputFormats(operation switch
        {
            OperationKind.ImageConvert or OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => MediaCategory.Image,
            OperationKind.AudioConvert => MediaCategory.Audio,
            OperationKind.VideoConvert => MediaCategory.Video,
            OperationKind.DocumentConvert => MediaCategory.Document,
            OperationKind.EbookConvert => MediaCategory.Ebook,
            OperationKind.ArchiveConvert => MediaCategory.Archive,
            OperationKind.AudioProcess => MediaCategory.Audio,
            OperationKind.ImageProcess => MediaCategory.Image,
            OperationKind.VideoProcess => MediaCategory.Video,
            OperationKind.VideoUpscale => MediaCategory.Video,
            _ => MediaCategory.Unknown
        });

    public MediaFormat? FindFormat(string idOrExtension, MediaCategory? category = null)
    {
        if (string.IsNullOrWhiteSpace(idOrExtension)) return null;
        var value = idOrExtension.Trim();
        var candidates = _state.AllFormats.Where(f => category is null || f.Category == category);
        return candidates.FirstOrDefault(f =>
                   string.Equals(f.Id, value.TrimStart('.'), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(f.NormalizedExtension, value, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(f =>
                   string.Equals(f.Id, value.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    public MediaFormat? FindFormatByExtension(string extension, MediaCategory? category = null)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return _state.AllFormats.FirstOrDefault(f =>
            (category is null || f.Category == category) &&
            string.Equals(f.NormalizedExtension, ext, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CodecCapability> GetVideoCodecs(string containerId)
    {
        var format = _state.AllFormats.FirstOrDefault(f =>
            f.Category == MediaCategory.Video && string.Equals(f.Id, containerId, StringComparison.OrdinalIgnoreCase));
        if (format is null || !format.CanWrite) return [];
        return FilterVideoCodecs(containerId);
    }

    public IReadOnlyList<CodecCapability> GetAudioCodecs(string containerId, bool forVideo)
    {
        var category = forVideo || _state.AllFormats.Any(f => f.Category == MediaCategory.Video &&
                        string.Equals(f.Id, containerId, StringComparison.OrdinalIgnoreCase))
            ? MediaCategory.Video
            : MediaCategory.Audio;
        var format = _state.AllFormats.FirstOrDefault(f =>
            f.Category == category && string.Equals(f.Id, containerId, StringComparison.OrdinalIgnoreCase));
        if (format is null || !format.CanWrite) return [];
        if (forVideo && !CompatibilityMatrix.ContainerSupportsAudio(containerId)) return [];
        return FilterAudioCodecs(containerId);
    }

    public IReadOnlyList<EncoderCapability> GetEncoders(string codecId, string containerId) =>
        _state.VideoCodecs.Concat(_state.AudioCodecs)
            .FirstOrDefault(c => string.Equals(c.Id, codecId, StringComparison.OrdinalIgnoreCase))
            ?.Encoders
            .Where(e => e.IsAvailable && e.ContainerIds.Contains(containerId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(e => e.Priority)
            .ToList() ?? [];

    public EncoderCapability? FindEncoder(string encoderId) =>
        _state.AllEncoders.FirstOrDefault(e => string.Equals(e.Id, encoderId, StringComparison.OrdinalIgnoreCase));

    public CodecCapability? FindCodec(string codecId) =>
        _state.VideoCodecs.Concat(_state.AudioCodecs)
            .FirstOrDefault(c => string.Equals(c.Id, codecId, StringComparison.OrdinalIgnoreCase));

    public EngineCapability? GetEngine(string engineId) =>
        _state.EngineCapabilities.FirstOrDefault(e => string.Equals(e.EngineId, engineId, StringComparison.OrdinalIgnoreCase));

    public EngineStatusInfo? GetEngineStatus(string engineId)
    {
        var engine = GetEngine(engineId);
        if (engine is null) return null;
        return new EngineStatusInfo
        {
            EngineId = engine.EngineId,
            DisplayName = engine.DisplayName,
            IsAvailable = engine.IsAvailable,
            Version = engine.Version,
            StatusText = engine.IsAvailable ? "Ready" : "Unavailable",
            Error = engine.Error,
            Details =
            [
                $"{engine.Formats.Count(f => f.CanRead)} input formats",
                $"{engine.Formats.Count(f => f.CanWrite)} output formats",
                $"{engine.VideoCodecs.Count(c => c.IsAvailable)} video codecs",
                $"{engine.AudioCodecs.Count(c => c.IsAvailable)} audio codecs"
            ]
        };
    }

    public bool IsVideoCodecCompatible(string containerId, string codecId) =>
        CompatibilityMatrix.IsVideoCodecCompatible(containerId, codecId);

    public bool IsAudioCodecCompatible(string containerId, string codecId) =>
        CompatibilityMatrix.IsAudioCodecCompatible(containerId, codecId);

    public bool CanCopyVideo(string containerId, string codecId) =>
        CompatibilityMatrix.CanCopyVideo(containerId, codecId);

    public bool CanCopyAudio(string containerId, string codecId) =>
        CompatibilityMatrix.CanCopyAudio(containerId, codecId);

    private IReadOnlyList<CodecCapability> FilterVideoCodecs(string containerId)
    {
        var list = new List<CodecCapability>();
        foreach (var codec in _state.VideoCodecs.OrderBy(c => c.DisplayName))
        {
            if (!CompatibilityMatrix.IsVideoCodecCompatible(containerId, codec.Id)) continue;
            var encoders = codec.Encoders
                .Where(e => e.IsAvailable && e.Kind == CodecKind.Video &&
                            e.ContainerIds.Contains(containerId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(e => e.Priority)
                .ToList();
            if (encoders.Count == 0) continue;
            list.Add(new CodecCapability
            {
                Id = codec.Id,
                DisplayName = codec.DisplayName,
                TechnicalId = codec.TechnicalId,
                Kind = codec.Kind,
                ContainerIds = [containerId],
                Encoders = encoders
            });
        }

        return list;
    }

    private IReadOnlyList<CodecCapability> FilterAudioCodecs(string containerId)
    {
        var list = new List<CodecCapability>();
        foreach (var codec in _state.AudioCodecs.OrderBy(c => c.DisplayName))
        {
            if (!CompatibilityMatrix.IsAudioCodecCompatible(containerId, codec.Id)) continue;
            var encoders = codec.Encoders
                .Where(e => e.IsAvailable && e.Kind == CodecKind.Audio &&
                            e.ContainerIds.Contains(containerId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(e => e.Priority)
                .ToList();
            if (encoders.Count == 0) continue;
            list.Add(new CodecCapability
            {
                Id = codec.Id,
                DisplayName = codec.DisplayName,
                TechnicalId = codec.TechnicalId,
                Kind = codec.Kind,
                ContainerIds = [containerId],
                Encoders = encoders
            });
        }

        return list;
    }

    private string? CachePath => _paths is null
        ? null
        : Path.Combine(_paths.CacheDirectory, "engine-capabilities.json");

    private static string CacheKey()
    {
        var assembly = typeof(CapabilityRegistry).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "";
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                return $"{version}|{File.GetLastWriteTimeUtc(location).Ticks}";
        }
        catch
        {
            // Fall back to the assembly version.
        }

        return version;
    }

    private bool TryLoadCache(out IReadOnlyList<EngineCapability> engines)
    {
        engines = [];
        try
        {
            if (CachePath is not { } path || !File.Exists(path)) return false;
            var cache = JsonSerializer.Deserialize<CapabilityCache>(File.ReadAllText(path), CacheJson);
            if (cache is null || cache.Schema != 1 || cache.Engines.Count == 0) return false;
            if (!string.Equals(cache.Version, CacheKey(), StringComparison.Ordinal)) return false;
            if (DateTimeOffset.Now - cache.SavedAt > TimeSpan.FromDays(7)) return false;
            engines = cache.Engines;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capability cache could not be read");
            return false;
        }
    }

    private void SaveCache(IReadOnlyList<EngineCapability> engines)
    {
        try
        {
            if (CachePath is not { } path) return;
            Directory.CreateDirectory(_paths!.CacheDirectory);
            var cache = new CapabilityCache
            {
                Schema = 1,
                Version = CacheKey(),
                SavedAt = DateTimeOffset.Now,
                Engines = engines.ToList()
            };
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, CacheJson));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capability cache could not be written");
        }
    }

    private sealed class CapabilityCache
    {
        public int Schema { get; set; }
        public string Version { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
        public List<EngineCapability> Engines { get; set; } = [];
    }

    private RegistryState BuildState(IReadOnlyList<EngineCapability> engineCaps)
    {
        var allFormats = engineCaps
            .Where(e => e.IsAvailable)
            .SelectMany(e => e.Formats)
            .GroupBy(f => (f.Category, f.Id))
            .Select(g => g.OrderByDescending(f => f.CanWrite).First())
            .ToList();

        var imageFormats = allFormats
            .Where(f => f.Category == MediaCategory.Image && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var audioFormats = allFormats
            .Where(f => f.Category == MediaCategory.Audio && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var videoFormats = allFormats
            .Where(f => f.Category == MediaCategory.Video && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var documentFormats = allFormats
            .Where(f => f.Category == MediaCategory.Document && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var ebookFormats = allFormats
            .Where(f => f.Category == MediaCategory.Ebook && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var archiveFormats = allFormats
            .Where(f => f.Category == MediaCategory.Archive && f.CanWrite)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.DisplayName)
            .ToList();

        var videoCodecs = engineCaps
            .Where(e => e.IsAvailable)
            .SelectMany(e => e.VideoCodecs)
            .Where(c => c.Encoders.Any(en => en.IsAvailable))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => MergeCodecs(g.ToList()))
            .ToList();

        var audioCodecs = engineCaps
            .Where(e => e.IsAvailable)
            .SelectMany(e => e.AudioCodecs)
            .Where(c => c.Encoders.Any(en => en.IsAvailable))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => MergeCodecs(g.ToList()))
            .ToList();

        // Only advertise an audio/video container if at least one usable encoder can write it.
        audioFormats = audioFormats
            .Where(f => audioCodecs.Any(c => c.Encoders.Any(e => e.IsAvailable &&
                e.ContainerIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase) &&
                CompatibilityMatrix.IsAudioCodecCompatible(f.Id, c.Id))))
            .ToList();

        videoFormats = videoFormats
            .Where(f => string.Equals(f.Id, "gif", StringComparison.OrdinalIgnoreCase)
                ? videoCodecs.Any(c => c.Id == "gif" && c.IsAvailable)
                : videoCodecs.Any(c => c.Encoders.Any(e => e.IsAvailable &&
                    e.ContainerIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase) &&
                    CompatibilityMatrix.IsVideoCodecCompatible(f.Id, c.Id))))
            .ToList();

        var algorithms = engineCaps.Where(e => e.IsAvailable)
            .SelectMany(e => e.Algorithms)
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (algorithms.Count == 0)
        {
            algorithms = DefaultAlgorithms().ToList();
        }

        var providers = engineCaps.Where(e => e.IsAvailable)
            .SelectMany(e => e.ExecutionProviders)
            .GroupBy(p => p.Kind)
            .Select(g => g.OrderByDescending(p => p.IsAvailable).First())
            .ToList();

        if (providers.Count == 0)
        {
            providers =
            [
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = true,
                    Details = "Built-in fallback"
                }
            ];
        }

        var allEncoders = videoCodecs.Concat(audioCodecs).SelectMany(c => c.Encoders)
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new RegistryState
        {
            IsInitialized = true,
            EngineCapabilities = engineCaps,
            AllFormats = allFormats,
            ImageOutputFormats = imageFormats,
            AudioOutputFormats = audioFormats,
            VideoOutputFormats = videoFormats,
            DocumentOutputFormats = documentFormats,
            EbookOutputFormats = ebookFormats,
            ArchiveOutputFormats = archiveFormats,
            VideoCodecs = videoCodecs,
            AudioCodecs = audioCodecs,
            AllEncoders = allEncoders,
            Algorithms = algorithms,
            ExecutionProviders = providers
        };
    }

    private static CodecCapability MergeCodecs(IReadOnlyList<CodecCapability> codecs)
    {
        var first = codecs[0];
        return new CodecCapability
        {
            Id = first.Id,
            DisplayName = first.DisplayName,
            TechnicalId = first.TechnicalId,
            Kind = first.Kind,
            ContainerIds = codecs.SelectMany(c => c.ContainerIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Encoders = codecs.SelectMany(c => c.Encoders)
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.IsAvailable).First())
                .OrderBy(e => e.Priority)
                .ToList()
        };
    }

    public static IReadOnlyList<AlgorithmCapability> DefaultAlgorithms() =>
    [
        new() { Id = "nearest", DisplayName = "Nearest Neighbor", Description = "Fastest; preserves hard pixel edges and is ideal for pixel art." },
        new() { Id = "bilinear", DisplayName = "Bilinear", Description = "Smooth and fast; can soften fine detail." },
        new() { Id = "bicubic", DisplayName = "Bicubic", Description = "Balanced smoothing and sharpness for photographs." },
        new() { Id = "lanczos", DisplayName = "Lanczos", Description = "High-quality sharpening with minimal ringing; a strong general default." },
        new() { Id = "mitchell", DisplayName = "Mitchell-Netravali", Description = "Soft, natural results with reduced halos and ringing." }
    ];

    private sealed record RegistryState
    {
        public static readonly RegistryState Empty = new();

        public bool IsInitialized { get; init; }
        public IReadOnlyList<EngineCapability> EngineCapabilities { get; init; } = [];
        public IReadOnlyList<MediaFormat> AllFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> ImageOutputFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> AudioOutputFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> VideoOutputFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> DocumentOutputFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> EbookOutputFormats { get; init; } = [];
        public IReadOnlyList<MediaFormat> ArchiveOutputFormats { get; init; } = [];
        public IReadOnlyList<CodecCapability> VideoCodecs { get; init; } = [];
        public IReadOnlyList<CodecCapability> AudioCodecs { get; init; } = [];
        public IReadOnlyList<EncoderCapability> AllEncoders { get; init; } = [];
        public IReadOnlyList<AlgorithmCapability> Algorithms { get; init; } = DefaultAlgorithms();
        public IReadOnlyList<ExecutionProviderInfo> ExecutionProviders { get; init; } = [];
    }
}
