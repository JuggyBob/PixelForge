namespace PixelForge.Domain.Capabilities;

using PixelForge.Domain.Models;

public static class NcnnRuntimeCatalog
{
    public static readonly NcnnRuntimeDescriptor Waifu2x = new()
    {
        Id = "waifu2x-ncnn-20220728",
        Name = "Waifu2x NCNN/Vulkan runtime",
        Version = "2022-07-28",
        DownloadUrl = "https://github.com/nihui/waifu2x-ncnn-vulkan/releases/download/20220728/waifu2x-ncnn-vulkan-20220728-windows.zip",
        FileSizeBytes = 35_528_876,
        Sha256 = "3F60BA0B26763C602CB75178C2051BF0C46F3CC9D13975A052A902773988A34B",
        InstallDirectoryName = "waifu2x-ncnn-20220728",
        ExecutableRelativePath = "waifu2x-ncnn-vulkan-20220728-windows/waifu2x-ncnn-vulkan.exe",
        LicenseName = "MIT",
        LicenseUrl = "https://github.com/nihui/waifu2x-ncnn-vulkan/blob/master/LICENSE",
        SourceUrl = "https://github.com/nihui/waifu2x-ncnn-vulkan",
        RequiredRelativePaths =
        [
            "waifu2x-ncnn-vulkan-20220728-windows/waifu2x-ncnn-vulkan.exe",
            "waifu2x-ncnn-vulkan-20220728-windows/models-cunet/scale2.0x_model.param",
            "waifu2x-ncnn-vulkan-20220728-windows/models-cunet/scale2.0x_model.bin",
            "waifu2x-ncnn-vulkan-20220728-windows/models-upconv_7_anime_style_art_rgb/scale2.0x_model.param",
            "waifu2x-ncnn-vulkan-20220728-windows/models-upconv_7_anime_style_art_rgb/scale2.0x_model.bin",
            "waifu2x-ncnn-vulkan-20220728-windows/models-upconv_7_photo/scale2.0x_model.param",
            "waifu2x-ncnn-vulkan-20220728-windows/models-upconv_7_photo/scale2.0x_model.bin"
        ],
        Notes = "Official Windows NCNN/Vulkan release. Includes CUnet and upconv anime/photo model directories."
    };

    public static readonly NcnnRuntimeDescriptor RealCugan = new()
    {
        Id = "realcugan-ncnn-20220728",
        Name = "Real-CUGAN NCNN/Vulkan runtime",
        Version = "2022-07-28",
        DownloadUrl = "https://github.com/nihui/realcugan-ncnn-vulkan/releases/download/20220728/realcugan-ncnn-vulkan-20220728-windows.zip",
        FileSizeBytes = 45_977_449,
        Sha256 = "C6E08D46C11704B1E3A1ADA9DDD591CB5005F52F132136C8633BA25DEF400E01",
        InstallDirectoryName = "realcugan-ncnn-20220728",
        ExecutableRelativePath = "realcugan-ncnn-vulkan-20220728-windows/realcugan-ncnn-vulkan.exe",
        LicenseName = "MIT",
        LicenseUrl = "https://github.com/bilibili/ailab/blob/main/Real-CUGAN/LICENSE",
        SourceUrl = "https://github.com/nihui/realcugan-ncnn-vulkan",
        RequiredRelativePaths =
        [
            "realcugan-ncnn-vulkan-20220728-windows/realcugan-ncnn-vulkan.exe",
            "realcugan-ncnn-vulkan-20220728-windows/models-se/up2x-conservative.param",
            "realcugan-ncnn-vulkan-20220728-windows/models-se/up2x-conservative.bin",
            "realcugan-ncnn-vulkan-20220728-windows/models-se/up4x-conservative.param",
            "realcugan-ncnn-vulkan-20220728-windows/models-se/up4x-conservative.bin",
            "realcugan-ncnn-vulkan-20220728-windows/models-pro/up2x-conservative.param",
            "realcugan-ncnn-vulkan-20220728-windows/models-pro/up2x-conservative.bin"
        ],
        Notes = "Official Windows NCNN/Vulkan release. Includes standard and Pro model directories."
    };

    public static readonly IReadOnlyList<NcnnRuntimeDescriptor> All = [Waifu2x, RealCugan];

    public static NcnnRuntimeDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
}
