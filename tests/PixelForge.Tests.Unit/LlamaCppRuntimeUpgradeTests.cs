using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Application.Models;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class LlamaCppRuntimeUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "PixelForge.RuntimeUpgradeTests", Guid.NewGuid().ToString("N"));

    public LlamaCppRuntimeUpgradeTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Legacy_runtime_is_replaced_and_removed()
    {
        var legacy = CreateRuntime("llama-cpp-b12110-vulkan");
        var manager = new FakeRuntimeManager(_root);
        var service = CreateService(manager);

        var result = await service.UpgradeAsync();

        Assert.Equal(LlamaCppRuntimeUpgradeOutcome.Updated, result.Outcome);
        Assert.Equal(1, manager.DownloadCount);
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public async Task No_legacy_runtime_means_no_download()
    {
        var manager = new FakeRuntimeManager(_root);
        var service = CreateService(manager);

        var result = await service.UpgradeAsync();

        Assert.Equal(LlamaCppRuntimeUpgradeOutcome.NotNeeded, result.Outcome);
        Assert.Equal(0, manager.DownloadCount);
    }

    [Fact]
    public async Task Current_runtime_is_kept_and_stale_directory_is_cleaned()
    {
        var legacy = CreateRuntime("llama-cpp-b12110-vulkan");
        var current = CreateRuntime(LlamaCppRuntimeCatalog.Vulkan.InstallDirectoryName);
        var manager = new FakeRuntimeManager(_root) { Installed = Installed(current) };
        var service = CreateService(manager);

        var result = await service.UpgradeAsync();

        Assert.Equal(LlamaCppRuntimeUpgradeOutcome.AlreadyCurrent, result.Outcome);
        Assert.True(Directory.Exists(current));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(0, manager.DownloadCount);
    }

    [Fact]
    public async Task Failed_upgrade_keeps_the_legacy_runtime()
    {
        var legacy = CreateRuntime("llama-cpp-b12110-vulkan");
        var manager = new FakeRuntimeManager(_root) { DownloadSucceeds = false };
        var service = CreateService(manager);

        var result = await service.UpgradeAsync();

        Assert.Equal(LlamaCppRuntimeUpgradeOutcome.Failed, result.Outcome);
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public async Task Unrelated_directory_containing_llama_server_is_not_deleted()
    {
        var unrelated = Path.Combine(_root, "My Documents");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "llama-server.exe"), "user file");
        var manager = new FakeRuntimeManager(_root);
        var service = CreateService(manager);

        var result = await service.UpgradeAsync();

        Assert.Equal(LlamaCppRuntimeUpgradeOutcome.NotNeeded, result.Outcome);
        Assert.Equal(0, manager.DownloadCount);
        Assert.True(Directory.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(unrelated, "llama-server.exe")));
    }

    private static LlamaCppRuntimeUpgradeService CreateService(ILlamaCppRuntimeManager manager) =>
        new(manager, NullLogger<LlamaCppRuntimeUpgradeService>.Instance);

    private string CreateRuntime(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "llama-server.exe"), "test");
        File.WriteAllText(Path.Combine(directory, "llama-server-impl.dll"), "test");
        File.WriteAllText(Path.Combine(directory, "ggml-base.dll"), "test");
        return directory;
    }

    private static InstalledLlamaCppRuntime Installed(string directory) => new()
    {
        Descriptor = LlamaCppRuntimeCatalog.Vulkan,
        InstallDirectory = directory,
        ExecutablePath = Path.Combine(directory, "llama-server.exe"),
        SizeBytes = 4,
        ActualSha256 = LlamaCppRuntimeCatalog.Vulkan.Sha256,
        InstalledAt = DateTimeOffset.UtcNow,
        IsValid = true
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeRuntimeManager : ILlamaCppRuntimeManager
    {
        private readonly string _root;

        public FakeRuntimeManager(string root)
        {
            _root = root;
            RuntimeDirectory = root;
        }

        public string RuntimeDirectory { get; }
        public IReadOnlyList<LlamaCppRuntimeDescriptor> AvailableRuntimes => LlamaCppRuntimeCatalog.All;
        public IReadOnlyList<InstalledLlamaCppRuntime> InstalledRuntimes =>
            Installed is null ? [] : [Installed];
        public event EventHandler? RuntimesChanged { add { } remove { } }
        public int DownloadCount { get; private set; }
        public bool DownloadSucceeds { get; init; } = true;
        public InstalledLlamaCppRuntime? Installed { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public InstalledLlamaCppRuntime? GetInstalled(string runtimeId) =>
            string.Equals(runtimeId, LlamaCppRuntimeCatalog.Vulkan.Id, StringComparison.OrdinalIgnoreCase)
                ? Installed
                : null;

        public Task<LlamaCppDownloadResult> DownloadAsync(string runtimeId,
            IProgress<LlamaCppDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            if (!DownloadSucceeds)
                return Task.FromResult(new LlamaCppDownloadResult
                {
                    Success = false,
                    Error = "The test download failed."
                });

            var directory = Path.Combine(_root, LlamaCppRuntimeCatalog.Vulkan.InstallDirectoryName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "llama-server.exe"), "test");
            File.WriteAllText(Path.Combine(directory, "llama-server-impl.dll"), "test");
            File.WriteAllText(Path.Combine(directory, "ggml-base.dll"), "test");
            return Task.FromResult(new LlamaCppDownloadResult
            {
                Success = true,
                Runtime = new InstalledLlamaCppRuntime
                {
                    Descriptor = LlamaCppRuntimeCatalog.Vulkan,
                    InstallDirectory = directory,
                    ExecutablePath = Path.Combine(directory, "llama-server.exe"),
                    SizeBytes = 4,
                    ActualSha256 = LlamaCppRuntimeCatalog.Vulkan.Sha256,
                    InstalledAt = DateTimeOffset.UtcNow,
                    IsValid = true
                }
            });
        }

        public Task<InstalledLlamaCppRuntime?> ValidateAsync(string runtimeId,
            CancellationToken cancellationToken = default) => Task.FromResult(GetInstalled(runtimeId));

        public Task<bool> RemoveAsync(string runtimeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
