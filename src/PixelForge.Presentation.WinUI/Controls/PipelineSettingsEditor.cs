using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PixelForge.Application.Pipelines;
using Microsoft.UI.Text;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.ViewModels;

namespace PixelForge.Presentation.Controls;

/// <summary>
/// Builds the expanded node settings from the action registry. Keeping the
/// editor descriptor-driven means every operation uses the same validation and
/// summary code, and adding an action does not require another near-identical
/// XAML page.
/// </summary>
public sealed class PipelineSettingsEditor : StackPanel
{
    private bool _initializing;
    private PipelineActionNodeViewModel? _node;
    private PipelineEditorViewModel? _editor;

    /// <summary>
    /// Raised after the visible field set changes (a conditional field was
    /// added or removed). The owning node remeasures its expanded card so the
    /// canvas layout solver never works from a stale content height.
    /// </summary>
    public event EventHandler? LayoutChanged;

    public PipelineSettingsEditor()
    {
        Spacing = 10;
        Padding = new Thickness(0, 2, 0, 2);
    }

    public void Build(PipelineActionNodeViewModel node, PipelineEditorViewModel editor)
    {
        _node = node;
        _editor = editor;
        _initializing = true;
        Children.Clear();

        foreach (var field in node.Fields)
        {
            if (!IsVisible(field, node.Settings)) continue;
            Children.Add(BuildField(field, node, editor));
        }

        _initializing = false;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Refreshes localized option labels in place. Never rebuilds the editor:
    /// clearing and recreating ComboBoxes during a language change can write a
    /// transient null back through a TwoWay selection binding and blank it.
    /// </summary>
    public void RefreshLocalization()
    {
        RefreshLocalizedItems(this);
    }

    private static void RefreshLocalizedItems(DependencyObject root)
    {
        if (root is ComboBox combo)
        {
            var items = combo.ItemsSource as IEnumerable<object> ?? combo.Items;
            foreach (var item in items ?? [])
            {
                var method = item?.GetType().GetMethod("RefreshLocalization", Type.EmptyTypes);
                if (method is { } refresh && refresh.ReturnType == typeof(void))
                    refresh.Invoke(item, null);
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            RefreshLocalizedItems(VisualTreeHelper.GetChild(root, i));
    }

    private UIElement BuildField(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor)
    {
        var container = new StackPanel { Spacing = 4 };
        var label = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("CaptionTextStyle", out var style)
                ? style as Style
                : null
        };
        Loc.SetText(label, field.LabelKey);
        container.Children.Add(label);

        var value = GetProperty(node.Settings, field.PropertyName);
        switch (field.Kind)
        {
            case PipelineFieldKind.Text:
                container.Children.Add(BuildTextBox(field, node, editor, value));
                break;
            case PipelineFieldKind.Integer:
            case PipelineFieldKind.Decimal:
                container.Children.Add(BuildNumberBox(field, node, editor, value));
                break;
            case PipelineFieldKind.Slider:
                container.Children.Add(BuildSlider(field, node, editor, value));
                break;
            case PipelineFieldKind.Boolean:
                container.Children.Add(BuildToggle(field, node, editor, value is true));
                break;
            case PipelineFieldKind.Choice:
                container.Children.Add(BuildChoice(field, node, editor, value));
                break;
            case PipelineFieldKind.Format:
                container.Children.Add(BuildFormatChoice(field, node, editor, value));
                break;
            case PipelineFieldKind.VideoCodec:
            case PipelineFieldKind.AudioCodec:
                container.Children.Add(BuildCodecChoice(field, node, editor, value));
                break;
            case PipelineFieldKind.Model:
                container.Children.Add(BuildModelChoice(field, node, editor, value));
                break;
            case PipelineFieldKind.OcrModel:
                container.Children.Add(BuildOcrModelChoice(field, node, editor, value));
                break;
        }

        return container;
    }

    private TextBox BuildTextBox(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var box = new TextBox
        {
            Text = value?.ToString() ?? "",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (field.Placeholder is not null) Loc.SetPlaceholder(box, field.Placeholder);
        Loc.SetAutomationName(box, field.LabelKey);
        AutomationProperties.SetAutomationId(box, "PipelineField_" + field.PropertyName);
        box.GotFocus += (_, _) => editor.RecordSettingChange(node, field.PropertyName);
        box.TextChanged += (_, _) =>
        {
            if (_initializing) return;
            SetProperty(node.Settings, field.PropertyName, box.Text);
            node.NotifySettingsChanged();
        };
        return box;
    }

    private NumberBox BuildNumberBox(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var box = new NumberBox
        {
            Minimum = field.Minimum,
            Maximum = field.Maximum,
            SmallChange = field.Step <= 0 ? 1 : field.Step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (value is not null)
        {
            var parsed = Convert.ToDouble(value);
            if (!double.IsNaN(parsed)) box.Value = parsed;
        }
        else
        {
            box.Value = double.NaN;
        }

        Loc.SetAutomationName(box, field.LabelKey);
        AutomationProperties.SetAutomationId(box, "PipelineField_" + field.PropertyName);
        box.GotFocus += (_, _) => editor.RecordSettingChange(node, field.PropertyName);
        box.ValueChanged += (_, args) =>
        {
            if (_initializing) return;
            var property = node.Settings.GetType().GetProperty(field.PropertyName);
            if (property is null) return;
            object? converted;
            if (double.IsNaN(args.NewValue))
                converted = Nullable.GetUnderlyingType(property.PropertyType) is not null ? null : 0;
            else if (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
                converted = (int)Math.Round(args.NewValue);
            else if (property.PropertyType == typeof(long) || property.PropertyType == typeof(long?))
                converted = (long)Math.Round(args.NewValue);
            else if (property.PropertyType == typeof(float) || property.PropertyType == typeof(float?))
                converted = (float)args.NewValue;
            else
                converted = args.NewValue;
            try
            {
                property.SetValue(node.Settings, converted);
                node.NotifySettingsChanged();
                if (ShouldRebuild(field)) Rebuild();
            }
            catch
            {
                // A validator will report the value if it cannot be applied.
            }
        };
        return box;
    }

    private UIElement BuildSlider(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var slider = new Slider
        {
            Minimum = field.Minimum,
            Maximum = field.Maximum,
            StepFrequency = field.Step <= 0 ? 1 : field.Step,
            Value = value is null ? field.Minimum : Convert.ToDouble(value)
        };
        var readout = new TextBlock
        {
            Text = slider.Value.ToString("0.##"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32
        };
        Grid.SetColumn(readout, 1);
        slider.ValueChanged += (_, args) =>
        {
            readout.Text = args.NewValue.ToString("0.##");
            if (_initializing) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, args.NewValue);
            node.NotifySettingsChanged();
        };
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        return grid;
    }

    private ToggleSwitch BuildToggle(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, bool value)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = "",
            OffContent = ""
        };
        Loc.SetAutomationName(toggle, field.LabelKey);
        AutomationProperties.SetAutomationId(toggle, "PipelineField_" + field.PropertyName);
        toggle.Toggled += (_, _) =>
        {
            if (_initializing) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, toggle.IsOn);
            node.NotifySettingsChanged();
            if (ShouldRebuild(field)) Rebuild();
        };
        return toggle;
    }

    private ComboBox BuildChoice(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var options = field.Choices.Select(c => new Option<string>(c.Value, c.LabelKey)).ToList();
        var current = value?.ToString() ?? "";
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = options.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetAutomationName(combo, field.LabelKey);
        AutomationProperties.SetAutomationId(combo, "PipelineField_" + field.PropertyName);
        combo.SelectionChanged += (_, _) =>
        {
            if (_initializing || combo.SelectedItem is not Option<string> selected) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, selected.Value);
            node.NotifySettingsChanged();
            if (field.PropertyName is "Tool" || ShouldRebuild(field)) Rebuild();
        };
        return combo;
    }

    private ComboBox BuildFormatChoice(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var options = field.FormatCategory is { } category
            ? editor.Registry.GetOutputFormats(category)
                .Where(f => f.IsEnabled)
                .Select(f => new Option<string>(f.Format.Id, f.DisplayName))
                .ToList()
            : [];
        var current = value?.ToString() ?? "";
        if (!options.Any(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(current))
            options.Add(new Option<string>(current, current.ToUpperInvariant()));

        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = options.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetAutomationName(combo, field.LabelKey);
        AutomationProperties.SetAutomationId(combo, "PipelineField_" + field.PropertyName);
        combo.SelectionChanged += (_, _) =>
        {
            if (_initializing || combo.SelectedItem is not Option<string> selected) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, selected.Value);
            node.NotifySettingsChanged();
            if (ShouldRebuild(field)) Rebuild();
        };
        return combo;
    }

    private ComboBox BuildCodecChoice(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var container = GetStringProperty(node.Settings, field.DependsOnProperty ?? "ContainerFormatId");
        var codecs = field.Kind == PipelineFieldKind.VideoCodec
            ? editor.Registry.GetVideoCodecs(container)
            : editor.Registry.GetAudioCodecs(container, forVideo: true);
        var options = codecs.Where(c => c.IsAvailable || string.Equals(c.Id, value?.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(c => new Option<string>(c.Id, c.DisplayName))
            .ToList();
        var current = value?.ToString() ?? "";
        if (!options.Any(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(current))
            options.Insert(0, new Option<string>(current, current.ToUpperInvariant()));
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = options.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetAutomationName(combo, field.LabelKey);
        AutomationProperties.SetAutomationId(combo, "PipelineField_" + field.PropertyName);
        combo.SelectionChanged += (_, _) =>
        {
            if (_initializing || combo.SelectedItem is not Option<string> selected) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, selected.Value);
            node.NotifySettingsChanged();
        };
        return combo;
    }

    private ComboBox BuildModelChoice(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var models = editor.Models.AvailableModels
            .Where(m => field.ModelTask is null || m.Task == field.ModelTask)
            .Select(m => new Option<string>(m.Id, m.Name))
            .ToList();
        var current = value?.ToString() ?? "";
        if (!models.Any(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(current))
            models.Insert(0, new Option<string>(current, current));
        var combo = new ComboBox
        {
            ItemsSource = models,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = models.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase))
                           ?? models.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetAutomationName(combo, field.LabelKey);
        AutomationProperties.SetAutomationId(combo, "PipelineField_" + field.PropertyName);
        combo.SelectionChanged += (_, _) =>
        {
            if (_initializing || combo.SelectedItem is not Option<string> selected) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, selected.Value);
            node.NotifySettingsChanged();
        };
        return combo;
    }

    private ComboBox BuildOcrModelChoice(PipelineFieldDescriptor field, PipelineActionNodeViewModel node,
        PipelineEditorViewModel editor, object? value)
    {
        var models = editor.OcrModels.AvailableModels
            .Select(m => new Option<string>(m.Id, m.Name))
            .ToList();
        var current = value?.ToString() ?? "";
        if (!models.Any(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(current))
            models.Insert(0, new Option<string>(current, current));
        var combo = new ComboBox
        {
            ItemsSource = models,
            DisplayMemberPath = "DisplayLabel",
            SelectedItem = models.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase))
                           ?? models.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetAutomationName(combo, field.LabelKey);
        AutomationProperties.SetAutomationId(combo, "PipelineField_" + field.PropertyName);
        combo.SelectionChanged += (_, _) =>
        {
            if (_initializing || combo.SelectedItem is not Option<string> selected) return;
            editor.RecordSettingChange(node, field.PropertyName);
            SetProperty(node.Settings, field.PropertyName, selected.Value);
            node.NotifySettingsChanged();
        };
        return combo;
    }

    private bool ShouldRebuild(PipelineFieldDescriptor field) =>
        _node?.Fields.Any(f => string.Equals(f.DependsOnProperty, field.PropertyName,
            StringComparison.OrdinalIgnoreCase)) == true;

    private void Rebuild()
    {
        if (_node is not null && _editor is not null) Build(_node, _editor);
    }

    private static bool IsVisible(PipelineFieldDescriptor field, OperationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(field.DependsOnProperty) || field.VisibleWhenValues is null) return true;
        var value = GetProperty(settings, field.DependsOnProperty)?.ToString();
        return value is not null && field.VisibleWhenValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static object? GetProperty(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static string GetStringProperty(object target, string property) =>
        GetProperty(target, property)?.ToString() ?? "";

    private static void SetProperty(object target, string property, object? value)
    {
        var info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info is null || !info.CanWrite) return;
        try
        {
            if (value is null)
            {
                info.SetValue(target, null);
                return;
            }

            var type = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
            if (type.IsEnum)
                info.SetValue(target, Enum.Parse(type, value.ToString()!, ignoreCase: true));
            else if (type == typeof(string))
                info.SetValue(target, value.ToString());
            else if (type == typeof(double))
                info.SetValue(target, Convert.ToDouble(value));
            else if (type == typeof(float))
                info.SetValue(target, Convert.ToSingle(value));
            else if (type == typeof(int))
                info.SetValue(target, Convert.ToInt32(value));
            else if (type == typeof(long))
                info.SetValue(target, Convert.ToInt64(value));
            else if (type == typeof(bool))
                info.SetValue(target, Convert.ToBoolean(value));
            else
                info.SetValue(target, Convert.ChangeType(value, type));
        }
        catch
        {
            // Invalid intermediate input is validated when the user runs.
        }
    }
}
