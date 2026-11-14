using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Diagnostics;

public sealed class SystemDiagnosticsService : IDiagnosticsService
{
    private readonly ICapabilityRegistry _registry;
    private readonly IModelManager _models;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;

    public SystemDiagnosticsService(ICapabilityRegistry registry, IModelManager models, IAppPaths paths,
        ISettingsService settings)
    {
        _registry = registry;
        _models = models;
        _paths = paths;
        _settings = settings;
    }

    public async Task<SystemDiagnostics> BuildAsync(CancellationToken cancellationToken = default)
    {
        await _registry.InitializeAsync(cancellationToken);
        return await Task.Run(() =>
        {
            var cpu = QueryFirst("Win32_Processor", "Name", "NumberOfLogicalProcessors");
            var system = QueryFirst("Win32_ComputerSystem", "TotalPhysicalMemory");
            var gpus = QueryGpus();
            var engines = _registry.Engines.Select(e => new EngineStatusInfo
            {
                EngineId = e.EngineId,
                DisplayName = e.DisplayName,
                IsAvailable = e.IsAvailable,
                Version = e.Version,
                StatusText = e.IsAvailable ? "Ready" : "Unavailable",
                Error = e.Error,
                Details = BuildEngineDetails(e)
            }).ToList();

            var videoCodecs = _registry.Engines.Where(e => e.IsAvailable).SelectMany(e => e.VideoCodecs)
                .Where(c => c.IsAvailable).Select(c => c.DisplayName).Distinct().OrderBy(s => s).ToList();
            var audioCodecs = _registry.Engines.Where(e => e.IsAvailable).SelectMany(e => e.AudioCodecs)
                .Where(c => c.IsAvailable).Select(c => c.DisplayName).Distinct().OrderBy(s => s).ToList();
            var formats = _registry.ImageOutputFormats.Select(f => f.NormalizedExtension)
                .Distinct().OrderBy(s => s).ToList();

            var upscaleEngine = _registry.GetEngine(EngineIds.Upscaling);
            return new SystemDiagnostics
            {
                AppVersion = GetAppVersion(),
                OperatingSystem = $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                CpuName = cpu.GetValueOrDefault("Name") ?? "Unknown CPU",
                LogicalCoreCount = int.TryParse(cpu.GetValueOrDefault("NumberOfLogicalProcessors"), out var cores) ? cores : Environment.ProcessorCount,
                TotalMemoryBytes = long.TryParse(system.GetValueOrDefault("TotalPhysicalMemory"), out var memory) ? memory : 0,
                Gpus = gpus,
                Engines = engines,
                ExecutionProviders = _registry.ExecutionProviders,
                InstalledModels = _models.InstalledModels,
                AvailableVideoCodecs = videoCodecs,
                AvailableAudioCodecs = audioCodecs,
                AvailableImageFormats = formats,
                SettingsPath = _paths.SettingsFile,
                DatabasePath = _paths.DatabaseFile,
                LogsPath = _paths.LogsDirectory,
                ModelDirectory = _models.ModelDirectory,
                TempDirectory = _settings.Settings.Processing.TempDirectory,
                AiRuntimeVersion = upscaleEngine?.Version
            };
        }, cancellationToken);
    }

    public async Task<string> BuildReportAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = await BuildAsync(cancellationToken);
        return BuildReport(diagnostics);
    }

    public Task<string> BuildReportAsync(SystemDiagnostics diagnostics, CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildReport(diagnostics));

    private static string BuildReport(SystemDiagnostics d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PixelForge diagnostics");
        sb.AppendLine("=====================");
        sb.AppendLine($"App version:        {d.AppVersion}");
        sb.AppendLine($"Operating system:   {d.OperatingSystem}");
        sb.AppendLine($"Architecture:       {d.Architecture}");
        sb.AppendLine($"CPU:                {d.CpuName} ({d.LogicalCoreCount} logical cores)");
        sb.AppendLine($"Memory:             {d.TotalMemoryBytes / 1024d / 1024 / 1024:0.0} GB");
        foreach (var gpu in d.Gpus)
            sb.AppendLine($"GPU:                {gpu.Name} · driver {gpu.DriverVersion ?? "unknown"} · {gpu.AdapterMemoryBytes / 1024d / 1024 / 1024:0.0} GB reported");
        sb.AppendLine();
        sb.AppendLine("Engines");
        foreach (var e in d.Engines)
        {
            sb.AppendLine($"  {e.DisplayName} [{e.EngineId}] {e.Version} — {(e.IsAvailable ? "READY" : "UNAVAILABLE")}");
            if (!string.IsNullOrWhiteSpace(e.Error)) sb.AppendLine($"    Error: {e.Error}");
            foreach (var detail in e.Details) sb.AppendLine($"    {detail}");
        }

        sb.AppendLine();
        sb.AppendLine("AI execution providers");
        foreach (var p in d.ExecutionProviders)
            sb.AppendLine($"  {p.DisplayName} — {(p.IsAvailable ? "available" : "unavailable")} {(p.DeviceName is null ? "" : $"({p.DeviceName})")}");

        sb.AppendLine();
        sb.AppendLine("Installed models");
        if (d.InstalledModels.Count == 0) sb.AppendLine("  None");
        foreach (var m in d.InstalledModels)
            sb.AppendLine($"  {m.Descriptor.Name} {m.Descriptor.Version} — {(m.IsValid ? "valid" : "INVALID")} — {m.FilePath}");

        sb.AppendLine();
        sb.AppendLine($"Video codecs ({d.AvailableVideoCodecs.Count}): {string.Join(", ", d.AvailableVideoCodecs)}");
        sb.AppendLine($"Audio codecs ({d.AvailableAudioCodecs.Count}): {string.Join(", ", d.AvailableAudioCodecs)}");
        sb.AppendLine($"Image output formats ({d.AvailableImageFormats.Count}): {string.Join(", ", d.AvailableImageFormats)}");
        sb.AppendLine();
        sb.AppendLine($"Settings:  {d.SettingsPath}");
        sb.AppendLine($"Database:  {d.DatabasePath}");
        sb.AppendLine($"Logs:      {d.LogsPath}");
        sb.AppendLine($"Models:    {d.ModelDirectory}");
        sb.AppendLine($"Temp:      {d.TempDirectory}");
        return sb.ToString();
    }

    private static List<string> BuildEngineDetails(EngineCapability e)
    {
        var details = new List<string>();
        var read = e.Formats.Where(f => f.CanRead).Select(f => f.NormalizedExtension).Distinct().ToList();
        var write = e.Formats.Where(f => f.CanWrite).Select(f => f.NormalizedExtension).Distinct().ToList();
        if (read.Count > 0) details.Add($"Read: {string.Join(" ", read)}");
        if (write.Count > 0) details.Add($"Write: {string.Join(" ", write)}");
        var hardware = e.VideoCodecs.Where(c => c.IsAvailable).SelectMany(c => c.Encoders)
            .Where(x => x.IsHardware && x.IsAvailable).Select(x => x.Id).Distinct().ToList();
        if (hardware.Count > 0) details.Add($"Hardware encoders: {string.Join(", ", hardware)}");
        return details;
    }

    private static Dictionary<string, string> QueryFirst(string wmiClass, params string[] properties)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {string.Join(",", properties)} FROM {wmiClass}");
            foreach (ManagementObject obj in searcher.Get())
            {
                foreach (var property in properties)
                {
                    var value = obj[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) result[property] = value;
                }

                break;
            }
        }
        catch
        {
            // WMI is best-effort.
        }

        return result;
    }

    private static List<GpuInfo> QueryGpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "GPU";
                long? memory = long.TryParse(obj["AdapterRAM"]?.ToString(), out var ram) ? ram : null;
                result.Add(new GpuInfo
                {
                    Name = name,
                    DriverVersion = obj["DriverVersion"]?.ToString(),
                    AdapterMemoryBytes = memory,
                    Vendor = obj["AdapterCompatibility"]?.ToString()
                });
            }
        }
        catch
        {
            // WMI is best-effort.
        }

        return result;
    }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SystemDiagnosticsService).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return version;
    }
}
