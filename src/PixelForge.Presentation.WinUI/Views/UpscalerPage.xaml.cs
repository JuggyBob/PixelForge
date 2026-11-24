using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class UpscalerPage : Page, ILocalizationAwarePage
{
    public UpscalerViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public UpscalerPage()
    {
        ViewModel = App.Services.GetRequiredService<UpscalerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.InitializeAsync(e.Parameter as UpscalerImportRequest);
        }
        catch
        {
            // Errors are surfaced by the view model.
        }
    }

    private void OnMethodChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        if (tag == "AI" && !ViewModel.IsAi) ViewModel.SelectAiCommand.Execute(null);
        else if (tag == "Traditional" && !ViewModel.IsTraditional) ViewModel.SelectTraditionalCommand.Execute(null);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.T("Add images or scan a folder");
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
            if (files.Count > 0) await ViewModel.AddImagesAsync(files);
            if (folders.Count > 0) await ViewModel.AddFolderAsync(folders[0]);
        }
        catch
        {
            // Ignore malformed drag payloads.
        }
    }
}
