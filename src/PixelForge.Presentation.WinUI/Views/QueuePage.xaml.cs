using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class QueuePage : Page, ILocalizationAwarePage
{
    public QueueViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public QueuePage()
    {
        ViewModel = App.Services.GetRequiredService<QueueViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private void OnOpenConverterClick(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<INavigationService>().Navigate("Converter");
    }
}
