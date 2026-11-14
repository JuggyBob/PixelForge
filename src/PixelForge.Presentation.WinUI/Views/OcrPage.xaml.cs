using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PixelForge.Presentation.Views;

public sealed partial class OcrPage : Page
{
    public OcrViewModel ViewModel { get; }

    public OcrPage()
    {
        ViewModel = App.Services.GetRequiredService<OcrViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.InitializeAsync();
            ViewModel.RefreshRuntimeSettings();
        }
        catch
        {
            // Errors are surfaced by the view model.
        }
    }

    private void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            var focused = XamlRoot is null
                ? FocusManager.GetFocusedElement()
                : FocusManager.GetFocusedElement(XamlRoot);
            if (focused is TextBox or PasswordBox or RichEditBox or NumberBox)
                return;
        }
        catch
        {
            // If focus cannot be resolved, still allow the paste command.
        }

        args.Handled = true;
        _ = ViewModel.PasteAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add pages or scan a folder";
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().Select(f => f.Path)
                .Where(File.Exists).ToList();
            var folders = items.OfType<StorageFolder>().Select(f => f.Path)
                .Where(Directory.Exists).ToList();
            if (files.Count > 0) await ViewModel.AddPathsAsync(files, allowDuplicates: true);
            foreach (var folder in folders) await ViewModel.AddFolderAsync(folder);
        }
        catch
        {
            // Ignore malformed drag payloads.
        }
    }
}
