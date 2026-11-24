using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Views;

public sealed partial class ComparePage : Page, ILocalizationAwarePage
{
    private readonly IMediaInspector _inspector;
    private readonly INavigationService _navigation;
    private ComparisonRequest? _current;
    private MediaInfo? _input;
    private MediaInfo? _output;

    public ComparePage()
    {
        _inspector = App.Services.GetRequiredService<IMediaInspector>();
        _navigation = App.Services.GetRequiredService<INavigationService>();
        InitializeComponent();
        LocalizationPageRegistry.Register(this);
    }

    public void RefreshLocalization()
    {
        if (_current is null)
        {
            TitleText.Text = Loc.T("Before / after");
            SubtitleText.Text = Loc.T("Choose an upscaling result from the queue or history to compare it here.");
            return;
        }

        TitleText.Text = Loc.T(_current.Title);
        SubtitleText.Text = Loc.T("Drag the divider to inspect fine detail. Zoom and pan stay synchronized on both sides.");
        UpdateMetadata();
    }

    private void UpdateMetadata()
    {
        if (_input is not { } input || _output is not { } output) return;
        OriginalNameText.Text = input.FileName;
        OriginalMetaText.Text = Loc.F("{0} × {1} · {2} · {3}",
            input.Width, input.Height, input.FormatName ?? input.Extension,
            FileSizeConverter.FormatBytes(input.FileSizeBytes));
        OutputNameText.Text = output.FileName;
        OutputMetaText.Text = Loc.F("{0} × {1} · {2} · {3}",
            output.Width, output.Height, output.FormatName ?? output.Extension,
            FileSizeConverter.FormatBytes(output.FileSizeBytes));
        var scaleX = output.Width!.Value / (double)input.Width!.Value;
        var scaleY = output.Height!.Value / (double)input.Height!.Value;
        var scale = Math.Abs(scaleX - scaleY) < 0.01
            ? LocalizationService.FormatNumber(scaleX, "0.##") + "×"
            : LocalizationService.FormatNumber(scaleX, "0.##") + "× / " +
              LocalizationService.FormatNumber(scaleY, "0.##") + "×";
        ScaleText.Text = Loc.F("{0} · original aligned to output", scale);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ComparisonRequest request)
        {
            TitleText.Text = Loc.T("Before / after");
            SubtitleText.Text = Loc.T("Choose an upscaling result from the queue or history to compare it here.");
            return;
        }

        _current = request;

        try
        {
            var input = await _inspector.InspectAsync(request.InputPath, MediaCategory.Image);
            var output = await _inspector.InspectAsync(request.OutputPath, MediaCategory.Image);
            if (!input.IsReadable || !output.IsReadable || input.Width is not > 0 || input.Height is not > 0 ||
                output.Width is not > 0 || output.Height is not > 0)
            {
                await App.Services.GetRequiredService<IDialogService>().ShowErrorAsync("Comparison unavailable",
                    "One of the images could not be read or has invalid dimensions.");
                return;
            }

            TitleText.Text = Loc.T(request.Title);
            SubtitleText.Text = Loc.T("Drag the divider to inspect fine detail. Zoom and pan stay synchronized on both sides.");
            _input = input;
            _output = output;
            UpdateMetadata();
            Comparator.SetImages(request.InputPath, request.OutputPath, output.Width.Value, output.Height.Value);
        }
        catch (Exception ex)
        {
            await App.Services.GetRequiredService<IDialogService>().ShowErrorAsync("Comparison failed",
                "The images could not be loaded for comparison.", ex.ToString());
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (!_navigation.GoBack()) _navigation.Navigate("History");
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Comparator.FitToScreen();
    private void OnActualSizeClick(object sender, RoutedEventArgs e) => Comparator.ShowActualSize();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Comparator.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Comparator.ZoomOut();
}
