namespace PixelForge.Domain.Capabilities;

using PixelForge.Domain.Models;

public static class OcrModelCatalog
{
    private const string LicenseUrl = "https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE";
    private const string SourceUrl = "https://huggingface.co/PaddlePaddle";

    public static readonly OcrModelDescriptor Tiny = new()
    {
        Id = "ppocrv6-tiny",
        Name = "PP-OCRv6 Tiny",
        Tier = "Tiny · 1.5M parameters",
        Architecture = "PP-OCRv6 DB detector + CTC recognizer",
        IntendedUse = "Low-resource printed-text OCR for older CPUs and laptops",
        DetectorFileName = "ppocrv6_tiny_det.onnx",
        RecognizerFileName = "ppocrv6_tiny_rec.onnx",
        DictionaryFileName = "ppocrv6_tiny_dict.txt",
        Detector = new OcrModelFile
        {
            FileName = "ppocrv6_tiny_det.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det_onnx/resolve/2ba1506c0380b8f0b03dd142459aac66d4421f6c/inference.onnx",
            SizeBytes = 1_780_590,
            Sha256 = "193BAB7A04FCA699A6C82E6ABB5B81BDB28177F0ABD4062552B04908DAFB19F8"
        },
        Recognizer = new OcrModelFile
        {
            FileName = "ppocrv6_tiny_rec.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec_onnx/resolve/2612ab37152ae0a677521bae4e1e3d4fb4cf7c30/inference.onnx",
            SizeBytes = 4_462_639,
            Sha256 = "9EF676D6ED3C88256A2D92C640C44F25B0C40947E111B14B8BE8F594091563E6"
        },
        SourceUrl = $"{SourceUrl}/PP-OCRv6_tiny_det_onnx",
        LicenseName = "Apache-2.0",
        LicenseUrl = LicenseUrl,
        Languages = ["English", "Italian", "Multilingual Latin/CJK dictionary"],
        Notes = "Validated with English and Italian documents, including accents and the euro sign. Detection and recognition run on CPU or DirectML.",
        ProviderSupport = "DirectML validated · CPU fallback available",
        SortOrder = 10
    };

    public static readonly OcrModelDescriptor Small = new()
    {
        Id = "ppocrv6-small",
        Name = "PP-OCRv6 Small",
        Tier = "Small · 7.7M parameters",
        Architecture = "PP-OCRv6 DB detector + CTC recognizer",
        IntendedUse = "Balanced local OCR for ordinary scans, screenshots and printed documents",
        DetectorFileName = "ppocrv6_small_det.onnx",
        RecognizerFileName = "ppocrv6_small_rec.onnx",
        DictionaryFileName = "ppocrv6_small_dict.txt",
        Detector = new OcrModelFile
        {
            FileName = "ppocrv6_small_det.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/28fe5895c24fd108c19eb3e8479f4ab385fbfc62/inference.onnx",
            SizeBytes = 9_880_512,
            Sha256 = "D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E"
        },
        Recognizer = new OcrModelFile
        {
            FileName = "ppocrv6_small_rec.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/b8f84f0b80c529de40b4fbb3544b84fa7233a513/inference.onnx",
            SizeBytes = 21_159_378,
            Sha256 = "5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634"
        },
        SourceUrl = $"{SourceUrl}/PP-OCRv6_small_det_onnx",
        LicenseName = "Apache-2.0",
        LicenseUrl = LicenseUrl,
        Languages = ["English", "Italian", "Multilingual Latin/CJK dictionary"],
        Notes = "Recommended default. Larger and more accurate than Tiny while remaining suitable for CPU inference.",
        ProviderSupport = "CPU validated · DirectML path available",
        SortOrder = 20
    };

    public static readonly OcrModelDescriptor Medium = new()
    {
        Id = "ppocrv6-medium",
        Name = "PP-OCRv6 Medium",
        Tier = "Medium · 34.5M parameters",
        Architecture = "PP-OCRv6 DB detector + CTC recognizer",
        IntendedUse = "Highest-accuracy PP-OCRv6 tier for complex pages and large scans",
        DetectorFileName = "ppocrv6_medium_det.onnx",
        RecognizerFileName = "ppocrv6_medium_rec.onnx",
        DictionaryFileName = "ppocrv6_medium_dict.txt",
        Detector = new OcrModelFile
        {
            FileName = "ppocrv6_medium_det.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/resolve/61323801669c338b7891481ec7bac61ce31b576a/inference.onnx",
            SizeBytes = 62_032_837,
            Sha256 = "EB13B44B25BB36F89528B68720AF8A61D9CF381176107F465DB1757B65D086E1"
        },
        Recognizer = new OcrModelFile
        {
            FileName = "ppocrv6_medium_rec.onnx",
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/resolve/50c7eacafc52fa7bcf4194e8cd08e46f8558504b/inference.onnx",
            SizeBytes = 76_554_979,
            Sha256 = "9C09ABF0957F7968C7586464B7397B84AD2387A0497A351AF40E9ACC71B673BA"
        },
        SourceUrl = $"{SourceUrl}/PP-OCRv6_medium_det_onnx",
        LicenseName = "Apache-2.0",
        LicenseUrl = LicenseUrl,
        Languages = ["English", "Italian", "Multilingual Latin/CJK dictionary"],
        Notes = "Largest tier. Inference-validated on English and Italian document fixtures with both CPU and DirectML. Slower per page than Small; use Tiny or Small on constrained systems.",
        ProviderSupport = "DirectML validated · CPU validated",
        VerificationStatus = "Detector and recognizer inference validated on CPU and DirectML; SHA-256 verified",
        IsInferenceVerified = true,
        SortOrder = 30
    };

    private static readonly OcrModelFile EmptyFile = new()
    {
        FileName = "",
        DownloadUrl = "",
        SizeBytes = 0,
        Sha256 = ""
    };

    public static readonly OcrModelDescriptor GlmOcr = new()
    {
        Id = "glm-ocr",
        Name = "GLM-OCR",
        Tier = "1.33B · Q8_0 model + Q8 projector",
        Architecture = "GLM-V encoder + GLM-0.5B decoder (llama.cpp)",
        IntendedUse = "Advanced document understanding: tables, formulas, seals, code-heavy pages and structured Markdown",
        DetectorFileName = "",
        RecognizerFileName = "",
        DictionaryFileName = "",
        Detector = EmptyFile,
        Recognizer = EmptyFile,
        MainModel = new OcrModelFile
        {
            FileName = "GLM-OCR-Q8_0.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/GLM-OCR-GGUF/resolve/main/GLM-OCR-Q8_0.gguf",
            SizeBytes = 950_433_408,
            Sha256 = "45BC244A6446AFF850521DC41F18BC8D7105AD5F0C2C8C28AF04E7CC4F4D50B1"
        },
        MmprojModel = new OcrModelFile
        {
            FileName = "mmproj-GLM-OCR-Q8_0.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/GLM-OCR-GGUF/resolve/main/mmproj-GLM-OCR-Q8_0.gguf",
            SizeBytes = 484_403_648,
            Sha256 = "9C4B58E33E316ED142EB5DCB41ABEC3844D3E6E5DC361FFB782C3FA9D175141F"
        },
        SourceUrl = "https://huggingface.co/ggml-org/GLM-OCR-GGUF",
        LicenseName = "MIT",
        LicenseUrl = "https://huggingface.co/zai-org/GLM-OCR",
        Notes = "Optional advanced OCR engine. Uses a local llama.cpp server; PP-OCRv6 remains the default. The official full document pipeline also uses PP-DocLayoutV3, which is not required for this page-level mode.",
        Languages = ["English", "Chinese", "French", "Spanish", "Russian", "German", "Japanese", "Korean"],
        RuntimeBackend = "llama.cpp v1.0.0 (b12359) · Vulkan / CPU",
        ProviderSupport = "Vulkan GPU · CPU fallback",
        VerificationStatus = "GGUF SHA-256 verified; inference validated through llama.cpp",
        Backend = OcrBackendKind.LlamaCpp,
        RuntimeId = "llama-cpp-v1.0.0-vulkan",
        Prompt = "Extract all text in reading order. Preserve tables as Markdown.",
        SortOrder = 100
    };

    public static readonly OcrModelDescriptor MiniCpmOcr = new()
    {
        Id = "minicpm-ocr-1.0",
        Name = "MiniCPM-OCR 1.0",
        Tier = "1.3B · Q4_K_M model + F16 projector",
        Architecture = "MiniCPM-V 4.6 OCR (llama.cpp)",
        IntendedUse = "Advanced document parsing for text, tables, formulas, charts and seals",
        DetectorFileName = "",
        RecognizerFileName = "",
        DictionaryFileName = "",
        Detector = EmptyFile,
        Recognizer = EmptyFile,
        MainModel = new OcrModelFile
        {
            FileName = "MiniCPM-OCR-1_0-Q4_K_M.gguf",
            DownloadUrl = "https://huggingface.co/openbmb/MiniCPM-OCR-1.0-gguf/resolve/main/MiniCPM-OCR-1_0-Q4_K_M.gguf",
            SizeBytes = 529_106_816,
            Sha256 = "C80A703400633F298CC2FDDF6D493A541BE36918C0801C0B1BF3F14C5703CE49"
        },
        MmprojModel = new OcrModelFile
        {
            FileName = "mmproj-model-f16-4x.gguf",
            DownloadUrl = "https://huggingface.co/openbmb/MiniCPM-OCR-1.0-gguf/resolve/main/mmproj-model-f16-4x.gguf",
            SizeBytes = 901_017_536,
            Sha256 = "CEED82261B3B1380C3165F8B37C10117A50093BE1362CBFEEB692FA036444682"
        },
        SourceUrl = "https://huggingface.co/openbmb/MiniCPM-OCR-1.0-gguf",
        LicenseName = "Apache-2.0",
        LicenseUrl = "https://github.com/OpenBMB/MiniCPM-OCR/blob/main/LICENSE",
        Notes = "Optional advanced OCR engine. The official full pipeline uses PP-DocLayoutV3 for layout and region cropping; this integration performs page-level recognition without the layout dependency.",
        Languages = ["English", "Chinese"],
        RuntimeBackend = "llama.cpp v1.0.0 (b12359) · Vulkan / CPU",
        ProviderSupport = "Vulkan GPU · CPU fallback",
        VerificationStatus = "GGUF SHA-256 verified; inference validated through llama.cpp",
        Backend = OcrBackendKind.LlamaCpp,
        RuntimeId = "llama-cpp-v1.0.0-vulkan",
        Prompt = "Extract all text in reading order. Preserve tables as Markdown.",
        SortOrder = 101
    };

    public static readonly OcrModelDescriptor XiaomiOcr = new()
    {
        Id = "xiaomi-ocr-0",
        Name = "Xiaomi-OCR-0",
        Tier = "0.8B · Q4_K_M model + F16 projector",
        Architecture = "Xiaomi-OCR-0 vision-language OCR (llama.cpp)",
        IntendedUse = "Advanced OCR and document understanding for English and Chinese pages",
        DetectorFileName = "",
        RecognizerFileName = "",
        DictionaryFileName = "",
        Detector = EmptyFile,
        Recognizer = EmptyFile,
        MainModel = new OcrModelFile
        {
            FileName = "Xiaomi-OCR-0-Q4_K_M.gguf",
            DownloadUrl = "https://huggingface.co/richardyoung/Xiaomi-OCR-0-GGUF/resolve/main/Xiaomi-OCR-0-Q4_K_M.gguf",
            SizeBytes = 541_904_320,
            Sha256 = "70256E1482297608B25A79DBD43DB794E75D38B4999B65FC4B75F9C66E348E1F"
        },
        MmprojModel = new OcrModelFile
        {
            FileName = "mmproj-Xiaomi-OCR-0-F16.gguf",
            DownloadUrl = "https://huggingface.co/richardyoung/Xiaomi-OCR-0-GGUF/resolve/main/mmproj-Xiaomi-OCR-0-F16.gguf",
            SizeBytes = 204_987_264,
            Sha256 = "CD6A69A6FD4C36DBC5CA56BD1A5B9CFCA3DEB2043E645E95B8491E2FC0F29659"
        },
        SourceUrl = "https://huggingface.co/richardyoung/Xiaomi-OCR-0-GGUF",
        LicenseName = "Apache-2.0",
        LicenseUrl = "https://github.com/xiaomi-research/Xiaomi-OCR-0",
        Notes = "Optional advanced OCR engine. Uses a community GGUF conversion of the official Apache-2.0 checkpoint; page-level recognition only. The official two-stage pipeline uses PP-DocLayoutV3.",
        Languages = ["English", "Chinese"],
        RuntimeBackend = "llama.cpp v1.0.0 (b12359) · Vulkan / CPU",
        ProviderSupport = "Vulkan GPU · CPU fallback",
        VerificationStatus = "GGUF SHA-256 verified; inference validated through llama.cpp",
        Backend = OcrBackendKind.LlamaCpp,
        RuntimeId = "llama-cpp-v1.0.0-vulkan",
        Prompt = "Extract all text in reading order. Preserve tables as Markdown.",
        SortOrder = 102
    };

    public static readonly IReadOnlyList<OcrModelDescriptor> All =
        [Tiny, Small, Medium, GlmOcr, MiniCpmOcr, XiaomiOcr];

    public static OcrModelDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
