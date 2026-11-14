using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.FileSystem;

public sealed class OutputPathService : IOutputPathService
{
    private readonly IFileNameService _fileNames;

    public OutputPathService(IFileNameService fileNames)
    {
        _fileNames = fileNames;
    }

    public string BuildOutputPath(string inputPath, string outputDirectory, string extension, string suffix,
        string? relativeDirectory, CollisionPolicy collisionPolicy, out string? adjustmentNote)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Choose an output directory.", nameof(outputDirectory));

        var fullInput = Path.GetFullPath(inputPath);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        var relative = SanitizeRelativeDirectory(relativeDirectory);
        var destinationDirectory = string.IsNullOrEmpty(relative)
            ? fullOutputDirectory
            : Path.GetFullPath(Path.Combine(fullOutputDirectory, relative));

        if (!IsPathInside(destinationDirectory, fullOutputDirectory))
            throw new InvalidOperationException("The output path would escape the selected destination folder.");

        var baseName = _fileNames.SanitizeFileName(Path.GetFileNameWithoutExtension(fullInput));
        var finalName = _fileNames.WithSuffix(baseName, suffix) + _fileNames.NormalizeExtension(extension);
        var desired = Path.Combine(destinationDirectory, finalName);
        adjustmentNote = null;

        if (PathsEqual(desired, fullInput))
        {
            desired = EnsureUniquePath(desired);
            adjustmentNote = "The output would have replaced the source file, so a unique name was used.";
            return desired;
        }

        switch (collisionPolicy)
        {
            case CollisionPolicy.Overwrite:
                if (File.Exists(desired))
                    adjustmentNote = "An existing output file will be replaced.";
                break;
            case CollisionPolicy.Skip:
                if (File.Exists(desired))
                    adjustmentNote = "The output file already exists and will be skipped.";
                break;
            default:
                if (File.Exists(desired))
                {
                    var unique = EnsureUniquePath(desired);
                    adjustmentNote = $"A file named {Path.GetFileName(desired)} already exists; using {Path.GetFileName(unique)}.";
                    desired = unique;
                }
                break;
        }

        return desired;
    }

    public string EnsureUniquePath(string desiredPath)
    {
        var directory = Path.GetDirectoryName(desiredPath) ?? "";
        var extension = Path.GetExtension(desiredPath);
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var candidate = desiredPath;
        var index = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            index++;
            if (index > 100_000)
                throw new IOException("Could not find a unique output filename.");
        }

        return candidate;
    }

    public bool IsPathInside(string childPath, string parentPath)
    {
        try
        {
            var child = Path.GetFullPath(childPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetFullPath(parentPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
            return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string? SanitizeRelativeDirectory(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory)) return null;
        var parts = relativeDirectory
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed is "." or ".." || string.IsNullOrWhiteSpace(trimmed)) continue;
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(trimmed.Where(ch => ch >= 32 && Array.IndexOf(invalid, ch) < 0).ToArray()).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned)) safe.Add(cleaned);
        }

        return safe.Count == 0 ? null : Path.Combine(safe.ToArray());
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
