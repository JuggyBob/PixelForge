using PixelForge.Domain.Interfaces;

namespace PixelForge.Tests.Integration;

internal sealed class TempAppPaths : IAppPaths, IDisposable
{
    public TempAppPaths(string? root = null)
    {
        RootDirectory = root ?? Path.Combine(Path.GetTempPath(), "PixelForge.Tests", Guid.NewGuid().ToString("N"));
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        DatabaseFile = Path.Combine(RootDirectory, "pixelforge.db");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        ThumbnailCacheDirectory = Path.Combine(CacheDirectory, "thumbnails");
        DefaultModelDirectory = Path.Combine(RootDirectory, "models");
        DefaultOutputDirectory = Path.Combine(RootDirectory, "output");
        DefaultUpscaleOutputDirectory = Path.Combine(RootDirectory, "upscaled");
        EnsureCreated();
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
        Directory.CreateDirectory(DefaultOutputDirectory);
        Directory.CreateDirectory(DefaultUpscaleOutputDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temporary test data.
        }
    }
}

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();
    public static string Fixtures => Path.Combine(Root, "testdata", "fixtures");
    public static string ThirdParty => Path.Combine(Root, "third_party");

    public static string Fixture(string name) => Path.Combine(Fixtures, name);

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "PixelForge.sln")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not find the PixelForge repository root from " + AppContext.BaseDirectory);
    }
}
