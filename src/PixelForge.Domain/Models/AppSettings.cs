namespace PixelForge.Domain.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public GeneralSettings General { get; set; } = new();
    public ConverterDefaults Converter { get; set; } = new();
    public UpscalerDefaults Upscaler { get; set; } = new();
    public VideoUpscalerDefaults VideoUpscaler { get; set; } = new();
    public OcrDefaults Ocr { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        General = General.Clone(),
        Converter = Converter.Clone(),
        Upscaler = Upscaler.Clone(),
        VideoUpscaler = VideoUpscaler.Clone(),
        Ocr = Ocr.Clone(),
        Processing = Processing.Clone(),
        Storage = Storage.Clone(),
        Updates = Updates.Clone(),
        Window = Window.Clone()
    };
}

public sealed class GeneralSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Stable BCP-47 UI language. An empty value means the Windows language is
    /// detected once on first launch and then persisted.
    /// </summary>
    public string Language { get; set; } = "";
    public string AccentColorHex { get; set; } = "#D2693A";
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public string MonoFontFamily { get; set; } = "JetBrains Mono";
    public double FontScale { get; set; } = 1.0;
    public string DefaultOutputDirectory { get; set; } = "";
    public bool ShowNotifications { get; set; } = true;
    public bool ConfirmOverwrite { get; set; } = true;
    public bool RememberLastPage { get; set; } = true;
    public string LastPage { get; set; } = "Home";
    public bool OpenOutputAfterJob { get; set; }
    public bool OnboardingCompleted { get; set; }

    public GeneralSettings Clone() => (GeneralSettings)MemberwiseClone();
}

public sealed class ConverterDefaults
{
    public string ImageFormatId { get; set; } = "png";
    public int ImageQuality { get; set; } = 90;
    public bool ImagePreserveMetadata { get; set; } = true;
    public string AudioFormatId { get; set; } = "mp3";
    public int AudioBitrateKbps { get; set; } = 192;
    public string VideoContainerId { get; set; } = "mp4";
    public string VideoCodecId { get; set; } = "h264";
    public string VideoAudioCodecId { get; set; } = "aac";
    public ConversionPreset VideoPreset { get; set; } = ConversionPreset.Balanced;
    public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.UniqueName;

    /// <summary>
    /// Optional destination overrides keyed by stable output format id (for
    /// example "png" or "jpg"). A missing or empty value uses the general
    /// output directory.
    /// </summary>
    public Dictionary<string, string> FormatOutputDirectories { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ConverterDefaults Clone() => new()
    {
        ImageFormatId = ImageFormatId,
        ImageQuality = ImageQuality,
        ImagePreserveMetadata = ImagePreserveMetadata,
        AudioFormatId = AudioFormatId,
        AudioBitrateKbps = AudioBitrateKbps,
        VideoContainerId = VideoContainerId,
        VideoCodecId = VideoCodecId,
        VideoAudioCodecId = VideoAudioCodecId,
        VideoPreset = VideoPreset,
        CollisionPolicy = CollisionPolicy,
        FormatOutputDirectories = new Dictionary<string, string>(
            FormatOutputDirectories, StringComparer.OrdinalIgnoreCase)
    };
}

public sealed class UpscalerDefaults
{
    public UpscaleMethod Method { get; set; } = UpscaleMethod.Traditional;
    public ResamplingAlgorithm Algorithm { get; set; } = ResamplingAlgorithm.Lanczos;
    public string ModelId { get; set; } = "realesrgan-x4plus";
    public int ScaleFactor { get; set; } = 4;
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public int TileSize { get; set; }
    public int TileOverlap { get; set; }
    public string OutputFormatId { get; set; } = FormatIds.Auto;
    public int OutputQuality { get; set; } = 95;
    public bool PreserveMetadata { get; set; } = true;
    public bool IncludeSubfolders { get; set; }
    public bool PreserveFolderStructure { get; set; } = true;
    public string ModelDirectory { get; set; } = "";
    public string DefaultOutputDirectory { get; set; } = "";
    public string FileNameSuffix { get; set; } = "";

    public UpscalerDefaults Clone() => (UpscalerDefaults)MemberwiseClone();
}

public sealed class VideoUpscalerDefaults
{
    public bool EnableSuperResolution { get; set; } = true;
    public string ImageModelId { get; set; } = "realesrgan-x4plus";
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public bool EnableInterpolation { get; set; }
    public string InterpolationModelId { get; set; } = "rife-4.25-lite";
    public int FrameRateMultiplier { get; set; } = 2;
    public int VideoBitrateKbps { get; set; } = 12000;
    public bool EnableTemporalStability { get; set; }
    public int TemporalStabilityStrength { get; set; } = 50;
    public string FileNameSuffix { get; set; } = "_upscaled";
    public bool IncludeSubfolders { get; set; } = true;
    public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.UniqueName;
    public string DefaultOutputDirectory { get; set; } = "";

    public VideoUpscalerDefaults Clone() => (VideoUpscalerDefaults)MemberwiseClone();
}

public sealed class OcrDefaults
{
    public string ModelId { get; set; } = "ppocrv6-small";
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public bool GeneratePdf { get; set; }
    public bool Preprocess { get; set; } = true;
    public bool ReviewBeforeExport { get; set; }
    public string DefaultOutputDirectory { get; set; } = "";

    public OcrDefaults Clone() => (OcrDefaults)MemberwiseClone();
}

public sealed class ProcessingSettings
{    public int MaxConcurrentJobs { get; set; } = 2;
    public int GpuConcurrency { get; set; } = 1;
    public string TempDirectory { get; set; } = "";
    public bool CleanupTempOnExit { get; set; } = true;
    public bool KeepTempOnFailure { get; set; }
    public string LogLevel { get; set; } = "Information";

    public ProcessingSettings Clone() => (ProcessingSettings)MemberwiseClone();
}

public sealed class StorageSettings
{
    public int HistoryRetentionDays { get; set; } = 180;
    public int MaxHistoryEntries { get; set; } = 5000;
    public bool SaveJobHistory { get; set; } = true;
    public int ThumbnailCacheDays { get; set; } = 30;

    public StorageSettings Clone() => (StorageSettings)MemberwiseClone();
}

public sealed class UpdateSettings
{
    /// <summary>Automatic checks are opt-in; manual checks always work.</summary>
    public bool CheckOnStartup { get; set; }
    public bool IncludePreReleases { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastNotifiedVersion { get; set; }
    public string? CachedETag { get; set; }
    public string? CachedReleaseJson { get; set; }

    public UpdateSettings Clone() => (UpdateSettings)MemberwiseClone();
}

public sealed class WindowPlacement
{
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 900;
    public int Left { get; set; } = -1;
    public int Top { get; set; } = -1;
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 280;

    public WindowPlacement Clone() => (WindowPlacement)MemberwiseClone();
}
