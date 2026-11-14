using PixelForge.Domain.Models;

namespace PixelForge.Domain.Capabilities;

public sealed record EncoderDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string CodecId { get; init; }
    public required CodecKind Kind { get; init; }
    public bool IsHardware { get; init; }
    public RateControlCapabilities RateControl { get; init; }
    public string? QualityLabel { get; init; }
    public double? QualityMin { get; init; }
    public double? QualityMax { get; init; }
    public double? QualityDefault { get; init; }
    public IReadOnlyList<string> Presets { get; init; } = [];
    public string? DefaultPreset { get; init; }
    public int DimensionMultiple { get; init; } = 2;
    public IReadOnlyList<string> ContainerIds { get; init; } = [];
    public string? Notes { get; init; }
    public int Priority { get; init; } = 50;
    public string? FfmpegCodecName { get; init; }
}

public sealed record CodecDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string TechnicalId { get; init; }
    public required CodecKind Kind { get; init; }
    public IReadOnlyList<EncoderDefinition> Encoders { get; init; } = [];
    public int SortOrder { get; init; }
    public IReadOnlyList<string> ContainerIds =>
        Encoders.SelectMany(e => e.ContainerIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public static class CodecCatalog
{
    private static EncoderDefinition Enc(string id, string display, string codec, CodecKind kind,
        string[] containers, int priority, RateControlCapabilities rc = RateControlCapabilities.Bitrate,
        bool hw = false, string? qLabel = null, double? qMin = null, double? qMax = null, double? qDefault = null,
        string[]? presets = null, string? defaultPreset = null, int multiple = 2, string? notes = null) => new()
    {
        Id = id, DisplayName = display, CodecId = codec, Kind = kind, ContainerIds = containers,
        Priority = priority, RateControl = rc, IsHardware = hw, QualityLabel = qLabel,
        QualityMin = qMin, QualityMax = qMax, QualityDefault = qDefault,
        Presets = presets ?? [], DefaultPreset = defaultPreset, DimensionMultiple = multiple, Notes = notes
    };

    public static readonly CodecDefinition H264 = new()
    {
        Id = "h264", DisplayName = "H.264 / AVC", TechnicalId = "h264", Kind = CodecKind.Video, SortOrder = 10,
        Encoders =
        [
            Enc("h264_amf", "AMD AMF H.264", "h264", CodecKind.Video, ["mp4", "mkv", "mov"], 20,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "QP", 0, 51, 23,
                ["speed", "balanced", "quality", "high_quality"], "balanced", 2,
                "Hardware encoder. Exposed only after a successful encode probe."),
            Enc("h264_nvenc", "NVIDIA NVENC H.264", "h264", CodecKind.Video, ["mp4", "mkv", "mov"], 10,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "CQ", 0, 51, 23,
                ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], "p4"),
            Enc("h264_qsv", "Intel Quick Sync H.264", "h264", CodecKind.Video, ["mp4", "mkv", "mov"], 15,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, true, "Global quality", 1, 51, 23,
                ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"], "medium"),
            Enc("h264_mf", "Media Foundation H.264", "h264", CodecKind.Video, ["mp4", "mkv", "mov"], 45,
                RateControlCapabilities.Bitrate, false, null, null, null, null, null, null, 2,
                "Windows Media Foundation encoder. Availability depends on installed media components."),
            Enc("libopenh264", "OpenH264 (software)", "h264", CodecKind.Video, ["mp4", "mkv", "mov"], 60,
                RateControlCapabilities.Bitrate, false, null, null, null, null, null, null, 2,
                "Software H.264 encoder bundled under BSD-3-Clause. Uses target bitrate.")
        ]
    };

    public static readonly CodecDefinition Hevc = new()
    {
        Id = "hevc", DisplayName = "H.265 / HEVC", TechnicalId = "hevc", Kind = CodecKind.Video, SortOrder = 20,
        Encoders =
        [
            Enc("hevc_amf", "AMD AMF HEVC", "hevc", CodecKind.Video, ["mp4", "mkv", "mov"], 20,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "QP", 0, 51, 25,
                ["speed", "balanced", "quality", "high_quality"], "balanced", 2,
                "Hardware encoder. Exposed only after a successful encode probe."),
            Enc("hevc_nvenc", "NVIDIA NVENC HEVC", "hevc", CodecKind.Video, ["mp4", "mkv", "mov"], 10,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "CQ", 0, 51, 25,
                ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], "p4"),
            Enc("hevc_qsv", "Intel Quick Sync HEVC", "hevc", CodecKind.Video, ["mp4", "mkv", "mov"], 15,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, true, "Global quality", 1, 51, 25,
                ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"], "medium"),
            Enc("hevc_mf", "Media Foundation HEVC", "hevc", CodecKind.Video, ["mp4", "mkv", "mov"], 45,
                RateControlCapabilities.Bitrate),
            Enc("libkvazaar", "Kvazaar (software)", "hevc", CodecKind.Video, ["mp4", "mkv", "mov"], 60,
                RateControlCapabilities.Bitrate, false, null, null, null, null, null, null, 8,
                "Software HEVC encoder. Dimensions are rounded to a multiple of 8.")
        ]
    };

    public static readonly CodecDefinition Av1 = new()
    {
        Id = "av1", DisplayName = "AV1", TechnicalId = "av1", Kind = CodecKind.Video, SortOrder = 30,
        Encoders =
        [
            Enc("av1_amf", "AMD AMF AV1", "av1", CodecKind.Video, ["mp4", "mkv", "webm"], 20,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "QP", 0, 63, 30,
                ["speed", "balanced", "quality", "high_quality"], "balanced", 2,
                "Hardware encoder. Exposed only after a successful encode probe."),
            Enc("av1_nvenc", "NVIDIA NVENC AV1", "av1", CodecKind.Video, ["mp4", "mkv", "webm"], 10,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, true, "CQ", 0, 63, 30,
                ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], "p4"),
            Enc("av1_qsv", "Intel Quick Sync AV1", "av1", CodecKind.Video, ["mp4", "mkv", "webm"], 15,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, true, "Global quality", 1, 63, 30,
                ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"], "medium"),
            Enc("libsvtav1", "SVT-AV1 (software)", "av1", CodecKind.Video, ["mp4", "mkv", "webm"], 30,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, false, "CRF", 0, 63, 28,
                [.. Enumerable.Range(0, 14).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], "8", 2,
                "Software AV1 encoder. Presets are SVT-AV1 preset numbers (0 = slowest)."),
            Enc("libaom-av1", "libaom AV1 (software)", "av1", CodecKind.Video, ["mp4", "mkv", "webm"], 70,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, false, "CRF", 0, 63, 30,
                [.. Enumerable.Range(0, 9).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], "4", 2,
                "Reference AV1 encoder. Much slower than SVT-AV1.")
        ]
    };

    public static readonly CodecDefinition Vp9 = new()
    {
        Id = "vp9", DisplayName = "VP9", TechnicalId = "vp9", Kind = CodecKind.Video, SortOrder = 40,
        Encoders =
        [
            Enc("libvpx-vp9", "libvpx VP9 (software)", "vp9", CodecKind.Video, ["webm", "mkv"], 20,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, false, "CRF", 0, 63, 32,
                [.. Enumerable.Range(0, 9).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], "2", 2,
                "Software VP9 encoder. Presets are cpu-used values (0 = slowest).")
        ]
    };

    public static readonly CodecDefinition Vp8 = new()
    {
        Id = "vp8", DisplayName = "VP8", TechnicalId = "vp8", Kind = CodecKind.Video, SortOrder = 50,
        Encoders =
        [
            Enc("libvpx", "libvpx VP8 (software)", "vp8", CodecKind.Video, ["webm", "mkv"], 20,
                RateControlCapabilities.ConstantQuality | RateControlCapabilities.Bitrate, false, "CRF", 4, 63, 30,
                [.. Enumerable.Range(0, 17).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], "4", 2,
                "Software VP8 encoder. Presets are cpu-used values (0 = slowest).")
        ]
    };

    public static readonly CodecDefinition Mpeg4 = new()
    {
        Id = "mpeg4", DisplayName = "MPEG-4 Part 2", TechnicalId = "mpeg4", Kind = CodecKind.Video, SortOrder = 60,
        Encoders =
        [
            Enc("mpeg4", "FFmpeg MPEG-4 (software)", "mpeg4", CodecKind.Video, ["mp4", "avi", "mkv", "mov"], 20,
                RateControlCapabilities.Quantizer | RateControlCapabilities.Bitrate, false, "qscale", 2, 31, 4)
        ]
    };

    public static readonly CodecDefinition ProRes = new()
    {
        Id = "prores", DisplayName = "Apple ProRes", TechnicalId = "prores", Kind = CodecKind.Video, SortOrder = 70,
        Encoders =
        [
            Enc("prores_ks", "ProRes KS (software)", "prores", CodecKind.Video, ["mov", "mkv"], 20,
                RateControlCapabilities.None, false, "Profile", null, null, null,
                ["proxy", "lt", "standard", "hq", "4444", "4444xq"], "standard", 2,
                "Intermediate codec. Quality is selected through the ProRes profile.")
        ]
    };

    public static readonly CodecDefinition GifVideo = new()
    {
        Id = "gif", DisplayName = "Animated GIF", TechnicalId = "gif", Kind = CodecKind.Video, SortOrder = 80,
        Encoders =
        [
            Enc("gif", "GIF encoder", "gif", CodecKind.Video, ["gif"], 5,
                RateControlCapabilities.None, false, null, null, null, null, null, null, 1,
                "Palette-optimized GIF output. No audio stream.")
        ]
    };

    public static readonly CodecDefinition Aac = new()
    {
        Id = "aac", DisplayName = "AAC (Advanced Audio Coding)", TechnicalId = "aac", Kind = CodecKind.Audio, SortOrder = 10,
        Encoders =
        [
            Enc("aac", "AAC (FFmpeg, software)", "aac", CodecKind.Audio, ["m4a", "mp4", "mkv", "mov", "aac"], 20,
                RateControlCapabilities.Bitrate),
            Enc("aac_mf", "AAC via Media Foundation", "aac", CodecKind.Audio, ["m4a", "mp4", "mkv", "mov", "aac"], 40,
                RateControlCapabilities.Bitrate)
        ]
    };

    public static readonly CodecDefinition Mp3 = new()
    {
        Id = "mp3", DisplayName = "MP3 (MPEG Audio Layer III)", TechnicalId = "mp3", Kind = CodecKind.Audio, SortOrder = 20,
        Encoders =
        [
            Enc("libmp3lame", "LAME MP3 (software)", "mp3", CodecKind.Audio, ["mp3", "mp4", "mkv", "mov", "avi"], 20,
                RateControlCapabilities.Bitrate | RateControlCapabilities.ConstantQuality, false,
                "VBR quality", 0, 9, 5, null, null, 1)
        ]
    };

    public static readonly CodecDefinition Flac = new()
    {
        Id = "flac", DisplayName = "FLAC (Free Lossless Audio Codec)", TechnicalId = "flac", Kind = CodecKind.Audio, SortOrder = 30,
        Encoders =
        [
            Enc("flac", "FLAC (software)", "flac", CodecKind.Audio, ["flac", "mkv"], 20,
                RateControlCapabilities.Lossless, false, null, null, null, null, null, null, 1)
        ]
    };

    public static readonly CodecDefinition Opus = new()
    {
        Id = "opus", DisplayName = "Opus", TechnicalId = "opus", Kind = CodecKind.Audio, SortOrder = 40,
        Encoders =
        [
            Enc("libopus", "libopus (software)", "opus", CodecKind.Audio, ["opus", "ogg", "webm", "mkv"], 20,
                RateControlCapabilities.Bitrate, false, null, null, null, null, null, null, 1)
        ]
    };

    public static readonly CodecDefinition Vorbis = new()
    {
        Id = "vorbis", DisplayName = "Vorbis", TechnicalId = "vorbis", Kind = CodecKind.Audio, SortOrder = 50,
        Encoders =
        [
            Enc("libvorbis", "libvorbis (software)", "vorbis", CodecKind.Audio, ["ogg", "webm", "mkv"], 20,
                RateControlCapabilities.ConstantQuality, false, "VBR quality", 0, 10, 6, null, null, 1)
        ]
    };

    public static readonly CodecDefinition Pcm = new()
    {
        Id = "pcm_s16le", DisplayName = "PCM 16-bit", TechnicalId = "pcm_s16le", Kind = CodecKind.Audio, SortOrder = 60,
        Encoders =
        [
            Enc("pcm_s16le", "PCM signed 16-bit little-endian", "pcm_s16le", CodecKind.Audio, ["wav", "mov", "mkv", "avi"], 20,
                RateControlCapabilities.Lossless, false, null, null, null, null, null, null, 1)
        ]
    };

    public static readonly CodecDefinition Alac = new()
    {
        Id = "alac", DisplayName = "ALAC (Apple Lossless)", TechnicalId = "alac", Kind = CodecKind.Audio, SortOrder = 70,
        Encoders =
        [
            Enc("alac", "ALAC (software)", "alac", CodecKind.Audio, ["m4a", "mov", "mkv"], 40,
                RateControlCapabilities.Lossless, false, null, null, null, null, null, null, 1)
        ]
    };

    public static readonly IReadOnlyList<CodecDefinition> VideoCodecs =
        [H264, Hevc, Av1, Vp9, Vp8, Mpeg4, ProRes, GifVideo];

    public static readonly IReadOnlyList<CodecDefinition> AudioCodecs =
        [Aac, Mp3, Flac, Opus, Vorbis, Pcm, Alac];

    private static readonly Dictionary<string, CodecDefinition> VideoById =
        VideoCodecs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, CodecDefinition> AudioById =
        AudioCodecs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, EncoderDefinition> EncodersById =
        VideoCodecs.Concat(AudioCodecs).SelectMany(c => c.Encoders)
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static CodecDefinition? FindVideoCodec(string? id) =>
        id is null ? null : VideoById.GetValueOrDefault(id);

    public static CodecDefinition? FindAudioCodec(string? id) =>
        id is null ? null : AudioById.GetValueOrDefault(id);

    public static EncoderDefinition? FindEncoder(string? id) =>
        id is null ? null : EncodersById.GetValueOrDefault(id);

    public static CodecDefinition? FindCodecByStreamName(string? name, CodecKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var list = kind == CodecKind.Video ? VideoCodecs : AudioCodecs;
        return list.FirstOrDefault(c => string.Equals(c.TechnicalId, name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<CodecDefinition> GetVideoCodecsForContainer(string containerId) =>
        VideoCodecs.Where(c => c.ContainerIds.Contains(containerId, StringComparer.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<CodecDefinition> GetAudioCodecsForContainer(string containerId) =>
        AudioCodecs.Where(c => c.ContainerIds.Contains(containerId, StringComparer.OrdinalIgnoreCase)).ToList();
}
