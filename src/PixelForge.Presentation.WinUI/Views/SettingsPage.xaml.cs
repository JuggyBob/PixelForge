using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Controls;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class SettingsPage : Page, ILocalizationAwarePage
{
    private bool _updateDialogOpen;
    private bool _updateSubscribed;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_updateSubscribed)
        {
            ViewModel.UpdateAvailable += OnUpdateAvailable;
            _updateSubscribed = true;
        }

        var logger = App.Services.GetService<PixelForge.Domain.Interfaces.IAppLogger>();
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Settings initialization failed while navigating");
            var dialogs = App.Services.GetService<PixelForge.Presentation.Services.IDialogService>();
            if (dialogs is not null)
            {
                try
                {
                    await dialogs.ShowErrorAsync("Settings could not be loaded",
                        "PixelForge could not finish loading the Settings page. Other workspaces remain available.",
                        ex.ToString());
                }
                catch (Exception dialogError)
                {
                    logger?.LogError(dialogError, "Settings error dialog could not be shown");
                }
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_updateSubscribed)
        {
            ViewModel.UpdateAvailable -= OnUpdateAvailable;
            _updateSubscribed = false;
        }
    }

    private void OnAccentWheelColorChanged(object? sender, ColorWheelColorChangedEventArgs e) =>
        ViewModel.SetAccentFromColor(e.Color);

    private void OnAccentColorDoneClick(object sender, RoutedEventArgs e) =>
        AccentColorFlyout.Hide();

    private async void OnUpdateAvailable(object? sender, UpdateCheckResult result)
    {
        if (_updateDialogOpen || XamlRoot is null || result.Release is null) return;
        _updateDialogOpen = true;
        try
        {
            var release = result.Release;
            var asset = release.Assets.FirstOrDefault(a => a.IsInstaller)
                        ?? release.Assets.FirstOrDefault(a => a.IsPortable);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = release.Name,
                CloseButtonText = Loc.T("Dismiss"),
                PrimaryButtonText = Loc.T(asset is null ? "Open release" : "Download"),
                SecondaryButtonText = Loc.T("View release"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new ScrollViewer
                {
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = MarkdownTextBuilder.Build(release.Body)
                }
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                OpenUrl(asset?.DownloadUrl ?? release.HtmlUrl);
            };
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                OpenUrl(release.HtmlUrl);
            };

            await dialog.ShowAsync();
        }
        catch
        {
            // A popup failure must never break Settings.
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // The user can open the release page manually.
        }
    }
}
