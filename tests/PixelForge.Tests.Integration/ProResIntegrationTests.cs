using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Engines.FFmpeg;
using PixelForge.Infrastructure.ProcessExecution;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class ProResIntegrationTests
{
    [Fact]
    public async Task Prores_uses_the_selected_profile_without_rate_control()
    {
        var source = RepoPaths.Fixture("clip-h264.mp4");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");

        using var paths = new TempAppPaths();
        var engine = new FFmpegEngine(new ProcessRunner(), paths, NullLogger<FFmpegEngine>.Instance);
        var info = await engine.TryInspectAsync(source, MediaCategory.Video);
        Assert.NotNull(info);

        var settings = new VideoConversionSettings
        {
            ContainerFormatId = "mov",
            VideoCodecId = "prores",
            VideoEncoderId = "prores_ks",
            AudioCodecId = "aac",
            AudioEncoderId = "aac",
            AudioBitrateKbps = 128,
            RateControlMode = VideoRateControlMode.Bitrate,
            EncoderPreset = "standard",
            PreserveChapters = true
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.VideoConvert,
            Settings = settings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "prores.mov"),
            OutputFormatId = "mov",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);

        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);

        var output = await engine.TryInspectAsync(request.TempOutputPath, MediaCategory.Video);
        Assert.NotNull(output);
        Assert.Equal("prores", output!.VideoCodec?.ToLowerInvariant());
        Assert.True(output.FileSizeBytes > 0);
    }
}
