using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.Archives;

public sealed class ArchiveToolLocator
{
    private ArchiveToolLocator(string? sevenZip, string? zstd)
    {
        SevenZipPath = sevenZip;
        ZstdPath = zstd;
    }

    public string? SevenZipPath { get; }
    public string? ZstdPath { get; }
    public bool IsAvailable => SevenZipPath is not null;

    public static ArchiveToolLocator Discover()
    {
        var baseDir = AppContext.BaseDirectory;
        var seven = new List<string>
        {
            Path.Combine(baseDir, "archives", "7z.exe"),
            Path.Combine(baseDir, "archives", "7za.exe"),
            Path.Combine(baseDir, "7z.exe"),
            Path.Combine(baseDir, "7za.exe")
        };
        var zstd = new List<string>
        {
            Path.Combine(baseDir, "archives", "zstd.exe"),
            Path.Combine(baseDir, "zstd.exe")
        };
        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            seven.Add(Path.Combine(current.FullName, "third_party", "archives", "7z.exe"));
            seven.Add(Path.Combine(current.FullName, "third_party", "archives", "7za.exe"));
            zstd.Add(Path.Combine(current.FullName, "third_party", "archives", "zstd.exe"));
        }

        return new ArchiveToolLocator(seven.FirstOrDefault(File.Exists), zstd.FirstOrDefault(File.Exists));
    }
}

public sealed class ArchiveEngine : IProcessingEngine
{
    private readonly IProcessRunner _runner;
    private readonly IAppPaths _paths;
    private readonly ILogger<ArchiveEngine> _logger;
    private readonly ArchiveToolLocator _tools;

    public ArchiveEngine(IProcessRunner runner, IAppPaths paths, ILogger<ArchiveEngine> logger)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
        _tools = ArchiveToolLocator.Discover();
    }

    public string Id => EngineIds.Archives;
    public string DisplayName => "Archive engine";
    public MediaCategory[] Categories => [MediaCategory.Archive];

    public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_tools.IsAvailable)
            return Task.FromResult(new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = "The archive engine is unavailable. The bundled 7-Zip command-line tool was not found."
            });

        var formats = ContentFormatCatalog.ArchiveFormats.Select(definition =>
        {
            var canWrite = definition.Id != "zst" || _tools.ZstdPath is not null;
            return definition.ToMediaFormat(Id, true, canWrite);
        }).ToList();

        return Task.FromResult(new EngineCapability
        {
            EngineId = Id,
            DisplayName = DisplayName,
            Version = "7-Zip 26.06" + (_tools.ZstdPath is null ? "" : " + Zstandard 1.5.7"),
            IsAvailable = true,
            Formats = formats,
            ExecutionProviders =
            [
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = true,
                    Details = "Archive operations run locally on the CPU."
                }
            ]
        });
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return Task.FromResult<MediaInfo?>(null);
        var definition = ContentFormatCatalog.FindByExtension(Path.GetExtension(fullPath));
        if (definition is null || definition.Category != MediaCategory.Archive)
            return Task.FromResult<MediaInfo?>(null);
        try
        {
            var file = new FileInfo(fullPath);
            if (!LooksLikeFormat(fullPath, definition.Id))
                return Task.FromResult<MediaInfo?>(new MediaInfo
                {
                    FilePath = fullPath,
                    FileName = file.Name,
                    Extension = file.Extension,
                    FileSizeBytes = file.Length,
                    Category = MediaCategory.Archive,
                    FormatId = definition.Id,
                    FormatName = definition.DisplayName,
                    EngineId = Id,
                    Error = $"The file contents do not match the {definition.DisplayName} signature."
                });
            return Task.FromResult<MediaInfo?>(new MediaInfo
            {
                FilePath = fullPath,
                FileName = file.Name,
                Extension = file.Extension,
                FileSizeBytes = file.Length,
                Category = MediaCategory.Archive,
                FormatId = definition.Id,
                FormatName = definition.DisplayName,
                EngineId = Id
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<MediaInfo?>(new MediaInfo
            {
                FilePath = fullPath, FileName = Path.GetFileName(fullPath), Extension = Path.GetExtension(fullPath),
                FileSizeBytes = new FileInfo(fullPath).Length, Category = MediaCategory.Archive,
                EngineId = Id, Error = ex.Message
            });
        }
    }

    public Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Settings is not ArchiveConversionSettings settings)
            return Task.FromResult(ValidationResult.Failure("archive.settings", "The archive settings are invalid."));
        if (!_tools.IsAvailable)
            return Task.FromResult(ValidationResult.Failure("archive.engine", "The archive engine is unavailable."));
        var target = ContentFormatCatalog.FindById(settings.OutputFormatId);
        if (settings.Action != ArchiveAction.Extract &&
            (target is null || target.Category != MediaCategory.Archive))
            return Task.FromResult(ValidationResult.Failure("archive.format", "The selected archive format is not supported."));
        if (target is { Id: "zst" } && _tools.ZstdPath is null)
            return Task.FromResult(ValidationResult.Failure("archive.zstd", "The bundled Zstandard tool is unavailable."));
        if (settings.Action is ArchiveAction.Repackage or ArchiveAction.Extract &&
            request.InputInfo?.Category != MediaCategory.Archive)
            return Task.FromResult(ValidationResult.Failure("archive.input",
                settings.Action == ArchiveAction.Extract
                    ? "Extraction requires an archive input."
                    : "Repackaging requires an archive input."));
        return Task.FromResult(ValidationResult.Success());
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request.Settings is not ArchiveConversionSettings settings)
            return ProcessingResult.Fail("The archive settings are invalid.");
        if (settings.Action == ArchiveAction.Extract)
            return await ProcessExtractAsync(request, settings, progress, stopwatch, cancellationToken);

        var target = ContentFormatCatalog.FindById(settings.OutputFormatId);
        if (target is null) return ProcessingResult.Fail("The archive target format is not supported.");

        var workDir = Path.Combine(_paths.TempDirectory, "archives", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var outputPath = Path.Combine(workDir, "output" + target.Extension);
            progress.Report(ProcessingProgress.Percent(0.1, "Preparing archive."));
            ProcessResult result;
            if (settings.Action == ArchiveAction.Repackage)
            {
                var maxEntries = Math.Clamp(settings.MaxEntryCount, 1, HardMaxEntryCount);
                var maxExpanded = Math.Clamp(settings.MaxExpandedBytes, 1, HardMaxExpandedBytes);
                var extractDir = Path.Combine(workDir, "extracted");
                Directory.CreateDirectory(extractDir);
                var listing = await ListEntriesAsync(request.InputPath, cancellationToken);
                if (listing.Error is not null) return ProcessingResult.Fail(listing.Error);
                if (listing.Count > maxEntries)
                    return ProcessingResult.Fail($"The archive contains too many entries ({listing.Count:N0}).");
                if (listing.TotalSize > maxExpanded)
                    return ProcessingResult.Fail("The expanded archive is larger than the configured safety limit.");
                var unsafePath = listing.Paths.FirstOrDefault(IsUnsafeEntryPath);
                if (unsafePath is not null)
                    return ProcessingResult.Fail($"The archive contains an unsafe path: {unsafePath}");

                var extract = await Run7zaAsync(["x", request.InputPath, $"-o{extractDir}", "-y"], workDir, cancellationToken);
                if (!extract.Succeeded)
                    return ProcessingResult.Fail("The archive could not be extracted.", Tail(extract.StandardError), stopwatch.Elapsed);
                var extractedVerification = VerifyExtractedFolder(extractDir, maxExpanded, requireFiles: false);
                if (!extractedVerification.IsValid)
                    return ProcessingResult.Fail(extractedVerification.Summary,
                        extractedVerification.Details, stopwatch.Elapsed);
                var entries = Directory.EnumerateFileSystemEntries(extractDir).ToList();
                if (settings.OutputFormatId is "gz" or "bz2" or "xz" or "zst" && entries.Count != 1)
                    return ProcessingResult.Fail($"A .{settings.OutputFormatId} file can contain only one compressed file; the archive has {entries.Count} top-level entries.");
                result = await CreateAsync(extractDir, outputPath, target, settings.CompressionLevel, workDir, cancellationToken);
            }
            else
            {
                result = await CreateAsync(Path.GetDirectoryName(request.InputPath)!, outputPath, target,
                    settings.CompressionLevel, workDir, cancellationToken, Path.GetFileName(request.InputPath));
            }

            if (!result.Succeeded)
                return ProcessingResult.Fail("The archive could not be created.", Tail(result.StandardError), stopwatch.Elapsed);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return ProcessingResult.Fail("The archive engine produced no output.");
            var verification = await VerifyOutputAsync(request, outputPath, cancellationToken);
            if (!verification.IsValid)
                return ProcessingResult.Fail(verification.Summary, verification.Details, stopwatch.Elapsed);
            if (File.Exists(request.TempOutputPath)) File.Delete(request.TempOutputPath);
            File.Copy(outputPath, request.TempOutputPath, overwrite: true);
            stopwatch.Stop();
            progress.Report(ProcessingProgress.Percent(1, "Archive ready."));
            var info = new MediaInfo
            {
                FilePath = request.TempOutputPath,
                FileName = Path.GetFileName(request.TempOutputPath),
                Extension = target.Extension,
                FileSizeBytes = new FileInfo(request.TempOutputPath).Length,
                Category = MediaCategory.Archive,
                FormatId = target.Id,
                FormatName = target.DisplayName,
                EngineId = Id
            };
            return ProcessingResult.Ok(request.TempOutputPath, info, info.FileSizeBytes, stopwatch.Elapsed, verification);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive operation failed for {Path}", request.InputPath);
            return ProcessingResult.Fail("The archive operation failed.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch { }
        }
    }

    private async Task<ProcessingResult> ProcessExtractAsync(ProcessingRequest request, ArchiveConversionSettings settings,
        IProgress<ProcessingProgress> progress, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var workDir = Path.Combine(_paths.TempDirectory, "archives", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            progress.Report(ProcessingProgress.Percent(0.1, "Reading archive contents…"));
            var listing = await ListEntriesAsync(request.InputPath, cancellationToken);
            if (listing.Error is not null) return ProcessingResult.Fail(listing.Error);
            var maxEntries = Math.Clamp(settings.MaxEntryCount, 1, HardMaxEntryCount);
            var maxExpanded = Math.Clamp(settings.MaxExpandedBytes, 1, HardMaxExpandedBytes);
            if (listing.Count > maxEntries)
                return ProcessingResult.Fail($"The archive contains too many entries ({listing.Count:N0}).");
            if (listing.TotalSize > maxExpanded)
                return ProcessingResult.Fail("The expanded archive is larger than the configured safety limit.");
            var unsafePath = listing.Paths.FirstOrDefault(IsUnsafeEntryPath);
            if (unsafePath is not null)
                return ProcessingResult.Fail($"The archive contains an unsafe path: {unsafePath}");

            var extractDir = Path.Combine(workDir, "extracted");
            Directory.CreateDirectory(extractDir);
            progress.Report(ProcessingProgress.Percent(0.35, "Extracting archive…"));
            var extract = await Run7zaAsync(["x", request.InputPath, $"-o{extractDir}", "-y"], workDir, cancellationToken);
            if (!extract.Succeeded)
                return ProcessingResult.Fail("The archive could not be extracted. It may be corrupted or password-protected.",
                    Tail(extract.StandardError), stopwatch.Elapsed);

            progress.Report(ProcessingProgress.Percent(0.8, "Validating extracted files…"));
            var verification = VerifyExtractedFolder(extractDir, maxExpanded);
            if (!verification.IsValid)
                return ProcessingResult.Fail(verification.Summary, verification.Details, stopwatch.Elapsed);

            if (Directory.Exists(request.TempOutputPath))
                Directory.Delete(request.TempOutputPath, recursive: true);
            CopyDirectory(extractDir, request.TempOutputPath);
            stopwatch.Stop();
            progress.Report(ProcessingProgress.Percent(1, "Archive extracted."));
            var info = verification.ActualInfo! with
            {
                FilePath = request.TempOutputPath,
                FileName = Path.GetFileName(request.TempOutputPath)
            };
            return ProcessingResult.Ok(request.TempOutputPath, info, info.FileSizeBytes, stopwatch.Elapsed, verification)
                with { IsDirectoryOutput = true };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive extraction failed for {Path}", request.InputPath);
            return ProcessingResult.Fail("The archive could not be extracted.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private VerificationResult VerifyExtractedFolder(string path, long maxExpandedBytes, bool requireFiles = true)
    {
        if (!Directory.Exists(path))
            return VerificationResult.Fail("The extracted folder does not exist.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true
        };
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                return VerificationResult.Fail("The archive contains a symbolic link or reparse point and was rejected.");
        }
        var files = Directory.EnumerateFiles(path, "*", options).ToList();
        if (requireFiles && files.Count == 0)
            return VerificationResult.Fail("The archive produced an empty folder.");
        long total = 0;
        foreach (var file in files)
        {
            try { total += new FileInfo(file).Length; }
            catch { }
        }

        if (total > maxExpandedBytes)
            return VerificationResult.Fail("The expanded archive is larger than the configured safety limit.");
        var info = new MediaInfo
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Extension = "",
            FileSizeBytes = total,
            Category = MediaCategory.Archive,
            FormatId = "folder",
            FormatName = "Extracted archive",
            EngineId = Id,
            Tags = new Dictionary<string, string>
            {
                ["Entries"] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ExpandedBytes"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        return VerificationResult.Ok(info,
            $"Extracted folder · {files.Count} file{(files.Count == 1 ? "" : "s")} · {FormatBytes(total)}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true
        };
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", options))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The extracted archive contains a reparse point and cannot be copied safely.");
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var relative = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(destination, relative), overwrite: true);
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (request.Settings is ArchiveConversionSettings { Action: ArchiveAction.Extract } extract)
            return VerifyExtractedFolder(outputPath, extract.MaxExpandedBytes);
        var target = request.Settings is ArchiveConversionSettings s ? ContentFormatCatalog.FindById(s.OutputFormatId) : null;
        if (target is null) return VerificationResult.Fail("The archive output format is unknown.");
        if (target.Id == "zst")
        {
            var test = await RunZstdAsync(["-t", outputPath], cancellationToken);
            if (!test.Succeeded) return VerificationResult.Fail("Zstandard validation failed.", details: Tail(test.StandardError));
        }
        else
        {
            var test = await Run7zaAsync(["t", outputPath], Path.GetDirectoryName(outputPath)!, cancellationToken);
            if (!test.Succeeded) return VerificationResult.Fail("Archive validation failed.", details: Tail(test.StandardError));
        }

        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length == 0) return VerificationResult.Fail("The archive output is empty.");
        var info = new MediaInfo
        {
            FilePath = outputPath,
            FileName = file.Name,
            Extension = target.Extension,
            FileSizeBytes = file.Length,
            Category = MediaCategory.Archive,
            FormatId = target.Id,
            FormatName = target.DisplayName,
            EngineId = Id
        };
        return VerificationResult.Ok(info, $"{target.DisplayName} · {file.Length / 1024d / 1024:0.0} MB · verified");
    }

    public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    private async Task<ProcessResult> CreateAsync(string workingDirectory, string outputPath, FormatDefinition target,
        int level, string workDir, CancellationToken cancellationToken, string? singleEntry = null)
    {
        var boundedLevel = Math.Clamp(level, 1, 9);
        if (target.Id == "zst")
        {
            var input = singleEntry is not null
                ? Path.Combine(workingDirectory, singleEntry)
                : Directory.EnumerateFileSystemEntries(workingDirectory).FirstOrDefault()
                  ?? throw new InvalidOperationException("The archive has no entries to compress.");
            return await RunZstdAsync(["-q", "-f", $"-{Math.Clamp(level, 1, 19)}", "-o", outputPath, input], cancellationToken);
        }

        var args = new List<string> { "a", "-t" + (target.TechnicalId ?? target.Id), outputPath, "-y", $"-mx={boundedLevel}" };
        if (singleEntry is not null)
        {
            // Never pass a bare file name: 7-Zip treats a leading '-' as a
            // command switch. Prefixing with a relative directory keeps the
            // stored entry name while guaranteeing an operand.
            args.Add("." + Path.DirectorySeparatorChar + singleEntry);
        }
        else
        {
            args.Add("*");
            args.Add("-r");
        }
        return await Run7zaAsync(args, workingDirectory, cancellationToken);
    }

    private async Task<(IReadOnlyList<string> Paths, long TotalSize, int Count, string? Error)> ListEntriesAsync(
        string archivePath, CancellationToken cancellationToken)
    {
        var definition = ContentFormatCatalog.FindByExtension(Path.GetExtension(archivePath));
        if (definition is null) return ([], 0, 0, "The archive format is not recognized.");
        if (definition.Id == "zst") return (["<single file>"], new FileInfo(archivePath).Length, 1, null);

        var result = await Run7zaAsync(["l", "-slt", archivePath], Path.GetDirectoryName(archivePath)!, cancellationToken);
        if (!result.Succeeded) return ([], 0, 0, "The archive could not be listed.");
        var paths = new List<string>();
        long total = 0;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Path = ", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[7..];
                if (value is not "-" and not "") paths.Add(value);
            }
            else if (trimmed.StartsWith("Size = ", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(trimmed[7..], out var size))
            {
                total += size;
            }
        }

        // The first Path entry is the archive itself in 7-Zip's listing. It can be
        // an absolute path, so do not rely on the file name alone.
        if (paths.Count > 0 &&
            (string.Equals(paths[0], archivePath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileName(paths[0]), Path.GetFileName(archivePath), StringComparison.OrdinalIgnoreCase)))
            paths.RemoveAt(0);
        return (paths, total, paths.Count, null);
    }

    private const int HardMaxEntryCount = 100_000;
    private const long HardMaxExpandedBytes = 20L * 1024 * 1024 * 1024;

    private static bool IsUnsafeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.IndexOf('\0') >= 0) return true;
        if (path.Any(char.IsControl)) return true;

        var normalized = path.Replace('/', '\\');
        // Absolute, UNC, device and drive-qualified names are never valid
        // relative archive entries. Reject alternate data stream colons too.
        if (normalized.StartsWith('\\')) return true;
        if (normalized.IndexOf(':') >= 0) return true;
        return normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(part =>
            {
                // Windows strips trailing dots/spaces, so ".. " can normalize
                // to ".."; reject any such component outright.
                var trimmed = part.TrimEnd(' ', '.');
                return trimmed.Length == 0 ||
                       trimmed is "." or ".." ||
                       part.EndsWith('.') ||
                       part.EndsWith(' ');
            });
    }

    private async Task<ProcessResult> Run7zaAsync(IReadOnlyList<string> args, string workingDirectory,
        CancellationToken cancellationToken) =>
        await _runner.RunAsync(new ProcessRequest
        {
            FileName = _tools.SevenZipPath!,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromMinutes(15),
            MaxCapturedLines = 400
        }, cancellationToken);

    private async Task<ProcessResult> RunZstdAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        await _runner.RunAsync(new ProcessRequest
        {
            FileName = _tools.ZstdPath!,
            Arguments = args,
            WorkingDirectory = _paths.TempDirectory,
            Timeout = TimeSpan.FromMinutes(15),
            MaxCapturedLines = 200
        }, cancellationToken);

    private static bool LooksLikeFormat(string path, string id)
    {
        Span<byte> header = stackalloc byte[512];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read <= 0) return false;
        var data = header[..read];
        return id switch
        {
            "zip" => data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B &&
                     (data[2] == 0x03 || data[2] == 0x05 || data[2] == 0x07),
            "7z" => data.Length >= 6 && data[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }),
            "gz" => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B,
            "bz2" => data.Length >= 3 && data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h',
            "xz" => data.Length >= 6 && data[..6].SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }),
            "zst" => data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD,
            "tar" => data.Length >= 262 && Encoding.ASCII.GetString(data.Slice(257, 5).ToArray()) == "ustar",
            _ => false
        };
    }

    private static string? Tail(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
    }
}
