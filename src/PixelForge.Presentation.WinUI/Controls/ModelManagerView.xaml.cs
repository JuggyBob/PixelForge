using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Controls;

public sealed partial class ModelManagerView : UserControl
{
    public ModelManagerView()
    {
        ViewModel = App.Services.GetRequiredService<ModelManagerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ModelManagerViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        LocalizationService.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => ViewModel.RefreshLocalization());
}
