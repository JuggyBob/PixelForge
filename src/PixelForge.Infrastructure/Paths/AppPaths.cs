using PixelForge.Domain.Interfaces;

namespace PixelForge.Infrastructure.Paths;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("PIXELFORGE_DATA_DIR");
        RootDirectory = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelForge")
            : Path.GetFullPath(overrideRoot);

        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        DatabaseFile = Path.Combine(RootDirectory, "pixelforge.db");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        ThumbnailCacheDirectory = Path.Combine(CacheDirectory, "thumbnails");
        DefaultModelDirectory = Path.Combine(RootDirectory, "models");

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        DefaultOutputDirectory = Path.Combine(pictures, "PixelForge Output");
        DefaultUpscaleOutputDirectory = Path.Combine(pictures, "PixelForge Upscaled");
    }

    public string RootDirectory { get; }
    public string SettingsFile { get; }
    public string DatabaseFile { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
    public string CacheDirectory { get; }
    public string ThumbnailCacheDirectory { get; }
    public string DefaultOutputDirectory { get; }
    public string DefaultUpscaleOutputDirectory { get; }
    public string DefaultModelDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
        Directory.CreateDirectory(DefaultModelDirectory);
        CleanupStaleTemp();
    }

    private void CleanupStaleTemp()
    {
        try
        {
            if (!Directory.Exists(TempDirectory)) return;
            if ((File.GetAttributes(TempDirectory) & FileAttributes.ReparsePoint) != 0) return;
            var cutoff = DateTime.UtcNow.AddDays(-2);
            var root = Path.GetFullPath(TempDirectory).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var dir in Directory.EnumerateDirectories(TempDirectory))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // A leftover directory may still be locked; try again next time.
                }
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true
            };
            foreach (var file in Directory.EnumerateFiles(TempDirectory, "*", options))
            {
                try
                {
                    var full = Path.GetFullPath(file);
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // Leave locked review files for the next cleanup pass.
                }
            }
        }
        catch
        {
            // Cleanup must never prevent startup.
        }
    }
}
