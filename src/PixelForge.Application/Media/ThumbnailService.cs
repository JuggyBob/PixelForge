using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Media;

public sealed class ThumbnailService : IThumbnailService
{
    private readonly IReadOnlyList<IProcessingEngine> _engines;
    private readonly IAppPaths _paths;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly SemaphoreSlim _concurrency = new(3, 3);
    private int _cleanupCounter;

    public ThumbnailService(IEnumerable<IProcessingEngine> engines, IAppPaths paths, ILogger<ThumbnailService> logger)
    {
        _engines = engines.ToList();
        _paths = paths;
        _logger = logger;
    }

    public async Task<string?> GetThumbnailPathAsync(string mediaPath, MediaCategory category, int maxSize = 160,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath)) return null;
        Directory.CreateDirectory(_paths.ThumbnailCacheDirectory);
        if (Interlocked.Increment(ref _cleanupCounter) % 40 == 0) CleanupOldFiles();

        var file = new FileInfo(mediaPath);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{maxSize}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        var finalPath = Path.Combine(_paths.ThumbnailCacheDirectory, hash + ".png");
        if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0) return finalPath;

        var engine = category == MediaCategory.Audio
            ? _engines.FirstOrDefault(e => e.Id == EngineIds.FFmpeg)
            : category == MediaCategory.Video
                ? _engines.FirstOrDefault(e => e.Id == EngineIds.FFmpeg)
                : _engines.FirstOrDefault(e => e.Id == EngineIds.Images);

        if (engine is null) return null;

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(finalPath)) return finalPath;
            var temp = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                var created = await engine.CreateThumbnailAsync(mediaPath, temp, maxSize, cancellationToken);
                if (!string.IsNullOrWhiteSpace(created) && File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    File.Move(temp, finalPath, overwrite: true);
                    return finalPath;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail generation failed for {Path}", mediaPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // Ignore cleanup failure.
                }
            }

            return null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_paths.ThumbnailCacheDirectory)) return;
                foreach (var file in Directory.EnumerateFiles(_paths.ThumbnailCacheDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { File.Delete(file); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
        }, cancellationToken);
    }

    private void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(_paths.ThumbnailCacheDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(_paths.ThumbnailCacheDirectory, "*.png"))
            {
                try
                {
                    if (File.GetLastAccessTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // Ignore.
                }
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
