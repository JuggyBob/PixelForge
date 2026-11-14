using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Engines.Images;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class ImageEngineTests
{
    [Fact]
    public async Task Png_with_alpha_converts_to_webp_and_is_verified()
    {
        using var paths = new TempAppPaths();
        var engine = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var input = Path.Combine(paths.RootDirectory, "source.png");
        using (var image = new MagickImage(MagickColors.Orange, 128, 96))
        {
            image.Alpha(AlphaAction.Set);
            image.Evaluate(Channels.Alpha, EvaluateOperator.Set, 0.6);
            image.Write(input);
        }

        var settings = new ImageConversionSettings
        {
            OutputFormatId = "webp",
            Quality = 88,
            PreserveMetadata = true
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ImageConvert,
            Settings = settings,
            InputPath = input,
            TempOutputPath = Path.Combine(paths.TempDirectory, "source.webp"),
            OutputFormatId = "webp",
            TempDirectory = paths.TempDirectory
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);

        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(request.TempOutputPath));
        Assert.True(result.OutputBytes > 0);

        var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
        Assert.True(verification.IsValid, verification.Summary);
        Assert.NotNull(verification.ActualInfo);
        Assert.Equal(128, verification.ActualInfo!.Width);
        Assert.Equal(96, verification.ActualInfo.Height);
        Assert.Equal("webp", verification.ActualInfo.FormatId);
    }

    [Fact]
    public async Task Vector_input_is_rasterized_at_the_requested_resolution()
    {
        var svg = RepoPaths.Fixture("vector.svg");
        Assert.True(File.Exists(svg), $"Fixture missing: {svg}");

        using var paths = new TempAppPaths();
        var engine = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var settings = new ImageConversionSettings
        {
            OutputFormatId = "png",
            ResizeWidth = 320,
            PreserveAspectRatio = true,
            PreserveAnimation = false
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ImageConvert,
            Settings = settings,
            InputPath = svg,
            TempOutputPath = Path.Combine(paths.TempDirectory, "vector.png"),
            OutputFormatId = "png",
            TempDirectory = paths.TempDirectory
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);
        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error);

        using var output = new MagickImage(request.TempOutputPath);
        Assert.Equal(320u, output.Width);
        Assert.True(output.Height > 0);
    }

    [Fact]
    public async Task Corrupt_input_fails_during_processing()
    {
        using var paths = new TempAppPaths();
        var engine = new ImageEngine(NullLogger<ImageEngine>.Instance);
        var input = Path.Combine(paths.RootDirectory, "corrupt.png");
        await File.WriteAllTextAsync(input, "this is not a PNG");
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.ImageConvert,
            Settings = new ImageConversionSettings { OutputFormatId = "png" },
            InputPath = input,
            TempOutputPath = Path.Combine(paths.TempDirectory, "corrupt-out.png"),
            OutputFormatId = "png",
            TempDirectory = paths.TempDirectory
        };

        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.False(result.Success);
        Assert.False(File.Exists(request.TempOutputPath));
    }
}
