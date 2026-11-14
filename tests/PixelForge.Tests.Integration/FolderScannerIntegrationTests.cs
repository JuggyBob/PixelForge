using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Application.Capabilities;
using PixelForge.Application.Media;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.Archives;
using PixelForge.Engines.Documents;
using PixelForge.Engines.FFmpeg;
using PixelForge.Engines.Images;
using PixelForge.Infrastructure.ProcessExecution;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class FolderScannerIntegrationTests
{
    [Fact]
    public async Task Mixed_folder_is_filtered_to_the_requested_converter_category()
    {
        using var paths = new TempAppPaths();
        var root = Path.Combine(paths.TempDirectory, "mixed");
        Directory.CreateDirectory(root);
        File.Copy(RepoPaths.Fixture("transparent.png"), Path.Combine(root, "photo.png"));
        File.Copy(RepoPaths.Fixture("photo-exif-orientation.jpg"), Path.Combine(root, "photo.jpg"));
        File.Copy(RepoPaths.Fixture("tone.mp3"), Path.Combine(root, "tone.mp3"));
        File.Copy(RepoPaths.Fixture("clip-h264.mp4"), Path.Combine(root, "clip.mp4"));
        await File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "# Notes\n\nFolder scan test.");
        await File.WriteAllTextAsync(Path.Combine(root, "unsupported.bin"), "not media");
        var temporaryZip = Path.Combine(paths.TempDirectory, "catalog.zip");
        ZipFile.CreateFromDirectory(root, temporaryZip, CompressionLevel.NoCompression, false);
        File.Move(temporaryZip, Path.Combine(root, "catalog.zip"));

        var services = BuildServices(paths);
        await services.Registry.InitializeAsync();
        var documentEngine = (DocumentEngine)services.Engines.Single(e => e.Id == EngineIds.Documents);
        var epub = Path.Combine(root, "book.epub");
        await ConvertToEpubAsync(documentEngine, Path.Combine(root, "notes.md"), epub, paths);

        var scanner = new FolderScanner(services.Inspector, services.Registry,
            NullLogger<FolderScanner>.Instance);

        var image = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Image,
            Recursive = false
        });
        Assert.Equal(2, image.IncludedCount);
        Assert.All(image.IncludedFiles, f => Assert.Equal(MediaCategory.Image, f.DetectedCategory));
        Assert.True(image.IneligibleCount >= 5);
        Assert.Contains("other files ignored", image.Summary);

        var audio = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Audio,
            Recursive = false
        });
        Assert.Equal(1, audio.IncludedCount);
        Assert.Equal("tone.mp3", Path.GetFileName(audio.IncludedFiles[0].Path));

        var video = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Video,
            Recursive = false
        });
        Assert.Equal(1, video.IncludedCount);
        Assert.Equal("clip.mp4", Path.GetFileName(video.IncludedFiles[0].Path));

        var document = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Document,
            Recursive = false
        });
        Assert.Equal(1, document.IncludedCount);
        Assert.Equal("notes.md", Path.GetFileName(document.IncludedFiles[0].Path));

        var ebook = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Ebook,
            Recursive = false
        });
        Assert.Equal(1, ebook.IncludedCount);
        Assert.Equal("book.epub", Path.GetFileName(ebook.IncludedFiles[0].Path));

        var archive = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Archive,
            Recursive = false
        });
        Assert.Equal(1, archive.IncludedCount);
        Assert.Equal("catalog.zip", Path.GetFileName(archive.IncludedFiles[0].Path));
    }

    [Fact]
    public async Task Output_directory_inside_the_scanned_folder_is_excluded()
    {
        using var paths = new TempAppPaths();
        var root = Path.Combine(paths.TempDirectory, "scan-root");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        File.Copy(RepoPaths.Fixture("transparent.png"), Path.Combine(root, "source.png"));
        File.Copy(RepoPaths.Fixture("photo-exif-orientation.jpg"), Path.Combine(output, "generated.jpg"));

        var services = BuildServices(paths);
        await services.Registry.InitializeAsync();
        var scanner = new FolderScanner(services.Inspector, services.Registry,
            NullLogger<FolderScanner>.Instance);
        var result = await scanner.ScanAsync(new FolderScanRequest
        {
            FolderPath = root,
            TargetCategory = MediaCategory.Image,
            Recursive = true,
            ExcludeDirectory = output
        });

        Assert.Equal(1, result.IncludedCount);
        Assert.Equal("source.png", Path.GetFileName(result.IncludedFiles[0].Path));
        Assert.DoesNotContain(result.Files, f => f.Path.Contains("generated.jpg", StringComparison.OrdinalIgnoreCase));
    }

    private static (IReadOnlyList<IProcessingEngine> Engines, CapabilityRegistry Registry, MediaInspector Inspector)
        BuildServices(TempAppPaths paths)
    {
        var runner = new ProcessRunner();
        var engines = new IProcessingEngine[]
        {
            new ImageEngine(NullLogger<ImageEngine>.Instance),
            new FFmpegEngine(runner, paths, NullLogger<FFmpegEngine>.Instance),
            new DocumentEngine(runner, paths, NullLogger<DocumentEngine>.Instance),
            new ArchiveEngine(runner, paths, NullLogger<ArchiveEngine>.Instance)
        };
        var registry = new CapabilityRegistry(engines, NullLogger<CapabilityRegistry>.Instance);
        var inspector = new MediaInspector(engines, NullLogger<MediaInspector>.Instance);
        return (engines, registry, inspector);
    }

    private static async Task ConvertToEpubAsync(DocumentEngine engine, string markdown, string output,
        TempAppPaths paths)
    {
        var info = new MediaInfo
        {
            FilePath = markdown,
            FileName = Path.GetFileName(markdown),
            Extension = ".md",
            FileSizeBytes = new FileInfo(markdown).Length,
            Category = MediaCategory.Document,
            FormatId = "md",
            EngineId = EngineIds.Documents
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.EbookConvert,
            Settings = new EbookConversionSettings
            {
                OutputFormatId = "epub",
                Title = "Folder scan test",
                Language = "en"
            },
            InputPath = markdown,
            TempOutputPath = output,
            OutputFormatId = "epub",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };
        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);
        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);
        Assert.True(File.Exists(output));
    }
}
