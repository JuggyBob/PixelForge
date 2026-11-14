using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Media;

public sealed class MediaInspector : IMediaInspector
{
    private readonly IReadOnlyList<IProcessingEngine> _engines;
    private readonly ILogger<MediaInspector> _logger;

    public MediaInspector(IEnumerable<IProcessingEngine> engines, ILogger<MediaInspector> logger)
    {
        _engines = engines.ToList();
        _logger = logger;
    }

    public async Task<MediaInfo> InspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            return CreateUnreadable(fullPath, categoryHint ?? FormatCatalog.DetectCategoryByExtension(Path.GetExtension(fullPath)),
                "The file does not exist.");
        }

        var extension = fileInfo.Extension;
        var hintedCategory = categoryHint ?? FormatCatalog.DetectCategoryByExtension(extension);
        var engineOrder = GetEngineOrder(hintedCategory);
        MediaInfo? bestError = null;

        foreach (var engine in engineOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = await engine.TryInspectAsync(fullPath, hintedCategory, cancellationToken);
                if (info is null) continue;
                if (info.IsReadable)
                {
                    return WithFileInfo(info, fileInfo);
                }

                bestError ??= info;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Inspection with engine {Engine} failed for {Path}", engine.Id, fullPath);
            }
        }

        if (bestError is not null)
        {
            return WithFileInfo(bestError, fileInfo);
        }

        return CreateUnreadable(fullPath, hintedCategory,
            "The file is not a readable supported media file.", fileInfo.Length);
    }

    public async Task<IReadOnlyList<MediaInfo>> InspectManyAsync(IEnumerable<string> paths,
        MediaCategory? categoryHint = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = paths.ToList();
        var results = new MediaInfo[list.Count];
        using var semaphore = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        var completed = 0;
        var tasks = list.Select(async (path, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = await InspectAsync(path, categoryHint, cancellationToken);
            }
            finally
            {
                semaphore.Release();
                progress?.Report(Interlocked.Increment(ref completed));
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    public bool IsSupportedExtension(string path, MediaCategory category)
    {
        var ext = Path.GetExtension(path);
        return FormatCatalog.GetInputExtensions(category).Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetSupportedExtensions(MediaCategory category) =>
        FormatCatalog.GetInputExtensions(category);

    private IReadOnlyList<IProcessingEngine> GetEngineOrder(MediaCategory category)
    {
        var image = _engines.FirstOrDefault(e => e.Id == EngineIds.Images);
        var ffmpeg = _engines.FirstOrDefault(e => e.Id == EngineIds.FFmpeg);
        var upscaler = _engines.FirstOrDefault(e => e.Id == EngineIds.Upscaling);
        var documents = _engines.FirstOrDefault(e => e.Id == EngineIds.Documents);
        var archives = _engines.FirstOrDefault(e => e.Id == EngineIds.Archives);
        return category switch
        {
            MediaCategory.Image => new[] { image, ffmpeg }.Where(e => e is not null).Cast<IProcessingEngine>().ToList(),
            MediaCategory.Audio => new[] { ffmpeg, image }.Where(e => e is not null).Cast<IProcessingEngine>().ToList(),
            MediaCategory.Video => new[] { ffmpeg, image }.Where(e => e is not null).Cast<IProcessingEngine>().ToList(),
            MediaCategory.Document or MediaCategory.Ebook => new[] { documents }.Where(e => e is not null).Cast<IProcessingEngine>().ToList(),
            MediaCategory.Archive => new[] { archives }.Where(e => e is not null).Cast<IProcessingEngine>().ToList(),
            _ => new[] { image, ffmpeg, upscaler, documents, archives }.Where(e => e is not null).Cast<IProcessingEngine>().ToList()
        };
    }

    private static MediaInfo WithFileInfo(MediaInfo info, FileInfo fileInfo) => info with
    {
        FilePath = fileInfo.FullName,
        FileName = fileInfo.Name,
        Extension = fileInfo.Extension.ToLowerInvariant(),
        FileSizeBytes = fileInfo.Length
    };

    private static MediaInfo CreateUnreadable(string path, MediaCategory category, string error, long size = 0) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path).ToLowerInvariant(),
        FileSizeBytes = size,
        Category = category == MediaCategory.Unknown ? MediaCategory.Unknown : category,
        Error = error
    };
}
