using System.Text;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.FileSystem;

public sealed class FileNameService : IFileNameService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "output";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || Array.IndexOf(invalid, ch) >= 0) builder.Append('_');
            else builder.Append(ch);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result)) result = "output";
        if (ReservedNames.Contains(result)) result = "_" + result;
        return Truncate(result, 120);
    }

    public string SanitizeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(suffix.Length);
        foreach (var ch in suffix.Trim())
        {
            if (ch < 32 || Array.IndexOf(invalid, ch) >= 0 || ch is '/' or '\\' or ':') continue;
            builder.Append(ch);
        }

        return Truncate(builder.ToString().Trim(), 40);
    }

    public string BuildDefaultSuffix(OperationSettings settings)
    {
        var configured = SanitizeSuffix(settings.FileNameSuffix);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        if (settings is UpscaleSettings upscale)
        {
            if (upscale.CustomWidth is > 0 && upscale.CustomHeight is > 0)
                return "_upscaled";
            return upscale.ScaleFactor > 1 ? $"_{upscale.ScaleFactor}x" : "_upscaled";
        }

        return "";
    }

    public string WithSuffix(string fileNameWithoutExtension, string suffix)
    {
        var clean = fileNameWithoutExtension;
        var cleanSuffix = SanitizeSuffix(suffix);
        if (string.IsNullOrEmpty(cleanSuffix)) return clean;
        if (clean.EndsWith(cleanSuffix, StringComparison.OrdinalIgnoreCase)) return clean;
        return clean + cleanSuffix;
    }

    public string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) throw new ArgumentException("An output extension is required.", nameof(extension));
        var ext = extension.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;
        if (ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ext.Contains(Path.DirectorySeparatorChar))
            throw new ArgumentException("The output extension is invalid.", nameof(extension));
        return ext.ToLowerInvariant();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
}
