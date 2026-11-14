using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.FFmpeg;

public sealed record FFmpegCapabilities(
    EngineCapability Capability,
    IReadOnlySet<string> AvailableEncoders,
    IReadOnlySet<string> Demuxers,
    IReadOnlySet<string> Muxers,
    string? Error);

public sealed class FFmpegCapabilityService
{
    private static readonly Regex EncoderRegex = new(@"^\s*[VAS][A-Z.]{5}\s+(\S+)", RegexOptions.Compiled);
    private readonly IProcessRunner _processRunner;
    private readonly FFmpegLocator _locator;
    private readonly IAppPaths _paths;
    private readonly ILogger<FFmpegCapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FFmpegCapabilities? _cached;

    public FFmpegCapabilityService(IProcessRunner processRunner, FFmpegLocator locator, IAppPaths paths,
        ILogger<FFmpegCapabilityService> logger)
    {
        _processRunner = processRunner;
        _locator = locator;
        _paths = paths;
        _logger = logger;
    }

    public async Task<FFmpegCapabilities> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cached is not null) return _cached;
            _cached = await BuildAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FFmpegCapabilities> BuildAsync(CancellationToken cancellationToken)
    {
        if (!_locator.IsAvailable || _locator.FfmpegPath is null)
        {
            return new FFmpegCapabilities(new EngineCapability
            {
                EngineId = EngineIds.FFmpeg,
                DisplayName = "FFmpeg",
                IsAvailable = false,
                Error = _locator.Error
            }, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), _locator.Error);
        }

        var versionResult = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath,
            Arguments = ["-hide_banner", "-version"],
            Timeout = TimeSpan.FromSeconds(20),
            MaxCapturedLines = 20
        }, cancellationToken);

        if (!versionResult.Succeeded)
        {
            var error = $"FFmpeg could not be started: {LastLine(versionResult.StandardError)}";
            return new FFmpegCapabilities(new EngineCapability
            {
                EngineId = EngineIds.FFmpeg,
                DisplayName = "FFmpeg",
                IsAvailable = false,
                Error = error
            }, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), error);
        }

        var version = versionResult.StandardOutputLines.FirstOrDefault() ?? "FFmpeg (unknown version)";
        var formatsResult = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath,
            Arguments = ["-hide_banner", "-formats"],
            Timeout = TimeSpan.FromSeconds(30),
            MaxCapturedLines = 1200
        }, cancellationToken);
        var encodersResult = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = _locator.FfmpegPath,
            Arguments = ["-hide_banner", "-encoders"],
            Timeout = TimeSpan.FromSeconds(30),
            MaxCapturedLines = 1200
        }, cancellationToken);

        var (demuxers, muxers) = ParseFormats(formatsResult.StandardOutputLines);
        var presentEncoders = ParseEncoders(encodersResult.StandardOutputLines);
        var probeCacheKey = $"{version}|{new FileInfo(_locator.FfmpegPath).Length}|{File.GetLastWriteTimeUtc(_locator.FfmpegPath).Ticks}";
        var cache = await LoadProbeCacheAsync(cancellationToken);
        var cacheMatches = cache is not null && cache.Key == probeCacheKey &&
                           DateTimeOffset.Now - cache.SavedAt < TimeSpan.FromDays(7);
        var softwareResults = cacheMatches
            ? new Dictionary<string, bool>(cache!.Software, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var candidates = CodecCatalog.VideoCodecs.Concat(CodecCatalog.AudioCodecs)
            .SelectMany(c => c.Encoders)
            .Where(e => presentEncoders.Contains(e.Id))
            .DistinctBy(e => e.Id)
            .ToList();

        var probeResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var probeSemaphore = new SemaphoreSlim(4, 4);
        var probeTasks = candidates.Select(async encoder =>
        {
            var needProbe = encoder.IsHardware || !cacheMatches || !softwareResults.ContainsKey(encoder.Id);
            if (!needProbe)
            {
                lock (probeResults) probeResults[encoder.Id] = softwareResults[encoder.Id];
                return;
            }

            await probeSemaphore.WaitAsync(cancellationToken);
            try
            {
                var ok = await ProbeEncoderAsync(encoder, cancellationToken);
                lock (probeResults) probeResults[encoder.Id] = ok;
                if (!encoder.IsHardware)
                {
                    lock (softwareResults) softwareResults[encoder.Id] = ok;
                }

                _logger.LogInformation("Encoder probe {Encoder}: {Result}", encoder.Id, ok ? "available" : "unavailable");
            }
            finally
            {
                probeSemaphore.Release();
            }
        });
        await Task.WhenAll(probeTasks);

        if (!cacheMatches || candidates.Any(c => !c.IsHardware && !cache!.Software.ContainsKey(c.Id)))
        {
            await SaveProbeCacheAsync(new ProbeCache
            {
                Key = probeCacheKey,
                SavedAt = DateTimeOffset.Now,
                Software = new Dictionary<string, bool>(softwareResults, StringComparer.OrdinalIgnoreCase)
            }, cancellationToken);
        }

        var videoCodecs = new List<CodecCapability>();
        var audioCodecs = new List<CodecCapability>();
        foreach (var codec in CodecCatalog.VideoCodecs)
        {
            var encoders = BuildEncoders(codec, probeResults, muxers);
            if (encoders.Count > 0)
            {
                videoCodecs.Add(new CodecCapability
                {
                    Id = codec.Id,
                    DisplayName = codec.DisplayName,
                    TechnicalId = codec.TechnicalId,
                    Kind = CodecKind.Video,
                    ContainerIds = encoders.Where(e => e.IsAvailable).SelectMany(e => e.ContainerIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Encoders = encoders
                });
            }
        }

        foreach (var codec in CodecCatalog.AudioCodecs)
        {
            var encoders = BuildEncoders(codec, probeResults, muxers);
            if (encoders.Count > 0)
            {
                audioCodecs.Add(new CodecCapability
                {
                    Id = codec.Id,
                    DisplayName = codec.DisplayName,
                    TechnicalId = codec.TechnicalId,
                    Kind = CodecKind.Audio,
                    ContainerIds = encoders.Where(e => e.IsAvailable).SelectMany(e => e.ContainerIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Encoders = encoders
                });
            }
        }

        var formats = new List<MediaFormat>();
        foreach (var definition in FormatCatalog.AudioFormats.Concat(FormatCatalog.VideoFormats))
        {
            var muxer = definition.MuxerName ?? definition.TechnicalId ?? definition.Id;
            var canRead = demuxers.Contains(muxer) || demuxers.Contains(definition.Id);
            var canWrite = muxers.Contains(muxer);
            var reason = !canRead && !canWrite
                ? $"This FFmpeg build does not provide the {definition.DisplayName} muxer/demuxer."
                : null;
            formats.Add(definition.ToMediaFormat(EngineIds.FFmpeg, canRead, canWrite, unavailableReason: reason));
        }

        var capability = new EngineCapability
        {
            EngineId = EngineIds.FFmpeg,
            DisplayName = "FFmpeg",
            Version = $"{version} — {_locator.FfmpegPath}",
            IsAvailable = true,
            Formats = formats,
            VideoCodecs = videoCodecs,
            AudioCodecs = audioCodecs
        };

        return new FFmpegCapabilities(capability, probeResults.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            demuxers, muxers, null);
    }

    private static List<EncoderCapability> BuildEncoders(CodecDefinition codec,
        IReadOnlyDictionary<string, bool> probeResults, IReadOnlySet<string> muxers)
    {
        var list = new List<EncoderCapability>();
        foreach (var encoder in codec.Encoders)
        {
            var isAvailable = probeResults.TryGetValue(encoder.Id, out var ok) && ok;
            var containers = encoder.ContainerIds
                .Where(id =>
                {
                    var muxer = FormatCatalog.FindById(id)?.MuxerName ?? id;
                    return muxers.Contains(muxer);
                })
                .ToList();
            var reason = isAvailable
                ? null
                : probeResults.ContainsKey(encoder.Id)
                    ? "The encoder is present but failed an initialization probe."
                    : "The encoder is not included in this FFmpeg build.";
            list.Add(new EncoderCapability
            {
                Id = encoder.Id,
                DisplayName = encoder.DisplayName,
                CodecId = encoder.CodecId,
                Kind = encoder.Kind,
                IsAvailable = isAvailable,
                IsHardware = encoder.IsHardware,
                RateControl = encoder.RateControl,
                QualityLabel = encoder.QualityLabel,
                QualityMin = encoder.QualityMin,
                QualityMax = encoder.QualityMax,
                QualityDefault = encoder.QualityDefault,
                SupportsPresets = encoder.Presets.Count > 0,
                Presets = encoder.Presets,
                DefaultPreset = encoder.DefaultPreset,
                DimensionMultiple = encoder.DimensionMultiple,
                ContainerIds = containers.Count > 0 ? containers : encoder.ContainerIds,
                Notes = encoder.Notes,
                Priority = encoder.Priority,
                UnavailableReason = reason
            });
        }

        return list;
    }

    private async Task<bool> ProbeEncoderAsync(EncoderDefinition encoder, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (encoder.Kind == CodecKind.Video)
        {
            args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=320x184:r=10", "-frames:v", "1", "-an", "-c:v", encoder.Id]);
            if (encoder.Id == "prores_ks")
                args.AddRange(["-pix_fmt", "yuv422p10le"]);
            args.AddRange(["-f", "null", "-"]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "0.3", "-c:a", encoder.Id, "-f", "null", "-"]);
        }

        try
        {
            var result = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = _locator.FfmpegPath!,
                Arguments = args,
                Timeout = TimeSpan.FromSeconds(25),
                MaxCapturedLines = 40
            }, cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogDebug("Encoder {Encoder} probe failed: {Error}", encoder.Id, LastLine(result.StandardError));
            }

            return result.Succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Encoder {Encoder} probe could not run", encoder.Id);
            return false;
        }
    }

    private static (HashSet<string> Demuxers, HashSet<string> Muxers) ParseFormats(IReadOnlyList<string> lines)
    {
        var demuxers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var muxers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length < 6) continue;
            var flags = line[..3];
            var hasD = flags.Contains('D');
            var hasE = flags.Contains('E');
            if (!hasD && !hasE) continue;
            var rest = line[3..].Trim();
            if (rest.Length == 0) continue;
            var namesToken = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(namesToken)) continue;
            foreach (var name in namesToken.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (hasD) demuxers.Add(name);
                if (hasE) muxers.Add(name);
            }
        }

        return (demuxers, muxers);
    }

    private static HashSet<string> ParseEncoders(IReadOnlyList<string> lines)
    {
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var match = EncoderRegex.Match(line);
            if (match.Success) encoders.Add(match.Groups[1].Value);
        }

        return encoders;
    }

    private async Task<ProbeCache?> LoadProbeCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(_paths.CacheDirectory, "ffmpeg-probe-cache.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ProbeCache>(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveProbeCacheAsync(ProbeCache cache, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            var path = Path.Combine(_paths.CacheDirectory, "ffmpeg-probe-cache.json");
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save FFmpeg probe cache");
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private sealed class ProbeCache
    {
        public string Key { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
        public Dictionary<string, bool> Software { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
