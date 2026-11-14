using PixelForge.Domain.Models;

namespace PixelForge.Domain.Models;

public sealed class DocumentConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.DocumentConvert;
    public string OutputFormatId { get; set; } = "pdf";
    public string? Title { get; set; }
    public string? Author { get; set; }
    public bool OpenAfterConversion { get; set; }
    public override OperationSettings Clone() => (DocumentConversionSettings)MemberwiseClone();
}

public sealed class EbookConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.EbookConvert;
    public string OutputFormatId { get; set; } = "epub";
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }
    public override OperationSettings Clone() => (EbookConversionSettings)MemberwiseClone();
}

public sealed class ArchiveConversionSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.ArchiveConvert;
    public string OutputFormatId { get; set; } = "zip";
    public ArchiveAction Action { get; set; } = ArchiveAction.Create;
    public int CompressionLevel { get; set; } = 5;
    public bool PreserveDirectoryStructure { get; set; } = true;
    public int MaxEntryCount { get; set; } = 20000;
    public long MaxExpandedBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public override OperationSettings Clone() => (ArchiveConversionSettings)MemberwiseClone();
}

public sealed class AudioToolSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.AudioProcess;
    public AudioToolOperation Tool { get; set; } = AudioToolOperation.NormalizeLoudness;
    public string OutputFormatId { get; set; } = "wav";
    public string CodecId { get; set; } = "pcm_s16le";
    public string EncoderId { get; set; } = "pcm_s16le";
    public int BitrateKbps { get; set; } = 192;
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    // Loudness
    public double TargetLufs { get; set; } = -16;
    public double TruePeakDb { get; set; } = -1.5;
    public double LoudnessRange { get; set; } = 11;
    public bool MeasuredLoudnessAvailable { get; set; }
    public double MeasuredLufs { get; set; }

    // Noise reduction
    public NoiseReductionMethod NoiseMethod { get; set; } = NoiseReductionMethod.Spectral;
    public double NoiseReductionDb { get; set; } = 12;
    public string? RnnModelPath { get; set; }

    // Silence removal
    public double SilenceThresholdDb { get; set; } = -40;
    public double MinSilenceSeconds { get; set; } = 0.7;
    public double KeepSilenceMarginSeconds { get; set; } = 0.15;

    // Trimming and merge
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public List<string> MergeInputPaths { get; set; } = [];

    // Metadata
    public AudioMetadataMode MetadataMode { get; set; } = AudioMetadataMode.Preserve;
    public string? MetaTitle { get; set; }
    public string? MetaArtist { get; set; }
    public string? MetaAlbum { get; set; }
    public string? MetaAlbumArtist { get; set; }
    public string? MetaGenre { get; set; }
    public string? MetaYear { get; set; }
    public string? MetaComment { get; set; }
    public string? MetaComposer { get; set; }
    public string? MetaTrack { get; set; }
    public string? MetaDisc { get; set; }
    public string? CoverArtPath { get; set; }
    public bool RemoveCoverArt { get; set; }

    // Extract-from-video
    public int AudioStreamIndex { get; set; }
    public bool CopyAudioStream { get; set; }

    public override OperationSettings Clone()
    {
        var clone = (AudioToolSettings)MemberwiseClone();
        clone.MergeInputPaths = [.. MergeInputPaths];
        return clone;
    }
}

public sealed class ImageToolSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.ImageProcess;
    public ImageToolOperation Tool { get; set; } = ImageToolOperation.Optimize;
    public string OutputFormatId { get; set; } = "webp";
    public int Quality { get; set; } = 86;
    public bool Lossless { get; set; }
    public int PngCompression { get; set; } = 6;
    public int AvifSpeed { get; set; } = 6;
    public int GifColors { get; set; } = 256;
    public bool PreserveAlpha { get; set; } = true;
    public bool PreserveIccProfile { get; set; } = true;

    // Batch edit pipeline.
    public double DenoiseStrength { get; set; }
    public double SharpenAmount { get; set; }
    public double SharpenRadius { get; set; } = 1.0;
    public double Brightness { get; set; } = 100;
    public double Contrast { get; set; } = 100;
    public double Saturation { get; set; } = 100;
    public double Exposure { get; set; }
    public double Gamma { get; set; } = 1.0;
    public int? ResizeWidth { get; set; }
    public int? ResizeHeight { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;

    // Crop
    public int? CropX { get; set; }
    public int? CropY { get; set; }
    public int? CropWidth { get; set; }
    public int? CropHeight { get; set; }
    public string? CropAspectRatio { get; set; }

    // Watermark
    public WatermarkKind WatermarkKind { get; set; }
    public string? WatermarkText { get; set; } = "PixelForge";
    public string? WatermarkImagePath { get; set; }
    public string WatermarkPosition { get; set; } = "BottomRight";
    public double WatermarkOpacity { get; set; } = 0.6;
    public double WatermarkScale { get; set; } = 0.18;
    public int WatermarkMargin { get; set; } = 24;

    // Metadata editor
    public string? MetaDescription { get; set; }
    public string? MetaCopyright { get; set; }
    public string? MetaArtist { get; set; }
    public string? MetaComment { get; set; }
    public DateTimeOffset? MetaDateTaken { get; set; }
    public bool RemoveAllMetadata { get; set; }

    // AI background removal
    public string BackgroundModelId { get; set; } = "u2netp";
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Auto;
    public string BackgroundColor { get; set; } = "transparent";

    public override OperationSettings Clone() => (ImageToolSettings)MemberwiseClone();
}

public sealed class VideoToolSettings : OperationSettings
{
    public override OperationKind Operation => OperationKind.VideoProcess;
    public VideoToolOperation Tool { get; set; } = VideoToolOperation.Compress;
    public string ContainerFormatId { get; set; } = "mp4";
    public string VideoCodecId { get; set; } = "h264";
    public string VideoEncoderId { get; set; } = "";
    public string AudioCodecId { get; set; } = "aac";
    public string AudioEncoderId { get; set; } = "";
    public VideoCompressionPreset CompressionPreset { get; set; } = VideoCompressionPreset.Balanced;
    public double TargetSizeMb { get; set; } = 25;
    public int AudioBitrateKbps { get; set; } = 128;
    public double QualityValue { get; set; } = 23;
    public int VideoBitrateKbps { get; set; } = 8000;
    public bool AllowTwoPass { get; set; } = true;
    public bool PreferHardware { get; set; } = true;

    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public bool FrameAccurateTrim { get; set; } = true;
    public List<string> MergeInputPaths { get; set; } = [];

    public int? ResizeWidth { get; set; }
    public int? ResizeHeight { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;
    public double FrameRate { get; set; }

    public double StabilizationStrength { get; set; } = 0.5;
    public double ThumbnailTimeSeconds { get; set; } = 1.0;
    public string ThumbnailFormatId { get; set; } = "jpg";
    public double GifStartSeconds { get; set; }
    public double GifEndSeconds { get; set; } = 3;
    public int GifFps { get; set; } = 12;
    public int GifWidth { get; set; } = 640;

    public string ExtractAudioFormatId { get; set; } = "mp3";
    public string ExtractAudioCodecId { get; set; } = "mp3";
    public string ExtractAudioEncoderId { get; set; } = "libmp3lame";
    public int AudioStreamIndex { get; set; }
    public bool CopyAudioStream { get; set; }
    public string? ReplacementAudioPath { get; set; }
    public bool OffsetAudio { get; set; }
    public double AudioOffsetSeconds { get; set; }

    public SubtitleToolMode SubtitleMode { get; set; } = SubtitleToolMode.Extract;
    public int SubtitleStreamIndex { get; set; }
    public string SubtitleFormatId { get; set; } = "srt";

    public string? MetaTitle { get; set; }
    public string? MetaArtist { get; set; }
    public string? MetaComment { get; set; }
    public string? MetaYear { get; set; }
    public bool RemoveAllMetadata { get; set; }

    public bool HdrToSdr { get; set; }
    public double ToneMapPeakNits { get; set; } = 100;

    public override OperationSettings Clone()
    {
        var clone = (VideoToolSettings)MemberwiseClone();
        clone.MergeInputPaths = [.. MergeInputPaths];
        return clone;
    }
}
