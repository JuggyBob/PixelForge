using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Infrastructure.FileSystem;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class FileNameAndPathTests
{
    private readonly FileNameService _fileNames = new();

    [Theory]
    [InlineData("normal.png", "normal.png")]
    [InlineData("trailing...", "trailing")]
    [InlineData("bad/chars?.png", "bad_chars_.png")]
    [InlineData("CON", "_CON")]
    [InlineData("   ", "output")]
    public void SanitizeFileName_removes_invalid_characters(string input, string expected)
    {
        Assert.Equal(expected, _fileNames.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeSuffix_drops_path_separators_and_reserved_characters()
    {
        Assert.Equal("_4x", _fileNames.SanitizeSuffix("_4x"));
        Assert.Equal("_converted", _fileNames.SanitizeSuffix(" /:_converted "));
        Assert.Equal("", _fileNames.SanitizeSuffix("   "));
    }

    [Fact]
    public void Upscale_default_suffix_reflects_scale_and_custom_size()
    {
        Assert.Equal("_4x", _fileNames.BuildDefaultSuffix(new UpscaleSettings { ScaleFactor = 4 }));
        Assert.Equal("_2x", _fileNames.BuildDefaultSuffix(new UpscaleSettings { ScaleFactor = 2 }));
        Assert.Equal("_upscaled", _fileNames.BuildDefaultSuffix(new UpscaleSettings
        {
            ScaleFactor = 4, CustomWidth = 1024, CustomHeight = 768
        }));
        Assert.Equal("_mine", _fileNames.BuildDefaultSuffix(new UpscaleSettings
        {
            ScaleFactor = 4, FileNameSuffix = "_mine"
        }));
    }

    [Fact]
    public void WithSuffix_does_not_duplicate_an_existing_suffix()
    {
        Assert.Equal("photo_4x", _fileNames.WithSuffix("photo", "_4x"));
        Assert.Equal("photo_4x", _fileNames.WithSuffix("photo_4x", "_4x"));
        Assert.Equal("photo", _fileNames.WithSuffix("photo", ""));
    }

    [Theory]
    [InlineData("png", ".png")]
    [InlineData(".PNG", ".png")]
    [InlineData(" .WebP ", ".webp")]
    public void NormalizeExtension_adds_dot_and_lowercases(string input, string expected)
    {
        Assert.Equal(expected, _fileNames.NormalizeExtension(input));
    }

    [Fact]
    public void NormalizeExtension_rejects_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => _fileNames.NormalizeExtension(""));
        Assert.Throws<ArgumentException>(() => _fileNames.NormalizeExtension("p:ng"));
    }

    [Fact]
    public void OutputPathService_preserves_relative_structure_and_enforces_boundary()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new OutputPathService(_fileNames);
            var input = Path.Combine(root, "source", "photo.png");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            File.WriteAllBytes(input, [1, 2, 3]);

            var result = paths.BuildOutputPath(input, output, ".webp", "_converted",
                Path.Combine("album", "2026"), CollisionPolicy.UniqueName, out var note);
            Assert.Equal(Path.Combine(output, "album", "2026", "photo_converted.webp"), result);
            Assert.Null(note);
            Assert.True(paths.IsPathInside(result, output));
            Assert.False(paths.IsPathInside(Path.Combine(root, "outside.webp"), output));

            var traversal = paths.BuildOutputPath(input, output, ".webp", "",
                Path.Combine("..", "..", "escape"), CollisionPolicy.UniqueName, out _);
            Assert.True(paths.IsPathInside(traversal, output));
            Assert.DoesNotContain("..", traversal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OutputPathService_uses_unique_names_and_never_overwrites_source()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new OutputPathService(_fileNames);
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            var input = Path.Combine(root, "same.webp");
            File.WriteAllBytes(input, [1, 2, 3]);

            var first = paths.BuildOutputPath(input, output, "webp", "", null,
                CollisionPolicy.UniqueName, out _);
            File.WriteAllBytes(first, [4, 5, 6]);
            var second = paths.BuildOutputPath(input, output, "webp", "", null,
                CollisionPolicy.UniqueName, out var note);
            Assert.NotEqual(first, second);
            Assert.Contains("(1)", second);
            Assert.NotNull(note);

            var inPlace = paths.BuildOutputPath(input, root, "webp", "", null,
                CollisionPolicy.Overwrite, out var inPlaceNote);
            Assert.NotEqual(input, inPlace);
            Assert.NotNull(inPlaceNote);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Operation_settings_round_trip_preserves_values()
    {
        var serializer = new OperationSettingsSerializer();
        var settings = new VideoConversionSettings
        {
            ContainerFormatId = "mkv",
            VideoCodecId = "hevc",
            VideoEncoderId = "libkvazaar",
            AudioCodecId = "opus",
            QualityValue = 27,
            Width = 1920,
            Height = 1080,
            PreserveChapters = false,
            FastStart = true,
            SubtitleMode = SubtitleMode.Convert,
            FileNameSuffix = "_test"
        };

        var json = serializer.Serialize(settings);
        var restored = Assert.IsType<VideoConversionSettings>(
            serializer.Deserialize(OperationKind.VideoConvert, json));

        Assert.Equal("mkv", restored.ContainerFormatId);
        Assert.Equal("hevc", restored.VideoCodecId);
        Assert.Equal("libkvazaar", restored.VideoEncoderId);
        Assert.Equal("opus", restored.AudioCodecId);
        Assert.Equal(27, restored.QualityValue);
        Assert.Equal(1920, restored.Width);
        Assert.Equal(1080, restored.Height);
        Assert.False(restored.PreserveChapters);
        Assert.True(restored.FastStart);
        Assert.Equal(SubtitleMode.Convert, restored.SubtitleMode);
        Assert.Equal("_test", restored.FileNameSuffix);
    }

    [Fact]
    public void Video_upscale_settings_round_trip_preserves_values()
    {
        var serializer = new OperationSettingsSerializer();
        var settings = new VideoUpscaleSettings
        {
            EnableSuperResolution = true,
            ImageModelId = "realesrgan-x4plus-anime",
            ScaleFactor = 4,
            EnableInterpolation = true,
            InterpolationModelId = "rife-4.25-lite",
            FrameRateMultiplier = 3,
            ExecutionProvider = ExecutionProviderKind.Cpu,
            TileSize = 128,
            TileOverlap = 16,
            VideoBitrateKbps = 6000,
            FileNameSuffix = "_video"
        };

        var json = serializer.Serialize(settings);
        var restored = Assert.IsType<VideoUpscaleSettings>(
            serializer.Deserialize(OperationKind.VideoUpscale, json));

        Assert.True(restored.EnableSuperResolution);
        Assert.Equal("realesrgan-x4plus-anime", restored.ImageModelId);
        Assert.Equal(4, restored.ScaleFactor);
        Assert.True(restored.EnableInterpolation);
        Assert.Equal("rife-4.25-lite", restored.InterpolationModelId);
        Assert.Equal(3, restored.FrameRateMultiplier);
        Assert.Equal(ExecutionProviderKind.Cpu, restored.ExecutionProvider);
        Assert.Equal(128, restored.TileSize);
        Assert.Equal(16, restored.TileOverlap);
        Assert.Equal(6000, restored.VideoBitrateKbps);
        Assert.Equal("_video", restored.FileNameSuffix);
    }

    [Fact]
    public void Operation_settings_deserialize_handles_malformed_json_with_defaults()
    {
        var serializer = new OperationSettingsSerializer();
        var settings = serializer.Deserialize(OperationKind.ImageConvert, "{ not valid json");
        Assert.IsType<ImageConversionSettings>(settings);
        Assert.Equal("png", ((ImageConversionSettings)settings).OutputFormatId);

        var upscale = serializer.Deserialize(OperationKind.UpscaleAi, "");
        Assert.IsType<UpscaleSettings>(upscale);
        Assert.Equal(UpscaleMethod.Traditional, ((UpscaleSettings)upscale).Method);
    }

    [Fact]
    public void AppSettings_clone_is_deep_and_independent()
    {
        var original = new AppSettings();
        original.General.AccentColorHex = "#123456";
        original.Upscaler.ScaleFactor = 2;
        var clone = original.Clone();
        clone.General.AccentColorHex = "#ABCDEF";
        clone.Upscaler.ScaleFactor = 4;

        Assert.Equal("#123456", original.General.AccentColorHex);
        Assert.Equal(2, original.Upscaler.ScaleFactor);
        Assert.NotSame(original.General, clone.General);
        Assert.NotSame(original.Upscaler, clone.Upscaler);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PixelForge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
