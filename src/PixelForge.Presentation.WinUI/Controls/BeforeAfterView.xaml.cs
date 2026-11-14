using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace PixelForge.Presentation.Controls;

public sealed partial class BeforeAfterView : UserControl
{
    private double _imageWidth;
    private double _imageHeight;
    private double _scale = 1;
    private double _translateX;
    private double _translateY;
    private double _fitScale = 1;
    private double _divider = 0.5;
    private bool _panning;
    private bool _dividerDragging;
    private Point _lastPoint;

    public BeforeAfterView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayoutForViewport();
    }

    public bool HasImages => _imageWidth > 0 && _imageHeight > 0;

    public void SetImages(string inputPath, string outputPath, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _imageWidth = width;
        _imageHeight = height;
        Scene.Width = width;
        Scene.Height = height;
        AfterImage.Width = width;
        AfterImage.Height = height;
        BeforeImage.Width = width;
        BeforeImage.Height = height;
        try
        {
            AfterImage.Source = new BitmapImage(new Uri(outputPath));
            BeforeImage.Source = new BitmapImage(new Uri(inputPath));
        }
        catch
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        _divider = 0.5;
        DispatcherQueue.TryEnqueue(() =>
        {
            FitToScreen();
            UpdateDividerVisuals();
        });
    }

    public void Clear()
    {
        AfterImage.Source = null;
        BeforeImage.Source = null;
        _imageWidth = 0;
        _imageHeight = 0;
        EmptyText.Visibility = Visibility.Visible;
    }

    public void FitToScreen()
    {
        if (!HasImages || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        _fitScale = Math.Min(Viewport.ActualWidth / _imageWidth, Viewport.ActualHeight / _imageHeight) * 0.98;
        _scale = Math.Clamp(_fitScale, 0.001, 8);
        _translateX = (Viewport.ActualWidth - _imageWidth * _scale) / 2;
        _translateY = (Viewport.ActualHeight - _imageHeight * _scale) / 2;
        ApplyTransform();
    }

    public void ShowActualSize()
    {
        if (!HasImages) return;
        _scale = 1;
        _translateX = (Viewport.ActualWidth - _imageWidth) / 2;
        _translateY = (Viewport.ActualHeight - _imageHeight) / 2;
        ApplyTransform();
    }

    public void ZoomIn() => ZoomAround(1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    public void ZoomOut() => ZoomAround(0.8, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (!HasImages) return;
        FitToScreen();
        UpdateDividerVisuals();
    }

    private void UpdateLayoutForViewport()
    {
        if (!HasImages) return;
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight) };
        FitToScreen();
        UpdateDividerVisuals();
    }

    private void ApplyTransform()
    {
        _scale = Math.Clamp(_scale, 0.001, 12);
        ClampTranslation();
        SceneTransform.ScaleX = _scale;
        SceneTransform.ScaleY = _scale;
        SceneTransform.TranslateX = _translateX;
        SceneTransform.TranslateY = _translateY;
        UpdateBeforeClip();
        UpdateDividerVisuals();
    }

    private void ClampTranslation()
    {
        var viewW = Viewport.ActualWidth;
        var viewH = Viewport.ActualHeight;
        var imageW = _imageWidth * _scale;
        var imageH = _imageHeight * _scale;
        if (imageW <= viewW)
            _translateX = (viewW - imageW) / 2;
        else
            _translateX = Math.Clamp(_translateX, viewW - imageW, 0);
        if (imageH <= viewH)
            _translateY = (viewH - imageH) / 2;
        else
            _translateY = Math.Clamp(_translateY, viewH - imageH, 0);
    }

    private void UpdateBeforeClip()
    {
        if (!HasImages) return;
        var dividerScreen = _divider * Viewport.ActualWidth;
        var localWidth = Math.Clamp((dividerScreen - _translateX) / _scale, 0, _imageWidth);
        BeforeImage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, localWidth, _imageHeight) };
    }

    private void UpdateDividerVisuals()
    {
        var x = _divider * Viewport.ActualWidth;
        DividerLine.Height = Viewport.ActualHeight;
        Canvas.SetLeft(DividerLine, x - 1);
        Canvas.SetTop(DividerLine, 0);
        Canvas.SetLeft(DividerGrip, x - 15);
        Canvas.SetTop(DividerGrip, Math.Max(8, Viewport.ActualHeight / 2 - 25));
        Overlay.Width = Viewport.ActualWidth;
        Overlay.Height = Viewport.ActualHeight;
        UpdateBeforeClip();
    }

    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HasImages) return;
        _panning = true;
        _lastPoint = e.GetCurrentPoint(Viewport).Position;
        Viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var point = e.GetCurrentPoint(Viewport).Position;
        _translateX += point.X - _lastPoint.X;
        _translateY += point.Y - _lastPoint.Y;
        _lastPoint = point;
        ApplyTransform();
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!HasImages) return;
        var point = e.GetCurrentPoint(Viewport);
        var factor = point.Properties.MouseWheelDelta > 0 ? 1.15 : 0.87;
        ZoomAround(factor, point.Position);
        e.Handled = true;
    }

    private void ZoomAround(double factor, Point screenPoint)
    {
        if (!HasImages) return;
        var imageX = (screenPoint.X - _translateX) / _scale;
        var imageY = (screenPoint.Y - _translateY) / _scale;
        var target = _scale * factor;
        target = Math.Clamp(target, Math.Max(0.02, _fitScale * 0.5), 12);
        _translateX = screenPoint.X - imageX * target;
        _translateY = screenPoint.Y - imageY * target;
        _scale = target;
        ApplyTransform();
    }

    private void OnDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dividerDragging = true;
        DividerGrip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dividerDragging) return;
        var point = e.GetCurrentPoint(Viewport).Position;
        _divider = Math.Clamp(point.X / Math.Max(1, Viewport.ActualWidth), 0.02, 0.98);
        UpdateDividerVisuals();
        e.Handled = true;
    }

    private void OnDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dividerDragging = false;
        DividerGrip.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}
