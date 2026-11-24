using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.ViewModels;
using Windows.Foundation;

namespace PixelForge.Presentation.Controls;

public sealed class PipelinePortDragEventArgs : EventArgs
{
    public required PipelineNodeViewModel Node { get; init; }
    public required Windows.Foundation.Point Position { get; init; }
}

public sealed partial class PipelineNodeControl : UserControl
{
    public const double HeaderHeight = 64;

    private PipelineNodeViewModel? _node;
    private PipelineEditorViewModel? _editor;
    private bool _dragging;
    private bool _moved;
    private Windows.Foundation.Point _dragStart;
    private double _startX;
    private double _startY;
    private Storyboard? _expandStoryboard;
    private bool _portDragging;
    private bool _isConnectionTarget;
    private double _collapseGhostHeight;

    public event EventHandler<PipelinePortDragEventArgs>? PortDragStarted;
    public event EventHandler<PipelinePortDragEventArgs>? PortDragMoved;
    public event EventHandler<PipelinePortDragEventArgs>? PortDragCompleted;

    /// <summary>
    /// Raised when the measured expanded content changed outside an explicit
    /// expand/collapse action (conditional settings fields, localization
    /// rewrapping, input file list growth). The canvas re-solves the dropdown
    /// layout so a stale rectangle can never leave another card underneath it.
    /// </summary>
    public event EventHandler? LayoutChanged;

    public PipelineNodeControl()
    {
        InitializeComponent();
        Header.Tapped += OnHeaderTapped;
        Header.PointerPressed += OnHeaderPointerPressed;
        Header.PointerMoved += OnHeaderPointerMoved;
        Header.PointerReleased += OnHeaderPointerReleased;
        Header.PointerCanceled += OnHeaderPointerReleased;
        Header.PointerCaptureLost += OnHeaderPointerReleased;
        OutputPort.PointerPressed += OnOutputPortPointerPressed;
        OutputPort.PointerMoved += OnOutputPortPointerMoved;
        OutputPort.PointerReleased += OnOutputPortPointerReleased;
        OutputPort.PointerCanceled += OnOutputPortPointerReleased;
        OutputPort.PointerCaptureLost += OnOutputPortPointerReleased;
        Loaded += OnNodeLoaded;
    }

    public PipelineNodeViewModel? Node => _node;

    /// <summary>World-to-screen scale supplied by the canvas for stable drag deltas.</summary>
    public double CanvasScale { get; set; } = 1;

    public Windows.Foundation.Point InputAnchor => new(0, 32);
    public Windows.Foundation.Point OutputAnchor => new(LayoutWidth, 32);

    public double LayoutWidth => _node?.IsExpanded == true
        ? 380
        : Root.Width > 0 ? Root.Width : 280;

    public double ExpandedContentHeight { get; private set; }

    // The card Border contributes one pixel above and below the header/content
    // stack. Without it the collision rectangle is two pixels short and cards
    // that touch exactly can visibly overlap.
    private const double BorderAllowance = 2;

    public double LayoutHeight
    {
        get
        {
            if (_node?.IsExpanded == true)
                return HeaderHeight + ExpandedContentHeight + BorderAllowance;
            // Keep the collapsing dropdown as an obstacle until its close
            // animation finishes, otherwise other cards animate into the space
            // it still occupies.
            return _collapseGhostHeight > 0
                ? HeaderHeight + _collapseGhostHeight + BorderAllowance
                : HeaderHeight + BorderAllowance;
        }
    }

    public Windows.Foundation.Size NodeSize => new(LayoutWidth, Math.Max(LayoutHeight, 64));

    public bool IsDragActive => _dragging || _portDragging;

    public void SetConnectionTargetHighlight(bool active)
    {
        if (_isConnectionTarget == active) return;
        _isConnectionTarget = active;
        UpdateSelectionVisual();
    }

    public void Bind(PipelineNodeViewModel node, PipelineEditorViewModel editor)
    {
        if (_node is not null) _node.PropertyChanged -= OnNodePropertyChanged;
        _node = node;
        _editor = editor;
        _collapseGhostHeight = 0;
        ExpandedContentHeight = 0;
        _node.PropertyChanged += OnNodePropertyChanged;
        InputPort.Visibility = node.Kind == PipelineNodeKind.Input ? Visibility.Collapsed : Visibility.Visible;
        OutputPort.Visibility = node.Kind == PipelineNodeKind.Output ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(OutputPort, Loc.T("Drag to connect"));
        Root.Width = node.IsExpanded ? 380 : 280;
        UpdateVisuals();
        SetExpanded(node.IsExpanded, animate: false);
        if (node is PipelineInputNodeViewModel input)
        {
            input.Files.CollectionChanged -= OnInputFilesChanged;
            input.Files.CollectionChanged += OnInputFilesChanged;
        }
    }

    public void RefreshLocalization()
    {
        ToolTipService.SetToolTip(OutputPort, Loc.T("Drag to connect"));
        UpdateVisuals();
        if (_node is not { IsExpanded: true }) return;

        // Refresh options in place for action nodes. Input/output/unavailable
        // content is code-built, so rebuild it from the current node state;
        // the collision ComboBox selection lives on the view model and is
        // restored, not blanked.
        if (SettingsHost.Content is PipelineSettingsEditor editor) editor.RefreshLocalization();
        else BuildExpandedContent();
        if (_expandStoryboard is not null) return;

        RemeasureExpandedContent();
        SettingsHost.MaxHeight = double.PositiveInfinity;
    }

    private void OnSettingsLayoutChanged(object? sender, EventArgs e)
    {
        if (_node is not { IsExpanded: true }) return;
        // Do not measure against an animated MaxHeight; the completion handler
        // measures once the real content height is available.
        if (_expandStoryboard is not null) return;
        RemeasureExpandedContent();
    }

    private void OnNodeLoaded(object sender, RoutedEventArgs e)
    {
        if (_node is not { IsExpanded: true }) return;
        if (_expandStoryboard is not null) return;
        RemeasureExpandedContent();
    }

    private void RemeasureExpandedContent()
    {
        if (_node is not { IsExpanded: true }) return;
        if (SettingsHost.Visibility != Visibility.Visible) return;
        MeasureSettingsHost();
        var measured = Math.Max(SettingsHost.DesiredSize.Height, 1);
        if (Math.Abs(measured - ExpandedContentHeight) < 0.5) return;
        ExpandedContentHeight = measured;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MeasureSettingsHost()
    {
        var width = Root.Width > 0 ? Math.Max(Root.Width - BorderAllowance, 1) : 356;
        SettingsHost.Measure(new Size(width, double.PositiveInfinity));
    }

    public void RefreshTheme()
    {
        UpdateSelectionVisual();
        UpdateStatusVisual();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PipelineNodeViewModel.Title):
            case nameof(PipelineNodeViewModel.Summary):
            case nameof(PipelineNodeViewModel.IconGlyph):
                UpdateVisuals();
                break;
            case nameof(PipelineNodeViewModel.IsSelected):
                UpdateSelectionVisual();
                break;
            case nameof(PipelineNodeViewModel.Status):
                UpdateStatusVisual();
                break;
            case nameof(PipelineNodeViewModel.IsExpanded):
                SetExpanded(_node?.IsExpanded == true, animate: true);
                break;
        }
    }

    private void OnInputFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_node is { IsExpanded: true } and PipelineInputNodeViewModel) BuildExpandedContent();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (_node is null) return;
        TitleText.Text = _node.Title;
        SummaryText.Text = _node.Summary;
        Icon.Glyph = _node.IconGlyph;
        Root.Width = _node.IsExpanded ? 380 : 280;
        UpdateSelectionVisual();
        UpdateStatusVisual();
        AutomationProperties.SetName(Root, _node.Title);
        AutomationProperties.SetAutomationId(Root, "PipelineNode_" + _node.Id);
        AutomationProperties.SetName(this, _node.Title);
        AutomationProperties.SetAutomationId(this, "PipelineNode_" + _node.Id);
    }

    private void UpdateSelectionVisual()
    {
        if (_node is null) return;
        if (_node.IsSelected || _isConnectionTarget)
        {
            Card.BorderBrush = GetBrush("AccentPrimaryBrush");
        }
        else
        {
            Card.BorderBrush = GetBrush("DividerBrush");
        }

        // Keep the border thickness constant: changing it changes the control
        // size and feeds SizeChanged back into connection and offset rebuilds.
        Card.BorderThickness = new Thickness(1);
    }

    private void UpdateStatusVisual()
    {
        if (_node is null) return;
        var brush = _node.Status switch
        {
            PipelineNodeStatus.Ready => GetBrush("AccentPrimaryBrush"),
            PipelineNodeStatus.Invalid or PipelineNodeStatus.Failed => GetBrush("DangerBrush"),
            PipelineNodeStatus.Running => GetBrush("AccentPrimaryBrush"),
            PipelineNodeStatus.Completed => GetBrush("SuccessBrush"),
            _ => GetBrush("TextFillColorTertiaryBrush")
        };
        StatusDot.Fill = brush;
        var status = Loc.T(_node.StatusTextKey);
        ToolTipService.SetToolTip(StatusDot, status);
        AutomationProperties.SetHelpText(Root, status);
    }

    private Point PortPoint(PointerRoutedEventArgs e) =>
        e.GetCurrentPoint(Root).Position;

    private void OnOutputPortPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_node is null || e.GetCurrentPoint(OutputPort).Properties.IsLeftButtonPressed is false) return;
        _portDragging = true;
        OutputPort.CapturePointer(e.Pointer);
        PortDragStarted?.Invoke(this, new PipelinePortDragEventArgs
        {
            Node = _node,
            Position = PortPoint(e)
        });
        e.Handled = true;
    }

    private void OnOutputPortPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_portDragging || _node is null) return;
        PortDragMoved?.Invoke(this, new PipelinePortDragEventArgs
        {
            Node = _node,
            Position = PortPoint(e)
        });
        e.Handled = true;
    }

    private void OnOutputPortPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_portDragging || _node is null) return;
        _portDragging = false;
        OutputPort.ReleasePointerCapture(e.Pointer);
        PortDragCompleted?.Invoke(this, new PipelinePortDragEventArgs
        {
            Node = _node,
            Position = PortPoint(e)
        });
        e.Handled = true;
    }

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_node is null || _editor is null || _moved) return;
        _editor.SelectNode(_node);
        _editor.ToggleNodeExpansion(_node);
        e.Handled = true;
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_node is null || _editor is null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed is false) return;
        _dragStart = StablePoint(e);
        _startX = _node.X;
        _startY = _node.Y;
        _dragging = true;
        _moved = false;
        Header.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _node is null || _editor is null) return;
        var point = StablePoint(e);
        var scale = Math.Max(CanvasScale, 0.01);
        var dx = (point.X - _dragStart.X) / scale;
        var dy = (point.Y - _dragStart.Y) / scale;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) > 3)
        {
            _moved = true;
            _editor.BeginNodeMove(_node);
        }

        if (_moved) _editor.UpdateNodePosition(_node, _startX + dx, _startY + dy);
        e.Handled = true;
    }

    private async void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Header.ReleasePointerCapture(e.Pointer);
        if (_moved && _node is not null && _editor is not null)
        {
            try
            {
                await _editor.CompleteNodeMoveAsync(_node);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        e.Handled = true;
    }

    private Point StablePoint(PointerRoutedEventArgs e)
    {
        var root = XamlRoot?.Content as UIElement;
        return e.GetCurrentPoint(root ?? this).Position;
    }

    private void SetExpanded(bool expanded, bool animate)
    {
        Root.Width = expanded ? 380 : 280;
        _expandStoryboard?.Stop();
        _expandStoryboard = null;

        if (expanded)
        {
            _collapseGhostHeight = 0;
            SettingsHost.Visibility = Visibility.Visible;
            SettingsHost.MaxHeight = double.PositiveInfinity;
            BuildExpandedContent();
            MeasureSettingsHost();
            ExpandedContentHeight = Math.Max(SettingsHost.DesiredSize.Height, 1);
            var target = ExpandedContentHeight;
            ExpandGlyph.Glyph = "\uE70E";
            if (!animate)
            {
                SettingsHost.MaxHeight = double.PositiveInfinity;
                SettingsHost.Opacity = 1;
                return;
            }

            SettingsHost.Opacity = 0;
            SettingsHost.MaxHeight = 0;
            var storyboard = new Storyboard();
            var height = new DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(height, SettingsHost);
            Storyboard.SetTargetProperty(height, "MaxHeight");
            var opacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(140)
            };
            Storyboard.SetTarget(opacity, SettingsHost);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            storyboard.Children.Add(height);
            storyboard.Children.Add(opacity);
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_expandStoryboard, storyboard)) return;
                _expandStoryboard = null;
                if (_node?.IsExpanded != true) return;
                SettingsHost.Visibility = Visibility.Visible;
                SettingsHost.MaxHeight = double.PositiveInfinity;
                SettingsHost.Opacity = 1;
                // Conditional fields or localized text may have changed while
                // the height animation was running. Measure again now that
                // MaxHeight no longer constrains the content.
                RemeasureExpandedContent();
            };
            _expandStoryboard = storyboard;
            storyboard.Begin();
            return;
        }

        ExpandGlyph.Glyph = "\uE70D";
        _collapseGhostHeight = Math.Max(ExpandedContentHeight, SettingsHost.ActualHeight);
        if (!animate)
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            SettingsHost.MaxHeight = 0;
            SettingsHost.Opacity = 0;
            _collapseGhostHeight = 0;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var collapse = new Storyboard();
        var heightOut = new DoubleAnimation
        {
            From = SettingsHost.ActualHeight,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(heightOut, SettingsHost);
        Storyboard.SetTargetProperty(heightOut, "MaxHeight");
        var opacityOut = new DoubleAnimation
        {
            From = SettingsHost.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(110)
        };
        Storyboard.SetTarget(opacityOut, SettingsHost);
        Storyboard.SetTargetProperty(opacityOut, "Opacity");
        collapse.Children.Add(heightOut);
        collapse.Children.Add(opacityOut);
        collapse.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_expandStoryboard, collapse)) return;
            _expandStoryboard = null;
            _collapseGhostHeight = 0;
            if (_node?.IsExpanded == true) return;
            SettingsHost.Visibility = Visibility.Collapsed;
            SettingsHost.MaxHeight = 0;
            SettingsHost.Opacity = 0;
            // Release the obstacle only after the dropdown has finished
            // shrinking so other cards cannot move into visible content.
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
        _expandStoryboard = collapse;
        collapse.Begin();
    }

    private void BuildExpandedContent()
    {
        if (_node is null || _editor is null) return;
        SettingsHost.Padding = new Thickness(0, 0, 0, 12);
        switch (_node)
        {
            case PipelineActionNodeViewModel action:
            {
                var settings = new PipelineSettingsEditor();
                settings.Padding = new Thickness(12, 2, 12, 2);
                settings.Build(action, _editor);
                settings.LayoutChanged += OnSettingsLayoutChanged;
                SettingsHost.Content = settings;
                break;
            }
            case PipelineUnavailableNodeViewModel unavailable:
                SettingsHost.Content = BuildUnavailableContent(unavailable);
                break;
            case PipelineInputNodeViewModel input:
                SettingsHost.Content = BuildInputContent(input);
                break;
            case PipelineOutputNodeViewModel output:
                SettingsHost.Content = BuildOutputContent(output);
                break;
        }

        if (_node.IsExpanded && _expandStoryboard is null)
        {
            MeasureSettingsHost();
            ExpandedContentHeight = Math.Max(SettingsHost.DesiredSize.Height, 1);
            SettingsHost.MaxHeight = double.PositiveInfinity;
        }
    }

    private UIElement BuildUnavailableContent(PipelineUnavailableNodeViewModel node)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(12, 0, 12, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Pipeline.Validation.UnknownAction"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetBrush("DangerBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = node.UnavailableDetail,
            FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["AppMonoFontFamily"],
            FontSize = 11,
            Foreground = GetBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private UIElement BuildInputContent(PipelineInputNodeViewModel node)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(12, 0, 12, 0) };
        var dropZone = new Border
        {
            Background = GetBrush("SubtleSurfaceBrush"),
            BorderBrush = GetBrush("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE898", FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["IconFontFamily"],
                        FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = Loc.T("Drop files here"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center
                    }
                }
            }
        };
        panel.Children.Add(dropZone);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chooseFiles = new Button
        {
            Content = Loc.T("Choose files"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = ResourceStyle("SecondaryButtonStyle")
        };
        chooseFiles.Click += async (_, _) =>
        {
            if (await _editor!.ChooseInputFilesAsync()) BuildExpandedContent();
        };
        var chooseFolder = new Button
        {
            Content = Loc.T("Choose folder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = ResourceStyle("SecondaryButtonStyle")
        };
        chooseFolder.Click += async (_, _) =>
        {
            if (await _editor!.ChooseInputFolderAsync()) BuildExpandedContent();
        };
        Grid.SetColumn(chooseFolder, 1);
        actions.Children.Add(chooseFiles);
        actions.Children.Add(chooseFolder);
        panel.Children.Add(actions);

        var recursive = new ToggleSwitch
        {
            IsOn = node.Recursive,
            OnContent = Loc.T("Include subfolders"),
            OffContent = Loc.T("Include subfolders")
        };
        recursive.Toggled += (_, _) =>
        {
            node.Recursive = recursive.IsOn;
        };
        panel.Children.Add(recursive);

        if (node.Files.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("No files selected"),
                Foreground = GetBrush("TextFillColorSecondaryBrush"),
                FontSize = 12
            });
        }
        else
        {
            var list = new ItemsControl
            {
                ItemsSource = node.Files,
                ItemTemplate = Resources["PipelineInputFileTemplate"] as DataTemplate
            };
            panel.Children.Add(list);
            var clear = new Button
            {
                Content = Loc.T("Clear"),
                Style = ResourceStyle("SubtleButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            clear.Click += async (_, _) =>
            {
                await _editor!.ClearInputAsync();
                BuildExpandedContent();
            };
            panel.Children.Add(clear);
        }

        return panel;
    }

    private UIElement BuildOutputContent(PipelineOutputNodeViewModel node)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(12, 0, 12, 0) };
        panel.Children.Add(FieldLabel("Destination folder"));
        var folderGrid = new Grid { ColumnSpacing = 8 };
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var directory = new TextBox { Text = node.Directory, HorizontalAlignment = HorizontalAlignment.Stretch };
        directory.GotFocus += (_, _) => _editor!.RecordUndo("output:directory");
        directory.TextChanged += (_, _) =>
        {
            node.Directory = directory.Text;
        };
        var browse = new Button
        {
            Content = Loc.T("Browse"),
            Style = ResourceStyle("SecondaryButtonStyle")
        };
        browse.Click += async (_, _) =>
        {
            await node.ChooseDirectoryCommand.ExecuteAsync(null);
            directory.Text = node.Directory;
            BuildExpandedContent();
        };
        Grid.SetColumn(browse, 1);
        folderGrid.Children.Add(directory);
        folderGrid.Children.Add(browse);
        panel.Children.Add(folderGrid);

        panel.Children.Add(FieldLabel("File name suffix"));
        var suffix = new TextBox { Text = node.FileNameSuffix, PlaceholderText = "_pipeline" };
        suffix.GotFocus += (_, _) => _editor!.RecordUndo("output:suffix");
        suffix.TextChanged += (_, _) => node.FileNameSuffix = suffix.Text;
        panel.Children.Add(suffix);

        panel.Children.Add(FieldLabel("If a file already exists"));
        var collision = new ComboBox
        {
            ItemsSource = node.CollisionOptions,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = node.SelectedCollision,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        collision.SelectionChanged += (_, _) =>
        {
            if (collision.SelectedItem is Option<CollisionPolicy> option)
            {
                _editor!.RecordUndo("output:collision");
                node.SelectedCollision = option;
            }
        };
        panel.Children.Add(collision);

        var structure = new ToggleSwitch
        {
            IsOn = node.PreserveFolderStructure,
            OnContent = Loc.T("Preserve folder structure"),
            OffContent = Loc.T("Preserve folder structure")
        };
        structure.Toggled += (_, _) => node.PreserveFolderStructure = structure.IsOn;
        panel.Children.Add(structure);

        var keep = new ToggleSwitch
        {
            IsOn = node.KeepIntermediateFiles,
            OnContent = Loc.T("Keep intermediate files"),
            OffContent = Loc.T("Keep intermediate files")
        };
        keep.Toggled += (_, _) => node.KeepIntermediateFiles = keep.IsOn;
        panel.Children.Add(keep);

        if (node.IsFolderOutput)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("This pipeline produces folders. Intermediate suffixes and file formats are not used."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = GetBrush("TextFillColorSecondaryBrush")
            });
        }

        return panel;
    }

    private static TextBlock FieldLabel(string key)
    {
        var label = new TextBlock();
        Loc.SetText(label, key);
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("CaptionTextStyle", out var style))
            label.Style = style as Style;
        return label;
    }

    private void OnRemoveInputFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PipelineInputFileViewModel file } || _editor is null) return;
        _ = _editor.RemoveInputFileAsync(file).ContinueWith(_ =>
            DispatcherQueue.TryEnqueue(BuildExpandedContent));
    }

    private static Style? ResourceStyle(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var style) ? style as Style : null;

    private static Brush GetBrush(string key)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
