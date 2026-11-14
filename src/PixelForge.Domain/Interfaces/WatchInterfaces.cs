using PixelForge.Domain.Models;

namespace PixelForge.Domain.Interfaces;

public interface IWatchFolderService
{
    IReadOnlyList<WatchFolderRule> Rules { get; }
    event EventHandler? RulesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WatchFolderRule>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<WatchFolderRule> CreateAsync(WatchFolderRule rule, CancellationToken cancellationToken = default);
    Task UpdateAsync(WatchFolderRule rule, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
