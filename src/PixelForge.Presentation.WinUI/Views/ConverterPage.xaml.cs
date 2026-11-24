using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Domain;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class ConverterPage : Page, ILocalizationAwarePage
{
    public ConverterViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public ConverterPage()
    {
        ViewModel = App.Services.GetRequiredService<ConverterViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            switch (e.Parameter)
            {
                case ConverterImportRequest request:
                    await ViewModel.InitializeAsync(request.PreferredCategory);
                    await ViewModel.AddFilesAsync(request.Files, request.PreferredCategory);
                    // Imports can route files to several workspaces; the dropped
                    // batch still determines the category the user should land on.
                    ViewModel.SelectCategory(request.PreferredCategory);
                    break;
                case MediaCategory category:
                    await ViewModel.InitializeAsync(category);
                    ViewModel.SelectCategory(category);
                    break;
                default:
                    await ViewModel.InitializeAsync();
                    break;
            }
        }
        catch
        {
            // The view model surfaces errors through dialogs and notifications.
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
    }

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        if (!Enum.TryParse<MediaCategory>(tag, ignoreCase: true, out var category)) return;
        if (ViewModel.SelectedCategory != category) ViewModel.SelectCategory(category);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.T("Import into the converter");
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
            var folders = items.Where(i => i.IsOfType(Windows.Storage.StorageItemTypes.Folder))
                .Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (files.Count > 0) await ViewModel.AddFilesAsync(files, ViewModel.SelectedCategory);
            if (folders.Count > 0)
            {
                if (ViewModel.SelectedCategory == MediaCategory.Archive &&
                    ViewModel.Archives.SelectedAction.Value == ArchiveAction.Create)
                {
                    foreach (var folder in folders) await ViewModel.AddFolderAsync(folder);
                }
                else
                {
                    var dialogs = App.Services.GetRequiredService<Services.IDialogService>();
                    await dialogs.ShowMessageAsync("Folder input",
                        "Folders can be packaged with Archives → Create archive. Folder scanning for images is part of the Image Upscaler workspace.");
                }
            }
        }
        catch
        {
            // Ignore malformed drag payloads.
        }
    }
}
