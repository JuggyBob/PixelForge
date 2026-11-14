using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PixelForge.Presentation.Views;

public sealed partial class VideoUpscalerPage : Page
{
    public VideoUpscalerViewModel ViewModel { get; }

    public VideoUpscalerPage()
    {
        ViewModel = App.Services.GetRequiredService<VideoUpscalerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.InitializeAsync();
            if (ViewModel.Files.Count == 0) ViewModel.ApplyDefaults();
        }
        catch
        {
            // Errors are surfaced by the view model.
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add videos to the Video Upscaler";
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.Where(i => i.IsOfType(Windows.Storage.StorageItemTypes.File))
                .Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var folders = items.Where(i => i.IsOfType(Windows.Storage.StorageItemTypes.Folder))
                .Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (files.Count > 0) await ViewModel.AddVideosAsync(files);
            if (folders.Count > 0) await ViewModel.AddFolderAsync(folders[0]);
        }
        catch
        {
            // Ignore malformed drag payloads.
        }
    }
}
