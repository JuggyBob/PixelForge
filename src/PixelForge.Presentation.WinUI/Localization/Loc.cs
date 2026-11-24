using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace PixelForge.Presentation.Localization;

/// <summary>
/// Attached localization properties for XAML. The assigned value is the English
/// source string; the runtime catalog supplies the selected language and every
/// registered value is refreshed when the language changes.
/// </summary>
public static class Loc
{
    private enum TargetKind
    {
        Text,
        Content,
        Header,
        Placeholder,
        ToolTip,
        AutomationName,
        HelpText,
        Title,
        Message,
        Label
    }

    private sealed class Entry
    {
        public required WeakReference<DependencyObject> Target { get; init; }
        public required TargetKind Kind { get; init; }
        public required string Source { get; init; }
    }

    private static readonly List<Entry> Entries = [];
    private static readonly object Gate = new();

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Loc), new PropertyMetadata(null, OnTextChanged));
    public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
        "Content", typeof(string), typeof(Loc), new PropertyMetadata(null, OnContentChanged));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached(
        "Header", typeof(string), typeof(Loc), new PropertyMetadata(null, OnHeaderChanged));
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Loc), new PropertyMetadata(null, OnPlaceholderChanged));
    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(string), typeof(Loc), new PropertyMetadata(null, OnToolTipChanged));
    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.RegisterAttached(
        "AutomationName", typeof(string), typeof(Loc), new PropertyMetadata(null, OnAutomationNameChanged));
    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.RegisterAttached(
        "HelpText", typeof(string), typeof(Loc), new PropertyMetadata(null, OnHelpTextChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(Loc), new PropertyMetadata(null, OnTitleChanged));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(string), typeof(Loc), new PropertyMetadata(null, OnMessageChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(Loc), new PropertyMetadata(null, OnLabelChanged));

    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);
    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetContent(DependencyObject target, string? value) => target.SetValue(ContentProperty, value);
    public static string? GetContent(DependencyObject target) => (string?)target.GetValue(ContentProperty);
    public static void SetHeader(DependencyObject target, string? value) => target.SetValue(HeaderProperty, value);
    public static string? GetHeader(DependencyObject target) => (string?)target.GetValue(HeaderProperty);
    public static void SetPlaceholder(DependencyObject target, string? value) => target.SetValue(PlaceholderProperty, value);
    public static string? GetPlaceholder(DependencyObject target) => (string?)target.GetValue(PlaceholderProperty);
    public static void SetToolTip(DependencyObject target, string? value) => target.SetValue(ToolTipProperty, value);
    public static string? GetToolTip(DependencyObject target) => (string?)target.GetValue(ToolTipProperty);
    public static void SetAutomationName(DependencyObject target, string? value) => target.SetValue(AutomationNameProperty, value);
    public static string? GetAutomationName(DependencyObject target) => (string?)target.GetValue(AutomationNameProperty);
    public static void SetHelpText(DependencyObject target, string? value) => target.SetValue(HelpTextProperty, value);
    public static string? GetHelpText(DependencyObject target) => (string?)target.GetValue(HelpTextProperty);
    public static void SetTitle(DependencyObject target, string? value) => target.SetValue(TitleProperty, value);
    public static string? GetTitle(DependencyObject target) => (string?)target.GetValue(TitleProperty);
    public static void SetMessage(DependencyObject target, string? value) => target.SetValue(MessageProperty, value);
    public static string? GetMessage(DependencyObject target) => (string?)target.GetValue(MessageProperty);
    public static void SetLabel(DependencyObject target, string? value) => target.SetValue(LabelProperty, value);
    public static string? GetLabel(DependencyObject target) => (string?)target.GetValue(LabelProperty);

    public static string T(string? source) => LocalizationService.T(source);
    public static string F(string source, params object?[] args) => LocalizationService.F(source, args);
    public static string P(string key, int count, params object?[] args) => LocalizationService.P(key, count, args);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Text, e.NewValue as string);
    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Content, e.NewValue as string);
    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Header, e.NewValue as string);
    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Placeholder, e.NewValue as string);
    private static void OnToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.ToolTip, e.NewValue as string);
    private static void OnAutomationNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.AutomationName, e.NewValue as string);
    private static void OnHelpTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.HelpText, e.NewValue as string);
    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Title, e.NewValue as string);
    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Message, e.NewValue as string);
    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Register(d, TargetKind.Label, e.NewValue as string);

    private static void Register(DependencyObject target, TargetKind kind, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        Apply(target, kind, source);
        lock (Gate)
        {
            Entries.RemoveAll(entry =>
            {
                if (!entry.Target.TryGetTarget(out var existing)) return true;
                return ReferenceEquals(existing, target) && entry.Kind == kind;
            });
            Entries.Add(new Entry { Target = new WeakReference<DependencyObject>(target), Kind = kind, Source = source });
        }
    }

    internal static void RefreshAll()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(RefreshAll);
            return;
        }

        List<Entry> snapshot;
        lock (Gate) snapshot = Entries.ToList();
        foreach (var entry in snapshot)
        {
            if (entry.Target.TryGetTarget(out var target))
                Apply(target, entry.Kind, entry.Source);
        }

        lock (Gate)
            Entries.RemoveAll(entry => !entry.Target.TryGetTarget(out _));
    }

    private static void Apply(DependencyObject target, TargetKind kind, string source)
    {
        var value = LocalizationService.T(source);
        switch (kind)
        {
            case TargetKind.Text:
                if (target is TextBlock textBlock) textBlock.Text = value;
                else if (target is Run run) run.Text = value;
                else SetProperty(target, "Text", value);
                break;
            case TargetKind.Content:
                if (target is ContentControl contentControl) contentControl.Content = value;
                else SetProperty(target, "Content", value);
                break;
            case TargetKind.Header:
                SetProperty(target, "Header", value);
                break;
            case TargetKind.Placeholder:
                SetProperty(target, "PlaceholderText", value);
                break;
            case TargetKind.ToolTip:
                if (target is FrameworkElement element)
                    ToolTipService.SetToolTip(element, value);
                break;
            case TargetKind.AutomationName:
                AutomationProperties.SetName(target, value);
                break;
            case TargetKind.HelpText:
                AutomationProperties.SetHelpText(target, value);
                break;
            case TargetKind.Title:
                SetProperty(target, "Title", value);
                break;
            case TargetKind.Message:
                SetProperty(target, "Message", value);
                break;
            case TargetKind.Label:
                SetProperty(target, "Label", value);
                break;
        }
    }

    private static void SetProperty(DependencyObject target, string propertyName, string value)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite) return;
        var type = property.PropertyType;
        if (type != typeof(object) && type != typeof(string)) return;
        try
        {
            property.SetValue(target, value);
        }
        catch
        {
            // A control template can make a normal property read-only at runtime.
        }
    }
}
