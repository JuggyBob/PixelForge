using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class QueuePage : Page
{
    public QueueViewModel ViewModel { get; }

    public QueuePage()
    {
        ViewModel = App.Services.GetRequiredService<QueueViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
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
