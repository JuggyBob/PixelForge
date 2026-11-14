using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class ModelManagerViewModel : ObservableObject, IDisposable
{
    private readonly IModelManager _models;
    private readonly IOcrModelManager _ocrModels;
    private readonly IModelPanelService _panel;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public ModelManagerViewModel(IModelManager models, IOcrModelManager ocrModels, IModelPanelService panel,
        IFilePickerService filePicker, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, IAppLogger logger)
    {
        _models = models;
        _ocrModels = ocrModels;
        _panel = panel;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SelectedFilter = FilterOptions[0];
        _models.ModelsChanged += OnModelsChanged;
        _ocrModels.ModelsChanged += OnOcrModelsChanged;
    }

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<OcrModelManagerItemViewModel> OcrModels { get; } = [];
    [ObservableProperty] private ObservableCollection<object> _visibleModels = [];
    public IReadOnlyList<ModelFilterOption> FilterOptions { get; } =
    [
        new(ModelPanelFilter.All, "All models"),
        new(ModelPanelFilter.ImageUpscaling, "Image upscaling + restoration"),
        new(ModelPanelFilter.VideoUpscaling, "Video upscaling"),
        new(ModelPanelFilter.FrameInterpolation, "Frame interpolation"),
        new(ModelPanelFilter.Ocr, "OCR / document recognition")
    ];
    [ObservableProperty] private ModelFilterOption _selectedFilter;
    [ObservableProperty] private string _modelDirectory = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyStatus = "";
    [ObservableProperty] private string _modelRoot = "";
    [ObservableProperty] private bool _isPanelMode;

    public async Task InitializeAsync()
    {
        await _models.InitializeAsync();
        await _ocrModels.InitializeAsync();
        Refresh();
        ModelDirectory = _models.ModelDirectory;
        ModelRoot = _models.ModelDirectory;
    }

    public void SetPanelMode(ModelPanelFilter filter, bool isPanelMode)
    {
        IsPanelMode = isPanelMode;
        var option = FilterOptions.FirstOrDefault(o => o.Value == filter) ?? FilterOptions[0];
        if (ReferenceEquals(SelectedFilter, option)) ApplyFilter();
        else SelectedFilter = option;
    }

    private void Refresh()
    {
        var previousRegular = Models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var previousOcr = OcrModels.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        Models.Clear();
        foreach (var descriptor in _models.AvailableModels.OrderBy(m => m.SortOrder))
        {
            var installed = _models.GetInstalled(descriptor.Id);
            if (previousRegular.TryGetValue(descriptor.Id, out var existing))
            {
                existing.SetInstalled(installed);
                Models.Add(existing);
            }
            else
            {
                Models.Add(new ModelItemViewModel(descriptor, installed, this));
            }
        }

        OcrModels.Clear();
        foreach (var descriptor in _ocrModels.AvailableModels.OrderBy(m => m.SortOrder))
        {
            var installed = _ocrModels.GetInstalled(descriptor.Id);
            if (previousOcr.TryGetValue(descriptor.Id, out var existing))
            {
                existing.SetInstalled(installed);
                OcrModels.Add(existing);
            }
            else
            {
                OcrModels.Add(new OcrModelManagerItemViewModel(descriptor, installed, this));
            }
        }

        ModelDirectory = _models.ModelDirectory;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilter?.Value ?? ModelPanelFilter.All;
        IEnumerable<object> visible = filter switch
        {
            ModelPanelFilter.ImageUpscaling => Models.Where(m =>
                (m.Descriptor.Task == ModelTask.ImageUpscale || m.Descriptor.Task == ModelTask.ImageRestore) &&
                m.Descriptor.SupportsStillImage),
            ModelPanelFilter.VideoUpscaling => Models.Where(m =>
                m.Descriptor.Task == ModelTask.ImageUpscale && m.Descriptor.SupportsVideoFrames)
                .Cast<object>()
                .Concat(Models.Where(m =>
                    m.Descriptor.Task == ModelTask.FrameInterpolation && m.Descriptor.SupportsVideoFrames)),
            ModelPanelFilter.FrameInterpolation => Models.Where(m =>
                m.Descriptor.Task == ModelTask.FrameInterpolation && m.Descriptor.SupportsVideoFrames),
            ModelPanelFilter.Ocr => OcrModels,
            _ => Models.Cast<object>().Concat(OcrModels)
        };
        VisibleModels = new ObservableCollection<object>(visible);
    }

    partial void OnSelectedFilterChanged(ModelFilterOption value) => ApplyFilter();

    [RelayCommand]
    private void Back()
    {
        if (IsPanelMode)
        {
            _panel.Hide();
            return;
        }
        if (!_navigation.GoBack()) _navigation.Navigate("Upscaler");
    }

    [RelayCommand]
    private void ClosePanel() => _panel.Hide();

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_models.ModelDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _models.ModelDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not open the model folder", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_models.ModelDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!await _dialogs.ConfirmAsync("Change model folder?",
                "Existing model files are not moved. You can download them again into the new folder.",
                "Change folder", "Cancel")) return;
        await _models.SetModelDirectoryAsync(folder);
        ModelDirectory = _models.ModelDirectory;
        Refresh();
        _notifications.Notify("Model folder changed", ModelDirectory, NotificationSeverity.Success);
    }

    [RelayCommand]
    private void RefreshModels() => Refresh();

    public async Task DownloadAsync(ModelItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync($"Download {item.Name}?",
                $"The model is {item.SizeText}. The download is verified by SHA-256 before installation, then all inference runs locally.",
                "Download model", "Cancel")) return;
        await RunModelOperationAsync(item, "Downloading", async token =>
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                item.SetProgress(p.Fraction, p.Stage);
                BusyStatus = $"{item.Name} · {p.Stage} · {p.Fraction:P0}";
            });
            var result = await _models.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("Model installed", $"{item.Name} is ready for AI upscaling.", NotificationSeverity.Success);
            }
            else
            {
                item.SetError(result.Error ?? "Download failed", result.Details);
                await _dialogs.ShowErrorAsync("Model download failed", result.Error ?? "The model could not be installed.", result.Details);
            }
        });
    }

    public async Task InstallFromFileAsync(ModelItemViewModel item)
    {
        var path = await _filePicker.PickModelFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        await RunModelOperationAsync(item, "Validating", async token =>
        {
            var result = await _models.InstallFromFileAsync(path, item.Id, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("Model installed", $"{item.Name} was verified and installed.", NotificationSeverity.Success);
            }
            else
            {
                item.SetError(result.Error ?? "Model import failed", result.Details);
                await _dialogs.ShowErrorAsync("Model import failed", result.Error ?? "The model file could not be installed.", result.Details);
            }
        });
    }

    public async Task ValidateAsync(ModelItemViewModel item)
    {
        await RunModelOperationAsync(item, "Validating", async token =>
        {
            var result = await _models.ValidateAsync(item.Id, token);
            if (result is null)
            {
                await _dialogs.ShowMessageAsync("Model not installed", $"{item.Name} is not present in {_models.ModelDirectory}.");
                return;
            }
            item.SetInstalled(result);
            if (result.IsValid)
                _notifications.Notify("Model verified", $"{item.Name} passed validation.", NotificationSeverity.Success);
            else
                await _dialogs.ShowErrorAsync("Model validation failed", result.ValidationError ?? "The model could not be validated.");
        });
    }

    public async Task RemoveAsync(ModelItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync($"Remove {item.Name}?",
                "The model file will be deleted from disk. You can download it again later. No conversions are affected.",
                "Remove model", "Cancel", destructive: true)) return;
        var removed = await _models.RemoveAsync(item.Id);
        if (removed)
        {
            item.SetInstalled(null);
            _notifications.Notify("Model removed", item.Name, NotificationSeverity.Info);
        }
        else
        {
            await _dialogs.ShowErrorAsync("Could not remove the model", "The model file may be in use by another process.");
        }
    }

    public async Task DownloadOcrAsync(OcrModelManagerItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync($"Download {item.Name}?",
                $"The OCR model is {item.SizeText} (detector and recognizer). Files are SHA-256 verified before installation, then inference runs locally.",
                "Download model", "Cancel")) return;
        await RunOcrOperationAsync(item, "Downloading", async token =>
        {
            var progress = new Progress<OcrModelDownloadProgress>(p =>
            {
                item.SetProgress(p.Fraction, p.Stage);
                BusyStatus = $"{item.Name} · {p.Stage} · {p.Fraction:P0}";
            });
            var result = await _ocrModels.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("OCR model installed", $"{item.Name} is ready for local document OCR.", NotificationSeverity.Success);
            }
            else
            {
                item.SetError(result.Error ?? "Download failed", result.Details);
                await _dialogs.ShowErrorAsync("OCR model download failed", result.Error ?? "The model could not be installed.", result.Details);
            }
        });
    }

    public async Task ValidateOcrAsync(OcrModelManagerItemViewModel item)
    {
        await RunOcrOperationAsync(item, "Validating", async token =>
        {
            var result = await _ocrModels.ValidateAsync(item.Id, token);
            if (result is null)
            {
                await _dialogs.ShowMessageAsync("OCR model not installed", $"{item.Name} is not present.");
                return;
            }
            item.SetInstalled(result);
            if (result.IsValid)
                _notifications.Notify("OCR model verified", $"{item.Name} passed validation.", NotificationSeverity.Success);
            else
                await _dialogs.ShowErrorAsync("OCR model validation failed", result.ValidationError ?? "The model could not be validated.");
        });
    }

    public async Task RemoveOcrAsync(OcrModelManagerItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync($"Remove {item.Name}?",
                "The OCR model files will be deleted from disk. You can download them again later.",
                "Remove model", "Cancel", destructive: true)) return;
        var removed = await _ocrModels.RemoveAsync(item.Id);
        if (removed)
        {
            item.SetInstalled(null);
            _notifications.Notify("OCR model removed", item.Name, NotificationSeverity.Info);
            Refresh();
        }
        else
        {
            await _dialogs.ShowErrorAsync("Could not remove the OCR model", "The model files may be in use by another process.");
        }
    }

    public void CancelOcr(OcrModelManagerItemViewModel item) => item.RequestCancel();

    private async Task RunOcrOperationAsync(OcrModelManagerItemViewModel item, string operation,
        Func<CancellationToken, Task> action)
    {
        if (item.IsCancelling) return;
        using var cts = new CancellationTokenSource();
        item.BeginOperation(operation, cts);
        IsBusy = true;
        BusyStatus = $"{operation} · {item.Name}";
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            item.SetError("Cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR model operation {Operation} failed", operation);
            item.SetError(ex.Message, ex.ToString());
            await _dialogs.ShowErrorAsync("OCR model operation failed", ex.Message, ex.ToString());
        }
        finally
        {
            item.EndOperation();
            IsBusy = false;
            BusyStatus = "";
            Refresh();
        }
    }

    private async Task RunModelOperationAsync(ModelItemViewModel item, string operation, Func<CancellationToken, Task> action)
    {
        if (item.IsCancelling) return;
        using var cts = new CancellationTokenSource();
        item.BeginOperation(operation, cts);
        IsBusy = true;
        BusyStatus = $"{operation} · {item.Name}";
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            item.SetError("Cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model operation {Operation} failed", operation);
            item.SetError(ex.Message, ex.ToString());
            await _dialogs.ShowErrorAsync("Model operation failed", ex.Message, ex.ToString());
        }
        finally
        {
            item.EndOperation();
            IsBusy = false;
            BusyStatus = "";
            Refresh();
        }
    }

    public void Cancel(ModelItemViewModel item) => item.RequestCancel();

    private void OnModelsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnModelsChanged(sender, e));
            return;
        }
        Refresh();
    }

    private void OnOcrModelsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnOcrModelsChanged(sender, e));
            return;
        }
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _models.ModelsChanged -= OnModelsChanged;
        _ocrModels.ModelsChanged -= OnOcrModelsChanged;
    }
}

public sealed partial class ModelItemViewModel : ObservableObject
{
    private readonly ModelManagerViewModel _owner;
    private CancellationTokenSource? _cts;

    public ModelItemViewModel(ModelDescriptor descriptor, InstalledModel? installed, ModelManagerViewModel owner)
    {
        Descriptor = descriptor;
        Installed = installed;
        _owner = owner;
    }

    public ModelDescriptor Descriptor { get; }
    public InstalledModel? Installed { get; private set; }
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Version => Descriptor.Version;
    public string Architecture => Descriptor.Architecture;
    public string IntendedUse => Descriptor.IntendedUse;
    public string ScaleText => Descriptor.Task == ModelTask.FrameInterpolation
        ? "Interp."
        : $"{Descriptor.ScaleFactor}×";
    public string SizeText => Descriptor.DisplaySize;
    public string Tags => string.Join(" · ", Descriptor.Tags);
    public string SourceUrl => Descriptor.SourceUrl;
    public string LicenseName => Descriptor.LicenseName;
    public string Hash => Descriptor.Sha256;
    public string Notes => Descriptor.Notes ?? "";
    public string RuntimeBackend => Descriptor.RuntimeBackend;
    public string ProviderSupport => Descriptor.ProviderSupport;
    public string VerificationStatus => Descriptor.VerificationStatus;
    public string TaskText => Descriptor.Task switch
    {
        ModelTask.ImageUpscale when Descriptor.SupportsStillImage && Descriptor.SupportsVideoFrames => "Image + video frames",
        ModelTask.ImageUpscale when Descriptor.SupportsStillImage => "Image upscaling",
        ModelTask.ImageUpscale => "Video frames",
        ModelTask.ImageRestore => "Image restoration",
        ModelTask.FrameInterpolation => "Frame interpolation",
        _ => "AI model"
    };
    public string StatusText => IsInstalled ? "Installed" : IsDownloading ? "Downloading" : HasError ? "Error" : "Not installed";
    public bool IsInstalled => Installed is { IsValid: true };
    public bool CanDownload => !IsInstalled && !IsDownloading && Descriptor.IsDownloadable;
    public bool CanInstallFromFile => CanDownload && Descriptor.Backend == ModelBackendKind.OnnxRuntime;
    public bool CanRemove => IsInstalled;
    public bool CanValidate => IsInstalled;
    public string LicenseDetail => "Model weights follow the upstream licence shown above.";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDownloading => _cts is not null;
    public bool IsCancelling => _cts?.IsCancellationRequested == true;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _errorDetails = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showProgress;

    public void BeginOperation(string stage, CancellationTokenSource cts)
    {
        _cts = cts;
        ShowProgress = true;
        Progress = 0;
        ProgressText = stage + "…";
        Error = "";
        ErrorDetails = "";
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanInstallFromFile));
    }

    public void SetProgress(double fraction, string stage)
    {
        Progress = fraction * 100;
        ProgressText = $"{stage} · {fraction:P0}";
    }

    public void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        ShowProgress = false;
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanInstallFromFile));
    }

    public void RequestCancel() => _cts?.Cancel();

    public void SetInstalled(InstalledModel? installed)
    {
        Installed = installed;
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanInstallFromFile));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(InstalledDetail));
    }

    public void SetError(string error, string? details)
    {
        Error = error;
        ErrorDetails = details ?? "";
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
    }

    public string InstalledDetail => Installed is null
        ? "Not present on this PC"
        : $"{FileSizeConverter.FormatBytes(Installed.ActualSizeBytes)} · installed {Installed.InstalledAt.LocalDateTime:MMM d, yyyy}";

    [RelayCommand]
    private void Download() => _ = _owner.DownloadAsync(this);

    [RelayCommand]
    private void InstallFromFile() => _ = _owner.InstallFromFileAsync(this);

    [RelayCommand]
    private void Remove() => _ = _owner.RemoveAsync(this);

    [RelayCommand]
    private void Validate() => _ = _owner.ValidateAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.Cancel(this);
}

public sealed partial class OcrModelManagerItemViewModel : ObservableObject
{
    private readonly ModelManagerViewModel _owner;
    private CancellationTokenSource? _cts;

    public OcrModelManagerItemViewModel(OcrModelDescriptor descriptor, InstalledOcrModel? installed,
        ModelManagerViewModel owner)
    {
        Descriptor = descriptor;
        Installed = installed;
        _owner = owner;
    }

    public OcrModelDescriptor Descriptor { get; }
    public InstalledOcrModel? Installed { get; private set; }
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Version => Descriptor.Tier;
    public string Architecture => Descriptor.Architecture;
    public string IntendedUse => Descriptor.IntendedUse;
    public string ScaleText => "OCR";
    public string SizeText => Descriptor.DisplaySize;
    public string Tags => "OCR · " + string.Join(" · ", Descriptor.Languages.Take(2));
    public string SourceUrl => Descriptor.SourceUrl;
    public string LicenseName => Descriptor.LicenseName;
    public string Hash => Descriptor.Backend == OcrBackendKind.LlamaCpp
        ? $"{Descriptor.MainModel?.Sha256} / {Descriptor.MmprojModel?.Sha256}"
        : $"{Descriptor.Detector.Sha256} / {Descriptor.Recognizer.Sha256}";
    public string Notes => Descriptor.Notes ?? "";
    public string RuntimeBackend => Descriptor.RuntimeBackend;
    public string ProviderSupport => Descriptor.ProviderSupport;
    public string VerificationStatus => Descriptor.VerificationStatus;
    public string TaskText => "OCR / document recognition";
    public string StatusText => IsInstalled ? "Installed" : IsDownloading ? "Downloading" : HasError ? "Error" : "Not installed";
    public bool IsInstalled => Installed is { IsValid: true };
    public bool CanDownload => !IsInstalled && !IsDownloading;
    public bool CanInstallFromFile => false;
    public bool CanRemove => IsInstalled;
    public bool CanValidate => IsInstalled;
    public string LicenseDetail => Descriptor.Backend == OcrBackendKind.LlamaCpp
        ? $"GGUF OCR model and vision projector. License: {Descriptor.LicenseName}. The llama.cpp runtime is MIT-licensed."
        : "Detector and recognizer weights are Apache-2.0. OCR dictionaries are bundled with PixelForge.";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDownloading => _cts is not null;
    public bool IsCancelling => _cts?.IsCancellationRequested == true;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _errorDetails = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showProgress;

    public void BeginOperation(string stage, CancellationTokenSource cts)
    {
        _cts = cts;
        ShowProgress = true;
        Progress = 0;
        ProgressText = stage + "…";
        Error = "";
        ErrorDetails = "";
        NotifyState();
    }

    public void SetProgress(double fraction, string stage)
    {
        Progress = fraction * 100;
        ProgressText = $"{stage} · {fraction:P0}";
    }

    public void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        ShowProgress = false;
        NotifyState();
    }

    public void RequestCancel() => _cts?.Cancel();

    public void SetInstalled(InstalledOcrModel? installed)
    {
        Installed = installed;
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(InstalledDetail));
        NotifyState();
    }

    public void SetError(string error, string? details)
    {
        Error = error;
        ErrorDetails = details ?? "";
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
    }

    public string InstalledDetail => Installed is null
        ? "Not present on this PC"
        : $"{FileSizeConverter.FormatBytes(Installed.ActualBytes)} · installed {Installed.InstalledAt.LocalDateTime:MMM d, yyyy}";

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(HasError));
    }

    [RelayCommand]
    private void Download() => _ = _owner.DownloadOcrAsync(this);

    [RelayCommand]
    private void Remove() => _ = _owner.RemoveOcrAsync(this);

    [RelayCommand]
    private void Validate() => _ = _owner.ValidateOcrAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.CancelOcr(this);
}
