using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class ModelsPage : Page
{
    public ModelsPage()
    {
        InitializeComponent();
    }

    public ModelManagerViewModel ViewModel => ManagerView.ViewModel;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.SetPanelMode(ModelPanelFilter.All, isPanelMode: false);
        await ViewModel.InitializeAsync();
    }
}
