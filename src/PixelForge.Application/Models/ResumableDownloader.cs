using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PixelForge.Application.Models;

/// <summary>
/// Shared resumable HTTP downloader for model and runtime artifacts. It uses
/// Range requests only when the saved validator still matches the remote file,
/// and restarts safely when the server or file has changed.
/// </summary>
internal static class ResumableDownloader
{
    private sealed record ResumeState(string Url, long ExpectedSize, string? ETag, string? LastModified);

    public static async Task DownloadAsync(HttpClient http, string url, string destination,
        long expectedSize, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        if (!IsAllowedDownloadUri(url, out _))
            throw new InvalidOperationException("The download URL must use HTTPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        var metadataPath = destination + ".resume.json";
        var existing = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        ResumeState? state = null;
        if (existing > 0 && File.Exists(metadataPath))
        {
            try
            {
                state = JsonSerializer.Deserialize<ResumeState>(await File.ReadAllTextAsync(metadataPath, cancellationToken));
            }
            catch (JsonException)
            {
                state = null;
            }
        }

        var canResume = state is not null &&
                        string.Equals(state.Url, url, StringComparison.Ordinal) &&
                        (expectedSize <= 0 || state.ExpectedSize == expectedSize) &&
                        existing > 0 && existing <= state.ExpectedSize;
        var offset = canResume ? existing : 0;
        if (!canResume && File.Exists(destination)) File.Delete(destination);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);
        if (canResume && !string.IsNullOrWhiteSpace(state!.ETag))
            request.Headers.TryAddWithoutValidation("If-Range", state.ETag);
        else if (canResume && !string.IsNullOrWhiteSpace(state!.LastModified))
            request.Headers.TryAddWithoutValidation("If-Range", state.LastModified);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !IsAllowedDownloadUri(finalUri.ToString(), out _))
            throw new HttpRequestException("The download redirected to an untrusted URL.");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            File.Delete(destination);
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            throw new IOException("The server rejected the resume range. Retry the download.");
        }

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        if (append && response.Content.Headers.ContentRange is { } range &&
            range.From is { } from && from != offset)
        {
            append = false;
        }

        if (!append)
        {
            offset = 0;
            if (File.Exists(destination)) File.Delete(destination);
        }

        var total = expectedSize > 0
            ? expectedSize
            : (response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0);
        var nextState = new ResumeState(url, total,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified?.ToString("O"));
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(nextState), cancellationToken);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        var received = offset;
        var lastReport = 0L;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var read = await source.ReadAsync(buffer, readTimeout.Token);
            if (read <= 0) break;
            if (total > 0 && received + read > total)
                throw new IOException("The server sent more data than the expected file size.");
            await target.WriteAsync(buffer.AsMemory(0, read), readTimeout.Token);
            received += read;
            if (Environment.TickCount64 - lastReport > 120)
            {
                lastReport = Environment.TickCount64;
                progress?.Invoke(received, total);
            }
        }

        progress?.Invoke(received, total);
        if (total > 0 && received != total)
            throw new IOException($"The download ended at {received:N0} of {total:N0} bytes.");

        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    private static bool IsAllowedDownloadUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        uri = parsed;
        if (parsed.Scheme == Uri.UriSchemeHttps) return true;
        return parsed.Scheme == Uri.UriSchemeHttp && (parsed.IsLoopback || parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }
}
