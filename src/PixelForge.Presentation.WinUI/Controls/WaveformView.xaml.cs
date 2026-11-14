using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace PixelForge.Presentation.Controls;

public sealed partial class WaveformView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(WaveformView),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(
        nameof(DurationSeconds), typeof(double), typeof(WaveformView),
        new PropertyMetadata(0d, OnVisualChanged));

    public static readonly DependencyProperty StartSecondsProperty = DependencyProperty.Register(
        nameof(StartSeconds), typeof(double), typeof(WaveformView),
        new PropertyMetadata(0d, OnVisualChanged));

    public static readonly DependencyProperty EndSecondsProperty = DependencyProperty.Register(
        nameof(EndSeconds), typeof(double), typeof(WaveformView),
        new PropertyMetadata(0d, OnVisualChanged));

    public static readonly DependencyProperty ShowMarkersProperty = DependencyProperty.Register(
        nameof(ShowMarkers), typeof(bool), typeof(WaveformView),
        new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(WaveformView),
        new PropertyMetadata(false, OnVisualChanged));

    public WaveformView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public double StartSeconds
    {
        get => (double)GetValue(StartSecondsProperty);
        set => SetValue(StartSecondsProperty, value);
    }

    public double EndSeconds
    {
        get => (double)GetValue(EndSecondsProperty);
        set => SetValue(EndSecondsProperty, value);
    }

    public bool ShowMarkers
    {
        get => (bool)GetValue(ShowMarkersProperty);
        set => SetValue(ShowMarkersProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaveformView)d).Render();

    private void Render()
    {
        if (WaveImage is null || Overlay is null || LoadingRing is null || EmptyText is null) return;
        WaveImage.Source = Source;
        LoadingRing.IsActive = IsLoading;
        LoadingRing.Visibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = Source is null && !IsLoading ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Children.Clear();
        if (Source is null || !ShowMarkers || DurationSeconds <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var width = ActualWidth;
        var height = ActualHeight;
        var startX = Math.Clamp(StartSeconds / DurationSeconds * width, 0, width);
        var end = EndSeconds > 0 ? Math.Clamp(EndSeconds / DurationSeconds * width, 0, width) : width;
        if (end < startX) (startX, end) = (end, startX);

        var shade = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        if (startX > 0)
        {
            var left = new Rectangle { Width = startX, Height = height, Fill = shade };
            Canvas.SetLeft(left, 0);
            Canvas.SetTop(left, 0);
            Overlay.Children.Add(left);
        }

        if (end < width)
        {
            var right = new Rectangle { Width = width - end, Height = height, Fill = shade };
            Canvas.SetLeft(right, end);
            Canvas.SetTop(right, 0);
            Overlay.Children.Add(right);
        }

        var marker = new SolidColorBrush(Color.FromArgb(255, 210, 105, 58));
        foreach (var x in new[] { startX, end })
        {
            var line = new Rectangle { Width = 2, Height = height, Fill = marker };
            Canvas.SetLeft(line, Math.Clamp(x - 1, 0, width - 2));
            Canvas.SetTop(line, 0);
            Overlay.Children.Add(line);
        }
    }
}
