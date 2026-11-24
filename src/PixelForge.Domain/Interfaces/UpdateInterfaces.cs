using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface IUpdateService
{
    string CurrentVersion { get; }
    Task<UpdateCheckResult> CheckAsync(bool includePreReleases, CancellationToken cancellationToken = default);
}
