using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class LocalizationCatalogTests
{
    private static readonly string[] Locales =
    [
        "en-US", "de-DE", "es-ES", "pt-BR", "ko-KR", "it-IT", "fr-FR", "ja-JP", "zh-CN", "ru-RU"
    ];

    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    [Fact]
    public void Every_requested_language_has_a_complete_catalog()
    {
        var english = Load("en-US");
        Assert.True(english.Count > 1000, "The English catalog is unexpectedly small.");

        foreach (var locale in Locales)
        {
            var catalog = Load(locale);
            Assert.False(catalog.Count == 0, $"{locale} is empty.");
            foreach (var key in english.Keys)
                Assert.True(catalog.ContainsKey(key), $"{locale} is missing '{key}'.");

            foreach (var (key, value) in catalog)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{locale} has a blank value for '{key}'.");
                if (key.EndsWith(".few", StringComparison.Ordinal) ||
                    key.EndsWith(".many", StringComparison.Ordinal))
                    Assert.Equal("ru-RU", locale);
            }
        }
    }

    [Fact]
    public void Translations_preserve_every_placeholder()
    {
        var english = Load("en-US");
        foreach (var locale in Locales)
        {
            var catalog = Load(locale);
            foreach (var key in english.Keys)
            {
                var expected = Placeholder.Matches(key).Select(m => m.Value).OrderBy(x => x).ToArray();
                if (expected.Length == 0) continue;
                var actual = Placeholder.Matches(catalog[key]).Select(m => m.Value).OrderBy(x => x).ToArray();
                Assert.True(expected.SequenceEqual(actual),
                    $"{locale}: placeholders differ for '{key}' ({string.Join(",", expected)} vs {string.Join(",", actual)}).");
            }
        }
    }

    [Theory]
    [InlineData("Count.File.one", "{0} файл")]
    [InlineData("Count.File.few", "{0} файла")]
    [InlineData("Count.File.many", "{0} файлов")]
    [InlineData("Count.Page.one", "{0} страница")]
    [InlineData("Count.Page.few", "{0} страницы")]
    [InlineData("Count.Page.many", "{0} страниц")]
    public void Russian_uses_cldr_plural_categories(string key, string expected)
    {
        Assert.Equal(expected, Load("ru-RU")[key]);
    }

    [Fact]
    public void Every_language_has_a_bundled_flag_asset()
    {
        var root = FindRepoRoot();
        var assets = Path.Combine(root, "src", "PixelForge.Presentation.WinUI", "Assets", "Flags");
        foreach (var file in new[] { "us", "de", "es", "br", "kr", "it", "fr", "jp", "cn", "ru" })
            Assert.True(File.Exists(Path.Combine(assets, file + ".png")), $"Missing flag asset {file}.png.");
    }

    [Fact]
    public void Translation_source_lists_every_english_catalog_key()
    {
        var english = Load("en-US");
        var path = Path.Combine(FindRepoRoot(), "scripts", "localization", "source-strings.json");
        Assert.True(File.Exists(path), $"Translation source not found: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var source = document.RootElement.GetProperty("strings").EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in english.Keys)
            Assert.True(source.Contains(key), $"source-strings.json is missing '{key}'.");
    }

    private static Dictionary<string, string> Load(string locale)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "PixelForge.Presentation.WinUI",
            "Localization", "Resources", locale + ".json");
        Assert.True(File.Exists(path), $"Catalog not found: {path}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PixelForge.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PixelForge.sln was not found.");
    }
}
