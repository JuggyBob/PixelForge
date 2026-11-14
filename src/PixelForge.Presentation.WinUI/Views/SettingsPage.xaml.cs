using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.Controls;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private void OnAccentWheelColorChanged(object? sender, ColorWheelColorChangedEventArgs e) =>
        ViewModel.SetAccentFromColor(e.Color);

    private void OnAccentColorDoneClick(object sender, RoutedEventArgs e) =>
        AccentColorFlyout.Hide();
}
