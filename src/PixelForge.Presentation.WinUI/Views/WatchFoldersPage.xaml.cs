using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class WatchFoldersPage : Page
{
    public WatchFoldersViewModel ViewModel { get; }

    public WatchFoldersPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchFoldersViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { await ViewModel.InitializeAsync(); }
        catch { }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }
}
