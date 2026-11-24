using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Pipelines;

/// <summary>
/// Versioned, atomic JSON storage for saved pipeline definitions. Pipelines
/// live beside settings.json rather than in the SQLite job database so a
/// pipeline can be exported, backed up or edited without touching history.
/// </summary>
public sealed class PipelineFileStore : IPipelineStore
{
    private sealed class PipelineStoreDocument
    {
        public int SchemaVersion { get; set; } = PipelineDefinition.CurrentSchemaVersion;
        public List<PipelineDefinition> Pipelines { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<PipelineFileStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PipelineFileStore(IAppPaths paths, ILogger<PipelineFileStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string StorePath => Path.Combine(_paths.RootDirectory, "pipelines.json");

    public async Task<IReadOnlyList<PipelineDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            pipeline.SchemaVersion = Math.Min(pipeline.SchemaVersion, PipelineDefinition.CurrentSchemaVersion);
            if (pipeline.SchemaVersion <= 0) pipeline.SchemaVersion = PipelineDefinition.CurrentSchemaVersion;
            if (pipeline.Id == Guid.Empty) pipeline.Id = Guid.NewGuid();
            if (pipeline.CreatedAt == default) pipeline.CreatedAt = DateTimeOffset.Now;
            pipeline.ModifiedAt = DateTimeOffset.Now;

            var index = document.Pipelines.FindIndex(p => p.Id == pipeline.Id);
            if (index >= 0) document.Pipelines[index] = pipeline.Clone();
            else document.Pipelines.Add(pipeline.Clone());

            await SaveDocumentAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid pipelineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            document.Pipelines.RemoveAll(p => p.Id == pipelineId);
            await SaveDocumentAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAllAsync(IReadOnlyCollection<PipelineDefinition> pipelines,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var ids = pipelines.Select(p => p.Id).ToHashSet();
            document.Pipelines.RemoveAll(p => ids.Contains(p.Id));
            document.Pipelines.AddRange(pipelines.Select(p => p.Clone()));
            await SaveDocumentAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PipelineDefinition>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(cancellationToken);
        return document.Pipelines;
    }

    private async Task<PipelineStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath)) return new PipelineStoreDocument();

        try
        {
            var json = await File.ReadAllTextAsync(StorePath, cancellationToken);
            var document = JsonSerializer.Deserialize<PipelineStoreDocument>(json, JsonOptions)
                           ?? new PipelineStoreDocument();
            document.SchemaVersion = document.SchemaVersion <= 0
                ? PipelineDefinition.CurrentSchemaVersion
                : document.SchemaVersion;

            foreach (var pipeline in document.Pipelines)
            {
                if (pipeline.Id == Guid.Empty) pipeline.Id = Guid.NewGuid();
                if (pipeline.CreatedAt == default) pipeline.CreatedAt = DateTimeOffset.Now;
                if (pipeline.ModifiedAt == default) pipeline.ModifiedAt = pipeline.CreatedAt;
                if (pipeline.SchemaVersion <= 0) pipeline.SchemaVersion = document.SchemaVersion;
                pipeline.Nodes ??= [];
                pipeline.Connections ??= [];
                foreach (var node in pipeline.Nodes)
                {
                    node.Id ??= Guid.NewGuid().ToString("N");
                    node.Input ??= new PipelineInputConfiguration();
                    node.Output ??= new PipelineOutputConfiguration();
                    node.Input.Paths ??= [];
                }
            }

            return document;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read pipelines from {Path}", StorePath);
            TryQuarantine();
            return new PipelineStoreDocument();
        }
    }

    private async Task SaveDocumentAsync(PipelineStoreDocument document, CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var temp = StorePath + ".tmp";
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, StorePath, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var bad = StorePath + ".bad-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(StorePath, bad, overwrite: true);
            _logger.LogWarning("Quarantined unreadable pipeline store at {Path}", bad);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not quarantine the unreadable pipeline store");
        }
    }
}
