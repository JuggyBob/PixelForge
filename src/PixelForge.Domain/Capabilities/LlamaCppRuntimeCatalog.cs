namespace PixelForge.Domain.Capabilities;

using PixelForge.Domain.Models;

public static class LlamaCppRuntimeCatalog
{
    public static readonly LlamaCppRuntimeDescriptor Vulkan = new()
    {
        Id = "llama-cpp-v1.0.0-vulkan",
        Name = "llama.cpp v1.0.0 Vulkan runtime",
        Version = "v1.0.0 (nightly b12359)",
        DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-vulkan-x64.zip",
        FileSizeBytes = 38_090_643,
        Sha256 = "E8D18C6E4210CCA87E7307C2EA78363AC8D158E4E1397358EC96AB70B53A0D5E",
        InstallDirectoryName = "llama-cpp-v1.0.0-vulkan",
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
        Notes = "Official llama.cpp v1.0.0 release-designated Windows Vulkan build (nightly b12359; v1.0.0 itself has no binary assets). Used for advanced OCR models through a loopback-only llama-server."
    };

    public static readonly IReadOnlyList<LlamaCppRuntimeDescriptor> All = [Vulkan];

    public static LlamaCppRuntimeDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
}
