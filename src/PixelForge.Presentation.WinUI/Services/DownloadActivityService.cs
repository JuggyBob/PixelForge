using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Services;

public sealed partial class DownloadActivityItem : ObservableObject
{
    public DownloadActivityItem(string id, string name) { Id = id; Name = name; }
    public string Id { get; }
    public string Name { get; }
    public string Glyph => "\uE896";
    [ObservableProperty] private string _stateText = "Downloading";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    private Action? _pause;
    private Action? _resume;
    private Action? _cancel;

    public void SetActions(Action? pause, Action? resumeAction, Action? cancel)
    {
        _pause = pause;
        _resume = resumeAction;
        _cancel = cancel;
    }

    [RelayCommand] private void Pause() => _pause?.Invoke();
    [RelayCommand] private void Resume() => _resume?.Invoke();
    [RelayCommand] private void Cancel() => _cancel?.Invoke();
}

public interface IDownloadActivityService
{
    ObservableCollection<DownloadActivityItem> Items { get; }
    event EventHandler? Changed;
    DownloadActivityItem Update(string id, string name, string state, double progress, string progressText,
        bool canPause, bool canResume, bool canCancel, Action? pause, Action? resumeAction, Action? cancel);
    void Remove(string id);
}

public sealed class DownloadActivityService : IDownloadActivityService
{
    public ObservableCollection<DownloadActivityItem> Items { get; } = [];
    public event EventHandler? Changed;

    public DownloadActivityItem Update(string id, string name, string state, double progress, string progressText,
        bool canPause, bool canResume, bool canCancel, Action? pause, Action? resumeAction, Action? cancel)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            item = new DownloadActivityItem(id, name);
            Items.Add(item);
        }
        item.StateText = LocalizationService.T(state);
        item.Progress = progress;
        item.ProgressText = progressText;
        item.CanPause = canPause;
        item.CanResume = canResume;
        item.CanCancel = canCancel;
        item.SetActions(pause, resumeAction, cancel);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public void Remove(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        Items.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

