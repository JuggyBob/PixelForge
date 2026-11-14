namespace PixelForge.Domain.Models;

public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record ValidationIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;
    public string? Field { get; init; }
    public string? Details { get; init; }

    public static ValidationIssue Error(string code, string message, string? field = null, string? details = null) =>
        new() { Code = code, Message = message, Severity = ValidationSeverity.Error, Field = field, Details = details };

    public static ValidationIssue Warning(string code, string message, string? field = null, string? details = null) =>
        new() { Code = code, Message = message, Severity = ValidationSeverity.Warning, Field = field, Details = details };

    public static ValidationIssue Info(string code, string message) =>
        new() { Code = code, Message = message, Severity = ValidationSeverity.Info };
}

public sealed record ValidationResult
{
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(i => i.Severity != ValidationSeverity.Error);
    public IReadOnlyList<ValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
    public IReadOnlyList<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();
    public string Summary => IsValid
        ? (Warnings.Count == 0 ? "Ready to process" : $"{Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")}")
        : Errors[0].Message;

    public static ValidationResult Success(params ValidationIssue[] issues) => new() { Issues = issues };
    public static ValidationResult Failure(string code, string message, string? details = null) =>
        new() { Issues = [ValidationIssue.Error(code, message, details: details)] };

    public ValidationResult Merge(ValidationResult other) =>
        new() { Issues = Issues.Concat(other.Issues).ToList() };
}

public sealed record VerificationResult
{
    public required bool IsValid { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];
    public MediaInfo? ActualInfo { get; init; }
    public string? Details { get; init; }

    public static VerificationResult Ok(MediaInfo? info, string summary) =>
        new() { IsValid = true, Summary = summary, ActualInfo = info };

    public static VerificationResult Fail(string summary, MediaInfo? info = null, string? details = null) =>
        new()
        {
            IsValid = false,
            Summary = summary,
            ActualInfo = info,
            Details = details,
            Issues = [ValidationIssue.Error("output.verification", summary, details: details)]
        };
}
