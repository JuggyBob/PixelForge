namespace PixelForge.Domain.Models;

public sealed record OcrModelFile
{
    public required string FileName { get; init; }
    public required string DownloadUrl { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record OcrModelDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Tier { get; init; }
    public required string Architecture { get; init; }
    public required string IntendedUse { get; init; }
    public required string DetectorFileName { get; init; }
    public required string RecognizerFileName { get; init; }
    public required string DictionaryFileName { get; init; }
    public required OcrModelFile Detector { get; init; }
    public required OcrModelFile Recognizer { get; init; }
    public required string SourceUrl { get; init; }
    public required string LicenseName { get; init; }
    public required string LicenseUrl { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public string RuntimeBackend { get; init; } = "ONNX Runtime 1.24.4 · DirectML / CPU";
    public string ProviderSupport { get; init; } = "DirectML or CPU · provider reported after load";
    public string VerificationStatus { get; init; } = "Detector and recognizer SHA-256 verified";
    public OcrBackendKind Backend { get; init; } = OcrBackendKind.PpOcr;
    public string? RuntimeId { get; init; }
    public OcrModelFile? MainModel { get; init; }
    public OcrModelFile? MmprojModel { get; init; }
    public string? Prompt { get; init; }
    public bool IsInferenceVerified { get; init; } = true;
    public int SortOrder { get; init; }
    public long TotalSizeBytes => Detector.SizeBytes + Recognizer.SizeBytes +
                                  (MainModel?.SizeBytes ?? 0) + (MmprojModel?.SizeBytes ?? 0);
    public string DisplaySize => TotalSizeBytes >= 1024L * 1024 * 1024
        ? $"{TotalSizeBytes / 1024d / 1024 / 1024:0.0} GB"
        : $"{TotalSizeBytes / 1024d / 1024:0.0} MB";
}

public sealed record InstalledOcrModel
{
    public required OcrModelDescriptor Descriptor { get; init; }
    public required string DirectoryPath { get; init; }
    public required long ActualBytes { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public bool IsValid { get; init; }
    public string? ValidationError { get; init; }
    public string DetectorPath => Path.Combine(DirectoryPath, Descriptor.DetectorFileName);
    public string RecognizerPath => Path.Combine(DirectoryPath, Descriptor.RecognizerFileName);
    public string DictionaryPath => Path.Combine(DirectoryPath, Descriptor.DictionaryFileName);
    public string MainModelPath => Path.Combine(DirectoryPath, Descriptor.MainModel?.FileName ?? "");
    public string MmprojPath => Path.Combine(DirectoryPath, Descriptor.MmprojModel?.FileName ?? "");
}

public sealed record OcrModelDownloadProgress
{
    public required string ModelId { get; init; }
    public required string Stage { get; init; }
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

public sealed record OcrModelDownloadResult
{
    public required bool Success { get; init; }
    public InstalledOcrModel? Model { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
}
