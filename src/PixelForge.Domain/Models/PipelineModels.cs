using System.Text.Json.Serialization;

namespace PixelForge.Domain.Models;

/// <summary>
/// The media contract carried by a pipeline port. Folder is only produced by
/// archive extraction and cannot be consumed by another processing step.
/// </summary>
public enum PipelineMediaType
{
    Unknown = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    Document = 4,
    Ebook = 5,
    Archive = 6,
    Folder = 7
}

public enum PipelineNodeKind
{
    Input = 0,
    Action = 1,
    Output = 2
}

public enum PipelineIssueSeverity
{
    Warning = 0,
    Error = 1
}

public enum PipelineRunState
{
    Preparing = 0,
    Running = 1,
    Completed = 2,
    CompletedWithFailures = 3,
    Failed = 4,
    Cancelled = 5,
    Interrupted = 6
}

public sealed class PipelineInputConfiguration
{
    public List<string> Paths { get; set; } = [];

    /// <summary>
    /// When set, the folder is rescanned when the pipeline runs so new files are
    /// picked up without resaving the pipeline.
    /// </summary>
    public string? FolderPath { get; set; }

    public bool Recursive { get; set; } = true;

    public bool PreserveFolderStructure { get; set; } = true;

    public PipelineInputConfiguration Clone() => new()
    {
        Paths = [.. Paths],
        FolderPath = FolderPath,
        Recursive = Recursive,
        PreserveFolderStructure = PreserveFolderStructure
    };
}

public sealed class PipelineOutputConfiguration
{
    public string Directory { get; set; } = "";
    public string FileNameSuffix { get; set; } = "";
    public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.UniqueName;
    public bool PreserveFolderStructure { get; set; } = true;
    public bool KeepIntermediateFiles { get; set; }

    public PipelineOutputConfiguration Clone() => (PipelineOutputConfiguration)MemberwiseClone();
}

public sealed class PipelineNodeDefinition
{
    /// <summary>Stable node instance identity. Never derived from a localized label.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public PipelineNodeKind Kind { get; set; }

    /// <summary>Stable registered action id for action nodes; empty for boundaries.</summary>
    public string ActionId { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Serialized operation settings for action nodes. The concrete settings
    /// type is selected by the action registry, never by the language.
    /// </summary>
    public string? ConfigurationJson { get; set; }

    public PipelineInputConfiguration Input { get; set; } = new();
    public PipelineOutputConfiguration Output { get; set; } = new();

    public PipelineNodeDefinition Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        ActionId = ActionId,
        X = X,
        Y = Y,
        IsExpanded = IsExpanded,
        ConfigurationJson = ConfigurationJson,
        Input = Input.Clone(),
        Output = Output.Clone()
    };
}

public sealed class PipelineConnectionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromNodeId { get; set; } = "";
    public string ToNodeId { get; set; } = "";

    public PipelineConnectionDefinition Clone() => (PipelineConnectionDefinition)MemberwiseClone();
}

public sealed class PipelineDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? TemplateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public List<PipelineNodeDefinition> Nodes { get; set; } = [];
    public List<PipelineConnectionDefinition> Connections { get; set; } = [];

    [JsonIgnore]
    public PipelineNodeDefinition? InputNode =>
        Nodes.FirstOrDefault(n => n.Kind == PipelineNodeKind.Input);

    [JsonIgnore]
    public PipelineNodeDefinition? OutputNode =>
        Nodes.FirstOrDefault(n => n.Kind == PipelineNodeKind.Output);

    public PipelineDefinition Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        TemplateId = TemplateId,
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        Nodes = Nodes.Select(n => n.Clone()).ToList(),
        Connections = Connections.Select(c => c.Clone()).ToList()
    };

    /// <summary>
    /// Creates a definition with the mandatory boundaries and a single
    /// Input → Output connection. Action nodes are inserted between them.
    /// </summary>
    public static PipelineDefinition CreateEmpty(string name)
    {
        var input = new PipelineNodeDefinition
        {
            Kind = PipelineNodeKind.Input,
            X = 80,
            Y = 280,
            IsExpanded = false
        };
        var output = new PipelineNodeDefinition
        {
            Kind = PipelineNodeKind.Output,
            X = 560,
            Y = 280,
            IsExpanded = false
        };

        return new PipelineDefinition
        {
            Name = name,
            Nodes = [input, output],
            Connections =
            [
                new PipelineConnectionDefinition
                {
                    FromNodeId = input.Id,
                    ToNodeId = output.Id
                }
            ]
        };
    }
}

public sealed record PipelineValidationIssue(
    PipelineIssueSeverity Severity,
    string Code,
    string? NodeId = null,
    string? Detail = null);

public sealed class PipelineValidationResult
{
    public IReadOnlyList<PipelineValidationIssue> Issues { get; init; } = [];

    public bool IsValid => Issues.All(i => i.Severity != PipelineIssueSeverity.Error);

    public int ErrorCount => Issues.Count(i => i.Severity == PipelineIssueSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == PipelineIssueSeverity.Warning);

    public static PipelineValidationResult Valid(params PipelineValidationIssue[] warnings) =>
        new() { Issues = warnings };

    public static PipelineValidationResult Invalid(params PipelineValidationIssue[] issues) =>
        new() { Issues = issues };
}

public sealed class PipelineRunSnapshot
{
    public Guid Id { get; init; }
    public Guid PipelineId { get; init; }
    public required string PipelineName { get; init; }
    public PipelineRunState State { get; set; } = PipelineRunState.Preparing;
    public double Progress { get; set; }
    public int StepIndex { get; set; }
    public int StepCount { get; set; }
    public int TotalFiles { get; set; }
    public int FinishedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<Guid> JobIds { get; init; } = [];

    public PipelineRunSnapshot Clone() => new()
    {
        Id = Id,
        PipelineId = PipelineId,
        PipelineName = PipelineName,
        State = State,
        Progress = Progress,
        StepIndex = StepIndex,
        StepCount = StepCount,
        TotalFiles = TotalFiles,
        FinishedFiles = FinishedFiles,
        FailedFiles = FailedFiles,
        SkippedFiles = SkippedFiles,
        Message = Message,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        JobIds = [.. JobIds]
    };

    public bool IsTerminal => State is PipelineRunState.Completed
        or PipelineRunState.CompletedWithFailures or PipelineRunState.Failed
        or PipelineRunState.Cancelled or PipelineRunState.Interrupted;
}
