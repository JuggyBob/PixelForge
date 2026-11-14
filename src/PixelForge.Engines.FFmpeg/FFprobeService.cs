using System.Globalization;
using System.Text.Json;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.FFmpeg;

public sealed class FFprobeService
{
    private readonly IProcessRunner _processRunner;
    private readonly FFmpegLocator _locator;

    public FFprobeService(IProcessRunner processRunner, FFmpegLocator locator)
    {
        _processRunner = processRunner;
        _locator = locator;
    }

    public async Task<MediaInfo> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return ErrorInfo(path, "The file does not exist.", 0);

        if (!_locator.IsAvailable || _locator.FfprobePath is null)
            return ErrorInfo(path, _locator.Error ?? "FFprobe is not available.", file.Length);

        var request = new ProcessRequest
        {
            FileName = _locator.FfprobePath,
            Arguments =
            [
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                "-show_chapters",
                "-i", file.FullName
            ],
            Timeout = TimeSpan.FromSeconds(60),
            MaxCapturedLines = 200_000
        };

        var result = await _processRunner.RunAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            var detail = LastMeaningfulLine(result.StandardError);
            return ErrorInfo(path, string.IsNullOrWhiteSpace(detail)
                ? "FFprobe could not read this file."
                : detail, file.Length, detail);
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var hasFormat = root.TryGetProperty("format", out var format);
            var hasStreams = root.TryGetProperty("streams", out var streamsElement) &&
                             streamsElement.ValueKind == JsonValueKind.Array;
            if (!hasFormat && !hasStreams)
                return ErrorInfo(path, "FFprobe returned no media information.", file.Length);

            return BuildInfo(file, format, streamsElement, root);
        }
        catch (JsonException ex)
        {
            var preview = result.StandardOutput.Length > 500 ? result.StandardOutput[..500] : result.StandardOutput;
            return ErrorInfo(path, "FFprobe returned unreadable metadata: " + ex.Message, file.Length,
                "STDOUT:\n" + preview + "\nSTDERR:\n" + result.StandardError);
        }
    }

    private static MediaInfo BuildInfo(FileInfo file, JsonElement format, JsonElement streamsElement, JsonElement root)
    {
        var streams = streamsElement.ValueKind == JsonValueKind.Array
            ? streamsElement.EnumerateArray().ToList()
            : [];
        var extension = file.Extension.ToLowerInvariant();
        var formatName = GetString(format, "format_name") ?? "";
        var formatLongName = GetString(format, "format_long_name");
        var formatId = ResolveFormatId(formatName, extension);
        var formatInfo = formatId is null ? null : FormatCatalog.FindById(formatId);

        var video = streams.FirstOrDefault(s =>
            string.Equals(GetString(s, "codec_type"), "video", StringComparison.OrdinalIgnoreCase) &&
            GetInt(s, "disposition", "attached_pic") != 1);
        var audio = streams.FirstOrDefault(s =>
            string.Equals(GetString(s, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase));
        var subtitleCount = streams.Count(s =>
            string.Equals(GetString(s, "codec_type"), "subtitle", StringComparison.OrdinalIgnoreCase));

        var category = DetermineCategory(formatName, extension, streams);
        var duration = ParseDouble(GetString(format, "duration")) ??
                       streams.Select(s => ParseDouble(GetString(s, "duration")) ?? 0).DefaultIfEmpty(0).Max();
        var frameCount = TryInt(GetString(video, "nb_frames"));
        var frameRate = ParseRational(GetString(video, "avg_frame_rate")) ??
                        ParseRational(GetString(video, "r_frame_rate"));

        var rotation = 0;
        if (video.ValueKind == JsonValueKind.Object &&
            video.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                var r = ParseDouble(GetString(entry, "rotation"));
                if (r is not null && Math.Abs(r.Value) > 0.01)
                {
                    rotation = (int)Math.Round(r.Value);
                    break;
                }
            }
        }

        if (rotation == 0)
            rotation = (int)(ParseDouble(GetString(video, "tags", "rotate")) ?? 0);
        var normalizedRotation = ((rotation % 360) + 360) % 360;

        var width = GetInt(video, "width");
        var height = GetInt(video, "height");
        if (normalizedRotation is 90 or 270 && width is not null && height is not null)
            (width, height) = (height, width);

        var tags = GetTags(format);
        var videoCodec = GetString(video, "codec_name");
        var audioCodec = GetString(audio, "codec_name");
        var pixelFormat = GetString(video, "pix_fmt");
        var chapterCount = 0;
        if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            chapterCount = chapters.GetArrayLength();

        var streamInfos = new List<MediaStreamInfo>();
        foreach (var stream in streams)
        {
            var type = GetString(stream, "codec_type") ?? "unknown";
            streamInfos.Add(new MediaStreamInfo
            {
                Index = GetInt(stream, "index") ?? 0,
                Type = type,
                Codec = GetString(stream, "codec_name"),
                CodecLongName = GetString(stream, "codec_long_name"),
                Profile = GetString(stream, "profile"),
                Width = GetInt(stream, "width"),
                Height = GetInt(stream, "height"),
                FrameRate = ParseRational(GetString(stream, "avg_frame_rate")) ??
                            ParseRational(GetString(stream, "r_frame_rate")),
                SampleRate = TryInt(GetString(stream, "sample_rate")),
                Channels = GetInt(stream, "channels"),
                BitRate = ParseLong(GetString(stream, "bit_rate")),
                DurationSeconds = ParseDouble(GetString(stream, "duration")),
                Language = GetString(stream, "tags", "language"),
                Title = GetString(stream, "tags", "title"),
                IsDefault = GetInt(stream, "disposition", "default") == 1,
                IsAttachedPicture = GetInt(stream, "disposition", "attached_pic") == 1
            });
        }

        return new MediaInfo
        {
            FilePath = file.FullName,
            FileName = file.Name,
            Extension = extension,
            FileSizeBytes = file.Length,
            Category = category,
            FormatId = formatId,
            FormatName = formatInfo?.DisplayName ?? formatLongName ?? formatName,
            Width = width,
            Height = height,
            DurationSeconds = duration > 0 ? duration : null,
            FrameRate = frameRate,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            SampleRate = TryInt(GetString(audio, "sample_rate")),
            Channels = GetInt(audio, "channels"),
            AudioBitRate = ParseLong(GetString(audio, "bit_rate")),
            VideoBitRate = ParseLong(GetString(video, "bit_rate")),
            TotalBitRate = ParseLong(GetString(format, "bit_rate")),
            HasAlpha = !string.IsNullOrWhiteSpace(pixelFormat) &&
                       (pixelFormat.Contains("a", StringComparison.OrdinalIgnoreCase) ||
                        pixelFormat.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) ||
                        pixelFormat.StartsWith("bgra", StringComparison.OrdinalIgnoreCase)),
            IsAnimated = category == MediaCategory.Image && formatId is "gif" or "webp" && (frameCount is null or > 1),
            IsVector = extension is ".svg" or ".svgz",
            FrameCount = frameCount,
            SubtitleStreamCount = subtitleCount,
            ChapterCount = chapterCount,
            RotationDegrees = normalizedRotation == 0 ? null : normalizedRotation,
            ColorSpace = pixelFormat,
            Streams = streamInfos,
            Tags = tags,
            EngineId = EngineIds.FFmpeg
        };
    }

    private static MediaCategory DetermineCategory(string formatName, string extension, IReadOnlyList<JsonElement> streams)
    {
        var imageByExtension = FormatCatalog.FindByExtension(extension)?.Category == MediaCategory.Image;
        var isImagePipe = formatName.Contains("image2", StringComparison.OrdinalIgnoreCase) ||
                          formatName.Contains("_pipe", StringComparison.OrdinalIgnoreCase) ||
                          formatName is "png" or "jpeg" or "webp" or "gif" or "tiff" or "bmp" or "avif" or "heic" or "jxl" or "qoi";
        if (isImagePipe || (imageByExtension && streams.All(s => !string.Equals(GetString(s, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase))))
            return MediaCategory.Image;

        var hasVideo = streams.Any(s =>
            string.Equals(GetString(s, "codec_type"), "video", StringComparison.OrdinalIgnoreCase) &&
            GetInt(s, "disposition", "attached_pic") != 1);
        if (hasVideo) return MediaCategory.Video;
        if (streams.Any(s => string.Equals(GetString(s, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase)))
            return MediaCategory.Audio;
        return MediaCategory.Unknown;
    }

    private static string? ResolveFormatId(string formatName, string extension)
    {
        var extFormat = FormatCatalog.FindByExtension(extension);
        if (formatName.Contains("matroska", StringComparison.OrdinalIgnoreCase))
            return extension == ".webm" ? "webm" : "mkv";
        if (formatName.Contains("webm", StringComparison.OrdinalIgnoreCase)) return "webm";
        if (formatName.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
            formatName.Contains("mov", StringComparison.OrdinalIgnoreCase) ||
            formatName.Contains("m4a", StringComparison.OrdinalIgnoreCase))
        {
            if (extension == ".m4a") return "m4a";
            if (extension is ".mov" or ".qt") return "mov";
            return "mp4";
        }

        if (formatName.Contains("avi", StringComparison.OrdinalIgnoreCase)) return "avi";
        if (formatName.Contains("mp3", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (formatName.Contains("wav", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (formatName.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (formatName.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
        if (formatName.Contains("ogg", StringComparison.OrdinalIgnoreCase))
            return extension == ".opus" ? "opus" : "ogg";
        if (formatName.Contains("gif", StringComparison.OrdinalIgnoreCase)) return "gif";
        return extFormat?.Id;
    }

    private static Dictionary<string, string> GetTags(JsonElement element)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) return tags;
        if (!element.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
            return tags;
        foreach (var property in tagsElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value)) tags[property.Name] = value;
        }

        return tags;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string property, string nested)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var child))
            return null;
        return GetString(child, nested);
    }

    private static int? GetInt(JsonElement element, string property)
    {
        var value = GetString(element, property);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static int? GetInt(JsonElement element, string property, string nested)
    {
        var value = GetString(element, property, nested);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static MediaInfo ErrorInfo(string path, string error, long size, string? details = null) => new()
    {
        FilePath = Path.GetFullPath(path),
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path).ToLowerInvariant(),
        FileSizeBytes = size,
        Category = FormatCatalog.DetectCategoryByExtension(Path.GetExtension(path)),
        Error = error,
        Tags = details is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Details"] = details },
        EngineId = EngineIds.FFmpeg
    };

    private static string LastMeaningfulLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static int? TryInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static double? ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0") return null;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
            return numerator / denominator;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
