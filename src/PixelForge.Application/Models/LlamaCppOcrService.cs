using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Models;

/// <summary>
/// Runs advanced OCR models through a loopback-only llama.cpp server. The
/// server is started for one OCR job and stopped in a finally block so it does
/// not remain resident after the job finishes.
/// </summary>
public sealed class LlamaCppOcrService : ILlamaCppOcrService
{
    private readonly ILlamaCppRuntimeManager _runtime;
    private readonly ILogger<LlamaCppOcrService> _logger;

    public LlamaCppOcrService(ILlamaCppRuntimeManager runtime, ILogger<LlamaCppOcrService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public bool IsRuntimeAvailable =>
        _runtime.GetInstalled(LlamaCppRuntimeCatalog.Vulkan.Id) is { IsValid: true };

    public async Task<LlamaOcrResult> RecognizeAsync(InstalledOcrModel model, IReadOnlyList<string> pagePaths,
        string prompt, bool useGpu, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecognizeAttemptAsync(model, pagePaths, prompt, useGpu, progress, cancellationToken);
            }
            catch (LoopbackPortUnavailableException) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "The loopback OCR port became unavailable; retrying with another port (attempt {Attempt} of {Max}).",
                    attempt, maxAttempts);
            }
            catch (LoopbackPortUnavailableException)
            {
                return new LlamaOcrResult
                {
                    Success = false,
                    Error = "The advanced OCR server could not bind a loopback port.",
                    Details = "Close other OCR jobs or applications using the loopback port and try again."
                };
            }
        }
    }

    private async Task<LlamaOcrResult> RecognizeAttemptAsync(InstalledOcrModel model,
        IReadOnlyList<string> pagePaths, string prompt, bool useGpu, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken)
    {
        var descriptor = model.Descriptor;
        if (descriptor.MainModel is null || descriptor.MmprojModel is null)
            return new LlamaOcrResult { Success = false, Error = "The advanced OCR model manifest is incomplete." };
        var runtime = _runtime.GetInstalled(descriptor.RuntimeId ?? LlamaCppRuntimeCatalog.Vulkan.Id);
        if (runtime is null)
            return new LlamaOcrResult
            {
                Success = false,
                Error = "The llama.cpp runtime is not installed.",
                Details = "Install the llama.cpp Vulkan runtime from AI Models before using this OCR model."
            };
        if (!File.Exists(model.MainModelPath) || !File.Exists(model.MmprojPath))
            return new LlamaOcrResult { Success = false, Error = "The GGUF model files are missing." };

        var port = FindFreePort();
        var logs = new ConcurrentQueue<string>();
        var vulkanSeen = new int[1];
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtime.ExecutablePath,
                WorkingDirectory = runtime.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(model.MainModelPath);
        process.StartInfo.ArgumentList.Add("--mmproj");
        process.StartInfo.ArgumentList.Add(model.MmprojPath);
        process.StartInfo.ArgumentList.Add("-ngl");
        process.StartInfo.ArgumentList.Add(useGpu ? "99" : "0");
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add("127.0.0.1");
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("8192");
        process.StartInfo.ArgumentList.Add("--parallel");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--no-webui");
        process.StartInfo.ArgumentList.Add("--jinja");
        process.StartInfo.ArgumentList.Add("--alias");
        process.StartInfo.ArgumentList.Add("pixel-forge-ocr");
        process.StartInfo.ArgumentList.Add("-v");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.OutputDataReceived += (_, e) => AddLog(logs, e.Data, vulkanSeen);
            process.ErrorDataReceived += (_, e) => AddLog(logs, e.Data, vulkanSeen);
            if (!process.Start())
                return new LlamaOcrResult { Success = false, Error = "llama-server.exe could not start." };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromMinutes(10)
            };
            if (!await WaitForReadyAsync(http, process, logs, cancellationToken))
            {
                if (process.HasExited && LooksLikePortConflict(logs))
                    throw new LoopbackPortUnavailableException(
                        "llama-server exited because the selected loopback port was already in use.", logs.ToArray());
                return new LlamaOcrResult
                {
                    Success = false,
                    Error = "The llama.cpp OCR server did not become ready.",
                    Details = "Check that the runtime and model files are installed and that no other OCR job is running."
                };
            }

            var pages = new List<string>();
            for (var i = 0; i < pagePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(ProcessingProgress.Percent(0.12 + 0.7 * i / Math.Max(1, pagePaths.Count),
                    $"Recognizing page {i + 1} of {pagePaths.Count} with {descriptor.Name}…"));
                var page = await RecognizePageAsync(http, descriptor, pagePaths[i], prompt, cancellationToken);
                if (!page.Success)
                    return new LlamaOcrResult { Success = false, Error = page.Error, Details = page.Details };
                if (!string.IsNullOrWhiteSpace(page.Text)) pages.Add(page.Text.Trim());
            }

            if (pages.Count == 0)
                return new LlamaOcrResult { Success = false, Error = "The advanced OCR model returned no text." };

            var markdown = JoinPages(pages);
            _logger.LogInformation("llama.cpp output tail: {Lines}", string.Join(" | ", logs.TakeLast(12)));
            var usedVulkan = useGpu && Volatile.Read(ref vulkanSeen[0]) == 1;
            var provider = useGpu
                ? usedVulkan ? "llama.cpp Vulkan" : "llama.cpp GPU"
                : "llama.cpp CPU";
            var warning = useGpu && !usedVulkan
                ? "llama.cpp did not report a Vulkan device; the OCR result may have used CPU execution."
                : null;
            stopwatch.Stop();
            return new LlamaOcrResult
            {
                Success = true,
                Markdown = markdown,
                Provider = provider,
                Warning = warning,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LoopbackPortUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advanced OCR inference failed for {Model}", descriptor.Id);
            return new LlamaOcrResult
            {
                Success = false,
                Error = "Advanced OCR inference failed.",
                Details = $"The OCR server process failed ({ex.GetType().Name}). Check the runtime and model files."
            };
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch { }
            process.Dispose();
        }
    }

    private static async Task<(bool Success, string Text, string? Error, string? Details)> RecognizePageAsync(
        HttpClient http, OcrModelDescriptor descriptor, string pagePath, string prompt,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(pagePath, cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, "", $"Page could not be read: {Path.GetFileName(pagePath)}", ex.Message);
        }

        var mime = Path.GetExtension(pagePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
        var payload = new
        {
            model = "pixel-forge-ocr",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } },
                        new { type = "text", text = string.IsNullOrWhiteSpace(prompt) ? descriptor.Prompt : prompt }
                    }
                }
            },
            max_tokens = 4096,
            temperature = 0,
            stream = false
        };

        using var response = await http.PostAsync("/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, "", $"The OCR server returned {(int)response.StatusCode}.", null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? ""
                : content.EnumerateArray()
                    .Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Aggregate("", (a, b) => a + b);
            return (true, CleanMarkdown(text), null, null);
        }
        catch (Exception)
        {
            return (false, "", "The OCR server returned an unexpected response.",
                "The recognized text could not be read. Try again or use the PP-OCR engine.");
        }
    }

    private static async Task<bool> WaitForReadyAsync(HttpClient http, Process process,
        ConcurrentQueue<string> logs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return false;
            try
            {
                using var response = await http.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode) return true;
            }
            catch { }
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    private static string CleanMarkdown(string text)
    {
        var value = RemoveRawBlocks(text.Trim());
        if (value.StartsWith("<md>", StringComparison.OrdinalIgnoreCase))
            value = value[4..].Trim();
        if (value.EndsWith("</md>", StringComparison.OrdinalIgnoreCase))
            value = value[..^5].Trim();
        if (value.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase))
            value = value["```markdown".Length..].TrimStart();
        if (value.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            value = value[3..].TrimStart();
        if (value.EndsWith("```", StringComparison.Ordinal))
            value = value[..^3].TrimEnd();
        var prefix = "The text recognition results are as follows:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..].TrimStart();
        return value.Trim();
    }

    /// <summary>
    /// Drops fenced raw-format blocks emitted by the model. PixelForge adds its
    /// own page-break fences after sanitisation; model-provided raw OpenXML
    /// could otherwise inject arbitrary document parts through Pandoc.
    /// </summary>
    private static string RemoveRawBlocks(string value)
    {
        if (value.IndexOf("{=", StringComparison.Ordinal) < 0) return value;
        var kept = new List<string>();
        var inRaw = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!inRaw)
            {
                if (TryReadFence(trimmed, out var ch, out var length, out var info) &&
                    info.StartsWith("{=", StringComparison.Ordinal))
                {
                    inRaw = true;
                    fenceChar = ch;
                    fenceLength = length;
                    continue;
                }

                kept.Add(line);
                continue;
            }

            if (TryReadFence(trimmed, out var closeChar, out var closeLength, out _) &&
                closeChar == fenceChar && closeLength >= fenceLength)
            {
                inRaw = false;
            }
        }

        return string.Join("\n", kept).Trim();
    }

    private static bool TryReadFence(string line, out char fence, out int length, out string info)
    {
        fence = '\0';
        length = 0;
        info = "";
        if (line.Length < 3 || (line[0] != '`' && line[0] != '~')) return false;
        fence = line[0];
        var index = 0;
        while (index < line.Length && line[index] == fence) index++;
        if (index < 3) return false;
        length = index;
        info = line[index..].TrimStart();
        return true;
    }

    private static string JoinPages(IReadOnlyList<string> pages)
    {
        const string pageBreak = "\n\n```{=openxml}\n<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>\n```\n\n";
        return string.Join(pageBreak, pages);
    }

    private static void AddLog(ConcurrentQueue<string> logs, string? line, int[] vulkanSeen)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ggml_vulkan", StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref vulkanSeen[0], 1);
        logs.Enqueue(line);
        while (logs.Count > 200) logs.TryDequeue(out _);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool LooksLikePortConflict(IReadOnlyCollection<string> logs)
    {
        string[] markers =
        [
            "bind", "address already in use", "only one usage", "permission denied", "access a socket"
        ];
        return logs.Any(line => markers.Any(marker =>
            line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class LoopbackPortUnavailableException(string message, IReadOnlyList<string> logs)
        : Exception(message)
    {
        public IReadOnlyList<string> Logs { get; } = logs;
    }
}
