namespace PixelForge.Domain.Capabilities;

using PixelForge.Domain.Models;

public static class LlamaCppRuntimeCatalog
{
    public static readonly LlamaCppRuntimeDescriptor Vulkan = new()
    {
        Id = "llama-cpp-b12110-vulkan",
        Name = "llama.cpp Vulkan runtime",
        Version = "b12110",
        DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b12110/llama-b12110-bin-win-vulkan-x64.zip",
        FileSizeBytes = 37_661_146,
        Sha256 = "9732A5E48887CA54D0BFE4AF4324DC85FD56CD8CDA19B52DF3542FA6EBE6874F",
        InstallDirectoryName = "llama-cpp-b12110-vulkan",
        ExecutableRelativePath = "llama-server.exe",
        LicenseName = "MIT",
        LicenseUrl = "https://github.com/ggml-org/llama.cpp/blob/master/LICENSE",
        SourceUrl = "https://github.com/ggml-org/llama.cpp",
        RequiredRelativePaths =
        [
            "llama-server.exe",
            "llama-server-impl.dll",
            "ggml-vulkan.dll",
            "ggml-base.dll"
        ],
        Notes = "Official llama.cpp Windows Vulkan build. Used for advanced OCR models through a loopback-only llama-server."
    };

    public static readonly IReadOnlyList<LlamaCppRuntimeDescriptor> All = [Vulkan];

    public static LlamaCppRuntimeDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
}
