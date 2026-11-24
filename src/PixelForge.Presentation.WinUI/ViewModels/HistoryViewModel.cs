using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed record HistoryFilter(OperationKind? Operation, string Label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    public override string ToString() => DisplayLabel;
}

public sealed record HistoryStatusFilter(JobItemState? Status, string Label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayLabel => LocalizationService.T(Label);
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    public override string ToString() => DisplayLabel;
}

public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryRepository _history;
    private readonly IJobService _jobs;
    private readonly IMediaInspector _inspector;
    private readonly IOperationSettingsSerializer _serializer;
    private readonly IFilePickerService _filePicker;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _searchCts;
    private bool _disposed;

    public HistoryViewModel(IHistoryRepository history, IJobService jobs, IMediaInspector inspector,
        IOperationSettingsSerializer serializer, IFilePickerService filePicker, INavigationService navigation,
        IDialogService dialogs, INotificationService notifications, IAppLogger logger)
    {
        _history = history;
        _jobs = jobs;
        _inspector = inspector;
        _serializer = serializer;
        _filePicker = filePicker;
        _navigation = navigation;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SelectedFilter = Filters[0];
        SelectedStatus = StatusFilters[0];
    }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<HistoryFilter> Filters { get; } =
    [
        new(null, "All operations"),
        new(OperationKind.ImageConvert, "Image conversion"),
        new(OperationKind.AudioConvert, "Audio conversion"),
        new(OperationKind.VideoConvert, "Video conversion"),
        new(OperationKind.UpscaleAi, "AI upscale"),
        new(OperationKind.UpscaleTraditional, "Upscale"),
        new(OperationKind.VideoUpscale, "Video upscaling"),
        new(OperationKind.OcrDocument, "OCR document")
    ];

    public IReadOnlyList<HistoryStatusFilter> StatusFilters { get; } =
    [
        new(null, "All statuses"),
        new(JobItemState.Completed, "Completed"),
        new(JobItemState.Failed, "Failed"),
        new(JobItemState.Cancelled, "Cancelled")
    ];

    /// <summary>
    /// Re-evaluates every localized binding in place without rebuilding the
    /// item collections, so the current ComboBox selection is preserved.
    /// </summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in Filters) option.RefreshLocalization();
        foreach (var option in StatusFilters) option.RefreshLocalization();
        foreach (var item in Items) item.RefreshLocalization();
        ResultSummary = itemsSummary(Items.Count);
    }

    [ObservableProperty] private ObservableCollection<HistoryItemViewModel> _items = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HistoryFilter _selectedFilter;
    [ObservableProperty] private HistoryStatusFilter _selectedStatus;
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _resultSummary = "Search locally recorded operations";

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await _history.QueryAsync(new HistoryQuery
            {
                Limit = 500,
                Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Operation = SelectedFilter?.Operation,
                Status = SelectedStatus?.Status
            });
            Items = new ObservableCollection<HistoryItemViewModel>(entries.Select(e =>
                new HistoryItemViewModel(e, this)));
            HasItems = Items.Count > 0;
            ResultSummary = itemsSummary(Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History could not be loaded");
            await _dialogs.ShowErrorAsync("History could not be loaded", ex.Message, ex.ToString());
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string itemsSummary(int count) =>
        count == 0
            ? LocalizationService.T("No recorded operations")
            : LocalizationService.F("{0} · stored locally",
                LocalizationService.P("Count.RecordedOperation", count));

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedSearchAsync(_searchCts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            if (!cancellationToken.IsCancellationRequested) await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
    }

    partial void OnSelectedFilterChanged(HistoryFilter value) => _ = LoadAsync();
    partial void OnSelectedStatusChanged(HistoryStatusFilter value) => _ = LoadAsync();

    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        var path = await _filePicker.PickJsonSaveFileAsync($"pixelforge-history-{DateTime.Now:yyyyMMdd}.json");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var document = await _history.ExportAsync(new HistoryQuery
            {
                Limit = 100_000,
                Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Operation = SelectedFilter?.Operation,
                Status = SelectedStatus?.Status
            }, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, ExportJsonOptions));
            _notifications.Notify("History exported",
                "The export contains local filenames and paths. Review it before sharing.",
                NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History export failed");
            await _dialogs.ShowErrorAsync("Could not export history", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private async Task ImportHistoryAsync()
    {
        var path = await _filePicker.PickJsonOpenFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var file = new FileInfo(path);
            if (file.Length > 50L * 1024 * 1024)
            {
                await _dialogs.ShowErrorAsync("Import file too large", "History imports are limited to 50 MB.");
                return;
            }

            var json = await File.ReadAllTextAsync(path);
            var document = JsonSerializer.Deserialize<HistoryExportDocument>(json, ExportJsonOptions);
            if (document is null || document.Entries is null)
            {
                await _dialogs.ShowErrorAsync("Invalid history file", "The selected file is not a PixelForge history export.");
                return;
            }

            if (!await _dialogs.ConfirmAsync("Import history?",
                    LocalizationService.F(
                    "Import {0}? Existing matching records will be skipped. Imported records keep their original paths and may refer to files that no longer exist.",
                    LocalizationService.P("Count.Record", document.Entries.Count)),
                    "Import", "Cancel"))
                return;

            var result = await _history.ImportAsync(document);
            await LoadAsync();
            await _dialogs.ShowMessageAsync("History imported",
                $"Imported {result.Imported}, skipped {result.Skipped}, invalid {result.Invalid}.");
        }
        catch (JsonException)
        {
            await _dialogs.ShowErrorAsync("Invalid history file", "The selected file is not valid PixelForge history JSON.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History import failed");
            await _dialogs.ShowErrorAsync("Could not import history", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear history?",
                "Delete all operation history from the local SQLite database? Your media files are not touched.",
                "Clear history", "Cancel", destructive: true)) return;
        await _history.ClearAsync();
        await LoadAsync();
    }

    public async Task RepeatAsync(HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.InputPath) || !File.Exists(entry.InputPath))
        {
            await _dialogs.ShowErrorAsync("Source file missing", "The original input file no longer exists, so this operation cannot be repeated.");
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.SettingsJson))
        {
            await _dialogs.ShowErrorAsync("Settings unavailable", "The recorded settings for this operation are incomplete.");
            return;
        }

        try
        {
            var settings = _serializer.Deserialize(entry.Operation, entry.SettingsJson);
            settings.CollisionPolicy = CollisionPolicy.UniqueName;
            var info = await _inspector.InspectAsync(entry.InputPath);
            var request = new JobRequest
            {
                Operation = entry.Operation,
                Title = LocalizationService.F("Repeat · {0}", entry.Title),
                Settings = settings,
                Inputs = [new JobInput { Path = entry.InputPath, Info = info }]
            };
            var validation = await _jobs.ValidateAsync(request);
            if (!validation.IsValid)
            {
                await _dialogs.ShowJobValidationAsync(validation);
                return;
            }

            var job = await _jobs.EnqueueAsync(request);
            _notifications.Notify("Repeat added to queue",
                LocalizationService.F("{0} · {1}", job.Title, LocalizationService.T("unique output names")),
                NotificationSeverity.Success);
            _navigation.Navigate("Queue");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History repeat failed");
            await _dialogs.ShowErrorAsync("Could not repeat the operation", ex.Message, ex.ToString());
        }
    }

    public void Compare(HistoryEntry entry)
    {
        if (entry.InputPath is null || entry.OutputPath is null) return;
        _navigation.Navigate("Compare", new ComparisonRequest(entry.InputPath, entry.OutputPath,
            entry.OutputFileName ?? entry.InputFileName ?? "Comparison"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}

public sealed partial class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryViewModel _owner;
    public HistoryItemViewModel(HistoryEntry entry, HistoryViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public HistoryEntry Entry { get; }
    public string Title => Entry.OutputFileName ?? Entry.InputFileName ?? "Processed file";
    public void RefreshLocalization() => OnPropertyChanged(string.Empty);
    public string InputName => Entry.InputFileName ?? "";
    public string OperationText => Entry.Operation switch
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
    public string IconGlyph => Entry.Operation switch
    {
        OperationKind.ImageConvert => "\uE91B",
        OperationKind.AudioConvert => "\uE8D6",
        OperationKind.VideoConvert => "\uE714",
        OperationKind.UpscaleAi or OperationKind.UpscaleTraditional => "\uE71E",
        OperationKind.VideoUpscale => "\uE8B2",
        OperationKind.OcrDocument => "\uE8A5",
        _ => "\uE8AB"
    };
    public string FormatText => string.IsNullOrWhiteSpace(Entry.FormatId) ? "—" : Entry.FormatId.ToUpperInvariant();
    public string StatusText => Entry.Status switch
    {
        JobItemState.Completed => "Completed",
        JobItemState.Failed => "Failed",
        JobItemState.Cancelled => "Cancelled",
        JobItemState.Skipped => "Skipped",
        _ => Entry.Status.ToString()
    };
    public bool IsSuccess => Entry.Status == JobItemState.Completed;
    public string TimeText => LocalizationService.FormatDateTime(Entry.CreatedAt, "g");
    public string DurationText => Entry.DurationMs > 0
        ? LocalizationService.F("{0}s", LocalizationService.FormatNumber(Entry.DurationMs / 1000, "0.0"))
        : "-";
    public string SizeText => Entry.OutputBytes is > 0 ? FileSizeConverter.FormatBytes(Entry.OutputBytes.Value) : "—";
    public string InputSizeText => FileSizeConverter.FormatBytes(Entry.InputBytes);
    public bool CanCompare => Entry.Operation is OperationKind.UpscaleAi or OperationKind.UpscaleTraditional or OperationKind.ImageProcess
                              && Entry.InputPath is not null && Entry.OutputPath is not null;
    public string ErrorText => Entry.Error ?? "";
    public string ProviderText => !string.IsNullOrWhiteSpace(Entry.ActualProvider)
        ? LocalizationService.F("Provider: {0}", Entry.ActualProvider) +
          (string.IsNullOrWhiteSpace(Entry.RequestedProvider) ? "" : LocalizationService.F(" (requested {0})", Entry.RequestedProvider))
        : !string.IsNullOrWhiteSpace(Entry.RequestedProvider)
            ? LocalizationService.F("Provider requested: {0}", Entry.RequestedProvider)
            : "";
    public string ModelText => string.IsNullOrWhiteSpace(Entry.ModelId)
        ? ""
        : LocalizationService.F("Model: {0}", Entry.ModelId);
    public string ErrorDetailsText => Entry.ErrorDetails ?? "";

    [RelayCommand]
    private void Open()
    {
        if (string.IsNullOrWhiteSpace(Entry.OutputPath) ||
            (!File.Exists(Entry.OutputPath) && !Directory.Exists(Entry.OutputPath)))
        {
            _ = ShowMissingAsync();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Entry.OutputPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = ShowOpenErrorAsync(ex.Message);
        }
    }

    [RelayCommand]
    private void Reveal()
    {
        var path = Entry.OutputPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Ignore.
        }
    }

    [RelayCommand]
    private void Compare() => _owner.Compare(Entry);

    [RelayCommand]
    private void Repeat() => _ = _owner.RepeatAsync(Entry);

    private Task ShowMissingAsync() =>
        App.Services.GetRequiredService<IDialogService>().ShowErrorAsync("File not available", "The output file no longer exists at its recorded location.");

    private Task ShowOpenErrorAsync(string message) =>
        App.Services.GetRequiredService<IDialogService>().ShowErrorAsync("Could not open the file", message);
}
