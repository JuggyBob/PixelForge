using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using XamlApplication = Microsoft.UI.Xaml.Application;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class HomePage : Page, ILocalizationAwarePage
{
    public HomeViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.T("Import into PixelForge");
            e.DragUIOverride.IsContentVisible = true;
            HeroDropZone.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)XamlApplication.Current.Resources["AccentPrimaryBrush"];
            HeroDropZone.BorderThickness = new Thickness(2);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        HeroDropZone.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)XamlApplication.Current.Resources["DropZoneBorderBrush"];
        HeroDropZone.BorderThickness = new Thickness(1);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            await ViewModel.HandleDroppedPathsAsync(paths);
        }
        catch
        {
            // Invalid drag payloads are ignored.
        }
    }
}
