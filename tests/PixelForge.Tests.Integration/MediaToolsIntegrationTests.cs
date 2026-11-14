using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Engines.Archives;
using PixelForge.Engines.Documents;
using PixelForge.Engines.FFmpegTools;
using PixelForge.Engines.Images;
using PixelForge.Engines.ImageTools;
using PixelForge.Infrastructure.ProcessExecution;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class MediaToolsIntegrationTests
{
    [Fact]
    public async Task Markdown_converts_to_docx_and_pdf()
    {
        using var paths = new TempAppPaths();
        var source = Path.Combine(paths.TempDirectory, "sample.md");
        await File.WriteAllTextAsync(source,
            "# PixelForge test\n\nUnicode: café, naïve, 日本語.\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n- one\n- two\n");
        var engine = new DocumentEngine(new ProcessRunner(), paths, NullLogger<DocumentEngine>.Instance);
        var capability = await engine.ProbeAsync();
        Assert.True(capability.IsAvailable, capability.Error);

        foreach (var format in new[] { "docx", "pdf" })
        {
            var settings = new DocumentConversionSettings
            {
                OutputFormatId = format,
                Title = "PixelForge test",
                PreserveMetadata = true
            };
            var info = new MediaInfo
            {
                FilePath = source,
                FileName = Path.GetFileName(source),
                Extension = ".md",
                FileSizeBytes = new FileInfo(source).Length,
                Category = MediaCategory.Document,
                FormatId = "md",
                EngineId = EngineIds.Documents
            };
            var request = new ProcessingRequest
            {
                JobId = Guid.NewGuid(),
                ItemId = Guid.NewGuid(),
                Operation = OperationKind.DocumentConvert,
                Settings = settings,
                InputPath = source,
                TempOutputPath = Path.Combine(paths.TempDirectory, "sample." + format),
                OutputFormatId = format,
                TempDirectory = paths.TempDirectory,
                InputInfo = info
            };
            var validation = await engine.ValidateAsync(request);
            Assert.True(validation.IsValid, validation.Summary);
            var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
            Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);
            Assert.True(File.Exists(request.TempOutputPath));
            Assert.True(new FileInfo(request.TempOutputPath).Length > 100);
            var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
            Assert.True(verification.IsValid, verification.Summary);
        }
    }

    [Fact]
    public async Task Archive_creates_zip_and_extracts_the_same_contents()
    {
        using var paths = new TempAppPaths();
        var sourceDirectory = Path.Combine(paths.TempDirectory, "archive-source");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        var source = Path.Combine(sourceDirectory, "hello.txt");
        await File.WriteAllTextAsync(source, "hello archive");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "nested", "unicode.txt"), "nested content");

        var engine = new ArchiveEngine(new ProcessRunner(), paths, NullLogger<ArchiveEngine>.Instance);
        var capability = await engine.ProbeAsync();
        Assert.True(capability.IsAvailable, capability.Error);

        var createSettings = new ArchiveConversionSettings
        {
            OutputFormatId = "zip",
            Action = ArchiveAction.Create,
            CompressionLevel = 5
        };
        var createRequest = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ArchiveConvert,
            Settings = createSettings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "hello.zip"),
            OutputFormatId = "zip",
            TempDirectory = paths.TempDirectory
        };
        var createResult = await engine.ProcessAsync(createRequest, new Progress<ProcessingProgress>());
        Assert.True(createResult.Success, createResult.Error + Environment.NewLine + createResult.ErrorDetails);
        Assert.True(File.Exists(createRequest.TempOutputPath));
        var createVerification = await engine.VerifyOutputAsync(createRequest, createRequest.TempOutputPath);
        Assert.True(createVerification.IsValid, createVerification.Summary);

        var extractSettings = new ArchiveConversionSettings
        {
            OutputFormatId = "zip",
            Action = ArchiveAction.Extract
        };
        var extractDirectory = Path.Combine(paths.TempDirectory, "extracted");
        var archiveInfo = new MediaInfo
        {
            FilePath = createRequest.TempOutputPath,
            FileName = "hello.zip",
            Extension = ".zip",
            FileSizeBytes = new FileInfo(createRequest.TempOutputPath).Length,
            Category = MediaCategory.Archive,
            FormatId = "zip",
            EngineId = EngineIds.Archives
        };
        var extractRequest = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ArchiveConvert,
            Settings = extractSettings,
            InputPath = createRequest.TempOutputPath,
            TempOutputPath = extractDirectory,
            OutputFormatId = "folder",
            TempDirectory = paths.TempDirectory,
            InputInfo = archiveInfo
        };
        var extractResult = await engine.ProcessAsync(extractRequest, new Progress<ProcessingProgress>());
        Assert.True(extractResult.Success, extractResult.Error + Environment.NewLine + extractResult.ErrorDetails);
        Assert.True(extractResult.IsDirectoryOutput);
        Assert.True(Directory.Exists(extractDirectory));
        var extracted = Path.Combine(extractDirectory, "hello.txt");
        Assert.True(File.Exists(extracted));
        Assert.Equal("hello archive", await File.ReadAllTextAsync(extracted));
    }

    [Fact]
    public async Task Image_tools_remove_background_to_transparent_png()
    {
        var source = RepoPaths.Fixture("transparent.png");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");
        using var paths = new TempAppPaths();
        var imageEngine = new ImageEngine(NullLogger<ImageEngine>.Instance);
        using var engine = new ImageToolsEngine(imageEngine, paths, NullLogger<ImageToolsEngine>.Instance);
        var info = await engine.TryInspectAsync(source, MediaCategory.Image);
        Assert.NotNull(info);
        Assert.True(info!.IsReadable);
        var settings = new ImageToolSettings
        {
            Tool = ImageToolOperation.RemoveBackground,
            OutputFormatId = "png",
            BackgroundColor = "transparent",
            ExecutionProvider = ExecutionProviderKind.Auto
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ImageProcess,
            Settings = settings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "nobg.png"),
            OutputFormatId = "png",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };
        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);
        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);
        Assert.True(File.Exists(request.TempOutputPath));
        var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
        Assert.True(verification.IsValid, verification.Summary);
        Assert.True(verification.ActualInfo?.HasAlpha);
    }

    [Fact]
    public async Task Ffmpeg_tools_trim_and_loudness_normalisation_verify_audio()
    {
        var source = RepoPaths.Fixture("tone.wav");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");
        using var paths = new TempAppPaths();
        var engine = new FFmpegToolsEngine(new ProcessRunner(), paths, NullLogger<FFmpegToolsEngine>.Instance);
        var info = await engine.TryInspectAsync(source, MediaCategory.Audio);
        Assert.NotNull(info);
        Assert.True(info!.IsReadable);

        var trim = new AudioToolSettings
        {
            Tool = AudioToolOperation.Trim,
            OutputFormatId = "mp3",
            BitrateKbps = 192,
            TrimStartSeconds = 0.5,
            TrimEndSeconds = 2
        };
        var trimRequest = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.AudioProcess,
            Settings = trim,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "trim.mp3"),
            OutputFormatId = "mp3",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };
        var trimValidation = await engine.ValidateAsync(trimRequest);
        Assert.True(trimValidation.IsValid, trimValidation.Summary);
        var trimResult = await engine.ProcessAsync(trimRequest, new Progress<ProcessingProgress>());
        Assert.True(trimResult.Success, trimResult.Error + Environment.NewLine + trimResult.ErrorDetails);
        var trimVerification = await engine.VerifyOutputAsync(trimRequest, trimRequest.TempOutputPath);
        Assert.True(trimVerification.IsValid, trimVerification.Summary);
        Assert.InRange(trimVerification.ActualInfo?.DurationSeconds ?? 0, 1.2, 1.8);

        var loudness = new AudioToolSettings
        {
            Tool = AudioToolOperation.NormalizeLoudness,
            OutputFormatId = "wav",
            TargetLufs = -16,
            TruePeakDb = -1.5,
            LoudnessRange = 11
        };
        var loudnessRequest = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.AudioProcess,
            Settings = loudness,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "normalized.wav"),
            OutputFormatId = "wav",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };
        var loudnessResult = await engine.ProcessAsync(loudnessRequest, new Progress<ProcessingProgress>());
        Assert.True(loudnessResult.Success, loudnessResult.Error + Environment.NewLine + loudnessResult.ErrorDetails);
        var loudnessVerification = await engine.VerifyOutputAsync(loudnessRequest, loudnessRequest.TempOutputPath);
        Assert.True(loudnessVerification.IsValid, loudnessVerification.Summary);
        Assert.Contains(loudnessVerification.Issues, issue => issue.Code == "audio.loudness.measured");
    }
}
