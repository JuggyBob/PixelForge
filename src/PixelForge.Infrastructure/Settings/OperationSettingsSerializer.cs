using System.Text.Json;
using System.Text.Json.Serialization;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Settings;

public sealed class OperationSettingsSerializer : IOperationSettingsSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize(OperationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, settings.GetType(), JsonOptions);
    }

    public OperationSettings Deserialize(OperationKind operation, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault(operation);
        }

        try
        {
            return operation switch
            {
                OperationKind.ImageConvert => JsonSerializer.Deserialize<ImageConversionSettings>(json, JsonOptions) ?? new ImageConversionSettings(),
                OperationKind.AudioConvert => JsonSerializer.Deserialize<AudioConversionSettings>(json, JsonOptions) ?? new AudioConversionSettings(),
                OperationKind.VideoConvert => JsonSerializer.Deserialize<VideoConversionSettings>(json, JsonOptions) ?? new VideoConversionSettings(),
                OperationKind.UpscaleTraditional or OperationKind.UpscaleAi =>
                    JsonSerializer.Deserialize<UpscaleSettings>(json, JsonOptions) ?? new UpscaleSettings(),
                OperationKind.VideoUpscale =>
                    JsonSerializer.Deserialize<VideoUpscaleSettings>(json, JsonOptions) ?? new VideoUpscaleSettings(),
                OperationKind.OcrDocument =>
                    JsonSerializer.Deserialize<OcrSettings>(json, JsonOptions) ?? new OcrSettings(),
                OperationKind.DocumentConvert =>
                    JsonSerializer.Deserialize<DocumentConversionSettings>(json, JsonOptions) ?? new DocumentConversionSettings(),
                OperationKind.EbookConvert =>
                    JsonSerializer.Deserialize<EbookConversionSettings>(json, JsonOptions) ?? new EbookConversionSettings(),
                OperationKind.ArchiveConvert =>
                    JsonSerializer.Deserialize<ArchiveConversionSettings>(json, JsonOptions) ?? new ArchiveConversionSettings(),
                OperationKind.AudioProcess =>
                    JsonSerializer.Deserialize<AudioToolSettings>(json, JsonOptions) ?? new AudioToolSettings(),
                OperationKind.ImageProcess =>
                    JsonSerializer.Deserialize<ImageToolSettings>(json, JsonOptions) ?? new ImageToolSettings(),
                OperationKind.VideoProcess =>
                    JsonSerializer.Deserialize<VideoToolSettings>(json, JsonOptions) ?? new VideoToolSettings(),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation kind")
            };
        }
        catch (JsonException)
        {
            return CreateDefault(operation);
        }
    }

    public static OperationSettings CreateDefault(OperationKind operation) => operation switch
    {
        OperationKind.ImageConvert => new ImageConversionSettings(),
        OperationKind.AudioConvert => new AudioConversionSettings(),
        OperationKind.VideoConvert => new VideoConversionSettings(),
        OperationKind.UpscaleTraditional or OperationKind.UpscaleAi => new UpscaleSettings(),
        OperationKind.VideoUpscale => new VideoUpscaleSettings(),
        OperationKind.OcrDocument => new OcrSettings(),
        OperationKind.DocumentConvert => new DocumentConversionSettings(),
        OperationKind.EbookConvert => new EbookConversionSettings(),
        OperationKind.ArchiveConvert => new ArchiveConversionSettings(),
        OperationKind.AudioProcess => new AudioToolSettings(),
        OperationKind.ImageProcess => new ImageToolSettings(),
        OperationKind.VideoProcess => new VideoToolSettings(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation kind")
    };
}
