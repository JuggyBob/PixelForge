using PixelForge.Domain.Models;

namespace PixelForge.Domain.Models;

public sealed class WatchFolderRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Watched folder";
    public string InputDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public bool IncludeSubdirectories { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public OperationKind Operation { get; set; } = OperationKind.ImageConvert;
    public OperationSettings Settings { get; set; } = new ImageConversionSettings();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public string? LastError { get; set; }

    public string OperationLabel => Operation switch
    {
        OperationKind.ImageConvert => "Image conversion",
        OperationKind.AudioConvert => "Audio conversion",
        OperationKind.VideoConvert => "Video conversion",
        OperationKind.UpscaleTraditional => "Image upscaling",
        OperationKind.UpscaleAi => "AI image upscaling",
        OperationKind.DocumentConvert => "Document conversion",
        OperationKind.EbookConvert => "E-book conversion",
        OperationKind.ArchiveConvert => "Archive conversion",
        OperationKind.AudioProcess => "Audio tool",
        OperationKind.ImageProcess => "Image tool",
        OperationKind.VideoProcess => "Video tool",
        _ => "Processing"
    };

    public string StatusText => !IsEnabled
        ? "Paused"
        : LastError is null
            ? LastTriggeredAt is { } last ? $"Last run {last:HH:mm:ss}" : "Watching"
            : "Needs attention: " + LastError;

    public WatchFolderRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        InputDirectory = InputDirectory,
        OutputDirectory = OutputDirectory,
        IncludeSubdirectories = IncludeSubdirectories,
        IsEnabled = IsEnabled,
        Operation = Operation,
        Settings = Settings.Clone(),
        CreatedAt = CreatedAt,
        LastTriggeredAt = LastTriggeredAt,
        LastError = LastError
    };
}
