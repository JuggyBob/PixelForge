using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public sealed class PipelineValidationException : Exception
{
    public PipelineValidationResult Validation { get; }

    public PipelineValidationException(PipelineValidationResult validation)
        : base(validation.Issues.FirstOrDefault()?.Code ?? "The pipeline is not valid.")
    {
        Validation = validation;
    }
}

public interface IPipelineStore
{
    Task<IReadOnlyList<PipelineDefinition>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PipelineDefinition pipeline, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid pipelineId, CancellationToken cancellationToken = default);
    Task SaveAllAsync(IReadOnlyCollection<PipelineDefinition> pipelines, CancellationToken cancellationToken = default);
}

public interface IPipelineRunService
{
    IReadOnlyList<PipelineRunSnapshot> Runs { get; }
    event EventHandler? RunsChanged;

    Task<PipelineRunSnapshot> StartAsync(PipelineDefinition pipeline,
        CancellationToken cancellationToken = default);
    Task CancelAsync(Guid runId, CancellationToken cancellationToken = default);
}
