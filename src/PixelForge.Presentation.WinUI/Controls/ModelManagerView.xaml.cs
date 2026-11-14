using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Controls;

public sealed partial class ModelManagerView : UserControl
{
    public ModelManagerView()
    {
        ViewModel = App.Services.GetRequiredService<ModelManagerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    public ModelManagerViewModel ViewModel { get; }
}
