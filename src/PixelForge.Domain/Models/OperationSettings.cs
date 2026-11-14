namespace PixelForge.Domain.Models;

public static class FormatIds
{
    public const string Copy = "copy";
    public const string None = "none";
    public const string Auto = "auto";
}

public abstract class OperationSettings
{
    public abstract OperationKind Operation { get; }
    public string OutputDirectory { get; set; } = "";
    public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.UniqueName;
    public string FileNameSuffix { get; set; } = "";
    public bool PreserveMetadata { get; set; } = true;

    public abstract OperationSettings Clone();
}

public sealed class ImageConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.ImageConvert;

    public string OutputFormatId { get; set; } = "png";
    public int Quality { get; set; } = 90;
    public int? CompressionLevel { get; set; }
    public bool Lossless { get; set; }
    public bool Progressive { get; set; } = true;
    public int? ResizeWidth { get; set; }
    public int? ResizeHeight { get; set; }
    public ImageResizeMode ResizeMode { get; set; } = ImageResizeMode.Fit;
    public bool PreserveAspectRatio { get; set; } = true;
    public ResamplingAlgorithm Resampling { get; set; } = ResamplingAlgorithm.Lanczos;
    public TransparencyMode TransparencyMode { get; set; } = TransparencyMode.Preserve;
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public bool PreserveAnimation { get; set; } = true;
    public bool FlattenAnimation { get; set; }
    public bool PreserveIccProfile { get; set; } = true;
    public string IcoSizes { get; set; } = "256,128,64,48,32,16";
    public string TiffCompression { get; set; } = "Lzw";
    public int GifColors { get; set; } = 256;
    public int AvifSpeed { get; set; } = 6;

    public override OperationSettings Clone() => (ImageConversionSettings)MemberwiseClone();
}

public sealed class AudioConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.AudioConvert;

    public string OutputFormatId { get; set; } = "mp3";
    public string CodecId { get; set; } = "mp3";
    public string EncoderId { get; set; } = "libmp3lame";
    public AudioRateControlMode RateControlMode { get; set; } = AudioRateControlMode.ConstantBitrate;
    public int BitrateKbps { get; set; } = 192;
    public int Quality { get; set; } = 5;
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int? CompressionLevel { get; set; } = 5;

    public override OperationSettings Clone() => (AudioConversionSettings)MemberwiseClone();
}

public sealed class VideoConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.VideoConvert;

    public string ContainerFormatId { get; set; } = "mp4";
    public string VideoCodecId { get; set; } = "h264";
    public string VideoEncoderId { get; set; } = "";
    public string AudioCodecId { get; set; } = "aac";
    public string AudioEncoderId { get; set; } = "";
    public VideoRateControlMode RateControlMode { get; set; } = VideoRateControlMode.ConstantQuality;
    public double QualityValue { get; set; } = 23;
    public int VideoBitrateKbps { get; set; } = 8000;
    public int AudioBitrateKbps { get; set; } = 192;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public VideoResizeMode ResizeMode { get; set; } = VideoResizeMode.None;
    public bool PreserveAspectRatio { get; set; } = true;
    public double FrameRate { get; set; }
    public ConversionPreset Preset { get; set; } = ConversionPreset.Balanced;
    public string EncoderPreset { get; set; } = "";
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Copy;
    public bool PreserveChapters { get; set; } = true;
    public bool FastStart { get; set; } = true;
    public int AudioChannels { get; set; }
    public int AudioSampleRate { get; set; }
    public bool VideoStreamCopy => VideoCodecId == FormatIds.Copy;
    public bool AudioStreamCopy => AudioCodecId == FormatIds.Copy;
    public bool NoAudio => AudioCodecId == FormatIds.None;

    public override OperationSettings Clone() => (VideoConversionSettings)MemberwiseClone();
}

public sealed class UpscaleSettings : OperationSettings
{
    public override OperationKind Operation =>
        Method == UpscaleMethod.Ai ? OperationKind.UpscaleAi : OperationKind.UpscaleTraditional;

    public UpscaleMethod Method { get; set; } = UpscaleMethod.Traditional;
    public ResamplingAlgorithm Algorithm { get; set; } = ResamplingAlgorithm.Lanczos;
    public string ModelId { get; set; } = "";
    public int ScaleFactor { get; set; } = 4;
    public int? CustomWidth { get; set; }
    public int? CustomHeight { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public int TileSize { get; set; }
    public int TileOverlap { get; set; }
    public int? NoiseLevel { get; set; }
    public string OutputFormatId { get; set; } = FormatIds.Auto;
    public int OutputQuality { get; set; } = 95;
    public bool PreserveAlpha { get; set; } = true;
    public bool PreserveIccProfile { get; set; } = true;
    public string BackgroundColor { get; set; } = "#FFFFFF";

    public override OperationSettings Clone() => (UpscaleSettings)MemberwiseClone();
}

public sealed class VideoUpscaleSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.VideoUpscale;

    public string ContainerFormatId { get; set; } = "mp4";
    public string VideoEncoderId { get; set; } = "";
    public int VideoBitrateKbps { get; set; } = 12000;
    public string AudioCodecId { get; set; } = "copy";
    public int AudioBitrateKbps { get; set; } = 192;

    public bool EnableSuperResolution { get; set; } = true;
    public string ImageModelId { get; set; } = "realesrgan-x4plus";
    public int ScaleFactor { get; set; } = 4;

    public bool EnableInterpolation { get; set; }
    public string InterpolationModelId { get; set; } = "rife-4.25-lite";
    public int FrameRateMultiplier { get; set; } = 2;

    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public int TileSize { get; set; }
    public int TileOverlap { get; set; }

    public override OperationSettings Clone() => (VideoUpscaleSettings)MemberwiseClone();
}

public sealed class OcrSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.OcrDocument;

    public string OcrModelId { get; set; } = "ppocrv6-small";
    public bool GeneratePdf { get; set; }
    public string DocumentName { get; set; } = "ScannedDocument_OCR";
    public bool Preprocess { get; set; } = true;
    public bool PreserveReadingOrder { get; set; } = true;
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public IReadOnlyList<string> PagePaths { get; set; } = [];

    public override OperationSettings Clone() => (OcrSettings)MemberwiseClone();
}
