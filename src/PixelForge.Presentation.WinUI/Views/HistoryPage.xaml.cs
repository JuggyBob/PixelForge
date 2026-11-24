using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class HistoryPage : Page, ILocalizationAwarePage
{
    public HistoryViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public HistoryPage()
    {
        ViewModel = App.Services.GetRequiredService<HistoryViewModel>();
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
}
