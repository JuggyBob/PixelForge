using PixelForge.Application.Updates;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.5", "1.2.3+build.5")]
    [InlineData("1.2.3-internal", "1.2.3-internal")]
    public void Parses_versions(string input, string expected)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("one.two.three")]
    [InlineData("1.2.-3")]
    public void Rejects_invalid_versions(string input) =>
        Assert.False(SemanticVersion.TryParse(input, out _));

    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.2.10", "1.2.9")]
    [InlineData("1.2.3", "1.2.3-internal")]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.1")]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha")]
    public void Compares_semantically(string later, string earlier)
    {
        Assert.True(SemanticVersion.TryParse(later, out var left));
        Assert.True(SemanticVersion.TryParse(earlier, out var right));
        Assert.True(left.CompareTo(right) > 0);
    }

    [Fact]
    public void Internal_build_does_not_treat_same_public_base_as_newer()
    {
        Assert.True(SemanticVersion.TryParse("1.1.0-internal", out var current));
        Assert.True(SemanticVersion.TryParse("1.1.0", out var publicRelease));
        Assert.Equal(0, publicRelease.WithoutPreRelease().CompareTo(current.WithoutPreRelease()));
    }
}
