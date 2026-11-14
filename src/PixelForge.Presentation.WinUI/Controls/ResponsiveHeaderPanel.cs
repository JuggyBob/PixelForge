using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PixelForge.Presentation.Controls;

/// <summary>
/// Lays out a title and an action row on one line when they fit. When they do
/// not fit, the action row moves below the title and starts at the left edge.
/// </summary>
public sealed class ResponsiveHeaderPanel : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ResponsiveHeaderPanel),
        new PropertyMetadata(10.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ResponsiveHeaderPanel),
        new PropertyMetadata(6.0, OnLayoutPropertyChanged));

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ResponsiveHeaderPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return default;

        var title = Children[0];
        var actions = Children.Count > 1 ? Children[1] : null;
        title.Measure(new Size(availableSize.Width, availableSize.Height));
        actions?.Measure(new Size(availableSize.Width, availableSize.Height));

        var titleSize = title.DesiredSize;
        var actionSize = actions?.DesiredSize ?? default;
        if (actions is null)
            return new Size(Math.Min(titleSize.Width, availableSize.Width), titleSize.Height);

        var fits = double.IsInfinity(availableSize.Width) ||
                   titleSize.Width + ColumnSpacing + actionSize.Width <= availableSize.Width;
        if (fits)
        {
            return new Size(
                Math.Min(titleSize.Width + ColumnSpacing + actionSize.Width, availableSize.Width),
                Math.Max(titleSize.Height, actionSize.Height));
        }

        return new Size(
            Math.Min(Math.Max(titleSize.Width, actionSize.Width), availableSize.Width),
            titleSize.Height + RowSpacing + actionSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        var title = Children[0];
        if (Children.Count == 1)
        {
            title.Arrange(new Rect(0, 0, Math.Min(title.DesiredSize.Width, finalSize.Width), title.DesiredSize.Height));
            return finalSize;
        }

        var actions = Children[1];
        var titleSize = title.DesiredSize;
        var actionSize = actions.DesiredSize;
        var fits = double.IsInfinity(finalSize.Width) ||
                   titleSize.Width + ColumnSpacing + actionSize.Width <= finalSize.Width;

        if (fits)
        {
            var height = Math.Max(titleSize.Height, actionSize.Height);
            var actionX = double.IsInfinity(finalSize.Width)
                ? titleSize.Width + ColumnSpacing
                : Math.Max(0, finalSize.Width - actionSize.Width);
            title.Arrange(new Rect(0, (height - titleSize.Height) / 2, titleSize.Width, titleSize.Height));
            actions.Arrange(new Rect(actionX, (height - actionSize.Height) / 2, actionSize.Width, actionSize.Height));
            return new Size(finalSize.Width, height);
        }

        title.Arrange(new Rect(0, 0, Math.Min(titleSize.Width, finalSize.Width), titleSize.Height));
        actions.Arrange(new Rect(0, titleSize.Height + RowSpacing,
            Math.Min(actionSize.Width, finalSize.Width), actionSize.Height));
        return new Size(finalSize.Width, titleSize.Height + RowSpacing + actionSize.Height);
    }
}
