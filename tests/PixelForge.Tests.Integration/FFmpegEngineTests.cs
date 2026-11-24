using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Engines.FFmpeg;
using PixelForge.Infrastructure.ProcessExecution;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class FFmpegEngineTests
{
    private static FFmpegEngine CreateEngine(TempAppPaths paths) =>
        new(new ProcessRunner(), paths, NullLogger<FFmpegEngine>.Instance);

    [Fact]
    public async Task Wav_converts_to_mp3_with_ffprobe_verification()
    {
        var source = RepoPaths.Fixture("tone.wav");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");

        using var paths = new TempAppPaths();
        var engine = CreateEngine(paths);
        var capability = await engine.ProbeAsync();
        Assert.True(capability.IsAvailable, capability.Error);

        var info = await engine.TryInspectAsync(source, MediaCategory.Audio);
        Assert.NotNull(info);
        Assert.True(info!.IsReadable);

        var settings = new AudioConversionSettings
        {
            OutputFormatId = "mp3",
            CodecId = "mp3",
            EncoderId = "libmp3lame",
            BitrateKbps = 192,
            SampleRate = 44100,
            Channels = 2
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.AudioConvert,
            Settings = settings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "tone.mp3"),
            OutputFormatId = "mp3",
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
        Assert.NotNull(verification.ActualInfo);
        Assert.Equal("mp3", verification.ActualInfo!.AudioCodec?.ToLowerInvariant());
        Assert.InRange(verification.ActualInfo.DurationSeconds ?? 0, 0.5, 3.0);
    }

    [Fact]
    public async Task Mp3_converts_to_m4a_with_aac_using_the_real_backend()
    {
        var source = RepoPaths.Fixture("tone.mp3");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");

        using var paths = new TempAppPaths();
        var engine = CreateEngine(paths);
        var capability = await engine.ProbeAsync();
        Assert.True(capability.IsAvailable, capability.Error);

        var info = await engine.TryInspectAsync(source, MediaCategory.Audio);
        Assert.NotNull(info);
        Assert.True(info!.IsReadable, info.Error);

        var settings = new AudioConversionSettings
        {
            OutputFormatId = "m4a",
            CodecId = "aac",
            EncoderId = "aac",
            BitrateKbps = 192
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.AudioConvert,
            Settings = settings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "tone.m4a"),
            OutputFormatId = "m4a",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);

        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);
        Assert.True(File.Exists(request.TempOutputPath));
        Assert.Equal(".m4a", Path.GetExtension(request.TempOutputPath), ignoreCase: true);

        var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
        Assert.True(verification.IsValid, verification.Summary);
        Assert.NotNull(verification.ActualInfo);
        var actual = verification.ActualInfo!;
        Assert.Equal(MediaCategory.Audio, actual.Category);
        Assert.Equal("aac", actual.AudioCodec?.ToLowerInvariant());
        Assert.InRange(actual.DurationSeconds ?? 0, 0.5, 5.0);
    }

    [Fact]
    public async Task H264_mp4_remuxes_to_mkv_as_stream_copy()
    {
        var source = RepoPaths.Fixture("clip-h264.mp4");
        Assert.True(File.Exists(source), $"Fixture missing: {source}");

        using var paths = new TempAppPaths();
        var engine = CreateEngine(paths);
        var info = await engine.TryInspectAsync(source, MediaCategory.Video);
        Assert.NotNull(info);
        Assert.True(info!.IsReadable);
        Assert.Equal("h264", info.VideoCodec?.ToLowerInvariant());

        var settings = new VideoConversionSettings
        {
            ContainerFormatId = "mkv",
            VideoCodecId = FormatIds.Copy,
            AudioCodecId = FormatIds.Copy,
            SubtitleMode = SubtitleMode.Copy,
            PreserveChapters = true,
            PreserveMetadata = true
        };
        var request = new ProcessingRequest
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.VideoConvert,
            Settings = settings,
            InputPath = source,
            TempOutputPath = Path.Combine(paths.TempDirectory, "clip.mkv"),
            OutputFormatId = "mkv",
            TempDirectory = paths.TempDirectory,
            InputInfo = info
        };

        var validation = await engine.ValidateAsync(request);
        Assert.True(validation.IsValid, validation.Summary);
        var result = await engine.ProcessAsync(request, new Progress<ProcessingProgress>());
        Assert.True(result.Success, result.Error + Environment.NewLine + result.ErrorDetails);

        var output = await engine.TryInspectAsync(request.TempOutputPath, MediaCategory.Video);
        Assert.NotNull(output);
        Assert.True(output!.IsReadable, output.Error);
        Assert.Equal("h264", output.VideoCodec?.ToLowerInvariant());
        Assert.True(output.DurationSeconds > 0);

        var verification = await engine.VerifyOutputAsync(request, request.TempOutputPath);
        Assert.True(verification.IsValid, verification.Summary);
    }
}
