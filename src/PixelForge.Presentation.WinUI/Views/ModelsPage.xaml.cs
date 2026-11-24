using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class ModelsPage : Page, ILocalizationAwarePage
{
    public ModelsPage()
    {
        InitializeComponent();
        LocalizationPageRegistry.Register(this);
    }

    public ModelManagerViewModel ViewModel => ManagerView.ViewModel;

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.SetPanelMode(ModelPanelFilter.All, isPanelMode: false);
        await ViewModel.InitializeAsync();
    }
}
