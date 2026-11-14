namespace PixelForge.Domain.Models;

public sealed record ModelValidationResult
{
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
    public int? ScaleFactor { get; init; }
    public string? InputShape { get; init; }
    public string? OutputShape { get; init; }

    public static ModelValidationResult Ok(int scale, string input, string output) => new()
    {
        IsValid = true,
        ScaleFactor = scale,
        InputShape = input,
        OutputShape = output
    };

    public static ModelValidationResult Fail(string error, string? details = null) => new()
    {
        IsValid = false,
        Error = error,
        Details = details
    };
}
