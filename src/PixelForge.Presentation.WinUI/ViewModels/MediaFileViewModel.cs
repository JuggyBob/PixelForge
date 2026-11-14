using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Converters;
using System.Collections.ObjectModel;

namespace PixelForge.Presentation.ViewModels;

public sealed class FormatItemViewModel
{
    public FormatItemViewModel(MediaFormat format)
    {
        Format = format;
    }

    public MediaFormat Format { get; }
    public string Id => Format.Id;
    public string Extension => Format.NormalizedExtension;
    public string ExtensionUpper => Format.NormalizedExtension.TrimStart('.').ToUpperInvariant();
    public string DisplayName => Format.DisplayName;
    public MediaCategory Category => Format.Category;
    public bool IsLossy => Format.IsLossy;
    public bool SupportsAlpha => Format.SupportsAlpha;
    public bool SupportsAnimation => Format.SupportsAnimation;
    public string CategoryText => Category switch
    {
        MediaCategory.Image => "Image",
        MediaCategory.Audio => "Audio",
        MediaCategory.Video => "Video",
        _ => "Media"
    };
    public string Badges
    {
        get
        {
            var parts = new List<string>();
            if (SupportsAlpha) parts.Add("alpha");
            if (SupportsAnimation) parts.Add("animated");
            if (IsLossy) parts.Add("lossy");
            return string.Join(" · ", parts);
        }
    }
    public string SearchText => string.Join(' ', new[]
    {
        Format.Id,
        Format.NormalizedExtension.TrimStart('.'),
        Format.DisplayName,
        CategoryText
    }.Concat(Format.Aliases)).ToLowerInvariant();
}

public sealed class CollisionOption
{
    public CollisionOption(CollisionPolicy policy, string label, string description)
    {
        Policy = policy;
        Label = label;
        Description = description;
    }

    public CollisionPolicy Policy { get; }
    public string Label { get; }
    public string Description { get; }
}

public sealed record ValidationIssueViewModel(string Message, ValidationSeverity Severity)
{
    public bool IsError => Severity == ValidationSeverity.Error;
    public string Glyph => Severity switch
    {
        ValidationSeverity.Error => "\uE783",
        ValidationSeverity.Warning => "\uE7BA",
        _ => "\uE946"
    };
}

public partial class MediaFileViewModel : ObservableObject
{
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<MediaFileViewModel> _remove;
    private bool _thumbnailRequested;

    public MediaFileViewModel(MediaInfo info, IThumbnailService thumbnails, Action<MediaFileViewModel> remove,
        string? relativeDirectory = null)
    {
        Info = info;
        _thumbnails = thumbnails;
        _remove = remove;
        RelativeDirectory = relativeDirectory;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = LoadThumbnailAsync();
    }

    public MediaInfo Info { get; }
    public string? RelativeDirectory { get; }
    public string DisplayPath => string.IsNullOrWhiteSpace(RelativeDirectory)
        ? FileName
        : Path.Combine(RelativeDirectory, FileName);
    public string FilePath => Info.FilePath;
    public string FileName => Info.FileName;
    public string Extension => Info.Extension;
    public string FormatText => Info.FormatName ?? Info.FormatId?.ToUpperInvariant() ?? Info.Extension.TrimStart('.').ToUpperInvariant();
    public string Dimensions => Info.Width is > 0 && Info.Height is > 0 ? $"{Info.Width} × {Info.Height}" : "";
    public string SizeText => FileSizeConverter.FormatBytes(Info.FileSizeBytes);
    public string DurationText => Info.DurationSeconds is > 0 ? TimeSpan.FromSeconds(Info.DurationSeconds.Value).ToString(@"m\:ss") : "";
    public bool IsImage => Info.Category == MediaCategory.Image;
    public bool IsAudio => Info.Category == MediaCategory.Audio;
    public bool IsVideo => Info.Category == MediaCategory.Video;
    public bool HasAlpha => Info.HasAlpha;
    public bool IsAnimated => Info.IsAnimated;
    public bool IsMultipage => Info.IsMultipage;
    public string IconGlyph => Info.Category switch
    {
        MediaCategory.Image => "\uE91B",
        MediaCategory.Audio => "\uE8D6",
        MediaCategory.Video => "\uE714",
        _ => "\uE8B7"
    };
    public bool HasBadge => Info.IsAnimated || Info.IsMultipage || Info.HasAlpha || Info.IsVector;
    public string BadgeText
    {
        get
        {
            var parts = new List<string>();
            if (Info.IsAnimated) parts.Add("animated");
            if (Info.IsMultipage) parts.Add($"{Info.FrameCount ?? 0} pages");
            if (Info.HasAlpha) parts.Add("alpha");
            if (Info.IsVector) parts.Add("vector");
            return string.Join(" · ", parts);
        }
    }
    public bool HasError => !Info.IsReadable;
    public string ErrorText => Info.Error ?? "";

    public string Detail
    {
        get
        {
            return Info.Category switch
            {
                MediaCategory.Image => $"{FormatText} · {Dimensions} · {SizeText}",
                MediaCategory.Audio => $"{FormatText} · {Info.AudioCodec ?? "audio"} · {SampleRateText} · {ChannelText} · {DurationText}",
                MediaCategory.Video => $"{FormatText} · {Info.VideoCodec ?? "video"}/{Info.AudioCodec ?? "no audio"} · {Dimensions} · {FrameRateText} · {DurationText}",
                _ => $"{FormatText} · {SizeText}"
            };
        }
    }

    private string SampleRateText => Info.SampleRate is > 0 ? $"{Info.SampleRate / 1000.0:0.#} kHz" : "—";
    private string ChannelText => Info.Channels switch
    {
        1 => "mono",
        2 => "stereo",
        > 2 => $"{Info.Channels} ch",
        _ => "—"
    };
    private string FrameRateText => Info.FrameRate is > 0 ? $"{Info.FrameRate:0.##} fps" : "—";

    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _hasThumbnail;

    private async Task LoadThumbnailAsync()
    {
        if (_thumbnailRequested || IsAudio || !Info.IsReadable) return;
        _thumbnailRequested = true;
        try
        {
            var path = await _thumbnails.GetThumbnailPathAsync(Info.FilePath, Info.Category, 112);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var bitmap = new BitmapImage { UriSource = new Uri(path) };
            if (_dispatcher.HasThreadAccess)
            {
                Thumbnail = bitmap;
                HasThumbnail = true;
            }
            else
            {
                _dispatcher.TryEnqueue(() =>
                {
                    Thumbnail = bitmap;
                    HasThumbnail = true;
                });
            }
        }
        catch
        {
            HasThumbnail = false;
        }
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Info.FilePath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening a media file is a convenience; failures are non-fatal.
        }
    }
}
