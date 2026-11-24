using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Domain;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class ToolsPage : Page, ILocalizationAwarePage
{
    public MediaToolsViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public ToolsPage()
    {
        ViewModel = App.Services.GetRequiredService<MediaToolsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            var category = e.Parameter as MediaCategory?;
            await ViewModel.InitializeAsync(category);
            if (category is { } selected) ViewModel.SelectCategory(selected);
        }
        catch
        {
            // The view model surfaces errors through notifications and dialogs.
        }
    }

    private void OnMergeMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MediaFileViewModel file })
            ViewModel.MoveMergeFileUpCommand.Execute(file);
    }

    private void OnMergeMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MediaFileViewModel file })
            ViewModel.MoveMergeFileDownCommand.Execute(file);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.T("Add to media tools");
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.Where(i => i.IsOfType(Windows.Storage.StorageItemTypes.File))
                .Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (files.Count > 0) await ViewModel.AddFilesAsync(files);
        }
        catch
        {
            // Ignore malformed drag payloads.
        }
    }
}
