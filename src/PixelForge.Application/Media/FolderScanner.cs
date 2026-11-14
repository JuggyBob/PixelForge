using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Media;

public sealed class FolderScanner : IFolderScanner
{
    private const int MaxFiles = 50_000;
    private readonly IMediaInspector _inspector;
    private readonly ICapabilityRegistry _registry;
    private readonly ILogger<FolderScanner> _logger;

    public FolderScanner(IMediaInspector inspector, ICapabilityRegistry registry, ILogger<FolderScanner> logger)
    {
        _inspector = inspector;
        _registry = registry;
        _logger = logger;
    }

    public async Task<FolderScanResult> ScanAsync(FolderScanRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!Directory.Exists(request.FolderPath))
        {
            return new FolderScanResult
            {
                RootPath = request.FolderPath,
                Recursive = request.Recursive,
                TargetCategory = request.TargetCategory,
                Warning = "The selected folder does not exist.",
                Duration = stopwatch.Elapsed
            };
        }

        var excludeDirectory = NormalizeDirectory(request.ExcludeDirectory);
        var root = Path.GetFullPath(request.FolderPath);
        if (excludeDirectory is not null && string.Equals(root, excludeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return new FolderScanResult
            {
                RootPath = root,
                Recursive = request.Recursive,
                TargetCategory = request.TargetCategory,
                Warning = "The selected folder is the output folder. Choose a different input folder to avoid converting generated files.",
                Duration = stopwatch.Elapsed
            };
        }

        var files = new List<string>();
        var truncated = false;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = request.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                ReturnSpecialDirectories = false
            };
            foreach (var file in Directory.EnumerateFiles(request.FolderPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (excludeDirectory is not null && IsInside(file, excludeDirectory)) continue;
                files.Add(file);
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Folder scan stopped early for {Folder}", request.FolderPath);
        }

        var engineExtensions = GetEngineExtensions(request.TargetCategory);
        var catalogExtensions = new HashSet<string>(FormatCatalog.GetInputExtensions(request.TargetCategory),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(string Path, string Relative, bool KnownTargetExtension)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file);
            var relative = Path.GetRelativePath(request.FolderPath, file);
            var known = engineExtensions.Contains(extension) || catalogExtensions.Contains(extension);
            if (known)
            {
                candidates.Add((file, relative, true));
                continue;
            }

            // Only sniff image headers. Other categories have distinctive
            // container extensions and must not be opened speculatively.
            if (request.TargetCategory == MediaCategory.Image && LooksLikeImage(file))
                candidates.Add((file, relative, true));
            else
                candidates.Add((file, relative, false));
        }

        var results = new ScannedFile[candidates.Count];
        using var semaphore = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        var completed = 0;
        var tasks = candidates.Select(async (candidate, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = await EvaluateAsync(candidate.Path, candidate.Relative, candidate.KnownTargetExtension,
                    request, engineExtensions, cancellationToken);
            }
            finally
            {
                semaphore.Release();
                progress?.Report(Interlocked.Increment(ref completed));
            }
        });
        await Task.WhenAll(tasks);
        stopwatch.Stop();

        var warning = truncated ? $"Only the first {MaxFiles:N0} files were scanned." : null;
        var result = new FolderScanResult
        {
            RootPath = root,
            Recursive = request.Recursive,
            TargetCategory = request.TargetCategory,
            Files = results.OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            Duration = stopwatch.Elapsed,
            Warning = warning
        };

        _logger.LogInformation("Folder scan {Folder} for {Category}: {Summary}", request.FolderPath,
            request.TargetCategory, result.Summary);
        return result;
    }

    private async Task<ScannedFile> EvaluateAsync(string path, string relativePath, bool knownTargetExtension,
        FolderScanRequest request, HashSet<string> engineExtensions, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        var relativeDirectory = request.Recursive && request.PreserveFolderStructure
            ? Path.GetDirectoryName(relativePath)
            : null;
        var target = request.TargetCategory;

        if (!knownTargetExtension)
        {
            var category = FormatCatalog.DetectCategoryByExtension(fileInfo.Extension);
            return new ScannedFile
            {
                Path = fileInfo.FullName,
                RelativePath = relativePath,
                RelativeDirectory = relativeDirectory,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                DetectedCategory = category,
                IsSupported = false,
                IsValid = false,
                Reason = category == MediaCategory.Unknown
                    ? $"Not a supported {CategoryNoun(target)} file."
                    : $"{CategoryNoun(category)} — not a {CategoryNoun(target)}."
            };
        }

        var info = await _inspector.InspectAsync(path, null, cancellationToken);
        var supportedByEngine = engineExtensions.Contains(info.Extension) ||
                                engineExtensions.Contains(fileInfo.Extension);
        if (!info.IsReadable)
        {
            return new ScannedFile
            {
                Path = fileInfo.FullName,
                RelativePath = relativePath,
                RelativeDirectory = relativeDirectory,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                DetectedCategory = target,
                IsSupported = supportedByEngine,
                IsValid = false,
                Reason = info.Error ?? $"The {CategoryNoun(target)} could not be read.",
                Info = info
            };
        }

        if (info.Category != target)
        {
            return new ScannedFile
            {
                Path = fileInfo.FullName,
                RelativePath = relativePath,
                RelativeDirectory = relativeDirectory,
                SizeBytes = info.FileSizeBytes,
                DetectedCategory = info.Category,
                IsSupported = false,
                IsValid = false,
                Reason = $"The file content is {CategoryNoun(info.Category)}, not a {CategoryNoun(target)}.",
                Info = info
            };
        }

        if (request.RejectAnimated && (info.IsAnimated || info.IsMultipage))
        {
            return new ScannedFile
            {
                Path = fileInfo.FullName,
                RelativePath = relativePath,
                RelativeDirectory = relativeDirectory,
                SizeBytes = info.FileSizeBytes,
                DetectedCategory = target,
                IsSupported = false,
                IsValid = false,
                Reason = "Animated or multi-page images are not supported for upscaling because the animation cannot be preserved.",
                Info = info
            };
        }

        if (request.RejectVector && (info.IsVector || string.Equals(info.Extension, ".svg", StringComparison.OrdinalIgnoreCase)))
        {
            return new ScannedFile
            {
                Path = fileInfo.FullName,
                RelativePath = relativePath,
                RelativeDirectory = relativeDirectory,
                SizeBytes = info.FileSizeBytes,
                DetectedCategory = target,
                IsSupported = false,
                IsValid = false,
                Reason = "Vector images must be rasterized at a chosen resolution before upscaling; use the image converter.",
                Info = info
            };
        }

        var actualExtension = info.Extension;
        var supported = engineExtensions.Contains(actualExtension) || engineExtensions.Contains(fileInfo.Extension);
        var reason = !supported
            ? $"The detected format {actualExtension} is not supported by the installed {CategoryNoun(target)} engine."
            : !string.Equals(actualExtension, fileInfo.Extension, StringComparison.OrdinalIgnoreCase)
                ? $"Extension does not match content; detected {actualExtension.TrimStart('.').ToUpperInvariant()}."
                : null;

        return new ScannedFile
        {
            Path = fileInfo.FullName,
            RelativePath = relativePath,
            RelativeDirectory = relativeDirectory,
            SizeBytes = info.FileSizeBytes,
            DetectedCategory = target,
            IsSupported = supported,
            IsValid = supported,
            Reason = reason,
            Info = info
        };
    }

    private HashSet<string> GetEngineExtensions(MediaCategory category)
    {
        var engineId = category switch
        {
            MediaCategory.Image => EngineIds.Images,
            MediaCategory.Audio or MediaCategory.Video => EngineIds.FFmpeg,
            MediaCategory.Document or MediaCategory.Ebook => EngineIds.Documents,
            MediaCategory.Archive => EngineIds.Archives,
            _ => null
        };
        var engine = engineId is null ? null : _registry.GetEngine(engineId);
        if (engine is null || !engine.IsAvailable)
            return new HashSet<string>(FormatCatalog.GetInputExtensions(category), StringComparer.OrdinalIgnoreCase);

        return engine.Formats
            .Where(f => f.CanRead && (category is MediaCategory.Audio or MediaCategory.Video
                ? f.Category == category
                : true))
            .SelectMany(f => new[] { f.NormalizedExtension }.Concat(f.Aliases.Select(NormalizeExtension)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension) => extension.StartsWith('.')
        ? extension
        : "." + extension;

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch { return null; }
    }

    private static bool IsInside(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CategoryNoun(MediaCategory category) => category switch
    {
        MediaCategory.Image => "image",
        MediaCategory.Audio => "audio file",
        MediaCategory.Video => "video",
        MediaCategory.Document => "document",
        MediaCategory.Ebook => "e-book",
        MediaCategory.Archive => "archive",
        _ => "file"
    };

    private static bool LooksLikeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[32];
            var read = stream.Read(header);
            if (read < 4) return false;
            ReadOnlySpan<byte> h = header[..read];

            if (StartsWithBytes(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return true; // PNG
            if (StartsWithBytes(h, 0xFF, 0xD8, 0xFF)) return true; // JPEG
            if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)) return true;
            if (h.StartsWith("BM"u8)) return true;
            if (StartsWithBytes(h, 0x49, 0x49, 0x2A, 0x00) || StartsWithBytes(h, 0x4D, 0x4D, 0x00, 0x2A)) return true; // TIFF
            if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return true;
            if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
            {
                var brand = System.Text.Encoding.ASCII.GetString(h[8..12]);
                if (brand is "avif" or "avis" or "heic" or "heix" or "mif1" or "msf1") return true;
            }
            if (StartsWithBytes(h, 0x00, 0x00, 0x01, 0x00)) return true; // ICO
            if (h.StartsWith("8BPS"u8)) return true; // PSD
            if (StartsWithBytes(h, 0x76, 0x2F, 0x31, 0x01)) return true; // EXR
            if (h.StartsWith("qoif"u8)) return true;
            if (StartsWithBytes(h, 0xFF, 0x0A) ||
                StartsWithBytes(h, 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return true; // JXL
            if (h[0] == (byte)'P' && h[1] is >= (byte)'1' and <= (byte)'6') return true; // PNM
            if (h.StartsWith("<?xml"u8) || h.StartsWith("<svg"u8)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartsWithBytes(ReadOnlySpan<byte> data, params byte[] prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
