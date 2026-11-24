using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PixelForge.Application.Pipelines;
using Microsoft.UI.Text;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using PixelForge.Presentation.ViewModels;
using Windows.Foundation;

namespace PixelForge.Presentation.Views;

public sealed partial class PipelinesPage : Page, ILocalizationAwarePage
{
    private bool _syncingName;
    private bool _drawerOpen;
    private PipelineActionCategory? _category;
    private string _actionQuery = "";

    // A Button consumes PointerPressed and therefore never promotes CanDrag
    // into a drag gesture in WinUI 3. The action cards are drag-only, so track
    // the pointer directly and move the card with it; this also makes the drop
    // position exactly the release position. The automation peer keeps the
    // card addressable by UI tests.
    private sealed class ActionDragEventArgs : EventArgs
    {
        public ActionDragEventArgs(string actionId, Point point)
        {
            ActionId = actionId;
            Point = point;
        }

        public string ActionId { get; }
        public Point Point { get; }
    }

    private sealed class ActionCardHost : ContentControl
    {
        private bool _pressed;
        private bool _dragging;
        private Point _pressPoint;
        private double _baseOpacity = 1;

        public string ActionId { get; set; } = "";
        public FrameworkElement? CoordinateRoot { get; set; }

        public event EventHandler<ActionDragEventArgs>? DragProgress;
        public event EventHandler<ActionDragEventArgs>? DragCompleted;
        public event EventHandler? DragCanceled;

        public ActionCardHost()
        {
            PointerEntered += (_, _) => { if (!_pressed) UpdateState("PointerOver"); };
            PointerExited += (_, _) => { if (!_pressed) UpdateState("Normal"); };
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCanceled += OnPointerCanceled;
            PointerCaptureLost += OnPointerCaptureLost;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            _pressed = true;
            _dragging = false;
            _pressPoint = point.Position;
            _baseOpacity = Opacity;
            CapturePointer(e.Pointer);
            UpdateState("Pressed");
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed) return;
            var local = e.GetCurrentPoint(this).Position;
            if (!_dragging)
            {
                var dx = local.X - _pressPoint.X;
                var dy = local.Y - _pressPoint.Y;
                if (dx * dx + dy * dy < 36) return;
                _dragging = true;
                Opacity = _baseOpacity * 0.65;
            }

            if (CoordinateRoot is not null)
            {
                var pagePoint = e.GetCurrentPoint(CoordinateRoot).Position;
                DragProgress?.Invoke(this, new ActionDragEventArgs(ActionId, pagePoint));
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var root = CoordinateRoot;
            var completed = _pressed && _dragging && root is not null;
            var pagePoint = completed ? e.GetCurrentPoint(root!).Position : default;
            Reset(e.Pointer);
            if (completed) DragCompleted?.Invoke(this, new ActionDragEventArgs(ActionId, pagePoint));
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            var wasDragging = _dragging;
            Reset(e.Pointer);
            if (wasDragging) DragCanceled?.Invoke(this, EventArgs.Empty);
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            var wasDragging = _dragging;
            _pressed = false;
            _dragging = false;
            Opacity = _baseOpacity;
            UpdateState("Normal");
            if (wasDragging) DragCanceled?.Invoke(this, EventArgs.Empty);
        }

        private void Reset(Pointer pointer)
        {
            _pressed = false;
            _dragging = false;
            Opacity = _baseOpacity;
            ReleasePointerCapture(pointer);
            UpdateState("Normal");
        }

        private void UpdateState(string state)
        {
            if (IsEnabled) VisualStateManager.GoToState(this, state, true);
        }

        protected override AutomationPeer OnCreateAutomationPeer() =>
            new FrameworkElementAutomationPeer(this);
    }

    public PipelinesPage()
    {
        ViewModel = App.Services.GetRequiredService<PipelinesViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ListChanged += OnListChanged;
        LocalizationPageRegistry.Register(this);
    }

    public PipelinesViewModel ViewModel { get; }

    public void RefreshLocalization()
    {
        ViewModel.RefreshLocalization();
        Canvas.RefreshLocalization();
        BuildCategoryBar();
        BuildActionLibrary();
        UpdateEditorStatus();
        if (_drawerOpen)
        {
            ActionDrawerBody.Measure(new Size(Math.Max(ActionDrawer.ActualWidth - 28, 480), double.PositiveInfinity));
            ActionDrawer.Height = Math.Clamp(
                12 + Math.Max(ActionDrawerToggle.ActualHeight, 32) + 6 + ActionDrawerBody.DesiredSize.Height + 2,
                140, 320);
        }
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await GuardAsync(async () =>
        {
            await ViewModel.LoadAsync();
            if (ViewModel.Editor is not null)
                ShowEditor();
            else
                ShowManagement();
            UpdateEmptyState();
        });
    }

    protected override async void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (ViewModel.Editor is null) return;
        try
        {
            await ViewModel.Editor.FlushAsync();
        }
        catch
        {
            // Save errors are reported by the editor.
        }
    }

    private void OnListChanged(object? sender, EventArgs e)
    {
        if (ManagementView.Visibility == Visibility.Visible) UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        PipelineEmptyState.Visibility = ViewModel.FilteredPipelines.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowManagement()
    {
        Canvas.Detach();
        if (ViewModel.Editor is not null)
        {
            ViewModel.Editor.GraphChanged -= OnEditorGraphChanged;
            ViewModel.Editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        EditorView.Visibility = Visibility.Collapsed;
        ManagementView.Visibility = Visibility.Visible;
        UpdateEmptyState();
        NewPipelineButton.IsEnabled = true;
    }

    private void ShowEditor()
    {
        var editor = ViewModel.Editor;
        if (editor is null) return;
        ManagementView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        _syncingName = true;
        PipelineNameBox.Text = editor.Name;
        _syncingName = false;
        editor.GraphChanged -= OnEditorGraphChanged;
        editor.GraphChanged += OnEditorGraphChanged;
        editor.PropertyChanged -= OnEditorPropertyChanged;
        editor.PropertyChanged += OnEditorPropertyChanged;
        Canvas.Attach(editor);
        BuildCategoryBar();
        BuildActionLibrary();
        UpdateEditorStatus();
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Delete || ViewModel.Editor is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or NumberBox or ComboBox or Slider or ToggleSwitch) return;
        if (ViewModel.Editor.SelectedNode is null && ViewModel.Editor.SelectedConnection is null) return;
        if (ViewModel.Editor.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.Editor.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnBackClick(object sender, RoutedEventArgs e) => await GuardAsync(GoBackAsync);

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ViewModel.ReportErrorAsync(ex);
        }
    }

    private async Task GoBackAsync()
    {
        await ViewModel.CloseEditorAsync();
        ShowManagement();
    }

    private async void OnOpenPipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is not Button { Tag: PipelineListItemViewModel item }) return;
            var editor = await ViewModel.OpenAsync(item);
            if (editor is not null) ShowEditor();
        });

    private async void OnRunPipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is not Button { Tag: PipelineListItemViewModel item }) return;
            await ViewModel.RunAsync(item);
        });

    private async void OnRenamePipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is not Button { Tag: PipelineListItemViewModel item }) return;
            var box = new TextBox { Text = item.Name, MinWidth = 320 };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.T("Rename pipeline"),
                PrimaryButtonText = Loc.T("Rename"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = box
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) await ViewModel.RenameAsync(item, box.Text);
        });

    private async void OnDuplicatePipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is Button { Tag: PipelineListItemViewModel item })
                await ViewModel.DuplicateAsync(item);
        });

    private async void OnDeletePipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is Button { Tag: PipelineListItemViewModel item })
                await ViewModel.DeleteAsync(item);
            if (!ViewModel.IsEditing) ShowManagement();
        });

    private async void OnUseTemplateClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            if (sender is not Button { Tag: PipelineTemplateItemViewModel template }) return;
            var editor = await ViewModel.CreatePipelineAsync(Loc.T(template.Template.NameKey), template.Template.Id);
            if (editor is not null) ShowEditor();
        });

    private async void OnNewPipelineClick(object sender, RoutedEventArgs e) =>
        await GuardAsync(async () =>
        {
            var name = new TextBox { Text = Loc.T("Untitled pipeline"), MinWidth = 340 };
            AutomationProperties.SetAutomationId(name, "NewPipelineNameBox");
            AutomationProperties.SetName(name, Loc.T("Name"));
            var templates = new ComboBox { MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(templates, "NewPipelineTemplateBox");
            AutomationProperties.SetName(templates, Loc.T("Template"));
            templates.Items.Add(new ComboBoxItem { Content = Loc.T("Empty pipeline"), Tag = null });
            foreach (var template in ViewModel.TemplateItems)
                templates.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template.Template.Id });
            templates.SelectedIndex = 0;

            var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
            panel.Children.Add(new TextBlock { Text = Loc.T("Name"), FontSize = 12 });
            panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = Loc.T("Template"), FontSize = 12 });
            panel.Children.Add(templates);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.T("New pipeline"),
                PrimaryButtonText = Loc.T("Create"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = panel
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                await GuardAsync(async () =>
                {
                    var templateId = (templates.SelectedItem as ComboBoxItem)?.Tag as string;
                    var editor = await ViewModel.CreatePipelineAsync(name.Text, templateId);
                    if (editor is null) return;
                    dialog.Hide();
                    ShowEditor();
                });
            };
            await dialog.ShowAsync();
        });

    private void OnPipelineSearchChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = PipelineSearch.Text;
        ViewModel.ApplyFilter();
        UpdateEmptyState();
    }

    private void OnPipelineNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingName || ViewModel.Editor is null) return;
        ViewModel.Editor.SetName(PipelineNameBox.Text);
    }

    private void OnActionDrawerToggle(object sender, RoutedEventArgs e)
    {
        _drawerOpen = !_drawerOpen;
        ActionDrawerBody.Visibility = _drawerOpen ? Visibility.Visible : Visibility.Collapsed;
        ActionDrawerGlyph.Glyph = _drawerOpen ? "\uE70D" : "\uE70E";
        if (_drawerOpen)
        {
            BuildActionLibrary();
            ActionDrawerBody.Measure(new Size(Math.Max(ActionDrawer.ActualWidth - 28, 480), double.PositiveInfinity));
        }

        var target = _drawerOpen
            ? Math.Clamp(12 + Math.Max(ActionDrawerToggle.ActualHeight, 32) + 6 + ActionDrawerBody.DesiredSize.Height + 2, 140, 320)
            : 48;
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = ActionDrawer.Height,
            To = target,
            Duration = TimeSpan.FromMilliseconds(160),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, ActionDrawer);
        Storyboard.SetTargetProperty(animation, "Height");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!_drawerOpen) ActionDrawerBody.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private void OnActionSearchChanged(object sender, TextChangedEventArgs e)
    {
        _actionQuery = ActionSearch.Text;
        BuildActionLibrary();
    }

    private void OnActionCardDragProgress(object? sender, ActionDragEventArgs e)
    {
        var local = Canvas.TransformToVisual(PipelinesRoot).TransformPoint(e.Point);
        if (IsInsideCanvas(local)) Canvas.ShowActionDropIndicator(local);
        else Canvas.HideActionDropIndicator();
    }

    private async void OnActionCardDragCompleted(object? sender, ActionDragEventArgs e)
    {
        Canvas.HideActionDropIndicator();
        var local = Canvas.TransformToVisual(PipelinesRoot).TransformPoint(e.Point);
        if (!IsInsideCanvas(local)) return;
        await Canvas.InsertActionFromPaletteAsync(e.ActionId, local);
    }

    private void OnActionCardDragCanceled(object? sender, EventArgs e) =>
        Canvas.HideActionDropIndicator();

    private bool IsInsideCanvas(Point local) =>
        local.X >= 0 && local.Y >= 0 &&
        local.X <= Canvas.ActualWidth && local.Y <= Canvas.ActualHeight;

    private void OnEditorGraphChanged(object? sender, EventArgs e)
    {
        // The palette is only visible while the drawer is open. Rebuilding all
        // action cards on every graph/status event was a major pan-start and
        // validation hitch; the toggle rebuilds on open.
        if (_drawerOpen) BuildActionLibrary();
        UpdateEditorStatus();
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PipelineEditorViewModel.IsValid) or
            nameof(PipelineEditorViewModel.ValidationSummary) or
            nameof(PipelineEditorViewModel.PrimaryValidationMessage))
            UpdateEditorStatus();
        if (e.PropertyName is nameof(PipelineEditorViewModel.Name) && !_syncingName &&
            ViewModel.Editor is not null && PipelineNameBox.Text != ViewModel.Editor.Name)
        {
            _syncingName = true;
            PipelineNameBox.Text = ViewModel.Editor.Name;
            _syncingName = false;
        }
    }

    private void UpdateEditorStatus()
    {
        var editor = ViewModel.Editor;
        if (editor is null)
        {
            EditorValidationBar.IsOpen = false;
            return;
        }

        EditorValidationBar.IsOpen = !string.IsNullOrWhiteSpace(editor.PrimaryValidationMessage);
        EditorValidationBar.Message = editor.PrimaryValidationMessage;
        EditorValidationBar.Severity = editor.IsValid
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Error;
    }

    private void BuildCategoryBar()
    {
        ActionCategoryBar.Children.Clear();
        ActionCategoryBar.Children.Add(CreateCategoryButton(null, "All"));
        foreach (var category in Enum.GetValues<PipelineActionCategory>())
            ActionCategoryBar.Children.Add(CreateCategoryButton(category, CategoryKey(category)));
    }

    private Button CreateCategoryButton(PipelineActionCategory? category, string labelKey)
    {
        var button = new Button
        {
            Content = Loc.T(labelKey),
            Style = Microsoft.UI.Xaml.Application.Current.Resources["SubtleButtonStyle"] as Style,
            Padding = new Thickness(8, 3, 8, 3),
            Height = 26
        };
        if (_category == category)
            button.Background = Microsoft.UI.Xaml.Application.Current.Resources["AccentSubtleBrush"] as Brush;
        button.Click += (_, _) =>
        {
            _category = category;
            BuildCategoryBar();
            BuildActionLibrary();
        };
        return button;
    }

    private void BuildActionLibrary()
    {
        var editor = ViewModel.Editor;
        if (editor is null) return;
        ActionCards.Children.Clear();
        var query = _actionQuery.Trim();
        foreach (var action in PipelineActionRegistry.All)
        {
            if (_category is not null && action.Category != _category) continue;
            var title = Loc.T(action.TitleKey);
            var description = Loc.T(action.DescriptionKey);
            if (!string.IsNullOrWhiteSpace(query) &&
                !title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !description.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var compatible = editor.CanAddAction(action);
            var card = new ActionCardHost
            {
                ActionId = action.Id,
                CoordinateRoot = PipelinesRoot,
                Width = 196,
                MinHeight = 72,
                Padding = new Thickness(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = Microsoft.UI.Xaml.Application.Current.Resources["ActionCardStyle"] as Style,
                IsEnabled = compatible,
                Opacity = compatible ? 1 : 0.48,
                Tag = action.Id,
                ManipulationMode = ManipulationModes.None
            };
            card.DragProgress += OnActionCardDragProgress;
            card.DragCompleted += OnActionCardDragCompleted;
            card.DragCanceled += OnActionCardDragCanceled;
            AutomationProperties.SetAutomationId(card, "PipelineAction_" + action.Id);
            AutomationProperties.SetName(card, title);
            if (!compatible)
                ToolTipService.SetToolTip(card, Loc.F("{0} is not compatible with the current media type.", title));
            else
                ToolTipService.SetToolTip(card, Loc.T("Drag actions onto the canvas to add them."));
            var content = new StackPanel { Spacing = 2 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new FontIcon
            {
                Glyph = action.IconGlyph,
                FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["IconFontFamily"],
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(header);
            content.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 10,
                Foreground = Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
            });
            if (action.RequiresModel)
                content.Children.Add(new TextBlock
                {
                    Text = Loc.T("Requires a downloaded model"),
                    FontSize = 9,
                    Foreground = Microsoft.UI.Xaml.Application.Current.Resources["WarningBrush"] as Brush
                });
            card.Content = content;
            ActionCards.Children.Add(card);
        }

        var dragHint = Loc.T("Drag actions onto the canvas to add them.");
        ActionInsertionHint.Text = editor.InputTypeAtEnd == Domain.Models.PipelineMediaType.Unknown
            ? dragHint
            : Loc.F("{0} · {1}", dragHint,
                Loc.F("Compatible with {0}", Loc.T(TypeLabel(editor.InputTypeAtEnd))));
    }

    private static string TypeLabel(Domain.Models.PipelineMediaType type) => type switch
    {
        Domain.Models.PipelineMediaType.Image => "Images",
        Domain.Models.PipelineMediaType.Audio => "Audio",
        Domain.Models.PipelineMediaType.Video => "Video",
        Domain.Models.PipelineMediaType.Document => "Documents",
        Domain.Models.PipelineMediaType.Ebook => "E-books",
        Domain.Models.PipelineMediaType.Archive => "Archives",
        Domain.Models.PipelineMediaType.Folder => "Folders",
        _ => "Output"
    };

    private static string CategoryKey(PipelineActionCategory category) => category switch
    {
        PipelineActionCategory.Images => "Images",
        PipelineActionCategory.Upscaling => "Upscaling",
        PipelineActionCategory.Video => "Video",
        PipelineActionCategory.Audio => "Audio",
        PipelineActionCategory.Documents => "Documents",
        PipelineActionCategory.Archives => "Archives",
        _ => "Utilities"
    };
}
