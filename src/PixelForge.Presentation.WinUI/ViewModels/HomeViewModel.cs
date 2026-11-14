using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryRepository _history;
    private readonly IJobService _jobs;
    private readonly ICapabilityRegistry _registry;
    private readonly IMediaInspector _inspector;
    private readonly INavigationService _navigation;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _refreshingHistory;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<RecentItemViewModel> _recent = [];
    [ObservableProperty] private ObservableCollection<ActiveJobViewModel> _activeJobs = [];
    [ObservableProperty] private ObservableCollection<EngineCardViewModel> _engines = [];
    [ObservableProperty] private bool _hasRecentFiles;
    [ObservableProperty] private bool _hasActiveJobs;
    [ObservableProperty] private string _statusText = "Starting local engines…";
    [ObservableProperty] private string _statusDetail = "No account or cloud service required";
    [ObservableProperty] private bool _isBusy;

    public HomeViewModel(IHistoryRepository history, IJobService jobs, ICapabilityRegistry registry,
        IMediaInspector inspector, INavigationService navigation, IFilePickerService filePicker,
        IDialogService dialogs, INotificationService notifications, IDiagnosticsService diagnostics,
        IAppLogger logger)
    {
        _history = history;
        _jobs = jobs;
        _registry = registry;
        _inspector = inspector;
        _navigation = navigation;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _notifications = notifications;
        _diagnostics = diagnostics;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _jobs.JobChanged += OnJobChanged;
        _jobs.QueueChanged += OnQueueChanged;
        _registry.CapabilitiesChanged += OnCapabilitiesChanged;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await LoadRecentAsync();
            LoadActiveJobs();
            LoadEngines();
            if (_registry.IsInitialized)
            {
                StatusText = "Ready to forge";
                StatusDetail = "Local processing · no cloud dependency";
            }
            else
            {
                StatusText = "Starting local engines…";
                StatusDetail = "Checking FFmpeg, ImageMagick and ONNX Runtime";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home refresh failed: {Message}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRecentAsync()
    {
        if (_refreshingHistory) return;
        _refreshingHistory = true;
        try
        {
            var entries = await _history.GetRecentAsync(8);
            var items = entries.Select(e => new RecentItemViewModel(e, _navigation, _dialogs)).ToList();
            Recent = new ObservableCollection<RecentItemViewModel>(items);
            HasRecentFiles = items.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Recent history could not be loaded: {Message}", ex.Message);
        }
        finally
        {
            _refreshingHistory = false;
        }
    }

    private void LoadActiveJobs()
    {
        var active = _jobs.Jobs.Where(j => j.State is JobState.Pending or JobState.Preparing or JobState.Running)
            .OrderBy(j => j.CreatedAt)
            .ToList();
        var existing = ActiveJobs.ToDictionary(j => j.Id);
        var next = new List<ActiveJobViewModel>();
        foreach (var job in active)
        {
            if (existing.TryGetValue(job.Id, out var item))
            {
                item.Update(job);
            }
            else
            {
                item = new ActiveJobViewModel(job, _navigation);
            }

            next.Add(item);
        }

        var currentIds = next.Select(n => n.Id).ToList();
        if (!currentIds.SequenceEqual(ActiveJobs.Select(a => a.Id)) ||
            ActiveJobs.Any(a => next.All(n => n.Id != a.Id)))
        {
            ActiveJobs = new ObservableCollection<ActiveJobViewModel>(next);
        }

        HasActiveJobs = ActiveJobs.Count > 0;
    }

    private void LoadEngines()
    {
        var cards = new List<EngineCardViewModel>();
        var image = _registry.GetEngineStatus(EngineIds.Images);
        var ffmpeg = _registry.GetEngineStatus(EngineIds.FFmpeg);
        var upscale = _registry.GetEngineStatus(EngineIds.Upscaling);
        cards.Add(new EngineCardViewModel("Image engine", "ImageMagick · PNG, JPEG, WebP, AVIF",
            image?.IsAvailable == true, image?.Version ?? "Checking…", image?.Error, "\uE91B"));
        cards.Add(new EngineCardViewModel("FFmpeg", "Audio & video · remux and transcode",
            ffmpeg?.IsAvailable == true, ffmpeg?.Version ?? "Checking…", ffmpeg?.Error, "\uE714"));
        var directMl = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML);
        var cpu = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Cpu);
        cards.Add(new EngineCardViewModel("AI upscaler", "ONNX Runtime · Real-ESRGAN models",
            upscale?.IsAvailable == true && (directMl?.IsAvailable == true || cpu?.IsAvailable == true),
            upscale?.Version ?? "Checking…", upscale?.Error, "\uE71E"));
        cards.Add(new EngineCardViewModel("GPU acceleration", "DirectML · AMD / NVIDIA / Intel",
            directMl?.IsAvailable == true,
            directMl?.IsAvailable == true ? "DirectML ready" : "CPU fallback",
            directMl?.Details, "\uE945"));
        Engines = new ObservableCollection<EngineCardViewModel>(cards);

        if (_registry.IsInitialized)
        {
            var unavailable = _registry.Engines.Where(e => !e.IsAvailable).ToList();
            StatusText = unavailable.Count == 0 ? "Ready to forge" : "Engine attention needed";
            StatusDetail = unavailable.Count == 0
                ? "Local processing · no cloud dependency"
                : string.Join(" · ", unavailable.Select(e => e.DisplayName));
        }
    }

    [RelayCommand]
    private void OpenImageConverter() => _navigation.Navigate("Converter", MediaCategory.Image);

    [RelayCommand]
    private void OpenAudioConverter() => _navigation.Navigate("Converter", MediaCategory.Audio);

    [RelayCommand]
    private void OpenVideoConverter() => _navigation.Navigate("Converter", MediaCategory.Video);

    [RelayCommand]
    private void OpenUpscaler() => _navigation.Navigate("Upscaler");

    [RelayCommand]
    private void OpenQueue() => _navigation.Navigate("Queue");

    [RelayCommand]
    private void OpenHistory() => _navigation.Navigate("History");

    [RelayCommand]
    private void OpenModels() => _navigation.Navigate("Models");

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate("Settings");

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var files = await _filePicker.PickFilesAsync(MediaCategory.Unknown);
        if (files.Count > 0) await HandleDroppedPathsAsync(files);
    }

    public async Task HandleDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var folders = paths.Where(Directory.Exists).ToList();
        if (folders.Count > 0)
        {
            _navigation.Navigate("Upscaler", new UpscalerImportRequest(folders[0], true));
            if (folders.Count > 1)
                _notifications.Notify("Folder ready", "The first dropped folder was sent to the upscaler.", NotificationSeverity.Info);
            return;
        }

        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;
        IsBusy = true;
        try
        {
            var infos = await _inspector.InspectManyAsync(files);
            var readable = infos.Where(i => i.IsReadable).ToList();
            var unreadable = infos.Where(i => !i.IsReadable).ToList();
            if (unreadable.Count > 0)
            {
                await _dialogs.ShowErrorAsync("Some files could not be imported",
                    string.Join("\n", unreadable.Take(6).Select(i => $"{i.FileName}: {i.Error}")));
            }

            if (readable.Count == 0)
            {
                await _dialogs.ShowMessageAsync("No supported media found",
                    "Drop images, audio, video, or a folder of images to get started.");
                return;
            }

            var images = readable.Where(i => i.Category == MediaCategory.Image).Select(i => i.FilePath).ToList();
            var audio = readable.Where(i => i.Category == MediaCategory.Audio).Select(i => i.FilePath).ToList();
            var video = readable.Where(i => i.Category == MediaCategory.Video).Select(i => i.FilePath).ToList();
            var preferred = images.Count >= audio.Count && images.Count >= video.Count
                ? MediaCategory.Image
                : audio.Count >= video.Count ? MediaCategory.Audio : MediaCategory.Video;
            _navigation.Navigate("Converter", new ConverterImportRequest(readable.Select(i => i.FilePath).ToList(), preferred));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnJobChanged(sender, e));
            return;
        }

        LoadActiveJobs();
        if (e.Job.IsTerminal)
            _ = LoadRecentAsync();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnQueueChanged(sender, e));
            return;
        }

        LoadActiveJobs();
    }

    private void OnCapabilitiesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnCapabilitiesChanged(sender, e));
            return;
        }

        LoadEngines();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.JobChanged -= OnJobChanged;
        _jobs.QueueChanged -= OnQueueChanged;
        _registry.CapabilitiesChanged -= OnCapabilitiesChanged;
    }
}

public sealed class EngineCardViewModel
{
    public EngineCardViewModel(string name, string description, bool isReady, string version, string? error, string glyph)
    {
        Name = name;
        Description = description;
        IsReady = isReady;
        Version = version;
        Error = error;
        Glyph = glyph;
        StatusText = isReady ? "Ready" : "Unavailable";
        Detail = string.IsNullOrWhiteSpace(error) ? version : error;
    }

    public string Name { get; }
    public string Description { get; }
    public bool IsReady { get; }
    public string Version { get; }
    public string? Error { get; }
    public string Glyph { get; }
    public string StatusText { get; }
    public string Detail { get; }
}

public sealed partial class ActiveJobViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    public ActiveJobViewModel(ProcessingJob job, INavigationService navigation)
    {
        _navigation = navigation;
        Id = job.Id;
        Update(job);
    }

    public Guid Id { get; }
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private string _iconGlyph = "\uE8AB";

    public void Update(ProcessingJob job)
    {
        Title = job.Title;
        StateText = job.StateText;
        Progress = job.Progress * 100;
        ProgressText = job.ProgressText;
        OperationText = job.OperationText;
        IconGlyph = job.Operation switch
        {
            OperationKind.ImageConvert => "\uE91B",
            OperationKind.AudioConvert => "\uE8D6",
            OperationKind.VideoConvert => "\uE714",
            OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
            OperationKind.VideoUpscale => "\uE8B2",
            OperationKind.OcrDocument => "\uE8A5",
            _ => "\uE8AB"
        };
    }

    [RelayCommand]
    private void OpenQueue() => _navigation.Navigate("Queue");
}

public sealed partial class RecentItemViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly HistoryEntry _entry;

    public RecentItemViewModel(HistoryEntry entry, INavigationService navigation, IDialogService dialogs)
    {
        _entry = entry;
        _navigation = navigation;
        _dialogs = dialogs;
    }

    public string Title => _entry.OutputFileName ?? _entry.InputFileName ?? "Processed file";
    public string Subtitle => _entry.Operation switch
    {
        OperationKind.ImageConvert => "Image conversion",
        OperationKind.AudioConvert => "Audio conversion",
        OperationKind.VideoConvert => "Video conversion",
        OperationKind.UpscaleAi => "AI upscale",
        OperationKind.UpscaleTraditional => "Image upscale",
        OperationKind.VideoUpscale => "Video upscaling",
        OperationKind.OcrDocument => "OCR document",
        _ => "Processing"
    };
    public string Detail => _entry.InputFileName is null ? "" : "from " + _entry.InputFileName;
    public string TimeText => FormatRelative(_entry.CreatedAt);
    public string SizeText => _entry.OutputBytes is > 0 ? FileSizeConverter.FormatBytes(_entry.OutputBytes.Value) : "";
    public bool IsSuccess => _entry.Status == JobItemState.Completed;
    public string StatusText => _entry.Status switch
    {
        JobItemState.Completed => "Completed",
        JobItemState.Failed => "Failed",
        JobItemState.Cancelled => "Cancelled",
        JobItemState.Skipped => "Skipped",
        _ => _entry.Status.ToString()
    };
    public string IconGlyph => _entry.Operation switch
    {
        OperationKind.ImageConvert => "\uE91B",
        OperationKind.AudioConvert => "\uE8D6",
        OperationKind.VideoConvert => "\uE714",
        OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
        OperationKind.VideoUpscale => "\uE8B2",
        _ => "\uE8AB"
    };

    public bool CanCompare => _entry.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional
                              && _entry.InputPath is not null && _entry.OutputPath is not null;

    private static string FormatRelative(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return time.LocalDateTime.ToString("MMM d");
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (string.IsNullOrWhiteSpace(_entry.OutputPath) || !File.Exists(_entry.OutputPath))
        {
            await _dialogs.ShowErrorAsync("File not available", "The output file no longer exists at its recorded location.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _entry.OutputPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the file", "The file could not be opened by its associated application.", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RevealAsync()
    {
        if (string.IsNullOrWhiteSpace(_entry.OutputPath))
        {
            await _dialogs.ShowErrorAsync("Folder not available", "No output folder was recorded for this item.");
            return;
        }

        var folder = Directory.Exists(_entry.OutputPath) ? _entry.OutputPath : Path.GetDirectoryName(_entry.OutputPath);
        if (folder is null || !Directory.Exists(folder))
        {
            await _dialogs.ShowErrorAsync("Folder not available", "The output folder no longer exists.");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    [RelayCommand]
    private void Compare()
    {
        if (CanCompare)
            _navigation.Navigate("Compare", new ComparisonRequest(_entry.InputPath!, _entry.OutputPath!, Title));
    }
}
