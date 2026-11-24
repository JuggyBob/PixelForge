using PixelForge.Domain;
using PixelForge.Domain.Capabilities;

namespace PixelForge.Engines.FFmpeg;

public sealed class FFmpegLocator
{
    private FFmpegLocator(string? ffmpeg, string? ffprobe, string? error)
    {
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
        Error = error;
        IsAvailable = ffmpeg is not null && ffprobe is not null;
    }

    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }
    public string? Error { get; }
    public bool IsAvailable { get; }

    public static FFmpegLocator Discover()
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg"));
        candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin"));

        var env = Environment.GetEnvironmentVariable("PIXELFORGE_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "third_party", "ffmpeg", "bin"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ffmpeg = Path.Combine(candidate, "ffmpeg.exe");
            var ffprobe = Path.Combine(candidate, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return new FFmpegLocator(ffmpeg, ffprobe, null);
        }

        return new FFmpegLocator(null, null,
            "FFmpeg and FFprobe were not found. The application should include them in its ffmpeg folder. " +
            "Repair or reinstall PixelForge.");
    }

    public string GetMuxerName(string formatId) =>
        FormatCatalog.FindById(formatId)?.MuxerName ?? formatId;
}
