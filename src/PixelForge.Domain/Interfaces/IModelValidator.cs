using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface IModelValidator
{
    Task<ModelValidationResult> ValidateAsync(string modelPath, ModelDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
