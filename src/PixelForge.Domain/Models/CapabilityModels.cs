namespace PixelForge.Domain.Models;

public sealed record EncoderCapability
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string CodecId { get; init; }
    public required CodecKind Kind { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsHardware { get; init; }
    public RateControlCapabilities RateControl { get; init; }
    public string? QualityLabel { get; init; }
    public double? QualityMin { get; init; }
    public double? QualityMax { get; init; }
    public double? QualityDefault { get; init; }
    public bool SupportsPresets { get; init; }
    public IReadOnlyList<string> Presets { get; init; } = [];
    public string? DefaultPreset { get; init; }
    public int DimensionMultiple { get; init; } = 2;
    public IReadOnlyList<string> ContainerIds { get; init; } = [];
    public string? Notes { get; init; }
    public int Priority { get; init; }
    public string? UnavailableReason { get; init; }
}

public sealed record CodecCapability
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string TechnicalId { get; init; }
    public required CodecKind Kind { get; init; }
    public IReadOnlyList<string> ContainerIds { get; init; } = [];
    public IReadOnlyList<EncoderCapability> Encoders { get; init; } = [];
    public bool IsAvailable => Encoders.Any(e => e.IsAvailable);
}

public sealed record AlgorithmCapability
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool IsAi { get; init; }
}

public sealed record EngineCapability
{
    public required string EngineId { get; init; }
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    public bool IsAvailable { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<MediaFormat> Formats { get; init; } = [];
    public IReadOnlyList<CodecCapability> VideoCodecs { get; init; } = [];
    public IReadOnlyList<CodecCapability> AudioCodecs { get; init; } = [];
    public IReadOnlyList<AlgorithmCapability> Algorithms { get; init; } = [];
    public IReadOnlyList<ExecutionProviderInfo> ExecutionProviders { get; init; } = [];
    public DateTimeOffset ProbedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record FormatOption
{
    public required MediaFormat Format { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    public string Extension => Format.NormalizedExtension;
    public string DisplayName => Format.DisplayName;
    public MediaCategory Category => Format.Category;
    public bool SupportsAlpha => Format.SupportsAlpha;
    public bool SupportsAnimation => Format.SupportsAnimation;
    public bool IsLossy => Format.IsLossy;
    public string SearchText =>
        string.Join(' ', new[] { Format.Id, Format.Extension.TrimStart('.'), Format.DisplayName }
            .Concat(Format.Aliases))
        .ToLowerInvariant();
}

public sealed record ExecutionProviderInfo
{
    public required ExecutionProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsAvailable { get; init; }
    public string? DeviceName { get; init; }
    public string? Details { get; init; }
}

public sealed record EngineStatusInfo
{
    public required string EngineId { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsAvailable { get; init; }
    public string? Version { get; init; }
    public string? StatusText { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Details { get; init; } = [];
}
