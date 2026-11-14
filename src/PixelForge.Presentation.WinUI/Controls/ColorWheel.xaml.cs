using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace PixelForge.Presentation.Controls;

public sealed class ColorWheelColorChangedEventArgs : EventArgs
{
    public ColorWheelColorChangedEventArgs(Color color) => Color = color;
    public Color Color { get; }
}

public sealed partial class ColorWheel : UserControl
{
    private const double WheelSize = 200;
    private const double Radius = 100;
    private bool _dragging;
    private bool _syncing;

    public ColorWheel()
    {
        InitializeComponent();
        BuildWheel();
        UpdateMarker();
    }

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColorWheel),
        new PropertyMetadata(Color.FromArgb(255, 210, 105, 58), OnColorPropertyChanged));

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public event EventHandler<ColorWheelColorChangedEventArgs>? ColorChanged;

    private static void OnColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorWheel wheel) wheel.UpdateMarker();
    }

    private void BuildWheel()
    {
        WheelCanvas.Children.Clear();
        const int segments = 180;
        const double center = WheelSize / 2;
        const double radius = Radius;
        for (var i = 0; i < segments; i++)
        {
            var start = i * 360.0 / segments;
            var end = (i + 1) * 360.0 / segments;
            var (x1, y1) = PointOnCircle(center, radius, start);
            var (x2, y2) = PointOnCircle(center, radius, end);
            var color = HsvToColor(start, 1, 1);
            WheelCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = new SolidColorBrush(color),
                Data = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Windows.Foundation.Point(center, center),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Windows.Foundation.Point(x1, y1) },
                                new ArcSegment
                                {
                                    Point = new Windows.Foundation.Point(x2, y2),
                                    Size = new Windows.Foundation.Size(radius, radius),
                                    SweepDirection = SweepDirection.Clockwise,
                                    IsLargeArc = false
                                }
                            }
                        }
                    }
                }
            });
        }
    }

    private static (double X, double Y) PointOnCircle(double center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return (center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        WheelRoot.Focus(FocusState.Pointer);
        WheelRoot.CapturePointer(e.Pointer);
        Pick(e.GetCurrentPoint(WheelRoot).Position);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        Pick(e.GetCurrentPoint(WheelRoot).Position);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        WheelRoot.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void Pick(Windows.Foundation.Point point)
    {
        var center = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var dx = point.X - center;
        var dy = point.Y - centerY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var saturation = Math.Clamp(distance / Radius, 0, 1);
        var hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (hue < 0) hue += 360;
        SetColor(HsvToColor(hue, saturation, 1), raiseEvent: true);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (hue, saturation, value) = ColorToHsv(Color);
        var changed = true;
        switch (e.Key)
        {
            case VirtualKey.Left: hue = (hue - 2 + 360) % 360; break;
            case VirtualKey.Right: hue = (hue + 2) % 360; break;
            case VirtualKey.Up: saturation = Math.Min(1, saturation + 0.02); break;
            case VirtualKey.Down: saturation = Math.Max(0, saturation - 0.02); break;
            default: changed = false; break;
        }

        if (!changed) return;
        SetColor(HsvToColor(hue, saturation, value), raiseEvent: true);
        e.Handled = true;
    }

    private void SetColor(Color color, bool raiseEvent)
    {
        _syncing = true;
        try
        {
            Color = color;
        }
        finally
        {
            _syncing = false;
        }

        UpdateMarker();
        if (raiseEvent) ColorChanged?.Invoke(this, new ColorWheelColorChangedEventArgs(color));
    }

    private void UpdateMarker()
    {
        if (_syncing) return;
        var (hue, saturation, _) = ColorToHsv(Color);
        var center = ActualWidth > 0 ? ActualWidth / 2 : 105;
        var centerY = ActualHeight > 0 ? ActualHeight / 2 : 105;
        var radians = hue * Math.PI / 180.0;
        var x = center + Radius * saturation * Math.Cos(radians);
        var y = centerY + Radius * saturation * Math.Sin(radians);
        Canvas.SetLeft(Marker, x - Marker.Width / 2);
        Canvas.SetTop(Marker, y - Marker.Height / 2);
    }

    internal static Color HsvToColor(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromArgb(255, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    internal static (double Hue, double Saturation, double Value) ColorToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
