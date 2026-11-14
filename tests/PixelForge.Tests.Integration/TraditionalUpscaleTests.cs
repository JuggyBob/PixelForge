using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Application.Models;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Engines.Upscaling;
using PixelForge.Infrastructure.ProcessExecution;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class TraditionalUpscaleTests
{
    [Theory]
    [InlineData(ResamplingAlgorithm.NearestNeighbor)]
    [InlineData(ResamplingAlgorithm.Bilinear)]
    [InlineData(ResamplingAlgorithm.Bicubic)]
    [InlineData(ResamplingAlgorithm.Lanczos)]
    [InlineData(ResamplingAlgorithm.MitchellNetravali)]
    public async Task Traditional_resampling_produces_requested_dimensions(ResamplingAlgorithm algorithm)
    {
        using var paths = new TempAppPaths();
        var input = Path.Combine(paths.RootDirectory, "resample.png");
        using (var image = new MagickImage(MagickColors.Orange, 64, 48))
        {
            image.Write(input);
        }

        var settingsService = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settingsService.LoadAsync();
        using var rife = new RifeInterpolator(NullLogger<RifeInterpolator>.Instance);
        var processRunner = new ProcessRunner();
        var ncnn = new NcnnRuntimeManager(paths, processRunner, NullLogger<NcnnRuntimeManager>.Instance);
        await ncnn.InitializeAsync();
        using var engine = new UpscalingEngine(paths, settingsService,
            NullLogger<UpscalingEngine>.Instance, rife, ncnn, processRunner);
        var settings = new UpscaleSettings
        {
            Method = UpscaleMethod.Traditional,
            Algorithm = algorithm,
            ScaleFactor = 2,
            OutputFormatId = "png",
            OutputDirectory = paths.DefaultUpscaleOutputDirectory
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.UpscaleTraditional,
            Settings = settings,
            InputPath = input,
            TempOutputPath = Path.Combine(paths.TempDirectory, $"resample-{algorithm}.png"),
            OutputFormatId = "png",
            TempDirectory = paths.TempDirectory
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);
        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(request.TempOutputPath));

        var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
        Assert.True(verification.IsValid, verification.Summary);
        Assert.Equal(128, verification.ActualInfo!.Width);
        Assert.Equal(96, verification.ActualInfo.Height);
        Assert.Equal("png", verification.ActualInfo.FormatId);
    }
}
