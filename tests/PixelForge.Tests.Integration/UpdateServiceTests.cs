using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Application.Updates;
using PixelForge.Domain.Models;
using PixelForge.Infrastructure.Paths;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task Older_public_release_is_reported_as_internal_build_up_to_date()
    {
        var (service, _) = Create("""
            {"tag_name":"v1.0.0","name":"PixelForge 1.0.0","draft":false,"prerelease":false,
             "html_url":"https://github.com/JuggyBob/PixelForge/releases/tag/v1.0.0",
             "published_at":"2026-11-14T22:32:28Z","body":"notes",
             "assets":[{"name":"PixelForge-Setup-1.0.0-x64.exe","size":100,
                        "browser_download_url":"https://github.com/JuggyBob/PixelForge/releases/download/v1.0.0/PixelForge-Setup-1.0.0-x64.exe"}]}
            """);
        var result = await service.CheckAsync(includePreReleases: false);
        var expected = service.CurrentVersion.Contains("-internal", StringComparison.OrdinalIgnoreCase)
            ? UpdateCheckOutcome.InternalBuildUpToDate
            : UpdateCheckOutcome.UpToDate;
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Release);
        Assert.DoesNotContain("usable release", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Newer_release_is_available_and_assets_are_classified()
    {
        var (service, _) = Create("""
            {"tag_name":"v2.1.0","name":"PixelForge 2.1.0","draft":false,"prerelease":false,
             "html_url":"https://github.com/JuggyBob/PixelForge/releases/tag/v2.1.0",
             "published_at":"2026-12-01T10:00:00Z","body":"**notes**",
             "assets":[
               {"name":"PixelForge-Setup-2.1.0-x64.exe","size":123,"browser_download_url":"https://github.com/JuggyBob/PixelForge/releases/download/v2.1.0/PixelForge-Setup-2.1.0-x64.exe"},
               {"name":"PixelForge-Portable-2.1.0-x64.zip","size":456,"browser_download_url":"https://github.com/JuggyBob/PixelForge/releases/download/v2.1.0/PixelForge-Portable-2.1.0-x64.zip"}
             ]}
            """);
        var result = await service.CheckAsync(includePreReleases: false);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.NotNull(result.Release);
        Assert.Equal("2.1.0", result.Release.Version);
        Assert.Contains(result.Release.Assets, a => a.IsInstaller);
        Assert.Contains(result.Release.Assets, a => a.IsPortable);
    }

    [Fact]
    public async Task NotFound_is_no_release_not_a_parser_failure()
    {
        var (service, _) = Create("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
        var result = await service.CheckAsync(includePreReleases: false);
        Assert.Equal(UpdateCheckOutcome.NoReleaseFound, result.Outcome);
        Assert.DoesNotContain("usable release", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Untrusted_urls_are_not_exposed_from_release_metadata()
    {
        var (service, _) = Create("""
            {"tag_name":"v2.1.0","name":"PixelForge 2.1.0","draft":false,"prerelease":false,
             "html_url":"file:///C:/Windows/System32/calc.exe","published_at":"2026-12-01T10:00:00Z","body":"",
             "assets":[{"name":"PixelForge-Setup-2.1.0-x64.exe","size":123,
                        "browser_download_url":"file:///C:/Windows/System32/calc.exe"}]}
            """);
        var result = await service.CheckAsync(includePreReleases: false);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.NotNull(result.Release);
        Assert.Empty(result.Release.Assets);
        Assert.StartsWith("https://github.com/JuggyBob/PixelForge", result.Release.HtmlUrl);
    }

    private static (UpdateService Service, TempAppPaths Paths) Create(string json,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var paths = new TempAppPaths();
        Directory.CreateDirectory(paths.RootDirectory);
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        settings.LoadAsync().GetAwaiter().GetResult();
        var handler = new StubHandler(json, status);
        var http = new HttpClient(handler);
        var service = new UpdateService(settings, paths, NullLogger<UpdateService>.Instance, http);
        return (service, paths);
    }

    private sealed class StubHandler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test\"");
            return Task.FromResult(response);
        }
    }
}

