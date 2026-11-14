using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelForge.Presentation.ViewModels;
using Windows.System;

namespace PixelForge.Presentation.Controls;

public sealed partial class FormatPicker : UserControl
{
    private readonly ObservableCollection<FormatItemViewModel> _filtered = [];

    public FormatPicker()
    {
        InitializeComponent();
        FormatList.ItemsSource = _filtered;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<FormatItemViewModel>), typeof(FormatPicker),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(FormatItemViewModel), typeof(FormatPicker),
        new PropertyMetadata(null, OnSelectedItemChanged));

    public static readonly DependencyProperty AutomationIdProperty = DependencyProperty.Register(
        nameof(AutomationId), typeof(string), typeof(FormatPicker), new PropertyMetadata(null, OnAutomationIdChanged));

    public IEnumerable<FormatItemViewModel>? ItemsSource
    {
        get => (IEnumerable<FormatItemViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public FormatItemViewModel? SelectedItem
    {
        get => (FormatItemViewModel?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string? AutomationId
    {
        get => (string?)GetValue(AutomationIdProperty);
        set => SetValue(AutomationIdProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FormatPicker picker) picker.RefreshFilter();
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FormatPicker picker) picker.UpdateDisplay();
    }

    private static void OnAutomationIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FormatPicker picker && e.NewValue is string id)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker.PickerButton, id);
    }

    private void UpdateDisplay()
    {
        var item = SelectedItem;
        if (item is null)
        {
            ExtensionText.Text = "—";
            NameText.Text = "Choose a format";
            HintText.Text = "Searchable format catalogue";
            return;
        }

        ExtensionText.Text = item.ExtensionUpper;
        NameText.Text = item.DisplayName;
        HintText.Text = string.IsNullOrWhiteSpace(item.Badges)
            ? $"{item.Extension} · {item.CategoryText}"
            : $"{item.Extension} · {item.Badges}";
    }

    private void RefreshFilter()
    {
        _filtered.Clear();
        if (ItemsSource is null) return;
        foreach (var item in ItemsSource) _filtered.Add(item);
        NoResultsText.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FormatList.Visibility = _filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (SelectedItem is not null)
            FormatList.SelectedItem = _filtered.FirstOrDefault(f => f.Id == SelectedItem.Id);
        UpdateDisplay();
    }

    private void OnFlyoutOpening(object? sender, object e)
    {
        SearchBox.Text = "";
        RefreshFilter();
        if (SelectedItem is not null)
        {
            var selected = _filtered.FirstOrDefault(f => f.Id == SelectedItem.Id);
            FormatList.SelectedItem = selected;
            if (selected is not null) FormatList.ScrollIntoView(selected);
        }
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    private void OnFlyoutClosed(object? sender, object e)
    {
        SearchBox.Text = "";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();
        _filtered.Clear();
        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                if (string.IsNullOrWhiteSpace(query) || item.SearchText.Contains(query, StringComparison.Ordinal))
                    _filtered.Add(item);
            }
        }

        NoResultsText.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FormatList.Visibility = _filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_filtered.Count > 0) FormatList.SelectedIndex = 0;
    }

    private void OnFormatClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FormatItemViewModel item)
        {
            Choose(item);
        }
    }

    private void Choose(FormatItemViewModel item)
    {
        SelectedItem = item;
        PickerFlyout.Hide();
        PickerButton.Focus(FocusState.Programmatic);
    }

    private void OnFormatListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FormatList.SelectedItem is FormatItemViewModel item)
        {
            Choose(item);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            PickerFlyout.Hide();
            e.Handled = true;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && _filtered.Count > 0)
        {
            FormatList.SelectedIndex = 0;
            FormatList.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && _filtered.Count > 0)
        {
            Choose(FormatList.SelectedItem as FormatItemViewModel ?? _filtered[0]);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            PickerFlyout.Hide();
            e.Handled = true;
        }
    }
}
