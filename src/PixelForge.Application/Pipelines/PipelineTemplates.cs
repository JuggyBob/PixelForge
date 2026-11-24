using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Pipelines;

public sealed record PipelineTemplateStep(string ActionId, Action<OperationSettings>? Configure = null);

public sealed record PipelineTemplate
{
    public required string Id { get; init; }
    public required string NameKey { get; init; }
    public required string DescriptionKey { get; init; }
    public required string IconGlyph { get; init; }
    public required PipelineMediaType InputType { get; init; }
    public required IReadOnlyList<PipelineTemplateStep> Steps { get; init; }
}

public static class PipelineTemplates
{
    public static IReadOnlyList<PipelineTemplate> All { get; } =
    [
        new()
        {
            Id = "image-web",
            NameKey = "Web-ready images",
            DescriptionKey = "Resize images to 1920 × 1080 and convert them to WebP.",
            IconGlyph = "\uE91B",
            InputType = PipelineMediaType.Image,
            Steps =
            [
                new(PipelineActionIds.ImageResize, settings =>
                {
                    var image = (ImageConversionSettings)settings;
                    image.ResizeWidth = 1920;
                    image.ResizeHeight = 1080;
                    image.ResizeMode = ImageResizeMode.Fit;
                }),
                new(PipelineActionIds.ImageConvert, settings =>
                {
                    var image = (ImageConversionSettings)settings;
                    image.OutputFormatId = "webp";
                    image.Quality = 90;
                })
            ]
        },
        new()
        {
            Id = "image-ai-upscale",
            NameKey = "AI upscale and convert",
            DescriptionKey = "Upscale an image four times and save a clean PNG.",
            IconGlyph = "\uE8A3",
            InputType = PipelineMediaType.Image,
            Steps =
            [
                new(PipelineActionIds.ImageUpscale, settings =>
                {
                    var upscale = (UpscaleSettings)settings;
                    upscale.ScaleFactor = 4;
                }),
                new(PipelineActionIds.ImageConvert, settings =>
                {
                    var image = (ImageConversionSettings)settings;
                    image.OutputFormatId = "png";
                })
            ]
        },
        new()
        {
            Id = "video-compress",
            NameKey = "Compress video",
            DescriptionKey = "Create a smaller H.264 MP4 with balanced quality.",
            IconGlyph = "\uE714",
            InputType = PipelineMediaType.Video,
            Steps =
            [
                new(PipelineActionIds.VideoTools, settings =>
                {
                    var video = (VideoToolSettings)settings;
                    video.Tool = VideoToolOperation.Compress;
                    video.CompressionPreset = VideoCompressionPreset.Balanced;
                })
            ]
        },
        new()
        {
            Id = "video-trim",
            NameKey = "Trim and transcode video",
            DescriptionKey = "Trim the first ten seconds and produce an H.264 MP4.",
            IconGlyph = "\uE714",
            InputType = PipelineMediaType.Video,
            Steps =
            [
                new(PipelineActionIds.VideoTools, settings =>
                {
                    var video = (VideoToolSettings)settings;
                    video.Tool = VideoToolOperation.Trim;
                    video.TrimStartSeconds = 0;
                    video.TrimEndSeconds = 10;
                }),
                new(PipelineActionIds.VideoConvert, settings =>
                {
                    var video = (VideoConversionSettings)settings;
                    video.ContainerFormatId = "mp4";
                    video.VideoCodecId = "h264";
                })
            ]
        },
        new()
        {
            Id = "audio-convert",
            NameKey = "Convert audio to MP3",
            DescriptionKey = "Produce a 192 kbps MP3 and preserve metadata.",
            IconGlyph = "\uE8D6",
            InputType = PipelineMediaType.Audio,
            Steps =
            [
                new(PipelineActionIds.AudioConvert, settings =>
                {
                    var audio = (AudioConversionSettings)settings;
                    audio.OutputFormatId = "mp3";
                    audio.BitrateKbps = 192;
                })
            ]
        },
        new()
        {
            Id = "ocr-pdf",
            NameKey = "OCR to searchable PDF",
            DescriptionKey = "Recognise a page image, then convert the Word document to PDF.",
            IconGlyph = "\uE8A5",
            InputType = PipelineMediaType.Image,
            Steps =
            [
                new(PipelineActionIds.Ocr),
                new(PipelineActionIds.DocumentConvert, settings =>
                {
                    var document = (DocumentConversionSettings)settings;
                    document.OutputFormatId = "pdf";
                })
            ]
        },
        new()
        {
            Id = "archive-zip",
            NameKey = "Create a ZIP archive",
            DescriptionKey = "Package the selected files or folder into a ZIP archive.",
            IconGlyph = "\uE8B7",
            InputType = PipelineMediaType.Folder,
            Steps =
            [
                new(PipelineActionIds.ArchiveCreate, settings =>
                {
                    var archive = (ArchiveConversionSettings)settings;
                    archive.OutputFormatId = "zip";
                })
            ]
        }
    ];

    public static PipelineTemplate? Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static PipelineDefinition Create(PipelineTemplate template, string name,
        IOperationSettingsSerializer serializer)
    {
        var definition = PipelineDefinition.CreateEmpty(name);
        definition.TemplateId = template.Id;
        var input = definition.InputNode!;
        var output = definition.OutputNode!;

        var x = input.X + 340;
        foreach (var step in template.Steps)
        {
            var descriptor = PipelineActionRegistry.Find(step.ActionId);
            if (descriptor is null) continue;
            var settings = descriptor.CreateSettings();
            step.Configure?.Invoke(settings);
            var node = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = descriptor.Id,
                X = x,
                Y = input.Y,
                ConfigurationJson = serializer.Serialize(settings)
            };
            definition.Nodes.Add(node);
            x += 340;
        }

        output.X = x;
        output.Y = input.Y;
        definition.Connections.Clear();
        var sequence = new List<PipelineNodeDefinition> { input };
        sequence.AddRange(definition.Nodes.Where(n => n.Kind == PipelineNodeKind.Action)
            .OrderBy(n => n.X));
        sequence.Add(output);
        for (var i = 0; i < sequence.Count - 1; i++)
        {
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = sequence[i].Id,
                ToNodeId = sequence[i + 1].Id
            });
        }

        return definition;
    }
}
