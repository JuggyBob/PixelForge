using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Updates;

public sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly record struct ReleaseParseResult(UpdateReleaseInfo? Release, bool SawValidRelease);
    public const string PublicRepository = "https://github.com/JuggyBob/PixelForge";
    private const string LatestReleaseApi = "https://api.github.com/repos/JuggyBob/PixelForge/releases/latest";
    private const string ReleasesApi = "https://api.github.com/repos/JuggyBob/PixelForge/releases?per_page=20";
    private const string UserAgent = "PixelForge/2.0 (+local desktop update check)";

    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public UpdateService(ISettingsService settings, IAppPaths paths, ILogger<UpdateService> logger)
        : this(settings, paths, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, true)
    {
    }

    public UpdateService(ISettingsService settings, IAppPaths paths, ILogger<UpdateService> logger,
        HttpClient http, bool ownsHttpClient = false)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
    }

    public string CurrentVersion
    {
        get
        {
            var assembly = typeof(UpdateService).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(bool includePreReleases,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.Now;
        var currentText = CurrentVersion;
        var currentIsInternal = currentText.Contains("-internal", StringComparison.OrdinalIgnoreCase);
        if (!SemanticVersion.TryParse(currentText, out var current))
            return Failed(currentText, "The running application version could not be parsed.",
                UpdateCheckOutcome.InvalidMetadata);

        var cacheFile = Path.Combine(_paths.CacheDirectory, "update-release.json");
        var etagFile = Path.Combine(_paths.CacheDirectory, "update-release.etag");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                includePreReleases ? ReleasesApi : LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            if (!includePreReleases && File.Exists(etagFile))
            {
                var etag = (await File.ReadAllTextAsync(etagFile, cancellationToken)).Trim();
                if (!string.IsNullOrWhiteSpace(etag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!File.Exists(cacheFile))
                    return Failed(currentText, "The cached update response is missing. Try again.",
                        UpdateCheckOutcome.InvalidMetadata);
                var cached = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                var cachedResult = ParseRelease(cached, includePreReleases, current, currentIsInternal);
                if (cachedResult.Release is null && !cachedResult.SawValidRelease)
                    return Failed(currentText, "The cached update metadata is no longer usable. Try again.",
                        UpdateCheckOutcome.InvalidMetadata);
                await _settings.UpdateAsync(s => s.Updates.LastCheckedAt = checkedAt, cancellationToken);
                return ResultFor(currentText, cachedResult.Release, checkedAt, currentIsInternal);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var message = response.Headers.RetryAfter is { } retry
                    ? $"GitHub rate limit reached. Try again after {retry.Delta?.TotalMinutes:0} minutes."
                    : "GitHub rate limit reached. Try again later.";
                return Failed(currentText, message, UpdateCheckOutcome.RateLimited);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failed(currentText, "No public PixelForge release has been published yet.",
                    UpdateCheckOutcome.NoReleaseFound);

            if ((int)response.StatusCode >= 500)
                return Failed(currentText, "GitHub is temporarily unavailable. Try again later.",
                    UpdateCheckOutcome.ServerError);

            if (!response.IsSuccessStatusCode)
                return Failed(currentText, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    UpdateCheckOutcome.ServerError);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return Failed(currentText, "GitHub returned an empty release response.",
                    UpdateCheckOutcome.InvalidMetadata);

            var parsed = ParseRelease(json, includePreReleases, current, currentIsInternal);
            if (parsed.Release is null && !parsed.SawValidRelease)
                return Failed(currentText, "The GitHub release response did not contain a usable release.",
                    UpdateCheckOutcome.InvalidMetadata);

            if (!includePreReleases)
            {
                Directory.CreateDirectory(_paths.CacheDirectory);
                await File.WriteAllTextAsync(cacheFile, json, cancellationToken);
                var etag = response.Headers.ETag?.Tag;
                if (!string.IsNullOrWhiteSpace(etag))
                    await File.WriteAllTextAsync(etagFile, etag, cancellationToken);
            }

            await _settings.UpdateAsync(s => s.Updates.LastCheckedAt = checkedAt, cancellationToken);
            _logger.LogInformation("Update check completed: current={Current}, latest={Latest}, sawValid={SawValid}, outcome={Outcome}",
                currentText, parsed.Release?.Version ?? "none", parsed.SawValidRelease,
                parsed.Release is null ? "no-newer-release" : "available");
            return ResultFor(currentText, parsed.Release, checkedAt, currentIsInternal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return Failed(currentText, ex is TaskCanceledException
                ? "The update check timed out."
                : "The update service is unavailable. Check your connection and try again.",
                UpdateCheckOutcome.NetworkUnavailable);
        }
    }

    private static UpdateCheckResult ResultFor(string currentText, UpdateReleaseInfo? release, DateTimeOffset checkedAt,
        bool currentIsInternal)
    {
        if (release is null)
            return new UpdateCheckResult
            {
                Outcome = currentIsInternal ? UpdateCheckOutcome.InternalBuildUpToDate : UpdateCheckOutcome.UpToDate,
                CurrentVersion = currentText,
                CheckedAt = checkedAt,
                Message = currentIsInternal
                    ? "No newer stable public release is available. This is a newer internal build."
                    : "You are running the latest stable version."
            };

        return new UpdateCheckResult
        {
            Outcome = release.IsPrerelease ? UpdateCheckOutcome.PreReleaseAvailable : UpdateCheckOutcome.UpdateAvailable,
            CurrentVersion = currentText,
            Release = release,
            CheckedAt = checkedAt,
            Message = $"Version {release.Version} is available."
        };
    }

    private static UpdateCheckResult Failed(string currentText, string message,
        UpdateCheckOutcome outcome = UpdateCheckOutcome.Failed) => new()
    {
        Outcome = outcome,
        CurrentVersion = currentText,
        Message = message
    };

    private static ReleaseParseResult ParseRelease(string json, bool includePreReleases,
        SemanticVersion current, bool currentIsInternal)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                UpdateReleaseInfo? best = null;
                SemanticVersion? bestVersion = null;
                var sawValidRelease = false;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var candidate = ParseReleaseElement(element, includePreReleases, current, currentIsInternal);
                    sawValidRelease |= candidate.Valid;
                    if (candidate.Release is null) continue;
                    if (!SemanticVersion.TryParse(candidate.Release.Version, out var candidateVersion)) continue;
                    if (bestVersion is null || candidateVersion.CompareTo(bestVersion.Value) > 0)
                    {
                        best = candidate.Release;
                        bestVersion = candidateVersion;
                    }
                }
                return new ReleaseParseResult(best, sawValidRelease);
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var candidate = ParseReleaseElement(document.RootElement, includePreReleases, current, currentIsInternal);
                return new ReleaseParseResult(candidate.Release, candidate.Valid);
            }
        }
        catch (JsonException)
        {
            return new ReleaseParseResult(null, false);
        }

        return new ReleaseParseResult(null, false);
    }

    private static (UpdateReleaseInfo? Release, bool Valid) ParseReleaseElement(JsonElement element,
        bool includePreReleases, SemanticVersion current, bool currentIsInternal)
    {
        if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return (null, false);

        var isPrerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        var tag = GetString(element, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version)) return (null, false);
        if (isPrerelease && !includePreReleases) return (null, true);

        var comparison = currentIsInternal
            ? version.WithoutPreRelease().CompareTo(current.WithoutPreRelease())
            : version.CompareTo(current);
        if (comparison <= 0) return (null, true);

        var assets = new List<UpdateAssetInfo>();
        if (element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                var assetName = GetString(asset, "name");
                var assetUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(assetUrl)) continue;
                if (!IsTrustedGitHubUrl(assetUrl, allowDownloads: true)) continue;
                long size = 0;
                if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
                    size = parsedSize;
                assets.Add(new UpdateAssetInfo
                {
                    Name = assetName,
                    DownloadUrl = assetUrl,
                    Size = size,
                    IsInstaller = assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                  assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase),
                    IsPortable = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                 assetName.Contains("Portable", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        DateTimeOffset? published = null;
        if (element.TryGetProperty("published_at", out var publishedElement) &&
            publishedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(publishedElement.GetString(), out var parsedPublished))
            published = parsedPublished;

        var htmlUrl = GetString(element, "html_url");
        if (!IsTrustedGitHubUrl(htmlUrl, allowDownloads: false))
            htmlUrl = PublicRepository + "/releases";
        return (new UpdateReleaseInfo
        {
            TagName = tag ?? version.ToString(),
            Version = version.ToString(),
            Name = GetString(element, "name") is { Length: > 0 } name ? name : tag ?? version.ToString(),
            PublishedAt = published,
            HtmlUrl = htmlUrl!,
            Body = GetString(element, "body") ?? "",
            IsPrerelease = isPrerelease,
            Assets = assets
        }, true);
    }

    private static bool IsTrustedGitHubUrl(string? value, bool allowDownloads)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            const string releasesPath = "/JuggyBob/PixelForge/releases";
            if (!uri.AbsolutePath.StartsWith(releasesPath, StringComparison.OrdinalIgnoreCase))
                return false;
            return allowDownloads
                ? uri.AbsolutePath.StartsWith(releasesPath + "/download/", StringComparison.OrdinalIgnoreCase) ||
                  uri.AbsolutePath.StartsWith(releasesPath + "/tag/", StringComparison.OrdinalIgnoreCase)
                : true;
        }

        return allowDownloads &&
               string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
