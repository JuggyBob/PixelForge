using System.Globalization;

namespace PixelForge.Application.Updates;

public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? PreRelease = null,
    string? BuildMetadata = null) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var plus = text.IndexOf('+');
        string? build = null;
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (string.IsNullOrWhiteSpace(build)) return false;
        }

        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (string.IsNullOrWhiteSpace(pre)) return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out var major) ||
            !TryNumber(parts[1], out var minor) ||
            !TryNumber(parts[2], out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch, pre, build);
        return true;
    }

    private static bool TryNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;

    public SemanticVersion WithoutPreRelease() => this with { PreRelease = null, BuildMetadata = null };

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ln);
            var rightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rn);
            if (leftNumeric && rightNumeric)
            {
                result = ln.CompareTo(rn);
                if (result != 0) return result;
            }
            else if (leftNumeric)
            {
                return -1;
            }
            else if (rightNumeric)
            {
                return 1;
            }
            else
            {
                result = string.Compare(left[i], right[i], StringComparison.Ordinal);
                if (result != 0) return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrWhiteSpace(PreRelease)) text += "-" + PreRelease;
        if (!string.IsNullOrWhiteSpace(BuildMetadata)) text += "+" + BuildMetadata;
        return text;
    }
}
