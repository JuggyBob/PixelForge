using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Domain.Interfaces;
using PixelForge.Engines.FFmpeg;

namespace PixelForge.Presentation.Services;

public interface IWaveformService
{
    Task<string?> GenerateAsync(string inputPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates a real waveform image from the audio signal using FFmpeg's
/// showwavespic filter. Results are cached by path, size and timestamp.
/// </summary>
public sealed class WaveformService : IWaveformService, IDisposable
{
    private readonly IProcessRunner _runner;
    private readonly IAppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly FFmpegLocator _locator;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public WaveformService(IProcessRunner runner, IAppPaths paths, IAppLogger logger)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
        _locator = FFmpegLocator.Discover();
    }

    public async Task<string?> GenerateAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!_locator.IsAvailable || !File.Exists(inputPath)) return null;
        var info = new FileInfo(inputPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        var directory = Path.Combine(_paths.CacheDirectory, "waveforms");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, key + ".png");
        if (File.Exists(output) && new FileInfo(output).Length > 0) return output;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(output) && new FileInfo(output).Length > 0) return output;
            var result = await _runner.RunAsync(new ProcessRequest
            {
                FileName = _locator.FfmpegPath!,
                Arguments =
                [
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-i", inputPath,
                    "-filter_complex", "[0:a]showwavespic=s=1200x160:colors=#D2693A[wave]",
                    "-map", "[wave]", "-frames:v", "1",
                    "-f", "image2", output
                ],
                WorkingDirectory = _paths.TempDirectory,
                Timeout = TimeSpan.FromMinutes(5),
                MaxCapturedLines = 80
            }, cancellationToken);
            if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                _logger.LogWarning("Waveform generation failed for {Path}: {Error}", inputPath,
                    result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
                return null;
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Waveform generation failed for {Path}: {Message}", inputPath, ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
