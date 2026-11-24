using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class ModelManagerViewModel : ObservableObject, IDisposable
{
    public sealed record ModelCompatibility(string Glyph, string Tooltip, SolidColorBrush Brush);
    private readonly IModelManager _models;
    private readonly IOcrModelManager _ocrModels;
    private readonly ICapabilityRegistry _registry;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IDownloadActivityService _downloads;
    private readonly IModelPanelService _panel;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly IAppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private SystemDiagnostics? _hardware;
    private bool _disposed;

    public ModelManagerViewModel(IModelManager models, IOcrModelManager ocrModels, ICapabilityRegistry registry,
        IDiagnosticsService diagnostics, IDownloadActivityService downloads, IModelPanelService panel,
        IFilePickerService filePicker, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, IAppLogger logger)
    {
        _models = models;
        _ocrModels = ocrModels;
        _registry = registry;
        _diagnostics = diagnostics;
        _downloads = downloads;
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

    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in FilterOptions) option.RefreshLocalization();
        foreach (var item in Models) item.RefreshLocalization();
        foreach (var item in OcrModels) item.RefreshLocalization();
        Refresh();
    }
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
        try { _hardware = await _diagnostics.BuildAsync(); } catch { }
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
                existing.SetCompatibility(Evaluate(descriptor, installed is { IsValid: true }));
                Models.Add(existing);
            }
            else
            {
                var item = new ModelItemViewModel(descriptor, installed, this);
                item.SetCompatibility(Evaluate(descriptor, installed is { IsValid: true }));
                if (installed is null && !item.IsDownloading && _models.HasPartialDownload(descriptor.Id))
                    item.SetPaused(true);
                Models.Add(item);
            }
        }

        OcrModels.Clear();
        foreach (var descriptor in _ocrModels.AvailableModels.OrderBy(m => m.SortOrder))
        {
            var installed = _ocrModels.GetInstalled(descriptor.Id);
            if (previousOcr.TryGetValue(descriptor.Id, out var existing))
            {
                existing.SetInstalled(installed);
                existing.SetCompatibility(EvaluateOcr(descriptor, installed is { IsValid: true }));
                OcrModels.Add(existing);
            }
            else
            {
                var item = new OcrModelManagerItemViewModel(descriptor, installed, this);
                item.SetCompatibility(EvaluateOcr(descriptor, installed is { IsValid: true }));
                if (installed is null && !item.IsDownloading && _ocrModels.HasPartialDownload(descriptor.Id))
                    item.SetPaused(true);
                OcrModels.Add(item);
            }
        }

        ModelDirectory = _models.ModelDirectory;
        ApplyFilter();
    }

    private ModelCompatibility Evaluate(ModelDescriptor descriptor, bool installed)
    {
        var vulkan = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Vulkan)?.IsAvailable == true;
        var dml = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML)?.IsAvailable == true;
        var gpu = _hardware?.Gpus.FirstOrDefault();
        var gpuText = gpu is null
            ? LocalizationService.T("No GPU detected")
            : LocalizationService.F("{0} ({1})", gpu.Name,
                gpu.DriverVersion ?? LocalizationService.T("driver unknown"));
        if (descriptor.Backend == ModelBackendKind.NcnnVulkan && !vulkan)
            return new ModelCompatibility("\u2212",
                $"Vulkan is not available; this model can only fall back to CPU and may be slow. {gpuText}",
                new SolidColorBrush(Colors.Goldenrod));
        if (descriptor.CpuOnly)
            return installed
                ? new ModelCompatibility("\u2713", $"CPU-only model. Installed and validated on this PC. {gpuText}",
                    new SolidColorBrush(Colors.ForestGreen))
                : new ModelCompatibility("\u2212", "CPU-only model; install it to verify its files. " + gpuText,
                    new SolidColorBrush(Colors.Goldenrod));
        if (!installed)
            return new ModelCompatibility("\u2212", "Not installed; hardware compatibility will be verified after download. " + gpuText,
                new SolidColorBrush(Colors.Goldenrod));
        if (descriptor.Backend == ModelBackendKind.OnnxRuntime && !dml)
            return new ModelCompatibility("\u2212", $"DirectML is unavailable; the model can run on CPU. {gpuText}",
                new SolidColorBrush(Colors.Goldenrod));
        return new ModelCompatibility("\u2713", $"Installed and validated. Required backend is available. {gpuText}",
            new SolidColorBrush(Colors.ForestGreen));
    }

    private ModelCompatibility EvaluateOcr(OcrModelDescriptor descriptor, bool installed)
    {
        var vulkan = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.Vulkan)?.IsAvailable == true;
        var dml = _registry.ExecutionProviders.FirstOrDefault(p => p.Kind == ExecutionProviderKind.DirectML)?.IsAvailable == true;
        var gpu = _hardware?.Gpus.FirstOrDefault();
        var gpuText = gpu is null
            ? LocalizationService.T("No GPU detected")
            : LocalizationService.F("{0} ({1})", gpu.Name,
                gpu.DriverVersion ?? LocalizationService.T("driver unknown"));
        if (descriptor.Backend == OcrBackendKind.LlamaCpp && !vulkan)
            return new ModelCompatibility("\u2212",
                $"The llama.cpp Vulkan runtime is not available; CPU fallback may be very slow. {gpuText}",
                new SolidColorBrush(Colors.Goldenrod));
        if (!installed)
            return new ModelCompatibility("\u2212", "Not installed; compatibility will be verified after download. " + gpuText,
                new SolidColorBrush(Colors.Goldenrod));
        if (descriptor.Backend == OcrBackendKind.PpOcr && !dml)
            return new ModelCompatibility("\u2212", $"DirectML is unavailable; OCR can run on CPU. {gpuText}",
                new SolidColorBrush(Colors.Goldenrod));
        return new ModelCompatibility("\u2713", $"Installed and validated. Required runtime is available. {gpuText}",
            new SolidColorBrush(Colors.ForestGreen));
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
                item.SetProgress(p.Fraction, LocalizationService.T(p.Stage));
                UpdateDownloadFor(item, LocalizationService.T(p.Stage), p.Fraction);
                BusyStatus = LocalizationService.F("{0} · {1} · {2}", item.Name,
                    LocalizationService.T(p.Stage), p.Fraction.ToString("P0", LocalizationService.CurrentCulture));
            });
            var result = await _models.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("Model installed", $"{item.Name} is ready for AI upscaling.", NotificationSeverity.Success);
            }
            else
            {
                if (item.IsPaused)
                {
                    item.SetPaused(true);
                    return;
                }
                if (item.IsCancelling)
                {
                    item.SetError("Cancelled", null);
                    await DiscardPartialAsync(item);
                    return;
                }
                item.SetError(result.Error ?? "Download failed", result.Details);
                await _dialogs.ShowErrorAsync("Model download failed", result.Error ?? "The model could not be installed.", result.Details);
            }
        });
    }

    public async Task ResumeAsync(ModelItemViewModel item)
    {
        if (!item.IsPaused) return;
        await RunModelOperationAsync(item, "Resuming", async token =>
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                item.SetProgress(p.Fraction, LocalizationService.T(p.Stage));
                UpdateDownloadFor(item, LocalizationService.T(p.Stage), p.Fraction);
                BusyStatus = LocalizationService.F("{0} · {1} · {2}", item.Name,
                    LocalizationService.T(p.Stage), p.Fraction.ToString("P0", LocalizationService.CurrentCulture));
            });
            var result = await _models.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("Model installed", $"{item.Name} is ready for AI upscaling.", NotificationSeverity.Success);
            }
            else
            {
                if (item.IsPaused)
                {
                    item.SetPaused(true);
                    return;
                }
                if (item.IsCancelling)
                {
                    item.SetError("Cancelled", null);
                    await DiscardPartialAsync(item);
                    return;
                }
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
                item.SetProgress(p.Fraction, LocalizationService.T(p.Stage));
                UpdateDownloadFor(item, LocalizationService.T(p.Stage), p.Fraction);
                BusyStatus = LocalizationService.F("{0} · {1} · {2}", item.Name,
                    LocalizationService.T(p.Stage), p.Fraction.ToString("P0", LocalizationService.CurrentCulture));
            });
            var result = await _ocrModels.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("OCR model installed", $"{item.Name} is ready for local document OCR.", NotificationSeverity.Success);
            }
            else
            {
                if (item.IsPaused)
                {
                    item.SetPaused(true);
                    return;
                }
                if (item.IsCancelling)
                {
                    item.SetError("Cancelled", null);
                    await DiscardPartialAsync(item);
                    return;
                }
                item.SetError(result.Error ?? "Download failed", result.Details);
                await _dialogs.ShowErrorAsync("OCR model download failed", result.Error ?? "The model could not be installed.", result.Details);
            }
        });
    }

    public async Task ResumeOcrAsync(OcrModelManagerItemViewModel item)
    {
        if (!item.IsPaused) return;
        await RunOcrOperationAsync(item, "Resuming", async token =>
        {
            var progress = new Progress<OcrModelDownloadProgress>(p =>
            {
                item.SetProgress(p.Fraction, LocalizationService.T(p.Stage));
                UpdateDownloadFor(item, LocalizationService.T(p.Stage), p.Fraction);
                BusyStatus = LocalizationService.F("{0} · {1} · {2}", item.Name,
                    LocalizationService.T(p.Stage), p.Fraction.ToString("P0", LocalizationService.CurrentCulture));
            });
            var result = await _ocrModels.DownloadAsync(item.Id, progress, token);
            if (result.Success)
            {
                item.SetInstalled(result.Model);
                _notifications.Notify("OCR model installed", $"{item.Name} is ready for local document OCR.", NotificationSeverity.Success);
            }
            else
            {
                if (item.IsPaused)
                {
                    item.SetPaused(true);
                    return;
                }
                if (item.IsCancelling)
                {
                    item.SetError("Cancelled", null);
                    await DiscardPartialAsync(item);
                    return;
                }
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

    private Task DiscardPartialAsync(object item) => item switch
    {
        ModelItemViewModel model => _models.DiscardPartialAsync(model.Id),
        OcrModelManagerItemViewModel ocr => _ocrModels.DiscardPartialAsync(ocr.Id),
        _ => Task.CompletedTask
    };

    private void FinishDownloadFor(object item)
    {
        switch (item)
        {
            case ModelItemViewModel model:
                if (!model.IsPaused) _downloads.Remove($"model:{model.Id}");
                break;
            case OcrModelManagerItemViewModel ocr:
                if (!ocr.IsPaused) _downloads.Remove($"ocr:{ocr.Id}");
                break;
        }
    }

    private void UpdateDownloadFor(object item, string stage, double fraction)
    {
        switch (item)
        {
            case ModelItemViewModel model: UpdateDownload(model, stage, fraction); break;
            case OcrModelManagerItemViewModel ocr: UpdateOcrDownload(ocr, stage, fraction); break;
        }
    }

    private void RegisterDownloadFor(object item, string operation)
    {
        if (operation is not ("Downloading" or "Resuming")) return;
        switch (item)
        {
            case ModelItemViewModel model: RegisterDownload(model, operation); break;
            case OcrModelManagerItemViewModel ocr: RegisterOcrDownload(ocr, operation); break;
        }
    }

    private void RegisterDownload(ModelItemViewModel item, string state)
    {
        _downloads.Update($"model:{item.Id}", item.Name, state, 0, "Starting", true, false, true,
            () => item.RequestPause(), () => _ = ResumeAsync(item), () => item.RequestCancel());
    }

    private void UpdateDownload(ModelItemViewModel item, string stage, double fraction)
    {
        _downloads.Update($"model:{item.Id}", item.Name,
            item.IsPaused ? LocalizationService.T("Paused") : stage, fraction * 100,
            LocalizationService.F("{0} · {1}", fraction.ToString("P0", LocalizationService.CurrentCulture), stage),
            !item.IsPaused, item.IsPaused, true,
            () => item.RequestPause(), () => _ = ResumeAsync(item), () => item.RequestCancel());
    }

    private void RegisterOcrDownload(OcrModelManagerItemViewModel item, string state)
    {
        _downloads.Update($"ocr:{item.Id}", item.Name, state, 0, "Starting", true, false, true,
            () => item.RequestPause(), () => _ = ResumeOcrAsync(item), () => item.RequestCancel());
    }

    private void UpdateOcrDownload(OcrModelManagerItemViewModel item, string stage, double fraction)
    {
        _downloads.Update($"ocr:{item.Id}", item.Name,
            item.IsPaused ? LocalizationService.T("Paused") : stage, fraction * 100,
            LocalizationService.F("{0} · {1}", fraction.ToString("P0", LocalizationService.CurrentCulture), stage),
            !item.IsPaused, item.IsPaused, true,
            () => item.RequestPause(), () => _ = ResumeOcrAsync(item), () => item.RequestCancel());
    }

    private async Task RunOcrOperationAsync(OcrModelManagerItemViewModel item, string operation,
        Func<CancellationToken, Task> action)
    {
        if (item.IsCancelling) return;
        using var cts = new CancellationTokenSource();
        item.BeginOperation(operation, cts);
        RegisterDownloadFor(item, operation);
        IsBusy = true;
        BusyStatus = LocalizationService.F("{0} · {1}", LocalizationService.T(operation), item.Name);
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (item.IsPaused)
            {
                item.SetPaused(true);
            }
            else
            {
                item.SetError("Cancelled", null);
                await DiscardPartialAsync(item);
            }
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
            FinishDownloadFor(item);
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
        RegisterDownloadFor(item, operation);
        IsBusy = true;
        BusyStatus = LocalizationService.F("{0} · {1}", LocalizationService.T(operation), item.Name);
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (item.IsPaused)
            {
                item.SetPaused(true);
            }
            else
            {
                item.SetError("Cancelled", null);
                await DiscardPartialAsync(item);
            }
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
            FinishDownloadFor(item);
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
    public string StatusText => IsInstalled ? "Installed" : IsPaused ? "Paused" : NeedsRepair ? "Repair required" : IsDownloading ? "Downloading" : HasError ? "Error" : "Not installed";
    public bool NeedsRepair => Installed is { IsValid: false };
    public string DownloadLabel => NeedsRepair ? "Repair" : "Download";
    public bool IsInstalled => Installed is { IsValid: true };
    public bool CanDownload => !IsInstalled && !IsDownloading && !IsPaused && Descriptor.IsDownloadable;
    public bool CanInstallFromFile => CanDownload && Descriptor.Backend == ModelBackendKind.OnnxRuntime;
    public bool CanRemove => IsInstalled;
    public bool CanValidate => IsInstalled;
    public string LicenseDetail => "Model weights follow the upstream licence shown above.";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDownloading => _cts is not null;
    public bool IsCancelling => _cts?.IsCancellationRequested == true;
    public bool IsPaused { get; private set; }
    public bool CanPause => IsDownloading && !IsPaused;
    public bool CanResume => IsPaused;
    public bool CanCancelDownload => IsDownloading || IsPaused;
    public ModelManagerViewModel.ModelCompatibility? Compatibility { get; private set; }
    public string CompatibilityGlyph => Compatibility?.Glyph ?? "";
    public string CompatibilityTooltip => Compatibility?.Tooltip ?? "";
    public SolidColorBrush CompatibilityBrush => Compatibility?.Brush ?? new SolidColorBrush(Colors.Gray);

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);

    public void SetCompatibility(ModelManagerViewModel.ModelCompatibility compatibility)
    {
        Compatibility = compatibility;
        OnPropertyChanged(nameof(Compatibility));
        OnPropertyChanged(nameof(CompatibilityGlyph));
        OnPropertyChanged(nameof(CompatibilityTooltip));
        OnPropertyChanged(nameof(CompatibilityBrush));
    }

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _errorDetails = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showProgress;

    public void BeginOperation(string stage, CancellationTokenSource cts)
    {
        _cts = cts;
        IsPaused = false;
        ShowProgress = true;
        Progress = 0;
        ProgressText = LocalizationService.T(stage) + "…";
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
        ProgressText = LocalizationService.F("{0} · {1}", stage,
                fraction.ToString("P0", LocalizationService.CurrentCulture));
    }

    public void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        ShowProgress = false;
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanInstallFromFile));
    }

    public void RequestPause()
    {
        if (_cts is null) return;
        IsPaused = true;
        NotifyPausedState();
        _cts.Cancel();
    }

    public void RequestCancel()
    {
        IsPaused = false;
        NotifyPausedState();
        _cts?.Cancel();
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        NotifyPausedState();
    }

    private void NotifyPausedState()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(StatusText));
    }

    public void SetInstalled(InstalledModel? installed)
    {
        Installed = installed;
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NeedsRepair));
        OnPropertyChanged(nameof(DownloadLabel));
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
        : LocalizationService.F("{0} · installed {1}",
            FileSizeConverter.FormatBytes(Installed.ActualSizeBytes),
            LocalizationService.FormatDate(Installed.InstalledAt, "d"));

    [RelayCommand]
    private void Download() => _ = _owner.DownloadAsync(this);

    [RelayCommand]
    private void InstallFromFile() => _ = _owner.InstallFromFileAsync(this);

    [RelayCommand]
    private void Remove() => _ = _owner.RemoveAsync(this);

    [RelayCommand]
    private void Validate() => _ = _owner.ValidateAsync(this);

    [RelayCommand]
    private void Pause() => RequestPause();

    [RelayCommand]
    private void Resume() => _ = _owner.ResumeAsync(this);

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
    public string StatusText => IsInstalled ? "Installed" : IsPaused ? "Paused" : NeedsRepair ? "Repair required" : IsDownloading ? "Downloading" : HasError ? "Error" : "Not installed";
    public bool NeedsRepair => Installed is { IsValid: false };
    public string DownloadLabel => NeedsRepair ? "Repair" : "Download";
    public bool IsInstalled => Installed is { IsValid: true };
    public bool CanDownload => !IsInstalled && !IsDownloading && !IsPaused;
    public bool CanInstallFromFile => false;
    public bool CanRemove => IsInstalled;
    public bool CanValidate => IsInstalled;
    public string LicenseDetail => Descriptor.Backend == OcrBackendKind.LlamaCpp
        ? $"GGUF OCR model and vision projector. License: {Descriptor.LicenseName}. The llama.cpp runtime is MIT-licensed."
        : "Detector and recognizer weights are Apache-2.0. OCR dictionaries are bundled with PixelForge.";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDownloading => _cts is not null;
    public bool IsCancelling => _cts?.IsCancellationRequested == true;
    public bool IsPaused { get; private set; }
    public bool CanPause => IsDownloading && !IsPaused;
    public bool CanResume => IsPaused;
    public bool CanCancelDownload => IsDownloading || IsPaused;
    public ModelManagerViewModel.ModelCompatibility? Compatibility { get; private set; }
    public string CompatibilityGlyph => Compatibility?.Glyph ?? "";
    public string CompatibilityTooltip => Compatibility?.Tooltip ?? "";
    public SolidColorBrush CompatibilityBrush => Compatibility?.Brush ?? new SolidColorBrush(Colors.Gray);

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);

    public void SetCompatibility(ModelManagerViewModel.ModelCompatibility compatibility)
    {
        Compatibility = compatibility;
        OnPropertyChanged(nameof(Compatibility));
        OnPropertyChanged(nameof(CompatibilityGlyph));
        OnPropertyChanged(nameof(CompatibilityTooltip));
        OnPropertyChanged(nameof(CompatibilityBrush));
    }

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _errorDetails = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showProgress;

    public void BeginOperation(string stage, CancellationTokenSource cts)
    {
        _cts = cts;
        IsPaused = false;
        ShowProgress = true;
        Progress = 0;
        ProgressText = LocalizationService.T(stage) + "…";
        Error = "";
        ErrorDetails = "";
        NotifyState();
    }

    public void SetProgress(double fraction, string stage)
    {
        Progress = fraction * 100;
        ProgressText = LocalizationService.F("{0} · {1}", stage,
                fraction.ToString("P0", LocalizationService.CurrentCulture));
    }

    public void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        ShowProgress = false;
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancelDownload));
        NotifyState();
    }

    public void RequestPause()
    {
        if (_cts is null) return;
        IsPaused = true;
        NotifyPausedState();
        _cts.Cancel();
    }

    public void RequestCancel()
    {
        IsPaused = false;
        NotifyPausedState();
        _cts?.Cancel();
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        NotifyPausedState();
    }

    private void NotifyPausedState()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(StatusText));
    }

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
        : LocalizationService.F("{0} · installed {1}",
            FileSizeConverter.FormatBytes(Installed.ActualBytes),
            LocalizationService.FormatDate(Installed.InstalledAt, "d"));

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NeedsRepair));
        OnPropertyChanged(nameof(DownloadLabel));
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
    private void Pause() => RequestPause();

    [RelayCommand]
    private void Resume() => _ = _owner.ResumeOcrAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.CancelOcr(this);
}
