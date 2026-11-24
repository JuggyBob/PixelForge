using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using PixelForge.Presentation.ViewModels;
using System.ComponentModel;
using Windows.Foundation;
using PixelForge.Presentation.Localization;

namespace PixelForge.Presentation.Views;

public sealed partial class WatchFoldersPage : Page, ILocalizationAwarePage
{
    private readonly Dictionary<WatchFolderItemViewModel, ScrollViewer> _editorClips = [];
    private readonly Dictionary<WatchFolderItemViewModel, PropertyChangedEventHandler> _editorHandlers = [];

    public WatchFoldersViewModel ViewModel { get; }

    public void RefreshLocalization() => ViewModel.RefreshLocalization();

    public WatchFoldersPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchFoldersViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        LocalizationPageRegistry.Register(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.Services.GetService<PixelForge.Domain.Interfaces.IAppLogger>()?
                .LogError(ex, "Watch folders page initialization failed");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private void EditorClip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer clip || clip.DataContext is not WatchFolderItemViewModel item) return;
        _editorClips[item] = clip;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(WatchFolderItemViewModel.IsEditing))
                AnimateEditor(item, animate: true);
        };
        _editorHandlers[item] = handler;
        item.PropertyChanged += handler;
        AnimateEditor(item, animate: false);
    }

    private void EditorClip_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer clip || clip.DataContext is not WatchFolderItemViewModel item) return;
        if (_editorHandlers.Remove(item, out var handler)) item.PropertyChanged -= handler;
        _editorClips.Remove(item);
    }

    private void AnimateEditor(WatchFolderItemViewModel item, bool animate)
    {
        if (!_editorClips.TryGetValue(item, out var clip) || clip.Content is not FrameworkElement content) return;
        var expanded = item.IsEditing;
        clip.Visibility = Visibility.Visible;
        clip.IsHitTestVisible = expanded;
        var width = clip.ActualWidth;
        if (width <= 0) width = (clip.Parent as FrameworkElement)?.ActualWidth ?? 0;
        if (width <= 0) width = WatchRuleList.ActualWidth;
        if (width <= 0) width = 720;
        content.Measure(new Size(width, double.PositiveInfinity));
        var target = expanded ? Math.Max(1, content.DesiredSize.Height) : 0;
        var from = expanded
            ? 0
            : double.IsNaN(clip.Height) ? content.ActualHeight : clip.Height;

        if (!animate)
        {
            clip.Height = expanded ? double.NaN : 0;
            clip.Opacity = expanded ? 1 : 0;
            if (!expanded) clip.Visibility = Visibility.Collapsed;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(expanded ? 220 : 180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation { From = from, To = target, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        var opacity = new DoubleAnimation { From = clip.Opacity, To = expanded ? 1 : 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(height, clip);
        Storyboard.SetTargetProperty(height, "Height");
        Storyboard.SetTarget(opacity, clip);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            clip.Opacity = expanded ? 1 : 0;
            if (expanded)
            {
                clip.Height = double.NaN;
            }
            else
            {
                clip.Height = 0;
                clip.Visibility = Visibility.Collapsed;
            }
        };
        storyboard.Begin();
    }

    private void RuleRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is Button or ToggleSwitch or TextBox) return;
            }
        }

        if ((sender as FrameworkElement)?.DataContext is WatchFolderItemViewModel item)
            item.ToggleEditCommand.Execute(null);
    }
}
