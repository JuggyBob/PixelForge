using System.Text.RegularExpressions;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Models;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class CatalogTests
{
    [Fact]
    public void Required_output_formats_are_present()
    {
        var images = FormatCatalog.ImageFormats.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "png", "jpg", "webp", "avif", "tif", "bmp", "gif", "ico" })
            Assert.Contains(id, images);

        var audio = FormatCatalog.AudioFormats.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus" })
            Assert.Contains(id, audio);

        var video = FormatCatalog.VideoFormats.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "mp4", "mkv", "mov", "webm", "avi" })
            Assert.Contains(id, video);
    }

    [Fact]
    public void Format_ids_and_extensions_are_unique()
    {
        var ids = FormatCatalog.All.Select(f => f.Id).ToList();
        var extensions = FormatCatalog.All.Select(f => f.Extension.ToLowerInvariant()).ToList();
        Assert.Equal(new[] { "gif" },
            ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToArray());
        Assert.Equal(new[] { ".gif" },
            extensions.GroupBy(ext => ext, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToArray());
        Assert.All(FormatCatalog.ImageFormats, f => Assert.NotEmpty(f.Id));
        Assert.Equal(FormatCatalog.ImageFormats.Count,
            FormatCatalog.ImageFormats.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(FormatCatalog.AudioFormats.Count,
            FormatCatalog.AudioFormats.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(FormatCatalog.VideoFormats.Count,
            FormatCatalog.VideoFormats.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(FormatCatalog.All, f => Assert.StartsWith(".", f.Extension));
    }

    [Fact]
    public void Extension_lookup_accepts_aliases_and_case()
    {
        Assert.Equal("jpg", FormatCatalog.FindByExtension(".JPEG")?.Id);
        Assert.Equal("jpg", FormatCatalog.FindByExtension("jfif")?.Id);
        Assert.Equal("tif", FormatCatalog.FindByExtension(".TIFF")?.Id);
        Assert.Equal("heic", FormatCatalog.FindByExtension(".heif")?.Id);
        Assert.Equal(MediaCategory.Image, FormatCatalog.DetectCategoryByExtension(".svg"));
        Assert.Equal(MediaCategory.Audio, FormatCatalog.DetectCategoryByExtension(".flac"));
        Assert.Equal(MediaCategory.Video, FormatCatalog.DetectCategoryByExtension(".mkv"));
        Assert.Equal(MediaCategory.Unknown, FormatCatalog.DetectCategoryByExtension(".not-a-real-extension"));
    }

    [Fact]
    public void Format_traits_match_documented_capabilities()
    {
        Assert.True(FormatCatalog.Png.SupportsAlpha);
        Assert.True(FormatCatalog.Png.SupportsAnimation);
        Assert.True(FormatCatalog.Svg.IsVector);
        Assert.True(FormatCatalog.Ico.SupportsMultipage);
        Assert.True(FormatCatalog.Tiff.SupportsMultipage);
        Assert.Contains(FormatCatalog.ImageFormats, f => f.Id == "heic");
    }

    [Fact]
    public void Codec_catalog_has_unique_ids_and_sane_encoders()
    {
        var all = CodecCatalog.VideoCodecs.Concat(CodecCatalog.AudioCodecs).ToList();
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var encoders = all.SelectMany(c => c.Encoders).ToList();
        Assert.Equal(encoders.Count, encoders.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(encoders, e => e.Id == "h264_amf");
        Assert.Contains(encoders, e => e.Id == "libmp3lame");
        Assert.All(encoders, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayName));
            Assert.NotEmpty(e.ContainerIds);
            if (e.QualityMin is not null && e.QualityMax is not null)
                Assert.True(e.QualityMax > e.QualityMin);
        });

        var h264 = CodecCatalog.FindVideoCodec("H264");
        Assert.NotNull(h264);
        Assert.Equal(CodecKind.Video, h264!.Kind);
        Assert.NotNull(CodecCatalog.FindEncoder("h264_amf"));
        Assert.NotNull(CodecCatalog.FindCodecByStreamName("h264", CodecKind.Video));
    }

    [Fact]
    public void Media_tool_catalog_filters_by_category_and_rejects_cross_category_ids()
    {
        var audio = MediaToolCatalog.For(MediaCategory.Audio);
        var image = MediaToolCatalog.For(MediaCategory.Image);
        var video = MediaToolCatalog.For(MediaCategory.Video);

        Assert.All(audio, tool => Assert.Equal(MediaCategory.Audio, tool.Category));
        Assert.All(image, tool => Assert.Equal(MediaCategory.Image, tool.Category));
        Assert.All(video, tool => Assert.Equal(MediaCategory.Video, tool.Category));

        Assert.Contains(audio, tool => tool.Id == "NormalizeLoudness");
        Assert.Contains(audio, tool => tool.Id == "ExtractFromVideo");
        Assert.DoesNotContain(audio, tool => tool.Id == "HdrToSdr");
        Assert.Contains(image, tool => tool.Id == "RemoveBackground");
        Assert.DoesNotContain(image, tool => tool.Id == "VideoTrim");
        Assert.Contains(video, tool => tool.Id == "HdrToSdr");
        Assert.DoesNotContain(video, tool => tool.Id == "NormalizeLoudness");

        Assert.True(MediaToolCatalog.IsCompatible(MediaCategory.Audio, "NormalizeLoudness"));
        Assert.False(MediaToolCatalog.IsCompatible(MediaCategory.Audio, "HdrToSdr"));
        Assert.False(MediaToolCatalog.IsCompatible(MediaCategory.Image, "VideoTrim"));
        Assert.False(MediaToolCatalog.IsCompatible(MediaCategory.Unknown, "NormalizeLoudness"));
        Assert.Equal(audio[0], MediaToolCatalog.DefaultFor(MediaCategory.Audio));
        Assert.Equal(image[0], MediaToolCatalog.DefaultFor(MediaCategory.Image));
        Assert.Equal(video[0], MediaToolCatalog.DefaultFor(MediaCategory.Video));
    }

    [Fact]
    public void Media_tool_catalog_has_unique_ids_and_non_empty_labels()
    {
        Assert.NotEmpty(MediaToolCatalog.All);
        Assert.Equal(MediaToolCatalog.All.Count,
            MediaToolCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(MediaToolCatalog.All, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Label));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.True(tool.Category is MediaCategory.Audio or MediaCategory.Image or MediaCategory.Video);
        });
    }

    [Fact]
    public void Model_catalog_contains_only_verified_downloads()
    {
        Assert.NotEmpty(ModelCatalog.All);
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(m => m.Id).Distinct().Count());
        var singleFileModels = ModelCatalog.All
            .Where(m => m.Backend == ModelBackendKind.OnnxRuntime)
            .ToList();
        Assert.Equal(singleFileModels.Count,
            singleFileModels.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ModelCatalog.All, model =>
        {
            Assert.Matches(new Regex("^[0-9A-Fa-f]{64}$"), model.Sha256);
            if (model.Task == ModelTask.FrameInterpolation)
            {
                Assert.Equal(1, model.ScaleFactor);
                Assert.Equal(64, model.InputSizeMultiple);
                Assert.Equal(0, model.DefaultTileSize);
            }
            else if (model.Task == ModelTask.ImageRestore)
            {
                Assert.Equal(1, model.ScaleFactor);
            }
            else
            {
                Assert.True(model.ScaleFactor is 2 or 4);
            }

            if (model.Backend == ModelBackendKind.NcnnVulkan)
            {
                Assert.False(string.IsNullOrWhiteSpace(model.RuntimeId));
                Assert.False(string.IsNullOrWhiteSpace(model.ModelDirectoryName));
                Assert.NotEmpty(model.SupportedScales);
                Assert.NotEmpty(model.SupportedNoiseLevels);
            }

            Assert.True(model.FileSizeBytes > 0);
            Assert.StartsWith("https://", model.DownloadUrl);
            Assert.False(string.IsNullOrWhiteSpace(model.LicenseName));
        });

        Assert.Equal("realesrgan-x4plus", ModelCatalog.Find("realesrgan-x4plus")?.Id);
        Assert.Equal("realesrgan-x4plus",
            ModelCatalog.FindByFileName(ModelCatalog.RealEsrganX4Plus.FileName)?.Id);
    }
}
