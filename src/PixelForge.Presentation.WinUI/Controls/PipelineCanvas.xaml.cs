using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace PixelForge.Presentation.Controls;

public sealed partial class PipelineCanvas : UserControl
{
    private sealed class ConnectionVisual
    {
        public required Path Line { get; init; }
        public required Path Hit { get; init; }
        public required Polygon Arrow { get; init; }
    }

    private const double MinimumZoom = 0.02;
    private const double MaximumZoom = 4.0;
    private const double GridBaseStep = 40;
    private const double GridMinimumSpacing = 16;
    private const double GridMaximumSpacing = 80;
    private const double ExpansionGap = 12;
    private const double ExpansionDurationMilliseconds = 180;

    private readonly Dictionary<string, PipelineNodeControl> _nodeControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionVisual> _connectionVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point> _nodeOffsets = new(StringComparer.Ordinal);
    private Dictionary<string, Point> _offsetStart = new(StringComparer.Ordinal);
    private Dictionary<string, Point> _offsetTarget = new(StringComparer.Ordinal);

    private PipelineEditorViewModel? _editor;
    private IThemeService? _theme;
    private bool _panning;
    private bool _panMoved;
    private Point _panStart;
    private double _panStartCameraX;
    private double _panStartCameraY;
    private double _zoom = 1;
    private double _cameraX = 400;
    private double _cameraY = 280;
    private bool _themeSubscribed;
    private PipelineNodeControl? _portSource;
    private PipelineNodeControl? _highlightedTarget;
    private bool _pendingFit = true;
    private bool _fitQueued;
    private bool _transformQueued;
    private bool _offsetsDirty;
    private double _lastAppliedZoom = double.NaN;
    private double _lastTranslateX = double.NaN;
    private double _lastTranslateY = double.NaN;
    private double _gridZoom = double.NaN;
    private double _gridViewportWidth = double.NaN;
    private double _gridViewportHeight = double.NaN;
    private Rect _gridCovered;
    private bool _cullStateValid;
    private double _lastCullX;
    private double _lastCullY;
    private double _lastCullZoom;
    private double _lastCullWidth;
    private double _lastCullHeight;
    private long _layoutSignature;
    private bool _layoutSignatureValid;
    private readonly HashSet<string> _movedNodeIds = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _offsetTimer;
    private Stopwatch? _offsetWatch;

    public PipelineCanvas()
    {
        InitializeComponent();
        Viewport.PointerWheelChanged += OnPointerWheelChanged;
        Viewport.PointerPressed += OnViewportPointerPressed;
        Viewport.PointerMoved += OnViewportPointerMoved;
        Viewport.PointerReleased += OnViewportPointerReleased;
        Viewport.PointerCanceled += OnViewportPointerReleased;
        Viewport.PointerCaptureLost += OnViewportPointerCaptureLost;
        Viewport.DragOver += OnDragOver;
        Viewport.DragLeave += OnDragLeave;
        Viewport.Drop += OnDrop;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnViewportSizeChanged;
        UpdateZoomBadge();
    }

    // UserControl does not create an automation peer by default, which keeps
    // the canvas out of the UI automation tree. Expose it so automation can
    // target the drop surface for drag-and-drop tests.
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    public void Attach(PipelineEditorViewModel editor)
    {
        if (_editor is not null)
        {
            _editor.GraphChanged -= OnGraphChanged;
            _editor.FitRequested -= OnFitRequested;
            _editor.ZoomRequested -= OnZoomRequested;
            _editor.Nodes.CollectionChanged -= OnNodesChanged;
        }

        _editor = editor;
        _editor.GraphChanged += OnGraphChanged;
        _editor.FitRequested += OnFitRequested;
        _editor.ZoomRequested += OnZoomRequested;
        _editor.Nodes.CollectionChanged += OnNodesChanged;
        _pendingFit = true;
        _fitQueued = false;
        World.Opacity = 0;
        GridWorld.Opacity = 0;
        SyncNodes();
        SyncConnections();
        ApplyTransformNow();
        ScheduleInitialFit();
    }

    public void Detach()
    {
        if (_editor is not null)
        {
            _editor.GraphChanged -= OnGraphChanged;
            _editor.FitRequested -= OnFitRequested;
            _editor.ZoomRequested -= OnZoomRequested;
            _editor.Nodes.CollectionChanged -= OnNodesChanged;
            _editor = null;
        }

        StopOffsetAnimation();
        foreach (var control in _nodeControls.Values)
        {
            control.Node!.PropertyChanged -= OnNodePropertyChanged;
            control.SizeChanged -= OnNodeSizeChanged;
            control.LayoutChanged -= OnNodeLayoutChanged;
            control.PortDragStarted -= OnPortDragStarted;
            control.PortDragMoved -= OnPortDragMoved;
            control.PortDragCompleted -= OnPortDragCompleted;
        }

        _highlightedTarget?.SetConnectionTargetHighlight(false);
        _highlightedTarget = null;
        _portSource = null;
        _offsetsDirty = false;
        ConnectionPreview.Visibility = Visibility.Collapsed;
        ActionDropIndicator.Visibility = Visibility.Collapsed;
        _nodeOffsets.Clear();
        _nodeControls.Clear();
        _connectionVisuals.Clear();
        NodeLayer.Children.Clear();
        ConnectionLayer.Children.Clear();
        GridPath.Data = null;
        _gridZoom = double.NaN;
        _lastAppliedZoom = double.NaN;
        _lastTranslateX = double.NaN;
        _lastTranslateY = double.NaN;
        _cullStateValid = false;
        _layoutSignatureValid = false;
    }

    public void RefreshLocalization()
    {
        foreach (var control in _nodeControls.Values) control.RefreshLocalization();
        UpdateConnections();
    }

    public void RefreshTheme()
    {
        foreach (var control in _nodeControls.Values) control.RefreshTheme();
        foreach (var visual in _connectionVisuals.Values)
        {
            visual.Line.Stroke = GetBrush("AccentPrimaryBrush");
            visual.Arrow.Fill = GetBrush("AccentPrimaryBrush");
            visual.Hit.Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_themeSubscribed)
        {
            _theme = App.Services.GetService(typeof(IThemeService)) as IThemeService;
            if (_theme is not null) _theme.ThemeApplied += OnThemeApplied;
            ActualThemeChanged -= OnActualThemeChanged;
            ActualThemeChanged += OnActualThemeChanged;
            _themeSubscribed = true;
        }

        RefreshTheme();
        ApplyTransformNow();
        ScheduleInitialFit();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RefreshTheme();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_themeSubscribed) return;
        if (_theme is not null) _theme.ThemeApplied -= OnThemeApplied;
        ActualThemeChanged -= OnActualThemeChanged;
        _themeSubscribed = false;
        StopOffsetAnimation();
        _layoutSignatureValid = false;
    }

    private void OnThemeApplied(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshTheme);

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        SyncConnections();
        if (_offsetTimer is not null) _offsetsDirty = true;
        else RecomputeExpansionOffsets(animate: false);
        UpdateViewportCulling();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncNodes();
        SyncConnections();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            Viewport.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }

        if (_pendingFit && _editor is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            RecomputeExpansionOffsets(animate: false);
            FitToView();
            return;
        }

        ApplyTransformNow();
        if (_pendingFit) ScheduleInitialFit();
    }

    private void ScheduleInitialFit()
    {
        if (!_pendingFit || _fitQueued) return;
        _fitQueued = true;
        var queued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _fitQueued = false;
            if (!_pendingFit || _editor is null) return;
            if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
            World.UpdateLayout();
            RecomputeExpansionOffsets(animate: false);
            FitToView();
        });
        if (!queued) _fitQueued = false;
    }

    private void OnFitRequested(object? sender, EventArgs e)
    {
        RecomputeExpansionOffsets(animate: false);
        FitToView();
    }

    private void OnZoomRequested(object? sender, double factor) =>
        ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), factor);

    private void SyncNodes()
    {
        if (_editor is null) return;
        var wanted = new Dictionary<string, PipelineNodeViewModel>(StringComparer.Ordinal);
        foreach (var node in _editor.Nodes) wanted.TryAdd(node.Id, node);

        foreach (var (id, control) in _nodeControls.ToList())
        {
            if (wanted.ContainsKey(id)) continue;
            control.Node!.PropertyChanged -= OnNodePropertyChanged;
            control.SizeChanged -= OnNodeSizeChanged;
            control.LayoutChanged -= OnNodeLayoutChanged;
            control.PortDragStarted -= OnPortDragStarted;
            control.PortDragMoved -= OnPortDragMoved;
            control.PortDragCompleted -= OnPortDragCompleted;
            NodeLayer.Children.Remove(control);
            _nodeControls.Remove(id);
            _nodeOffsets.Remove(id);
        }

        foreach (var node in _editor.Nodes)
        {
            if (_nodeControls.TryGetValue(node.Id, out var existing))
            {
                existing.CanvasScale = _zoom;
                ApplyDisplayPosition(existing, node);
                continue;
            }

            if (!wanted.Remove(node.Id)) continue;
            var control = new PipelineNodeControl { CanvasScale = _zoom };
            control.Bind(node, _editor);
            node.PropertyChanged += OnNodePropertyChanged;
            control.SizeChanged += OnNodeSizeChanged;
            control.LayoutChanged += OnNodeLayoutChanged;
            control.PortDragStarted += OnPortDragStarted;
            control.PortDragMoved += OnPortDragMoved;
            control.PortDragCompleted += OnPortDragCompleted;
            ApplyDisplayPosition(control, node);
            NodeLayer.Children.Add(control);
            _nodeControls[node.Id] = control;
        }

        UpdateViewportCulling();
        _layoutSignatureValid = false;
        if (!_pendingFit)
        {
            if (_offsetTimer is not null) _offsetsDirty = true;
            else RecomputeExpansionOffsets(animate: false);
        }
        if (_pendingFit) ScheduleInitialFit();
    }

    private void ApplyDisplayPosition(PipelineNodeControl control, PipelineNodeViewModel node)
    {
        var offset = _nodeOffsets.TryGetValue(node.Id, out var value) ? value : default;
        Canvas.SetLeft(control, node.X + offset.X);
        Canvas.SetTop(control, node.Y + offset.Y);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PipelineNodeViewModel node) return;
        if (_nodeControls.TryGetValue(node.Id, out var control))
        {
            switch (e.PropertyName)
            {
                case nameof(PipelineNodeViewModel.X):
                case nameof(PipelineNodeViewModel.Y):
                    ApplyDisplayPosition(control, node);
                    UpdateConnectionsForNode(node.Id);
                    UpdateViewportCulling();
                    break;
                case nameof(PipelineNodeViewModel.IsExpanded):
                    // The node control has already built and measured the new
                    // content by the time this handler runs.
                    RecomputeExpansionOffsets(animate: true);
                    UpdateConnectionsForNode(node.Id);
                    UpdateViewportCulling();
                    break;
                case nameof(PipelineNodeViewModel.IsSelected):
                    control.RefreshTheme();
                    break;
                case nameof(PipelineNodeViewModel.Status):
                    control.RefreshTheme();
                    UpdateConnectionsForNode(node.Id);
                    break;
            }
        }
    }

    private void OnNodeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is PipelineNodeControl control && control.Node is { } node)
        {
            // Connection anchors only depend on width; the expanded height
            // animates every frame but must not rebuild every edge.
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                UpdateConnectionsForNode(node.Id);
            if (node.IsExpanded && !_pendingFit)
            {
                if (_offsetTimer is not null) _offsetsDirty = true;
                else RecomputeExpansionOffsets(animate: true);
            }
        }
        if (_pendingFit) ScheduleInitialFit();
    }

    private void OnNodeLayoutChanged(object? sender, EventArgs e)
    {
        if (sender is not PipelineNodeControl { Node: { } node }) return;
        UpdateConnectionsForNode(node.Id);
        if (_offsetTimer is not null) _offsetsDirty = true;
        else RecomputeExpansionOffsets(animate: true);
        UpdateViewportCulling();
    }

    private void SyncConnections()
    {
        if (_editor is null) return;
        var wanted = new Dictionary<string, PipelineConnectionViewModel>(StringComparer.Ordinal);
        foreach (var connection in _editor.Connections) wanted.TryAdd(connection.Id, connection);
        foreach (var (id, visual) in _connectionVisuals.ToList())
        {
            if (wanted.ContainsKey(id)) continue;
            ConnectionLayer.Children.Remove(visual.Line);
            ConnectionLayer.Children.Remove(visual.Hit);
            ConnectionLayer.Children.Remove(visual.Arrow);
            _connectionVisuals.Remove(id);
        }

        foreach (var connection in _editor.Connections)
        {
            if (!wanted.ContainsKey(connection.Id)) continue;
            if (_connectionVisuals.ContainsKey(connection.Id)) continue;
            var line = new Path
            {
                Stroke = GetBrush("AccentPrimaryBrush"),
                StrokeThickness = 2.25,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            var hit = new Path
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                StrokeThickness = 16,
                IsHitTestVisible = true,
                Tag = connection
            };
            hit.Tapped += (_, args) =>
            {
                if (hit.Tag is PipelineConnectionViewModel vm && _editor is not null)
                {
                    _editor.SelectConnection(vm);
                    args.Handled = true;
                }
            };
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = Loc.T("Remove connection") };
            remove.Click += async (_, _) =>
            {
                if (hit.Tag is PipelineConnectionViewModel vm && _editor is not null)
                    await _editor.DeleteConnectionAsync(vm);
            };
            menu.Items.Add(remove);
            hit.ContextFlyout = menu;
            var arrow = new Polygon
            {
                Fill = GetBrush("AccentPrimaryBrush"),
                IsHitTestVisible = false
            };
            ConnectionLayer.Children.Add(line);
            ConnectionLayer.Children.Add(hit);
            ConnectionLayer.Children.Add(arrow);
            _connectionVisuals[connection.Id] = new ConnectionVisual { Line = line, Hit = hit, Arrow = arrow };
        }

        UpdateConnections();
    }

    private static PathGeometry BuildConnectionGeometry(Point start, Point end)
    {
        var width = Math.Max(Math.Abs(end.X - start.X), 80);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(start.X + width * 0.5, start.Y),
            Point2 = new Point(end.X - width * 0.5, end.Y),
            Point3 = end
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void UpdateConnections()
    {
        if (_editor is null) return;
        foreach (var connection in _editor.Connections)
            UpdateConnectionVisual(connection);
        UpdateViewportCulling();
    }

    private void UpdateConnectionsForNode(string nodeId)
    {
        if (_editor is null) return;
        foreach (var connection in _editor.Connections)
        {
            if (connection.FromNodeId == nodeId || connection.ToNodeId == nodeId)
                UpdateConnectionVisual(connection);
        }
    }

    private void UpdateConnectionVisual(PipelineConnectionViewModel connection)
    {
        if (!_connectionVisuals.TryGetValue(connection.Id, out var visual)) return;
        if (!_nodeControls.TryGetValue(connection.FromNodeId, out var from) ||
            !_nodeControls.TryGetValue(connection.ToNodeId, out var to))
        {
            visual.Line.Visibility = Visibility.Collapsed;
            visual.Hit.Visibility = Visibility.Collapsed;
            visual.Arrow.Visibility = Visibility.Collapsed;
            return;
        }

        var fromWidth = from.NodeSize.Width;
        var start = new Point(Canvas.GetLeft(from) + fromWidth, Canvas.GetTop(from) + 32);
        var end = new Point(Canvas.GetLeft(to), Canvas.GetTop(to) + 32);
        visual.Line.Data = BuildConnectionGeometry(start, end);
        visual.Hit.Data = BuildConnectionGeometry(start, end);
        visual.Line.Visibility = Visibility.Visible;
        visual.Hit.Visibility = Visibility.Visible;
        visual.Arrow.Visibility = Visibility.Visible;
        visual.Arrow.Points.Clear();
        visual.Arrow.Points.Add(end);
        visual.Arrow.Points.Add(new Point(end.X - 10, end.Y - 5.5));
        visual.Arrow.Points.Add(new Point(end.X - 10, end.Y + 5.5));
        var invalid = from.Node!.Status == PipelineNodeStatus.Invalid ||
                      to.Node!.Status == PipelineNodeStatus.Invalid;
        visual.Line.Opacity = invalid ? 0.7 : 1;
        visual.Line.StrokeThickness = connection.IsSelected ? 3.5 : 2.25;
        visual.Line.StrokeDashArray.Clear();
        if (invalid)
        {
            visual.Line.StrokeDashArray.Add(4);
            visual.Line.StrokeDashArray.Add(3);
        }

        visual.Line.Tag = connection;
    }

    private void UpdateGrid()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var step = GridBaseStep;
        while (step * _zoom < GridMinimumSpacing) step *= 2;
        while (step * _zoom > GridMaximumSpacing) step /= 2;
        if (step <= 0) return;

        var width = Viewport.ActualWidth;
        var height = Viewport.ActualHeight;
        var halfWidth = width / (2 * _zoom);
        var halfHeight = height / (2 * _zoom);
        var visibleLeft = _cameraX - halfWidth;
        var visibleRight = _cameraX + halfWidth;
        var visibleTop = _cameraY - halfHeight;
        var visibleBottom = _cameraY + halfHeight;

        // Keep a generous overscan so translation-only pans reuse the cached
        // geometry. The grid is in its own transformed layer, so the cached
        // lines move with the camera for free.
        if (!double.IsNaN(_gridZoom) &&
            Math.Abs(_gridZoom - _zoom) < 0.0001 &&
            Math.Abs(_gridViewportWidth - width) < 0.5 &&
            Math.Abs(_gridViewportHeight - height) < 0.5 &&
            _gridCovered.Left <= visibleLeft &&
            _gridCovered.Right >= visibleRight &&
            _gridCovered.Top <= visibleTop &&
            _gridCovered.Bottom >= visibleBottom)
        {
            return;
        }

        var marginX = Math.Max(step * 2, halfWidth * 0.75);
        var marginY = Math.Max(step * 2, halfHeight * 0.75);
        var left = visibleLeft - marginX;
        var right = visibleRight + marginX;
        var top = visibleTop - marginY;
        var bottom = visibleBottom + marginY;
        var geometry = new PathGeometry();
        for (var x = Math.Floor(left / step) * step; x <= right; x += step)
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(x, top),
                IsClosed = false
            };
            figure.Segments.Add(new LineSegment { Point = new Point(x, bottom) });
            geometry.Figures.Add(figure);
        }

        for (var y = Math.Floor(top / step) * step; y <= bottom; y += step)
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(left, y),
                IsClosed = false
            };
            figure.Segments.Add(new LineSegment { Point = new Point(right, y) });
            geometry.Figures.Add(figure);
        }

        GridPath.Data = geometry;
        var thickness = 1 / _zoom;
        if (Math.Abs(GridPath.StrokeThickness - thickness) > 0.0001)
            GridPath.StrokeThickness = thickness;
        _gridZoom = _zoom;
        _gridViewportWidth = width;
        _gridViewportHeight = height;
        _gridCovered = new Rect(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1));
    }

    private void ApplyTransformNow()
    {
        var width = Viewport.ActualWidth;
        var height = Viewport.ActualHeight;
        var scale = _zoom;
        var translateX = width / 2 - _cameraX * _zoom;
        var translateY = height / 2 - _cameraY * _zoom;

        var zoomChanged = double.IsNaN(_lastAppliedZoom) || Math.Abs(_lastAppliedZoom - _zoom) > 0.0001;
        if (zoomChanged)
        {
            WorldTransform.ScaleX = scale;
            WorldTransform.ScaleY = scale;
            GridTransform.ScaleX = scale;
            GridTransform.ScaleY = scale;
        }

        if (double.IsNaN(_lastTranslateX) ||
            Math.Abs(_lastTranslateX - translateX) > 0.01 ||
            Math.Abs(_lastTranslateY - translateY) > 0.01)
        {
            WorldTransform.TranslateX = translateX;
            WorldTransform.TranslateY = translateY;
            GridTransform.TranslateX = translateX;
            GridTransform.TranslateY = translateY;
            _lastTranslateX = translateX;
            _lastTranslateY = translateY;
        }

        if (zoomChanged)
        {
            foreach (var control in _nodeControls.Values) control.CanvasScale = _zoom;
            UpdateZoomBadge();
            _lastAppliedZoom = _zoom;
        }

        UpdateGrid();
        if (CullNeeded(width, height)) UpdateViewportCulling();
    }

    private bool CullNeeded(double width, double height)
    {
        if (!_cullStateValid) return true;
        if (Math.Abs(_lastCullZoom - _zoom) > 0.0001) return true;
        if (Math.Abs(_lastCullWidth - width) > 0.5 || Math.Abs(_lastCullHeight - height) > 0.5) return true;
        return Math.Abs(_cameraX - _lastCullX) * _zoom >= 32 ||
               Math.Abs(_cameraY - _lastCullY) * _zoom >= 32;
    }

    private void RequestTransform()
    {
        if (_transformQueued) return;
        _transformQueued = true;
        var queued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _transformQueued = false;
            ApplyTransformNow();
        });
        if (!queued)
        {
            _transformQueued = false;
            ApplyTransformNow();
        }
    }

    private void UpdateZoomBadge()
    {
        var text = $"{_zoom * 100:0}%";
        if (!string.Equals(ZoomText.Text, text, StringComparison.Ordinal))
            ZoomText.Text = text;
    }

    private Point ScreenToWorld(Point screenPoint)
    {
        var width = Math.Max(Viewport.ActualWidth, 1);
        var height = Math.Max(Viewport.ActualHeight, 1);
        return new Point(
            _cameraX + (screenPoint.X - width / 2) / _zoom,
            _cameraY + (screenPoint.Y - height / 2) / _zoom);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        var delta = point.Properties.MouseWheelDelta;
        ZoomAt(point.Position, delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void ZoomAt(Point screenPoint, double factor)
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var world = ScreenToWorld(screenPoint);
        var next = Math.Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(next - _zoom) < 0.0001) return;
        _zoom = next;
        _cameraX = world.X - (screenPoint.X - Viewport.ActualWidth / 2) / _zoom;
        _cameraY = world.Y - (screenPoint.Y - Viewport.ActualHeight / 2) / _zoom;
        if (_panning)
        {
            // Rebase the pan gesture so the next move continues from the
            // zoomed camera instead of snapping back to the pre-zoom path.
            _panStart = screenPoint;
            _panStartCameraX = _cameraX;
            _panStartCameraY = _cameraY;
        }

        RequestTransform();
    }

    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed is false) return;
        if (IsInsideNode(e.OriginalSource as DependencyObject) ||
            e.OriginalSource is Path or Polygon)
            return;
        _panning = true;
        _panMoved = false;
        _panStart = e.GetCurrentPoint(Viewport).Position;
        _panStartCameraX = _cameraX;
        _panStartCameraY = _cameraY;
        Viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var point = e.GetCurrentPoint(Viewport).Position;
        var dx = point.X - _panStart.X;
        var dy = point.Y - _panStart.Y;
        if (!_panMoved && Math.Abs(dx) + Math.Abs(dy) > 3)
            _panMoved = true;
        _cameraX = _panStartCameraX - dx / _zoom;
        _cameraY = _panStartCameraY - dy / _zoom;
        RequestTransform();
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleasePointerCapture(e.Pointer);

        // Only a click on empty canvas clears the selection. Deselecting on
        // press used to trigger a full graph rebuild before the first pan
        // frame and caused a visible hitch.
        if (!_panMoved && _editor is not null &&
            (_editor.SelectedNode is not null || _editor.SelectedConnection is not null))
        {
            _editor.SelectNode(null);
        }

        ApplyTransformNow();
        e.Handled = true;
    }

    private void OnViewportPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ApplyTransformNow();
    }

    public void FitToView()
    {
        if (_nodeControls.Count == 0)
        {
            _cameraX = 400;
            _cameraY = 280;
            _zoom = 1;
            _pendingFit = false;
            World.Opacity = 1;
            GridWorld.Opacity = 1;
            ApplyTransformNow();
            return;
        }

        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var control in _nodeControls.Values)
        {
            var left = Canvas.GetLeft(control);
            var top = Canvas.GetTop(control);
            if (double.IsNaN(left) || double.IsNaN(top)) continue;
            var size = control.NodeSize;
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + Math.Max(size.Width, 280));
            maxY = Math.Max(maxY, top + Math.Max(size.Height, 64));
        }

        if (minX == double.MaxValue)
        {
            _pendingFit = false;
            World.Opacity = 1;
            GridWorld.Opacity = 1;
            ApplyTransformNow();
            return;
        }

        const double margin = 80;
        var width = Math.Max(200, maxX - minX + margin * 2);
        var height = Math.Max(160, maxY - minY + margin * 2);
        var scale = Math.Min(Viewport.ActualWidth / width, Viewport.ActualHeight / height);
        _zoom = Math.Clamp(scale, MinimumZoom, 1.5);
        _cameraX = (minX + maxX) / 2;
        _cameraY = (minY + maxY) / 2;
        _pendingFit = false;
        World.Opacity = 1;
        GridWorld.Opacity = 1;
        ApplyTransformNow();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ActionDropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.T("Drop to add");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        ActionDropIndicator.Visibility = Visibility.Collapsed;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        ActionDropIndicator.Visibility = Visibility.Collapsed;

    public void ShowActionDropIndicator(Point viewportPoint) => UpdateDropIndicator(viewportPoint);

    public void HideActionDropIndicator() => ActionDropIndicator.Visibility = Visibility.Collapsed;

    public async Task InsertActionFromPaletteAsync(string actionId, Point viewportPoint)
    {
        if (_editor is null) return;
        var world = ScreenToWorld(viewportPoint);
        await _editor.InsertActionAtAsync(actionId, world.X - 140, world.Y - 32);
    }

    private void UpdateDropIndicator(Point viewportPoint)
    {
        var world = ScreenToWorld(viewportPoint);
        Canvas.SetLeft(ActionDropIndicator, world.X - 140);
        Canvas.SetTop(ActionDropIndicator, world.Y - 32);
        ActionDropIndicator.Visibility = Visibility.Visible;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ActionDropIndicator.Visibility = Visibility.Collapsed;
        var deferral = e.GetDeferral();
        try
        {
            await HandleDropAsync(e);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleDropAsync(DragEventArgs e)
    {
        if (_editor is null) return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var folder = items.Select(i => i.Path)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                await _editor.SetInputFolderAsync(folder, recursive: true);
                return;
            }

            var paths = items.Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .ToList();
            if (paths.Count > 0) await _editor.AddInputPathsAsync(paths);
        }
    }

    private void OnPortDragStarted(object? sender, PipelinePortDragEventArgs e)
    {
        if (_editor is null || sender is not PipelineNodeControl control) return;
        _portSource = control;
        ConnectionPreview.Stroke = GetBrush("AccentPrimaryBrush");
        ConnectionPreview.Visibility = Visibility.Visible;
        UpdatePortPreview(e.Position);
    }

    private void OnPortDragMoved(object? sender, PipelinePortDragEventArgs e)
    {
        if (_portSource is null) return;
        UpdatePortPreview(e.Position);
        var world = _portSource.TransformToVisual(World).TransformPoint(e.Position);
        HighlightTarget(FindNodeAt(world, _portSource));
    }

    private async void OnPortDragCompleted(object? sender, PipelinePortDragEventArgs e)
    {
        ConnectionPreview.Visibility = Visibility.Collapsed;
        HighlightTarget(null);
        if (_editor is null || _portSource?.Node is null) return;
        var sourceControl = _portSource;
        _portSource = null;
        var worldPoint = sourceControl.TransformToVisual(World).TransformPoint(e.Position);
        var target = FindNodeAt(worldPoint, sourceControl);
        if (target?.Node is null) return;
        try
        {
            await _editor.ConnectNodesAsync(sourceControl.Node.Id, target.Node.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private PipelineNodeControl? FindNodeAt(Point worldPoint, PipelineNodeControl exclude)
    {
        return _nodeControls.Values
            .Where(c => c != exclude && c.Node is not null && c.Node.Kind != PipelineNodeKind.Input)
            .FirstOrDefault(c =>
            {
                var left = Canvas.GetLeft(c);
                var top = Canvas.GetTop(c);
                if (double.IsNaN(left) || double.IsNaN(top)) return false;
                var size = c.NodeSize;
                return worldPoint.X >= left && worldPoint.X <= left + size.Width &&
                       worldPoint.Y >= top && worldPoint.Y <= top + size.Height;
            });
    }

    private void HighlightTarget(PipelineNodeControl? target)
    {
        if (ReferenceEquals(_highlightedTarget, target)) return;
        _highlightedTarget?.SetConnectionTargetHighlight(false);
        _highlightedTarget = target;
        _highlightedTarget?.SetConnectionTargetHighlight(true);
    }

    private void UpdatePortPreview(Point localPoint)
    {
        if (_portSource?.Node is null) return;
        var start = new Point(Canvas.GetLeft(_portSource) + _portSource.NodeSize.Width,
            Canvas.GetTop(_portSource) + 32);
        var end = _portSource.TransformToVisual(World).TransformPoint(localPoint);
        var width = Math.Max(Math.Abs(end.X - start.X), 80);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(start.X + width * 0.5, start.Y),
            Point2 = new Point(end.X - width * 0.5, end.Y),
            Point3 = end
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ConnectionPreview.Data = geometry;
    }

    private void RecomputeExpansionOffsets(bool animate)
    {
        if (_editor is null) return;
        _offsetsDirty = false;

        // Selection, validation and run-status changes raise GraphChanged too.
        // Skip the layout solve unless a position, expansion state or measured
        // size actually changed.
        var signature = ComputeLayoutSignature();
        if (_layoutSignatureValid && signature == _layoutSignature) return;
        _layoutSignature = signature;
        _layoutSignatureValid = true;

        var target = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var node in _editor.Nodes) target[node.Id] = default;

        Size SizeOf(PipelineNodeViewModel node)
        {
            if (_nodeControls.TryGetValue(node.Id, out var control)) return control.NodeSize;
            return node.IsExpanded
                ? new Size(380, PipelineNodeControl.HeaderHeight + 1)
                : new Size(280, PipelineNodeControl.HeaderHeight);
        }

        Rect BaseRect(PipelineNodeViewModel node)
        {
            var size = SizeOf(node);
            return new Rect(node.X, node.Y, Math.Max(size.Width, 1), Math.Max(size.Height, 1));
        }

        static Rect Moved(Rect rect, Point offset) =>
            new(rect.X + offset.X, rect.Y + offset.Y, rect.Width, rect.Height);

        static bool Near(Rect a, Rect b) =>
            a.Left < b.Right + ExpansionGap &&
            a.Right > b.Left - ExpansionGap &&
            a.Top < b.Bottom + ExpansionGap &&
            a.Bottom > b.Top - ExpansionGap;

        // Returns the smallest forward/down displacement that clears every
        // obstacle, or zero when the card is already clear. Pipeline flow is
        // left-to-right/top-to-bottom, so cards are never pushed above or to
        // the left of where the user placed them. initialCoverageWithGap
        // decides whether a nearby-but-not-overlapping card counts as covered.
        static Point FindOffset(Rect card, IReadOnlyList<Rect> obstacles,
            bool initialCoverageWithGap)
        {
            if (obstacles.Count == 0) return default;

            var covered = initialCoverageWithGap
                ? obstacles.Where(obstacle => Near(card, obstacle)).ToList()
                : obstacles.Where(obstacle => Intersects(card, obstacle)).ToList();
            if (covered.Count == 0) return default;

            // Any optimum can be slid left/up until an edge is flush with an
            // obstacle threshold, so the Cartesian product of every obstacle's
            // right/bottom edge contains the minimum-cost solution. This also
            // finds diagonal escapes (right of one card while below another).
            var xs = new List<double> { card.Left };
            var ys = new List<double> { card.Top };
            var maxRight = double.MinValue;
            var maxBottom = double.MinValue;
            foreach (var obstacle in obstacles)
            {
                xs.Add(obstacle.Right + ExpansionGap);
                ys.Add(obstacle.Bottom + ExpansionGap);
                if (obstacle.Right > maxRight) maxRight = obstacle.Right;
                if (obstacle.Bottom > maxBottom) maxBottom = obstacle.Bottom;
            }

            // Guaranteed fallbacks: fully right of or below every obstacle.
            xs.Add(maxRight + ExpansionGap);
            ys.Add(maxBottom + ExpansionGap);

            var best = default(Point);
            var bestCost = double.MaxValue;
            foreach (var x in xs)
            {
                foreach (var y in ys)
                {
                    var dx = x - card.Left;
                    var dy = y - card.Top;
                    if (dx < 0 || dy < 0) continue;

                    var moved = new Rect(x, y, card.Width, card.Height);
                    if (obstacles.Any(obstacle => Near(moved, obstacle))) continue;

                    var cost = dx + dy;
                    if (cost > bestCost + 0.01) continue;
                    if (Math.Abs(cost - bestCost) <= 0.01 && bestCost < double.MaxValue)
                    {
                        // Prefer keeping cards in their row, then left-most.
                        if (dy > best.Y + 0.01) continue;
                        if (Math.Abs(dy - best.Y) <= 0.01 && dx < best.X - 0.01) continue;
                    }

                    best = new Point(dx, dy);
                    bestCost = cost;
                }
            }

            return bestCost == double.MaxValue ? default : best;
        }

        // Phase 1: expanded cards move away from each other. Persisted model
        // coordinates are never touched; only the display offset is animated.
        var placedExpanded = new List<Rect>();
        var expanded = _editor.Nodes
            .Where(node => node.IsExpanded)
            .OrderBy(node => node.Y)
            .ThenBy(node => node.X)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var node in expanded)
        {
            var card = BaseRect(node);
            var offset = FindOffset(card, placedExpanded, initialCoverageWithGap: true);
            target[node.Id] = offset;
            placedExpanded.Add(Moved(card, offset));
        }

        // Phase 2: collapsed cards keep the same 12 px visual gap around every
        // expanded card and every already-placed collapsed card.
        var placedAll = new List<Rect>(placedExpanded);
        var collapsed = _editor.Nodes
            .Where(node => !node.IsExpanded)
            .OrderBy(node => node.Y)
            .ThenBy(node => node.X)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var node in collapsed)
        {
            var card = BaseRect(node);
            var offset = FindOffset(card, placedAll, initialCoverageWithGap: true);
            target[node.Id] = offset;
            placedAll.Add(Moved(card, offset));
        }

        AnimateOffsets(target, animate);
    }

    private long ComputeLayoutSignature()
    {
        if (_editor is null) return 0;
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var node in _editor.Nodes)
            {
                hash = (hash ^ (ulong)node.Id.GetHashCode()) * 1099511628211UL;
                hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(node.X)) * 1099511628211UL;
                hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(node.Y)) * 1099511628211UL;
                hash = (hash ^ (node.IsExpanded ? 1UL : 0UL)) * 1099511628211UL;
                var size = _nodeControls.TryGetValue(node.Id, out var control)
                    ? control.NodeSize
                    : node.IsExpanded
                        ? new Size(380, PipelineNodeControl.HeaderHeight + 1)
                        : new Size(280, PipelineNodeControl.HeaderHeight);
                hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(size.Width)) * 1099511628211UL;
                hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(size.Height)) * 1099511628211UL;
            }

            return (long)hash;
        }
    }

    private static bool SameOffsets(IReadOnlyDictionary<string, Point> a,
        IReadOnlyDictionary<string, Point> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (id, point) in a)
        {
            if (!b.TryGetValue(id, out var other)) return false;
            if (Math.Abs(point.X - other.X) > 0.01 || Math.Abs(point.Y - other.Y) > 0.01)
                return false;
        }

        return true;
    }

    private void AnimateOffsets(Dictionary<string, Point> target, bool animate)
    {
        if (_editor is null) return;
        // A second layout event while the current offset animation is running
        // must not restart the easing for every card when the target is the
        // same; that produces a visible hitch.
        if (_offsetTimer is not null && SameOffsets(target, _offsetTarget)) return;

        var hasChanges = false;
        foreach (var node in _editor.Nodes)
        {
            var current = _nodeOffsets.TryGetValue(node.Id, out var offset) ? offset : default;
            var wanted = target.TryGetValue(node.Id, out var value) ? value : default;
            if (Math.Abs(current.X - wanted.X) > 0.01 || Math.Abs(current.Y - wanted.Y) > 0.01)
            {
                hasChanges = true;
                break;
            }
        }

        if (!hasChanges && (_offsetTimer is null || SameOffsets(target, _offsetTarget)))
            return;

        if (!animate)
        {
            StopOffsetAnimation();
            _nodeOffsets.Clear();
            foreach (var (id, value) in target)
            {
                if (value.X != 0 || value.Y != 0) _nodeOffsets[id] = value;
            }

            foreach (var node in _editor.Nodes)
            {
                if (_nodeControls.TryGetValue(node.Id, out var control))
                    ApplyDisplayPosition(control, node);
            }

            UpdateConnections();
            return;
        }

        var start = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var node in _editor.Nodes)
        {
            var current = _nodeOffsets.TryGetValue(node.Id, out var offset) ? offset : default;
            start[node.Id] = current;
        }

        _offsetStart = start;
        _offsetTarget = target;
        _offsetWatch = Stopwatch.StartNew();
        if (_offsetTimer is null)
        {
            _offsetTimer = DispatcherQueue.CreateTimer();
            _offsetTimer.Interval = TimeSpan.FromMilliseconds(16);
            _offsetTimer.IsRepeating = true;
            _offsetTimer.Tick += OnOffsetTimerTick;
        }

        _offsetTimer.Start();
    }

    private void OnOffsetTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_editor is null || _offsetWatch is null)
        {
            StopOffsetAnimation();
            return;
        }

        var progress = Math.Clamp(_offsetWatch.Elapsed.TotalMilliseconds / ExpansionDurationMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var moved = _movedNodeIds;
        moved.Clear();
        foreach (var node in _editor.Nodes)
        {
            var from = _offsetStart.TryGetValue(node.Id, out var start) ? start : default;
            var to = _offsetTarget.TryGetValue(node.Id, out var target) ? target : default;
            var next = new Point(
                from.X + (to.X - from.X) * eased,
                from.Y + (to.Y - from.Y) * eased);
            var current = _nodeOffsets.TryGetValue(node.Id, out var existing) ? existing : default;
            if (Math.Abs(next.X - current.X) < 0.01 && Math.Abs(next.Y - current.Y) < 0.01) continue;
            _nodeOffsets[node.Id] = next;
            moved.Add(node.Id);
            if (_nodeControls.TryGetValue(node.Id, out var control))
                ApplyDisplayPosition(control, node);
        }

        foreach (var connection in _editor.Connections)
        {
            if (moved.Contains(connection.FromNodeId) || moved.Contains(connection.ToNodeId))
                UpdateConnectionVisual(connection);
        }

        UpdateViewportCulling();
        if (progress >= 1)
        {
            StopOffsetAnimation();
            _nodeOffsets.Clear();
            foreach (var (id, value) in _offsetTarget)
            {
                if (value.X != 0 || value.Y != 0) _nodeOffsets[id] = value;
            }

            if (_offsetsDirty) RecomputeExpansionOffsets(animate: true);
        }
    }

    private void StopOffsetAnimation()
    {
        if (_offsetTimer is not null)
        {
            _offsetTimer.Stop();
            _offsetTimer.Tick -= OnOffsetTimerTick;
            _offsetTimer = null;
        }

        _offsetWatch = null;
    }

    private void UpdateViewportCulling()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        // Overscan is expressed in screen pixels so culling behaves the same at
        // every zoom level and never pops a node in after it is already visible.
        var overscan = 160 / Math.Max(_zoom, 0.01);
        var visible = new Rect(
            _cameraX - Viewport.ActualWidth / (2 * _zoom) - overscan,
            _cameraY - Viewport.ActualHeight / (2 * _zoom) - overscan,
            Viewport.ActualWidth / _zoom + overscan * 2,
            Viewport.ActualHeight / _zoom + overscan * 2);
        foreach (var (id, control) in _nodeControls)
        {
            var left = Canvas.GetLeft(control);
            var top = Canvas.GetTop(control);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                SetCompositionVisible(control, false);
                continue;
            }

            var size = control.NodeSize;
            var bounds = new Rect(left, top, Math.Max(size.Width, 1), Math.Max(size.Height, 1));
            if (!control.IsDragActive)
                SetCompositionVisible(control, Intersects(visible, bounds));
        }

        if (_editor is null) return;
        foreach (var connection in _editor.Connections)
        {
            if (!_connectionVisuals.TryGetValue(connection.Id, out var visual)) continue;
            if (!_nodeControls.TryGetValue(connection.FromNodeId, out var from) ||
                !_nodeControls.TryGetValue(connection.ToNodeId, out var to))
                continue;
            var fromBounds = new Rect(Canvas.GetLeft(from), Canvas.GetTop(from),
                Math.Max(from.NodeSize.Width, 1), Math.Max(from.NodeSize.Height, 1));
            var toBounds = new Rect(Canvas.GetLeft(to), Canvas.GetTop(to),
                Math.Max(to.NodeSize.Width, 1), Math.Max(to.NodeSize.Height, 1));
            var lineBounds = new Rect(
                Math.Min(fromBounds.Right, toBounds.Right - 10),
                Math.Min(fromBounds.Top + 26, toBounds.Top + 26),
                Math.Max(Math.Abs(toBounds.Left - fromBounds.Right) + 20, 20),
                Math.Max(Math.Abs(toBounds.Top - fromBounds.Top) + 12, 12));
            var show = Intersects(visible, fromBounds) || Intersects(visible, toBounds) ||
                       Intersects(visible, lineBounds);
            // Composition visibility does not invalidate measure/arrange, so
            // panning across the cull boundary cannot trigger node SizeChanged
            // and the offset/connection rebuild cascades.
            SetCompositionVisible(visual.Line, show);
            SetCompositionVisible(visual.Hit, show);
            SetCompositionVisible(visual.Arrow, show);
        }

        _lastCullX = _cameraX;
        _lastCullY = _cameraY;
        _lastCullZoom = _zoom;
        _lastCullWidth = Viewport.ActualWidth;
        _lastCullHeight = Viewport.ActualHeight;
        _cullStateValid = true;
    }

    private static void SetCompositionVisible(UIElement element, bool visible)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.IsVisible != visible) visual.IsVisible = visible;
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool IsInsideNode(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is PipelineNodeControl) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Brush GetBrush(string key)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
