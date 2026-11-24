using PixelForge.Application.Pipelines;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Infrastructure.FileSystem;
using PixelForge.Infrastructure.Pipelines;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class PipelineRegistryTests
{
    private static readonly OperationSettingsSerializer Serializer = new();

    [Fact]
    public void Action_ids_are_unique_and_every_field_has_a_label()
    {
        var ids = PipelineActionRegistry.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var action in PipelineActionRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.TitleKey));
            Assert.False(string.IsNullOrWhiteSpace(action.DescriptionKey));
            Assert.True(action.InputTypes.Count > 0, action.Id);
            foreach (var field in action.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.LabelKey));
                foreach (var choice in field.Choices)
                    Assert.False(string.IsNullOrWhiteSpace(choice.LabelKey));
            }
        }
    }

    [Fact]
    public void Templates_only_reference_registered_actions_and_round_trip_settings()
    {
        foreach (var template in PipelineTemplates.All)
        {
            Assert.NotEmpty(template.Steps);
            foreach (var step in template.Steps)
                Assert.NotNull(PipelineActionRegistry.Find(step.ActionId));

            var definition = PipelineTemplates.Create(template, "Test", Serializer);
            Assert.Equal(PipelineDefinition.CurrentSchemaVersion, definition.SchemaVersion);
            Assert.Equal(template.Id, definition.TemplateId);
            Assert.NotNull(definition.InputNode);
            Assert.NotNull(definition.OutputNode);
            Assert.Equal(template.Steps.Count, definition.Nodes.Count(n => n.Kind == PipelineNodeKind.Action));
            Assert.Equal(definition.Nodes.Count - 1, definition.Connections.Count);

            foreach (var node in definition.Nodes.Where(n => n.Kind == PipelineNodeKind.Action))
            {
                var descriptor = PipelineActionRegistry.Find(node.ActionId);
                Assert.NotNull(descriptor);
                var settings = Serializer.Deserialize(descriptor!.Operation, node.ConfigurationJson!);
                Assert.Equal(descriptor.Operation, settings.Operation);
            }
        }
    }

    [Fact]
    public void Action_output_contracts_are_sequential()
    {
        Assert.Equal(PipelineMediaType.Image,
            PipelineActionRegistry.OutputTypeFor(PipelineActionRegistry.Find(PipelineActionIds.VideoThumbnail)!,
                new VideoToolSettings()));
        Assert.Equal(PipelineMediaType.Folder,
            PipelineActionRegistry.OutputTypeFor(PipelineActionRegistry.Find(PipelineActionIds.ArchiveExtract)!,
                new ArchiveConversionSettings { Action = ArchiveAction.Extract }));
        Assert.True(PipelineActionRegistry.Find(PipelineActionIds.ArchiveExtract)!.IsTerminal);
    }

    [Fact]
    public void Upscaling_actions_group_ai_and_traditional_image_upscaling()
    {
        Assert.Contains(PipelineActionCategory.Upscaling, Enum.GetValues<PipelineActionCategory>());

        var ai = PipelineActionRegistry.Find(PipelineActionIds.ImageUpscale);
        var traditional = PipelineActionRegistry.Find(PipelineActionIds.ImageUpscaleTraditional);
        Assert.NotNull(ai);
        Assert.NotNull(traditional);

        Assert.Equal(PipelineActionCategory.Upscaling, ai!.Category);
        Assert.Equal(PipelineActionCategory.Upscaling, traditional!.Category);
        Assert.True(ai.RequiresModel);

        Assert.Equal(OperationKind.UpscaleTraditional, traditional.Operation);
        Assert.Equal(new[] { PipelineMediaType.Image }, traditional.InputTypes);
        Assert.Equal(PipelineMediaType.Image, traditional.OutputType);
        Assert.Equal(PipelineMediaType.Image,
            PipelineActionRegistry.OutputTypeFor(traditional, traditional.CreateSettings()));
        Assert.False(traditional.RequiresModel);
        Assert.DoesNotContain(traditional.Fields, f => f.Kind == PipelineFieldKind.Model);
        Assert.Contains(traditional.Fields, f => f.PropertyName == "Algorithm");
        Assert.Contains(traditional.Fields, f => f.PropertyName == "ScaleFactor");
    }

    [Fact]
    public async Task Store_round_trips_and_deletes_pipeline_definitions()
    {
        using var paths = new TestPaths();
        var store = new PipelineFileStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineFileStore>.Instance);
        var definition = PipelineDefinition.CreateEmpty("Nightly images");
        definition.OutputNode!.Output.Directory = paths.DefaultOutputDirectory;
        definition.Nodes.Insert(1, new PipelineNodeDefinition
        {
            Kind = PipelineNodeKind.Action,
            ActionId = PipelineActionIds.ImageConvert,
            ConfigurationJson = Serializer.Serialize(new ImageConversionSettings { OutputFormatId = "webp" })
        });
        definition.Connections.Clear();
        definition.Connections.Add(new PipelineConnectionDefinition
        {
            FromNodeId = definition.InputNode!.Id,
            ToNodeId = definition.Nodes[1].Id
        });
        definition.Connections.Add(new PipelineConnectionDefinition
        {
            FromNodeId = definition.Nodes[1].Id,
            ToNodeId = definition.OutputNode.Id
        });

        await store.SaveAsync(definition);
        var loaded = await store.LoadAsync();
        var saved = Assert.Single(loaded);
        Assert.Equal(definition.Id, saved.Id);
        Assert.Equal("Nightly images", saved.Name);
        Assert.Equal(PipelineDefinition.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal(definition.Nodes.Count, saved.Nodes.Count);

        await store.DeleteAsync(definition.Id);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public void Structure_validator_reports_duplicate_node_ids_without_throwing()
    {
        var definition = PipelineDefinition.CreateEmpty("Duplicate");
        definition.Nodes.Add(new PipelineNodeDefinition
        {
            Id = definition.InputNode!.Id,
            Kind = PipelineNodeKind.Action,
            ActionId = PipelineActionIds.ImageConvert
        });
        var validator = new PipelineValidator(Serializer, null!, null!, null!, null!, null!, new FileNameService());
        var result = validator.ValidateStructure(definition);
        Assert.Contains(result.Issues, i => i.Code == "DuplicateNodeId");
    }

    [Fact]
    public void Preferred_pipeline_name_is_used_for_output_naming()
    {
        var service = new OutputPathService(new FileNameService());
        var path = service.BuildOutputPath(
            Path.Combine(Path.GetTempPath(), "intermediate.jpg"),
            Path.Combine(Path.GetTempPath(), "PixelForge.Unit", "out"),
            ".webp", "", null, CollisionPolicy.UniqueName, out _, "original-photo");
        Assert.Equal("original-photo.webp", Path.GetFileName(path));
    }

    private sealed class TestPaths : PixelForge.Domain.Interfaces.IAppPaths, IDisposable
    {
        public TestPaths()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "PixelForge.Unit", Guid.NewGuid().ToString("N"));
            SettingsFile = Path.Combine(RootDirectory, "settings.json");
            DatabaseFile = Path.Combine(RootDirectory, "pixelforge.db");
            LogsDirectory = Path.Combine(RootDirectory, "logs");
            TempDirectory = Path.Combine(RootDirectory, "temp");
            CacheDirectory = Path.Combine(RootDirectory, "cache");
            ThumbnailCacheDirectory = Path.Combine(CacheDirectory, "thumbnails");
            DefaultModelDirectory = Path.Combine(RootDirectory, "models");
            DefaultOutputDirectory = Path.Combine(RootDirectory, "output");
            DefaultUpscaleOutputDirectory = Path.Combine(RootDirectory, "upscaled");
            EnsureCreated();
        }

        public string RootDirectory { get; }
        public string SettingsFile { get; }
        public string DatabaseFile { get; }
        public string LogsDirectory { get; }
        public string TempDirectory { get; }
        public string CacheDirectory { get; }
        public string ThumbnailCacheDirectory { get; }
        public string DefaultOutputDirectory { get; }
        public string DefaultUpscaleOutputDirectory { get; }
        public string DefaultModelDirectory { get; }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(ThumbnailCacheDirectory);
            Directory.CreateDirectory(DefaultModelDirectory);
            Directory.CreateDirectory(DefaultOutputDirectory);
            Directory.CreateDirectory(DefaultUpscaleOutputDirectory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
