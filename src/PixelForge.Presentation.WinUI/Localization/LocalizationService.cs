using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using Windows.Globalization;

namespace PixelForge.Presentation.Localization;

public sealed record LanguageOption(string Locale, string NativeName, string EnglishName, string FlagAsset)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// Runtime localization for PixelForge. Catalogs are embedded JSON dictionaries
/// whose keys are the English source strings (or stable plural keys). Missing
/// entries fall back to the English source string, never to a blank UI.
/// </summary>
public static class LocalizationService
{
    public const string DefaultLocale = "en-US";

    public static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("en-US", "English", "English", "ms-appx:///Assets/Flags/us.png"),
        new("de-DE", "Deutsch", "German", "ms-appx:///Assets/Flags/de.png"),
        new("es-ES", "Español", "Spanish", "ms-appx:///Assets/Flags/es.png"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)", "ms-appx:///Assets/Flags/br.png"),
        new("ko-KR", "한국어", "Korean", "ms-appx:///Assets/Flags/kr.png"),
        new("it-IT", "Italiano", "Italian", "ms-appx:///Assets/Flags/it.png"),
        new("fr-FR", "Français", "French", "ms-appx:///Assets/Flags/fr.png"),
        new("ja-JP", "日本語", "Japanese", "ms-appx:///Assets/Flags/jp.png"),
        new("zh-CN", "简体中文", "Chinese (Simplified)", "ms-appx:///Assets/Flags/cn.png"),
        new("ru-RU", "Русский", "Russian", "ms-appx:///Assets/Flags/ru.png")
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, string>> ReverseCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _currentReverse = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private static List<PatternEntry> _patterns = [];
    private static bool _patternsBuilt;
    private static readonly Dictionary<string, string> PatternCache = new(StringComparer.Ordinal);

    private sealed record PatternEntry(Regex Regex, string Key, IReadOnlyDictionary<int, int> GroupIndexes);

    public static string CurrentLocale { get; private set; } = DefaultLocale;
    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLocale);
    public static bool IsInitialized { get; private set; }
    public static event EventHandler? LanguageChanged;

    public static IReadOnlyDictionary<string, string> CurrentCatalog => _current;

    /// <summary>
    /// Loads the saved locale. An empty or invalid value falls back to the
    /// detected Windows language on first launch, then to English.
    /// </summary>
    public static bool Initialize(string? locale)
    {
        var requested = string.IsNullOrWhiteSpace(locale) ? DetectSystemLanguage() : locale;
        var normalized = NormalizeLocale(requested);
        var applied = SetLanguageCore(normalized, raiseEvent: false);
        IsInitialized = true;
        if (!applied)
            applied = SetLanguageCore(DefaultLocale, raiseEvent: false);
        return applied;
    }

    /// <summary>Changes the language immediately without a restart.</summary>
    public static bool SetLanguage(string locale)
    {
        var normalized = NormalizeLocale(locale);
        return SetLanguageCore(normalized, raiseEvent: true);
    }

    public static bool TrySetLanguage(string locale, out string appliedLocale)
    {
        var normalized = NormalizeLocale(locale);
        var applied = SetLanguageCore(normalized, raiseEvent: true);
        appliedLocale = CurrentLocale;
        return applied;
    }

    private static bool SetLanguageCore(string normalized, bool raiseEvent)
    {
        Dictionary<string, string> catalog;
        Dictionary<string, string> reverse;
        lock (Gate)
        {
            if (!Catalogs.TryGetValue(normalized, out catalog!))
            {
                catalog = LoadCatalog(normalized);
                Catalogs[normalized] = catalog;
                reverse = BuildReverse(catalog);
                ReverseCatalogs[normalized] = reverse;
            }
            else
            {
                reverse = ReverseCatalogs.TryGetValue(normalized, out var existing)
                    ? existing
                    : BuildReverse(catalog);
                ReverseCatalogs[normalized] = reverse;
            }

            if (string.IsNullOrEmpty(normalized) || catalog is null)
                catalog = new Dictionary<string, string>(StringComparer.Ordinal);

            _current = catalog;
            _currentReverse = reverse;
            CurrentLocale = normalized;
            CurrentCulture = CultureFor(normalized);

            // Establish a stable English catalog for plural fallback and tests.
            if (_english.Count == 0)
                _english = string.Equals(normalized, DefaultLocale, StringComparison.OrdinalIgnoreCase)
                    ? catalog
                    : LoadCatalog(DefaultLocale);
            PatternCache.Clear();
        }

        ApplyCulture(CurrentCulture);

        try
        {
            if (!string.Equals(ApplicationLanguages.PrimaryLanguageOverride, CurrentLocale,
                    StringComparison.OrdinalIgnoreCase))
                ApplicationLanguages.PrimaryLanguageOverride = CurrentLocale;
        }
        catch
        {
            // Windows language APIs are unavailable in some test hosts.
        }

        if (raiseEvent)
            RaiseLanguageChanged();
        else
            RefreshAttachedText();
        return true;
    }

    private static bool _languageRefreshQueued;

    private static void RaiseLanguageChanged()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null)
        {
            Loc.RefreshAll();
            LanguageChanged?.Invoke(null, EventArgs.Empty);
            return;
        }

        // Coalesce attached-text and page refreshes into a single callback so
        // no page can observe the new language with stale text or vice versa.
        if (_languageRefreshQueued) return;
        _languageRefreshQueued = true;
        var queued = dispatcher.TryEnqueue(() =>
        {
            _languageRefreshQueued = false;
            Loc.RefreshAll();
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        });
        if (queued) return;
        _languageRefreshQueued = false;
        Loc.RefreshAll();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string DetectSystemLanguage()
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(ApplicationLanguages.Languages);
        }
        catch
        {
            // The WinRT language API can be unavailable in unit tests.
        }

        try
        {
            candidates.Add(CultureInfo.CurrentUICulture.Name);
            candidates.Add(CultureInfo.InstalledUICulture.Name);
        }
        catch
        {
            // Culture information can be restricted in some hosts.
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = candidate.Replace('_', '-');
            var exact = Languages.FirstOrDefault(l =>
                string.Equals(l.Locale, normalized, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Locale;

            var language = normalized.Split('-')[0];
            if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            var neutral = Languages.FirstOrDefault(l =>
                string.Equals(l.Locale.Split('-')[0], language, StringComparison.OrdinalIgnoreCase));
            if (neutral is not null) return neutral.Locale;
        }

        return DefaultLocale;
    }

    public static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return DefaultLocale;
        var normalized = locale.Trim().Replace('_', '-');
        var exact = Languages.FirstOrDefault(l =>
            string.Equals(l.Locale, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Locale;

        var language = normalized.Split('-')[0];
        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        var neutral = Languages.FirstOrDefault(l =>
            string.Equals(l.Locale.Split('-')[0], language, StringComparison.OrdinalIgnoreCase));
        return neutral?.Locale ?? DefaultLocale;
    }

    public static bool IsSupported(string? locale) =>
        Languages.Any(l => string.Equals(l.Locale, NormalizeLocaleExact(locale), StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeLocaleExact(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var normalized = locale.Trim().Replace('_', '-');
        return Languages.FirstOrDefault(l =>
            string.Equals(l.Locale, normalized, StringComparison.OrdinalIgnoreCase))?.Locale;
    }

    /// <summary>Translates a complete UI string. Unknown keys remain unchanged.</summary>
    public static string T(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        lock (Gate)
        {
            if (_current.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value))
                return value;
            if (_english.TryGetValue(source, out var fallback) && !string.IsNullOrEmpty(fallback))
                return fallback;
            if (PatternCache.TryGetValue(source, out var cached))
                return cached;
        }

        var patterned = TranslatePattern(source);
        if (patterned is null) return source;
        lock (Gate)
        {
            if (PatternCache.Count >= 4096) PatternCache.Clear();
            PatternCache[source] = patterned;
        }

        return patterned;
    }

    /// <summary>
    /// Matches an already-formatted runtime string against catalog templates
    /// such as <c>"Remove {0} from this workspace?"</c>. This keeps source code
    /// and machine values intact while still localizing dynamic UI messages.
    /// </summary>
    private static string? TranslatePattern(string source)
    {
        if (source.Length > 2048) return null;
        List<PatternEntry> patterns;
        lock (Gate)
        {
            if (!_patternsBuilt)
            {
                _patterns = BuildPatterns(_english);
                _patternsBuilt = true;
            }

            patterns = _patterns;
        }
        foreach (var entry in patterns)
        {
            var match = entry.Regex.Match(source);
            if (!match.Success) continue;

            string? template;
            lock (Gate)
            {
                if (!_current.TryGetValue(entry.Key, out template) || string.IsNullOrEmpty(template))
                    _english.TryGetValue(entry.Key, out template);
            }

            if (string.IsNullOrEmpty(template)) continue;
            return Regex.Replace(template, @"\{(\d+)\}", replacement =>
            {
                if (!int.TryParse(replacement.Groups[1].Value, out var index)) return replacement.Value;
                if (!entry.GroupIndexes.TryGetValue(index, out var group)) return replacement.Value;
                return match.Groups[group].Value;
            });
        }

        return null;
    }

    private static List<PatternEntry> BuildPatterns(Dictionary<string, string> catalog)
    {
        var patterns = new List<PatternEntry>();
        foreach (var key in catalog.Keys)
        {
            if (!key.Contains('{')) continue;
            var placeholder = Regex.Match(key, @"\{(\d+)\}");
            if (!placeholder.Success) continue;

            var literals = Regex.Replace(key, @"\{\d+\}", "");
            if (!Regex.IsMatch(literals, "[A-Za-z\u00c0-\u024f\u0400-\u04ff\u3040-\u30ff\u4e00-\u9fff\uac00-\ud7af]"))
                continue;

            var single = Regex.Match(key, @"^\{(\d+)\}$");
            if (single.Success) continue;

            var regex = new System.Text.StringBuilder("^");
            var groupIndexes = new Dictionary<int, int>();
            var position = 0;
            var group = 0;
            foreach (Match token in Regex.Matches(key, @"\{(\d+)\}"))
            {
                regex.Append(Regex.Escape(key[position..token.Index]));
                if (!int.TryParse(token.Groups[1].Value, out var index)) continue;
                if (!groupIndexes.ContainsKey(index))
                {
                    groupIndexes[index] = group++;
                    regex.Append("([\\s\\S]*?)");
                }
                else
                {
                    regex.Append("(?:[\\s\\S]*?)");
                }

                position = token.Index + token.Length;
            }

            regex.Append(Regex.Escape(key[position..]));
            regex.Append('$');
            patterns.Add(new PatternEntry(
                new Regex(regex.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant),
                key, groupIndexes));
        }

        return patterns;
    }

    /// <summary>Translates a source template, then formats it with the current culture.</summary>
    public static string F(string source, params object?[] args)
    {
        var translated = T(source);
        try
        {
            return string.Format(CurrentCulture, translated, args);
        }
        catch (FormatException)
        {
            return translated;
        }
    }

    /// <summary>
    /// Looks up a CLDR plural form using a stable key. The catalog stores forms
    /// as <c>key.one</c>, <c>key.few</c>, <c>key.many</c> and <c>key.other</c>.
    /// The count is supplied as argument zero to the template.
    /// </summary>
    public static string P(string key, int count, params object?[] extraArgs)
    {
        var category = GetPluralCategory(CurrentLocale, count);
        string? template = null;
        lock (Gate)
            template = LookupPluralTemplate(_current, key, category)
                       ?? LookupPluralTemplate(_english, key, category);
        if (string.IsNullOrEmpty(template))
        {
            lock (Gate)
                template = _current.TryGetValue(key, out var direct) ? direct : key;
        }

        var args = new object?[extraArgs.Length + 1];
        args[0] = count;
        Array.Copy(extraArgs, 0, args, 1, extraArgs.Length);
        try
        {
            return string.Format(CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>Looks up a plural template without formatting it.</summary>
    public static string PluralTemplate(string key, int count)
    {
        var category = GetPluralCategory(CurrentLocale, count);
        lock (Gate)
        {
            return LookupPluralTemplate(_current, key, category)
                   ?? LookupPluralTemplate(_english, key, category)
                   ?? (_current.TryGetValue(key, out var direct) ? direct : key);
        }
    }

    private static string? LookupPluralTemplate(Dictionary<string, string> catalog, string key, string category)
    {
        if (catalog.TryGetValue(key + "." + category, out var exact) && !string.IsNullOrEmpty(exact))
            return exact;
        if (catalog.TryGetValue(key + ".other", out var other) && !string.IsNullOrEmpty(other))
            return other;
        return null;
    }

    /// <summary>
    /// Builds the dynamic-string matcher off the UI thread after startup. Exact
    /// XAML strings do not need it, so this keeps first paint lighter.
    /// </summary>
    public static void WarmupPatterns()
    {
        lock (Gate)
        {
            if (_patternsBuilt) return;
            _patterns = BuildPatterns(_english);
            _patternsBuilt = true;
        }
    }

    public static string GetPluralCategory(string locale, int count)
    {
        var language = NormalizeLocale(locale);
        var n = Math.Abs(count);
        return language switch
        {
            "ru-RU" => RussianCategory(n),
            "fr-FR" => n is 0 or 1 ? "one" : "other",
            "pt-BR" => n is 0 or 1 ? "one" : "other",
            "de-DE" or "es-ES" or "it-IT" => n == 1 ? "one" : "other",
            "ko-KR" or "ja-JP" or "zh-CN" => "other",
            _ => n == 1 ? "one" : "other"
        };
    }

    private static string RussianCategory(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return "one";
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "few";
        return "many";
    }

    public static string FormatNumber(double value, string format = "0.#") =>
        value.ToString(format, CurrentCulture);

    public static string FormatDate(DateTimeOffset value, string format = "d") =>
        value.ToLocalTime().ToString(format, CurrentCulture);

    public static string FormatDateTime(DateTimeOffset value, string format = "g") =>
        value.ToLocalTime().ToString(format, CurrentCulture);

    /// <summary>
    /// Localizes the compact "2 completed · 1 failed" batch summary produced by
    /// the job layer while keeping the numeric values unchanged.
    /// </summary>
    public static string LocalizeBatchSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var parts = summary.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var match = Regex.Match(parts[i], @"^(\d+)\s+(completed|failed|skipped|cancelled)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                parts[i] = T(parts[i]);
                continue;
            }

            var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var key = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "completed" => "Count.BatchCompleted",
                "failed" => "Count.BatchFailed",
                "skipped" => "Count.BatchSkipped",
                _ => "Count.BatchCancelled"
            };
            parts[i] = P(key, count);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Formats a folder scan summary using the locale's count forms.</summary>
    public static string ScanSummary(FolderScanResult? result)
    {
        if (result is null) return "";
        var key = result.TargetCategory switch
        {
            MediaCategory.Image => "Count.Image",
            MediaCategory.Audio => "Count.Audio",
            MediaCategory.Video => "Count.Video",
            MediaCategory.Document => "Count.Document",
            MediaCategory.Ebook => "Count.Ebook",
            MediaCategory.Archive => "Count.Archive",
            _ => "Count.File"
        };
        var text = F("{0} ready · {1} other files ignored · {2} unsupported · {3} invalid",
            P(key, result.IncludedCount), result.IneligibleCount,
            result.UnsupportedCount, result.InvalidCount);
        if (!string.IsNullOrWhiteSpace(result.Warning))
            text += " · " + T(result.Warning);
        return text;
    }

    /// <summary>Formats a domain validation result without changing its technical issue details.</summary>
    public static string ValidationSummary(ValidationResult? validation)
    {
        if (validation is null) return "";
        if (!validation.IsValid) return T(validation.Errors.FirstOrDefault()?.Message ?? "");
        return validation.Warnings.Count == 0
            ? T("Ready to process")
            : P("Count.Warning", validation.Warnings.Count);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "-";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{FormatNumber(size, "0.#")} {units[unit]}";
    }

    private static Dictionary<string, string> LoadCatalog(string locale)
    {
        var assembly = typeof(LocalizationService).Assembly;
        var names = assembly.GetManifestResourceNames();
        var suffix = $".Resources.{locale}.json";
        var name = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null && !string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
            name = names.FirstOrDefault(n => n.EndsWith($".Resources.{DefaultLocale}.json",
                StringComparison.OrdinalIgnoreCase));
        if (name is null) return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return new Dictionary<string, string>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(stream);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    values[property.Name] = property.Value.GetString() ?? "";
            }

            return values;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> BuildReverse(Dictionary<string, string> catalog)
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in catalog)
        {
            if (string.IsNullOrEmpty(pair.Value)) continue;
            if (!reverse.ContainsKey(pair.Value))
                reverse[pair.Value] = pair.Key;
        }

        return reverse;
    }

    private static CultureInfo CultureFor(string locale)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            return CultureInfo.ReadOnly(culture);
        }
        catch
        {
            return CultureInfo.GetCultureInfo(DefaultLocale);
        }
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch
        {
            // Culture changes can be blocked in restricted hosts; strings still update.
        }
    }

    private static void RefreshAttachedText()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null)
        {
            Loc.RefreshAll();
            return;
        }

        // Keep the language ComboBox selection callback short. Attached text is
        // reapplied on the next dispatcher turn, just before LanguageChanged.
        if (!dispatcher.TryEnqueue(Loc.RefreshAll))
            Loc.RefreshAll();
    }

    /// <summary>
    /// Returns the English source for a translated value in the current locale.
    /// Used by diagnostics and by the live-refresh helpers.
    /// </summary>
    public static string ToSource(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        lock (Gate)
            return _currentReverse.TryGetValue(value, out var source) ? source : value;
    }

    internal static IReadOnlyDictionary<string, string> CatalogFor(string locale)
    {
        var normalized = NormalizeLocale(locale);
        lock (Gate)
        {
            if (!Catalogs.TryGetValue(normalized, out var catalog))
            {
                catalog = LoadCatalog(normalized);
                Catalogs[normalized] = catalog;
            }

            return catalog;
        }
    }
}
