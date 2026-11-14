using Microsoft.UI.Xaml;
using XamlApplication = Microsoft.UI.Xaml.Application;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Views;

namespace PixelForge.Presentation.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string closeText = "OK");
    Task ShowErrorAsync(string title, string message, string? details = null);
    Task<bool> ConfirmAsync(string title, string message, string primaryText = "Continue", string cancelText = "Cancel",
        bool destructive = false);
    Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog);
    Task ShowJobValidationAsync(ValidationResult validation);
}

public sealed class DialogService : IDialogService
{
    private readonly IThemeService _theme;
    private readonly IAppLogger _logger;

    public DialogService(IThemeService theme, IAppLogger logger)
    {
        _theme = theme;
        _logger = logger;
    }

    private static XamlRoot? XamlRoot => App.MainWindow?.Content?.XamlRoot;

    public async Task ShowMessageAsync(string title, string message, string closeText = "OK")
    {
        var dialog = CreateDialog(title, message, closeText);
        await ShowDialogAsync(dialog);
    }

    public async Task ShowErrorAsync(string title, string message, string? details = null)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });
        if (!string.IsNullOrWhiteSpace(details))
        {
            var detailsBox = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = _theme.ResolveFontFamily("JetBrains Mono"),
                FontSize = 12,
                MinHeight = 120,
                MaxHeight = 260
            };
            panel.Children.Add(new Expander
            {
                Header = "Technical details",
                IsExpanded = false,
                Content = detailsBox
            });
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        _theme.ApplyTo(dialog.Content as FrameworkElement ?? panel);
        await ShowDialogAsync(dialog);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText = "Continue",
        string cancelText = "Cancel", bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            PrimaryButtonText = primaryText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (destructive) dialog.PrimaryButtonStyle = (Style)XamlApplication.Current.Resources["DangerButtonStyle"];
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (XamlRoot is null) return ContentDialogResult.None;
        dialog.XamlRoot = XamlRoot;
        _theme.ApplyTo(dialog.Content as FrameworkElement ?? dialog);
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Dialog could not be shown: {Message}", ex.Message);
            return ContentDialogResult.None;
        }
    }

    public async Task ShowJobValidationAsync(ValidationResult validation)
    {
        var errors = validation.Errors;
        var warnings = validation.Warnings;
        if (errors.Count == 0 && warnings.Count == 0) return;
        if (errors.Count > 0)
        {
            await ShowErrorAsync("This operation needs attention", string.Join("\n\n", errors.Select(e => e.Message)));
        }
        else
        {
            await ShowMessageAsync("Review before processing", string.Join("\n\n", warnings.Select(w => w.Message)));
        }
    }

    private ContentDialog CreateDialog(string title, string message, string closeText) => new()
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
        CloseButtonText = closeText,
        DefaultButton = ContentDialogButton.Close,
        XamlRoot = XamlRoot
    };
}
