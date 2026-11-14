using PixelForge.Domain;

namespace PixelForge.Presentation.ViewModels;

public sealed record ConverterImportRequest(IReadOnlyList<string> Files, MediaCategory PreferredCategory);

public sealed record UpscalerImportRequest(string Path, bool IsFolder, IReadOnlyList<string>? Files = null);

public sealed record ComparisonRequest(string InputPath, string OutputPath, string Title);
