using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class WatchFolderItemViewModel : ObservableObject
{
    private readonly WatchFolderRule _rule;
    private readonly WatchFoldersViewModel _owner;
    private bool _syncing;

    public WatchFolderItemViewModel(WatchFolderRule rule, WatchFoldersViewModel owner)
    {
        _rule = rule;
        _owner = owner;
        _isEnabled = rule.IsEnabled;
    }

    public Guid Id => _rule.Id;
    public string Name => _rule.Name;
    public string InputDirectory => _rule.InputDirectory;
    public string OutputDirectory => _rule.OutputDirectory;
    public string OperationLabel => _rule.OperationLabel;
    public string StatusText => _rule.StatusText;
    public bool HasError => !string.IsNullOrWhiteSpace(_rule.LastError);
    public string ErrorText => _rule.LastError ?? "";
    public string Detail => $"{_rule.OperationLabel} · {(_rule.IncludeSubdirectories ? "include subfolders" : "top folder only")}";

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _owner.SetEnabledAsync(_rule.Id, value);
    }

    public void SyncEnabled(bool value)
    {
        _syncing = true;
        IsEnabled = value;
        _syncing = false;
    }

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(_rule.Id);

    [RelayCommand]
    private void OpenInput() => _owner.OpenFolder(_rule.InputDirectory);

    [RelayCommand]
    private void OpenOutput() => _owner.OpenFolder(_rule.OutputDirectory);
}

public sealed partial class WatchFoldersViewModel : ObservableObject, IDisposable
{
    private readonly IWatchFolderService _watch;
    private readonly ICapabilityRegistry _registry;
    private readonly IFilePickerService _picker;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _initialized;
    private bool _disposed;
    private bool _loadingFormats;

    public WatchFoldersViewModel(IWatchFolderService watch, ICapabilityRegistry registry, IFilePickerService picker,
        IAppPaths paths, ISettingsService settings, INotificationService notifications, IAppLogger logger)
    {
        _watch = watch;
        _registry = registry;
        _picker = picker;
        _paths = paths;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _watch.RulesChanged += OnRulesChanged;
        InputDirectory = "";
        OutputDirectory = Path.Combine(_paths.DefaultOutputDirectory, "Watched");
        SelectedCategory = Categories[0];
        SelectedFormat = null;
        Task.Run(async () => await RefreshAsync());
    }

    public ObservableCollection<WatchFolderItemViewModel> Rules { get; } = [];
    public ObservableCollection<FormatItemViewModel> Formats { get; } = [];
    public IReadOnlyList<Option<MediaCategory>> Categories { get; } =
    [
        new(MediaCategory.Image, "Images"),
        new(MediaCategory.Audio, "Audio"),
        new(MediaCategory.Video, "Video"),
        new(MediaCategory.Document, "Documents"),
        new(MediaCategory.Ebook, "E-books"),
        new(MediaCategory.Archive, "Archives")
    ];

    public IReadOnlyList<Option<int>> AudioBitrates { get; } =
    [
        new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"),
        new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")
    ];

    [ObservableProperty] private string _ruleName = "Watched folder";
    [ObservableProperty] private string _inputDirectory = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _includeSubdirectories = true;
    [ObservableProperty] private bool _enableImmediately = true;
    [ObservableProperty] private Option<MediaCategory> _selectedCategory;
    [ObservableProperty] private FormatItemViewModel? _selectedFormat;
    [ObservableProperty] private double _imageQuality = 88;
    [ObservableProperty] private Option<int> _selectedAudioBitrate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Create a rule to watch a folder and queue new files automatically.";
    [ObservableProperty] private bool _hasError;

    public bool IsImageCategory => SelectedCategory?.Value == MediaCategory.Image;
    public bool IsAudioCategory => SelectedCategory?.Value == MediaCategory.Audio;
    public bool IsVideoCategory => SelectedCategory?.Value == MediaCategory.Video;
    public bool IsDocumentCategory => SelectedCategory?.Value is MediaCategory.Document or MediaCategory.Ebook;
    public bool IsArchiveCategory => SelectedCategory?.Value == MediaCategory.Archive;
    public bool HasRules => Rules.Count > 0;
    public bool HasInputFolder => !string.IsNullOrWhiteSpace(InputDirectory) && Directory.Exists(InputDirectory);
    public string RunSummary => Rules.Count == 1 ? "1 watch rule" : $"{Rules.Count} watch rules";

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!_registry.IsInitialized) await _registry.InitializeAsync();
        await _watch.InitializeAsync();
        SelectedAudioBitrate = AudioBitrates[3];
        LoadFormats();
        await RefreshAsync();
    }

    private void LoadFormats()
    {
        if (SelectedCategory is null || !_registry.IsInitialized) return;
        _loadingFormats = true;
        Formats.Clear();
        foreach (var option in _registry.GetOutputFormats(SelectedCategory.Value))
            Formats.Add(new FormatItemViewModel(option.Format));
        SelectedFormat = Formats.FirstOrDefault();
        _loadingFormats = false;
        OnPropertyChanged(nameof(SelectedFormat));
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        try
        {
            var rules = await _watch.GetRulesAsync();
            void Apply()
            {
                Rules.Clear();
                foreach (var rule in rules.OrderBy(r => r.Name))
                    Rules.Add(new WatchFolderItemViewModel(rule, this));
                OnPropertyChanged(nameof(HasRules));
                OnPropertyChanged(nameof(RunSummary));
            }

            if (_dispatcher.HasThreadAccess) Apply();
            else _dispatcher.TryEnqueue(Apply);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load watch folders: {Message}", ex.Message);
        }
    }

    private void OnRulesChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        var folder = await _picker.PickFolderAsync(InputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) InputDirectory = folder;
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var folder = await _picker.PickFolderAsync(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder)) OutputDirectory = folder;
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (SelectedCategory is null || SelectedFormat is null)
        {
            await SetErrorAsync("Choose a category and output format.");
            return;
        }

        if (!Directory.Exists(InputDirectory))
        {
            await SetErrorAsync("Choose an existing input folder.");
            return;
        }

        IsBusy = true;
        try
        {
            var (operation, settings) = BuildRuleSettings();
            var rule = new WatchFolderRule
            {
                Name = string.IsNullOrWhiteSpace(RuleName) ? "Watched folder" : RuleName.Trim(),
                InputDirectory = InputDirectory,
                OutputDirectory = OutputDirectory,
                IncludeSubdirectories = IncludeSubdirectories,
                IsEnabled = EnableImmediately,
                Operation = operation,
                Settings = settings
            };
            await _watch.CreateAsync(rule);
            StatusMessage = $"Watching {Path.GetFileName(InputDirectory.TrimEnd(Path.DirectorySeparatorChar))} → {Path.GetFileName(OutputDirectory.TrimEnd(Path.DirectorySeparatorChar))}.";
            HasError = false;
            _notifications.Notify("Watch rule created", rule.Name, NotificationSeverity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (OperationKind Operation, OperationSettings Settings) BuildRuleSettings()
    {
        var category = SelectedCategory!.Value;
        var format = SelectedFormat!.Id;
        return category switch
        {
            MediaCategory.Image => (OperationKind.ImageConvert, new ImageConversionSettings
            {
                OutputFormatId = format,
                Quality = (int)Math.Clamp(ImageQuality, 1, 100),
                PreserveMetadata = true,
                PreserveIccProfile = true,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Audio => (OperationKind.AudioConvert, BuildAudioSettings(format)),
            MediaCategory.Video => (OperationKind.VideoConvert, BuildVideoSettings(format)),
            MediaCategory.Document => (OperationKind.DocumentConvert, new DocumentConversionSettings
            {
                OutputFormatId = format,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Ebook => (OperationKind.EbookConvert, new EbookConversionSettings
            {
                OutputFormatId = format,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            MediaCategory.Archive => (OperationKind.ArchiveConvert, new ArchiveConversionSettings
            {
                OutputFormatId = format,
                Action = ArchiveAction.Create,
                CompressionLevel = 5,
                OutputDirectory = OutputDirectory,
                CollisionPolicy = CollisionPolicy.UniqueName
            }),
            _ => throw new InvalidOperationException("Choose a supported category.")
        };
    }

    private AudioConversionSettings BuildAudioSettings(string formatId)
    {
        var codec = _registry.GetAudioCodecs(formatId, false).FirstOrDefault();
        var encoder = codec?.Encoders.FirstOrDefault(e => e.IsAvailable);
        return new AudioConversionSettings
        {
            OutputFormatId = formatId,
            CodecId = codec?.Id ?? "aac",
            EncoderId = encoder?.Id ?? "aac",
            BitrateKbps = SelectedAudioBitrate?.Value ?? 192,
            OutputDirectory = OutputDirectory,
            CollisionPolicy = CollisionPolicy.UniqueName
        };
    }

    private VideoConversionSettings BuildVideoSettings(string formatId)
    {
        var (codec, audio) = formatId switch
        {
            "webm" => ("vp9", "opus"),
            "avi" => ("mpeg4", "mp3"),
            "gif" => ("gif", "none"),
            _ => ("h264", "aac")
        };
        var codecs = _registry.GetVideoCodecs(formatId);
        if (codecs.Count > 0 && codecs.All(c => c.Id != codec)) codec = codecs[0].Id;
        var audioCodecs = _registry.GetAudioCodecs(formatId, true);
        if (audioCodecs.Count > 0 && audioCodecs.All(c => c.Id != audio)) audio = audioCodecs[0].Id;
        return new VideoConversionSettings
        {
            ContainerFormatId = formatId,
            VideoCodecId = codec,
            AudioCodecId = audio,
            AudioBitrateKbps = 128,
            QualityValue = 23,
            Preset = ConversionPreset.Balanced,
            FastStart = true,
            OutputDirectory = OutputDirectory,
            CollisionPolicy = CollisionPolicy.UniqueName
        };
    }

    public async Task SetEnabledAsync(Guid id, bool enabled)
    {
        try
        {
            await _watch.SetEnabledAsync(id, enabled);
            StatusMessage = enabled ? "Rule resumed." : "Rule paused.";
            HasError = false;
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        try
        {
            await _watch.DeleteAsync(id);
            _notifications.Notify("Watch rule removed", "Source files were not deleted.", NotificationSeverity.Info);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
    }

    public void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { }
    }

    private async Task SetErrorAsync(string message)
    {
        HasError = true;
        StatusMessage = message;
        await Task.CompletedTask;
    }

    partial void OnInputDirectoryChanged(string value) => OnPropertyChanged(nameof(HasInputFolder));
    partial void OnSelectedCategoryChanged(Option<MediaCategory> value)
    {
        if (value is null) return;
        LoadFormats();
        OnPropertyChanged(nameof(IsImageCategory));
        OnPropertyChanged(nameof(IsAudioCategory));
        OnPropertyChanged(nameof(IsVideoCategory));
        OnPropertyChanged(nameof(IsDocumentCategory));
        OnPropertyChanged(nameof(IsArchiveCategory));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watch.RulesChanged -= OnRulesChanged;
    }
}
