using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _settings = new();

    public SettingsService(IAppPaths paths, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Settings => _settings;
    public string SettingsPath => _paths.SettingsFile;

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureCreated();
            if (!File.Exists(_paths.SettingsFile))
            {
                _settings = CreateDefaults();
                await SaveCoreAsync(cancellationToken);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_paths.SettingsFile, cancellationToken);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                _settings = Normalize(loaded ?? CreateDefaults());
                _logger.LogInformation("Settings loaded from {Path}", _paths.SettingsFile);
            }
            catch (Exception ex)
            {
                var badPath = _paths.SettingsFile + $".bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                try
                {
                    File.Move(_paths.SettingsFile, badPath, overwrite: true);
                    _logger.LogWarning(ex, "Settings file was invalid and has been moved to {Path}", badPath);
                }
                catch (Exception moveEx)
                {
                    _logger.LogWarning(moveEx, "Settings file was invalid and could not be moved");
                }

                _settings = CreateDefaults();
                await SaveCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureCreated();
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var candidate = _settings.Clone();
            update(candidate);
            _settings = Normalize(candidate);
            _paths.EnsureCreated();
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _settings = CreateDefaults();
            _paths.EnsureCreated();
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var temp = _paths.SettingsFile + ".tmp";
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, _paths.SettingsFile, overwrite: true);
    }

    private AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        settings.General.DefaultOutputDirectory = _paths.DefaultOutputDirectory;
        settings.Upscaler.DefaultOutputDirectory = _paths.DefaultUpscaleOutputDirectory;
        settings.Upscaler.ModelDirectory = _paths.DefaultModelDirectory;
        settings.VideoUpscaler.DefaultOutputDirectory = Path.Combine(_paths.DefaultOutputDirectory, "Video Upscaled");
        settings.Processing.TempDirectory = _paths.TempDirectory;
        return settings;
    }

    private AppSettings Normalize(AppSettings settings)
    {
        settings.General.DefaultOutputDirectory = NormalizeDirectory(settings.General.DefaultOutputDirectory, _paths.DefaultOutputDirectory);
        settings.Upscaler.DefaultOutputDirectory = NormalizeDirectory(settings.Upscaler.DefaultOutputDirectory, _paths.DefaultUpscaleOutputDirectory);
        settings.Upscaler.ModelDirectory = NormalizeDirectory(settings.Upscaler.ModelDirectory, _paths.DefaultModelDirectory);
        settings.VideoUpscaler.DefaultOutputDirectory = NormalizeDirectory(settings.VideoUpscaler.DefaultOutputDirectory,
            Path.Combine(_paths.DefaultOutputDirectory, "Video Upscaled"));
        settings.VideoUpscaler.FrameRateMultiplier = settings.VideoUpscaler.FrameRateMultiplier is 2 or 3 or 4
            ? settings.VideoUpscaler.FrameRateMultiplier
            : 2;
        settings.VideoUpscaler.VideoBitrateKbps = Math.Clamp(settings.VideoUpscaler.VideoBitrateKbps, 100, 200_000);
        if (string.IsNullOrWhiteSpace(settings.VideoUpscaler.FileNameSuffix))
            settings.VideoUpscaler.FileNameSuffix = "_upscaled";
        var formatDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings.Converter.FormatOutputDirectories
                     ?? new Dictionary<string, string>())
        {
            var id = key?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value)) continue;
            try { formatDirectories[id] = Path.GetFullPath(value); }
            catch { /* Ignore an invalid stored path; fall back to the general destination. */ }
        }
        settings.Converter.FormatOutputDirectories = formatDirectories;
        settings.Ocr.DefaultOutputDirectory = NormalizeDirectory(settings.Ocr.DefaultOutputDirectory,
            Path.Combine(_paths.DefaultOutputDirectory, "OCR"));
        settings.Ocr.ExecutionProvider = settings.Ocr.ExecutionProvider is
            ExecutionProviderKind.Auto or ExecutionProviderKind.Cpu or ExecutionProviderKind.DirectML or ExecutionProviderKind.Vulkan
            ? settings.Ocr.ExecutionProvider
            : ExecutionProviderKind.Auto;
        settings.Processing.TempDirectory = NormalizeDirectory(settings.Processing.TempDirectory, _paths.TempDirectory);

        if (string.IsNullOrWhiteSpace(settings.General.FontFamily))
            settings.General.FontFamily = "Segoe UI Variable Text";
        if (string.IsNullOrWhiteSpace(settings.General.MonoFontFamily))
            settings.General.MonoFontFamily = "JetBrains Mono";
        if (string.IsNullOrWhiteSpace(settings.General.AccentColorHex))
            settings.General.AccentColorHex = "#D2693A";

        settings.General.FontScale = Math.Clamp(settings.General.FontScale, 0.85, 1.35);
        settings.Processing.MaxConcurrentJobs = Math.Clamp(settings.Processing.MaxConcurrentJobs, 1, 8);
        settings.Processing.GpuConcurrency = Math.Clamp(settings.Processing.GpuConcurrency, 1, 2);
        settings.Storage.MaxHistoryEntries = Math.Clamp(settings.Storage.MaxHistoryEntries, 100, 100_000);
        settings.Storage.HistoryRetentionDays = Math.Clamp(settings.Storage.HistoryRetentionDays, 0, 3650);
        settings.Window.Width = Math.Clamp(settings.Window.Width, 1080, 10000);
        settings.Window.Height = Math.Clamp(settings.Window.Height, 700, 10000);
        settings.Window.SidebarWidth = Math.Clamp(settings.Window.SidebarWidth, 240, 400);
        return settings;
    }

    private static string NormalizeDirectory(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return fallback;
        }
    }
}
