using PixelForge.Presentation.Services;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.ViewModels;

public sealed record VideoCodecItem(string Id, string DisplayName, string TechnicalId, bool IsCopy,
    IReadOnlyList<EncoderCapability> Encoders);

public sealed record VideoAudioCodecItem(string Id, string DisplayName, bool IsCopy, bool IsNone,
    IReadOnlyList<EncoderCapability> Encoders);

public sealed partial class VideoWorkspaceViewModel : WorkspaceViewModel
{
    private bool _updating;
    private bool _applyingPreset;

    public VideoWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Video, registry, settings, paths, filePicker)
    {
        SelectedPreset = PresetOptions.First(o => o.Value == settings.Settings.Converter.VideoPreset);
        SelectedRateControl = RateControlOptions[0];
        SelectedResizeMode = ResizeModes[0];
        SelectedSubtitleMode = SubtitleModes[1];
        SelectedAudioBitrate = AudioBitrateOptions.First(o => o.Value == 192);
        SelectedAudioSampleRate = SampleRateOptions[0];
        SelectedAudioChannels = ChannelOptions[0];
        SelectedEncoderPreset = "";
    }

    public override OperationKind Operation => OperationKind.VideoConvert;
    public override string CategoryTitle => "Video";
    public override string CategorySubtitle => "Remux or transcode video and audio streams with verification.";
    public override string CategoryGlyph => "\uE714";

    public IReadOnlyList<Option<VideoRateControlMode>> RateControlOptions { get; } =
    [
        new(VideoRateControlMode.ConstantQuality, "Constant quality"),
        new(VideoRateControlMode.Bitrate, "Target bitrate")
    ];

    public IReadOnlyList<Option<VideoResizeMode>> ResizeModes { get; } =
    [
        new(VideoResizeMode.None, "Keep original"),
        new(VideoResizeMode.Fit, "Fit with letterbox"),
        new(VideoResizeMode.Crop, "Crop to fill"),
        new(VideoResizeMode.Stretch, "Stretch")
    ];

    public IReadOnlyList<Option<SubtitleMode>> SubtitleModes { get; } =
    [
        new(SubtitleMode.Drop, "Drop subtitles"),
        new(SubtitleMode.Copy, "Preserve subtitles"),
        new(SubtitleMode.Convert, "Convert subtitles")
    ];

    public IReadOnlyList<Option<int>> AudioBitrateOptions { get; } =
    [
        new(64, "64 kbps"), new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"),
        new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")
    ];

    public IReadOnlyList<Option<int>> SampleRateOptions { get; } =
    [
        new(0, "Source sample rate"), new(44100, "44.1 kHz"), new(48000, "48 kHz"), new(96000, "96 kHz")
    ];

    public IReadOnlyList<Option<int>> ChannelOptions { get; } =
    [
        new(0, "Source channels"), new(1, "Mono"), new(2, "Stereo")
    ];

    public IReadOnlyList<Option<ConversionPreset>> PresetOptions { get; } =
    [
        new(ConversionPreset.Fast, "Fast conversion"),
        new(ConversionPreset.Balanced, "Balanced"),
        new(ConversionPreset.HighQuality, "High quality")
    ];

    [ObservableProperty] private ObservableCollection<VideoCodecItem> _videoCodecs = [];
    [ObservableProperty] private VideoCodecItem? _selectedVideoCodec;
    [ObservableProperty] private ObservableCollection<EncoderCapability> _videoEncoders = [];
    [ObservableProperty] private EncoderCapability? _selectedVideoEncoder;
    [ObservableProperty] private ObservableCollection<VideoAudioCodecItem> _audioCodecs = [];
    [ObservableProperty] private VideoAudioCodecItem? _selectedAudioCodec;
    [ObservableProperty] private ObservableCollection<EncoderCapability> _audioEncoders = [];
    [ObservableProperty] private EncoderCapability? _selectedAudioEncoder;
    [ObservableProperty] private ObservableCollection<string> _encoderPresets = [];
    [ObservableProperty] private string _selectedEncoderPreset = "";
    [ObservableProperty] private Option<ConversionPreset> _selectedPreset;
    [ObservableProperty] private Option<VideoRateControlMode> _selectedRateControl;
    [ObservableProperty] private double _qualityValue = 23;
    [ObservableProperty] private double _videoBitrate = 8000;
    [ObservableProperty] private double _audioBitrate = 192;
    [ObservableProperty] private Option<int> _selectedAudioBitrate;
    [ObservableProperty] private Option<int> _selectedAudioSampleRate;
    [ObservableProperty] private Option<int> _selectedAudioChannels;
    [ObservableProperty] private bool _enableResize;
    [ObservableProperty] private double? _width;
    [ObservableProperty] private double? _height;
    [ObservableProperty] private Option<VideoResizeMode> _selectedResizeMode;
    [ObservableProperty] private bool _preserveAspect = true;
    [ObservableProperty] private double _frameRate;
    [ObservableProperty] private Option<SubtitleMode> _selectedSubtitleMode;
    [ObservableProperty] private bool _preserveChapters = true;
    [ObservableProperty] private bool _fastStart = true;

    public bool IsVideoCopy => SelectedVideoCodec?.IsCopy == true;
    public bool IsAudioCopy => SelectedAudioCodec?.IsCopy == true;
    public bool IsAudioNone => SelectedAudioCodec?.IsNone == true;
    public bool ShowRateControl => !IsVideoCopy && SelectedVideoEncoder is not null &&
                                   SelectedVideoEncoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                                   (SelectedVideoEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) ||
                                    SelectedVideoEncoder.RateControl.HasFlag(RateControlCapabilities.Quantizer));
    public bool ShowQuality => !IsVideoCopy && SelectedVideoEncoder is not null &&
                               SelectedRateControl?.Value == VideoRateControlMode.ConstantQuality;
    public bool ShowVideoBitrate => !IsVideoCopy &&
                                    SelectedVideoEncoder is { } enc &&
                                    enc.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                                    (SelectedRateControl?.Value == VideoRateControlMode.Bitrate ||
                                     (!enc.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                                      !enc.RateControl.HasFlag(RateControlCapabilities.Quantizer)));
    public bool ShowEncoderPresets => !IsVideoCopy && SelectedVideoEncoder?.SupportsPresets == true && EncoderPresets.Count > 0;
    public bool ShowResize => !IsVideoCopy;
    public bool ShowFrameRate => !IsVideoCopy;
    public bool ShowAudioSettings => !IsAudioCopy && !IsAudioNone;
    public bool ShowAudioBitrate => ShowAudioSettings && SelectedAudioEncoder?.RateControl.HasFlag(RateControlCapabilities.Bitrate) == true;
    public bool ShowFastStart => SelectedFormat?.Id is "mp4" or "mov";
    public bool ShowSubtitles => SelectedFormat?.Id is not ("avi" or "gif");
    public bool ShowChapters => SelectedFormat?.Id is not ("avi" or "gif");
    public bool ShowContainerNotice => IsVideoCopy;
    public string QualityLabel => SelectedVideoEncoder?.QualityLabel ?? "Quality";
    public double QualityMinimum => SelectedVideoEncoder?.QualityMin ?? 0;
    public double QualityMaximum => SelectedVideoEncoder?.QualityMax ?? 63;
    public string EncoderHint => SelectedVideoEncoder?.Notes ?? SelectedVideoEncoder?.DisplayName ?? "Choose an encoder";
    public string AudioEncoderHint => SelectedAudioEncoder?.Notes ?? SelectedAudioEncoder?.DisplayName ?? "Choose an audio encoder";
    public string ModeTitle => IsVideoCopy && (IsAudioCopy || IsAudioNone) ? "Remux — no re-encode" : "Transcode";
    public string ModeDescription => IsVideoCopy
        ? "The video stream is copied without quality loss. Container compatibility is verified before processing."
        : "The video stream is re-encoded with the selected encoder. Quality settings use the encoder's native parameter.";

    protected override void OnFormatChanged()
    {
        _updating = true;
        var containerId = SelectedFormat?.Id ?? "mp4";
        var previousVideo = SelectedVideoCodec?.Id;
        var videoCodecs = Registry.GetVideoCodecs(containerId)
            .Select(c => new VideoCodecItem(c.Id, c.DisplayName, c.TechnicalId, false, c.Encoders))
            .ToList();
        if (!string.Equals(containerId, "gif", StringComparison.OrdinalIgnoreCase))
            videoCodecs.Insert(0, new VideoCodecItem(FormatIds.Copy, "Stream copy (remux)", "copy", true, []));
        VideoCodecs = new ObservableCollection<VideoCodecItem>(videoCodecs);
        var preferredVideo = previousVideo ?? Settings.Settings.Converter.VideoCodecId;
        SelectedVideoCodec = VideoCodecs.FirstOrDefault(c => string.Equals(c.Id, preferredVideo, StringComparison.OrdinalIgnoreCase))
                             ?? VideoCodecs.FirstOrDefault(c => !c.IsCopy && string.Equals(c.Id, CompatibilityMatrix.GetDefaultVideoCodec(containerId), StringComparison.OrdinalIgnoreCase))
                             ?? VideoCodecs.FirstOrDefault();

        var previousAudio = SelectedAudioCodec?.Id;
        var audioCodecs = new List<VideoAudioCodecItem>();
        if (CompatibilityMatrix.ContainerSupportsAudio(containerId))
        {
            audioCodecs.Add(new VideoAudioCodecItem(FormatIds.Copy, "Copy audio stream", true, false, []));
            audioCodecs.Add(new VideoAudioCodecItem(FormatIds.None, "No audio", false, true, []));
            audioCodecs.AddRange(Registry.GetAudioCodecs(containerId, true)
                .Select(c => new VideoAudioCodecItem(c.Id, c.DisplayName, false, false, c.Encoders)));
        }
        else
        {
            audioCodecs.Add(new VideoAudioCodecItem(FormatIds.None, "No audio", false, true, []));
        }

        AudioCodecs = new ObservableCollection<VideoAudioCodecItem>(audioCodecs);
        var preferredAudio = previousAudio ?? Settings.Settings.Converter.VideoAudioCodecId;
        SelectedAudioCodec = AudioCodecs.FirstOrDefault(c => string.Equals(c.Id, preferredAudio, StringComparison.OrdinalIgnoreCase))
                             ?? AudioCodecs.FirstOrDefault(c => !c.IsCopy && !c.IsNone && string.Equals(c.Id, CompatibilityMatrix.GetDefaultAudioCodec(containerId), StringComparison.OrdinalIgnoreCase))
                             ?? AudioCodecs.FirstOrDefault();
        _updating = false;
        LoadVideoEncoders();
        LoadAudioEncoders();
        RefreshTraits();
        RaiseChanged();
    }

    private void LoadVideoEncoders()
    {
        _updating = true;
        VideoEncoders = new ObservableCollection<EncoderCapability>((SelectedVideoCodec?.Encoders
            .Where(e => e.IsAvailable).OrderBy(e => e.Priority) ?? Enumerable.Empty<EncoderCapability>()).ToList());
        var preferred = SelectedVideoEncoder?.Id;
        SelectedVideoEncoder = VideoEncoders.FirstOrDefault(e => string.Equals(e.Id, preferred, StringComparison.OrdinalIgnoreCase))
                               ?? VideoEncoders.FirstOrDefault();
        _updating = false;
        UpdateEncoderPresets();
        RefreshTraits();
        RaiseChanged();
    }

    private void LoadAudioEncoders()
    {
        _updating = true;
        AudioEncoders = new ObservableCollection<EncoderCapability>((SelectedAudioCodec?.Encoders
            .Where(e => e.IsAvailable).OrderBy(e => e.Priority) ?? Enumerable.Empty<EncoderCapability>()).ToList());
        var preferred = SelectedAudioEncoder?.Id;
        SelectedAudioEncoder = AudioEncoders.FirstOrDefault(e => string.Equals(e.Id, preferred, StringComparison.OrdinalIgnoreCase))
                               ?? AudioEncoders.FirstOrDefault();
        _updating = false;
        RefreshTraits();
        RaiseChanged();
    }

    private void UpdateEncoderPresets()
    {
        _updating = true;
        IReadOnlyList<string> presets = SelectedVideoEncoder?.Presets ?? Array.Empty<string>();
        EncoderPresets = new ObservableCollection<string>(presets);
        var desired = !string.IsNullOrWhiteSpace(SelectedEncoderPreset) &&
                      presets.Contains(SelectedEncoderPreset, StringComparer.OrdinalIgnoreCase)
            ? SelectedEncoderPreset
            : SelectedVideoEncoder?.DefaultPreset ?? presets.FirstOrDefault() ?? "";
        SelectedEncoderPreset = desired;
        _updating = false;

        if (SelectedVideoEncoder is { } encoder)
        {
            if (encoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) ||
                encoder.RateControl.HasFlag(RateControlCapabilities.Quantizer))
            {
                SelectedRateControl = RateControlOptions.First(o => o.Value == VideoRateControlMode.ConstantQuality);
                if (QualityValue < QualityMinimum || QualityValue > QualityMaximum)
                    QualityValue = encoder.QualityDefault ?? QualityMinimum;
            }
            else
            {
                SelectedRateControl = RateControlOptions.First(o => o.Value == VideoRateControlMode.Bitrate);
            }
        }
    }

    private void RefreshTraits()
    {
        OnPropertyChanged(nameof(IsVideoCopy));
        OnPropertyChanged(nameof(IsAudioCopy));
        OnPropertyChanged(nameof(IsAudioNone));
        OnPropertyChanged(nameof(ShowRateControl));
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(ShowVideoBitrate));
        OnPropertyChanged(nameof(ShowEncoderPresets));
        OnPropertyChanged(nameof(ShowResize));
        OnPropertyChanged(nameof(ShowFrameRate));
        OnPropertyChanged(nameof(ShowAudioSettings));
        OnPropertyChanged(nameof(ShowAudioBitrate));
        OnPropertyChanged(nameof(ShowFastStart));
        OnPropertyChanged(nameof(ShowSubtitles));
        OnPropertyChanged(nameof(ShowChapters));
        OnPropertyChanged(nameof(ShowContainerNotice));
        OnPropertyChanged(nameof(QualityLabel));
        OnPropertyChanged(nameof(QualityMinimum));
        OnPropertyChanged(nameof(QualityMaximum));
        OnPropertyChanged(nameof(EncoderHint));
        OnPropertyChanged(nameof(AudioEncoderHint));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(ModeDescription));
    }

    protected override string BuildSummary()
    {
        if (SelectedFormat is null) return "Choose a video container";
        if (IsVideoCopy && (IsAudioCopy || IsAudioNone))
            return $"{FileCountText} → {SelectedFormat.ExtensionUpper} · remux · no video re-encode";
        var video = SelectedVideoCodec?.DisplayName ?? "video";
        var audio = IsAudioNone ? "no audio" : SelectedAudioCodec?.DisplayName ?? "audio";
        return $"{FileCountText} → {SelectedFormat.ExtensionUpper} · {video} · {audio}";
    }

    protected override string BuildDefaultTitle() => $"Convert {FileCountText} to {SelectedFormat?.ExtensionUpper ?? "video"}";

    public override bool Accepts(MediaInfo info) => info.Category == MediaCategory.Video;

    public override OperationSettings BuildSettings()
    {
        var settings = new VideoConversionSettings
        {
            ContainerFormatId = SelectedFormat?.Id ?? "mp4",
            VideoCodecId = SelectedVideoCodec?.Id ?? FormatIds.Copy,
            VideoEncoderId = SelectedVideoEncoder?.Id ?? "",
            AudioCodecId = SelectedAudioCodec?.Id ?? FormatIds.None,
            AudioEncoderId = SelectedAudioEncoder?.Id ?? "",
            RateControlMode = SelectedRateControl?.Value ?? VideoRateControlMode.ConstantQuality,
            QualityValue = QualityValue,
            VideoBitrateKbps = (int)Math.Clamp(VideoBitrate, 100, 200_000),
            AudioBitrateKbps = SelectedAudioBitrate.Value,
            Width = EnableResize && Width is > 0 ? (int)Math.Round(Width.Value) : null,
            Height = EnableResize && Height is > 0 ? (int)Math.Round(Height.Value) : null,
            ResizeMode = EnableResize ? SelectedResizeMode?.Value ?? VideoResizeMode.None : VideoResizeMode.None,
            PreserveAspectRatio = PreserveAspect,
            FrameRate = FrameRate > 0 ? FrameRate : 0,
            Preset = SelectedPreset?.Value ?? ConversionPreset.Balanced,
            EncoderPreset = SelectedEncoderPreset,
            SubtitleMode = SelectedSubtitleMode?.Value ?? SubtitleMode.Copy,
            PreserveChapters = PreserveChapters,
            FastStart = FastStart,
            AudioChannels = SelectedAudioChannels.Value,
            AudioSampleRate = SelectedAudioSampleRate.Value
        };
        CommonSettings(settings);
        return settings;
    }

    partial void OnSelectedVideoCodecChanged(VideoCodecItem? value)
    {
        if (_updating) return;
        LoadVideoEncoders();
        RefreshTraits();
        RaiseChanged();
    }

    partial void OnSelectedVideoEncoderChanged(EncoderCapability? value)
    {
        if (_updating) return;
        UpdateEncoderPresets();
        RefreshTraits();
        RaiseChanged();
    }

    partial void OnSelectedAudioCodecChanged(VideoAudioCodecItem? value)
    {
        if (_updating) return;
        LoadAudioEncoders();
        RefreshTraits();
        RaiseChanged();
    }

    partial void OnSelectedAudioEncoderChanged(EncoderCapability? value)
    {
        if (_updating) return;
        RefreshTraits();
        RaiseChanged();
    }

    partial void OnSelectedPresetChanged(Option<ConversionPreset> value)
    {
        if (_updating) return;
        ApplyPreset(value.Value);
    }

    private void ApplyPreset(ConversionPreset preset)
    {
        if (_applyingPreset || SelectedVideoEncoder is null || IsVideoCopy) return;
        _applyingPreset = true;
        try
        {
            var id = SelectedVideoEncoder.Id;
            var (encoderPreset, quality, bitrate) = (id, preset) switch
            {
                ("h264_amf" or "hevc_amf" or "av1_amf", ConversionPreset.Fast) => ("speed", 28.0, 4000),
                ("h264_amf" or "hevc_amf" or "av1_amf", ConversionPreset.HighQuality) => ("quality", 20.0, 12000),
                ("h264_amf" or "hevc_amf" or "av1_amf", _) => ("balanced", 23.0, 8000),
                ("h264_nvenc" or "hevc_nvenc" or "av1_nvenc", ConversionPreset.Fast) => ("p3", 26.0, 4000),
                ("h264_nvenc" or "hevc_nvenc" or "av1_nvenc", ConversionPreset.HighQuality) => ("p6", 20.0, 12000),
                ("h264_nvenc" or "hevc_nvenc" or "av1_nvenc", _) => ("p4", 23.0, 8000),
                ("h264_qsv" or "hevc_qsv" or "av1_qsv", ConversionPreset.Fast) => ("fast", 28.0, 4000),
                ("h264_qsv" or "hevc_qsv" or "av1_qsv", ConversionPreset.HighQuality) => ("slow", 20.0, 12000),
                ("h264_qsv" or "hevc_qsv" or "av1_qsv", _) => ("medium", 23.0, 8000),
                ("libsvtav1", ConversionPreset.Fast) => ("10", 32.0, 4000),
                ("libsvtav1", ConversionPreset.HighQuality) => ("4", 24.0, 12000),
                ("libsvtav1", _) => ("8", 28.0, 8000),
                ("libvpx-vp9" or "libvpx", ConversionPreset.Fast) => ("5", 36.0, 4000),
                ("libvpx-vp9" or "libvpx", ConversionPreset.HighQuality) => ("0", 28.0, 12000),
                ("libvpx-vp9" or "libvpx", _) => ("2", 32.0, 8000),
                ("mpeg4", ConversionPreset.Fast) => ("", 8.0, 4000),
                ("mpeg4", ConversionPreset.HighQuality) => ("", 2.0, 12000),
                ("mpeg4", _) => ("", 4.0, 8000),
                ("prores_ks", ConversionPreset.Fast) => ("lt", 0.0, 0),
                ("prores_ks", ConversionPreset.HighQuality) => ("hq", 0.0, 0),
                ("prores_ks", _) => ("standard", 0.0, 0),
                ("libkvazaar", ConversionPreset.Fast) => ("", 0.0, 4000),
                ("libkvazaar", ConversionPreset.HighQuality) => ("", 0.0, 12000),
                ("libkvazaar", _) => ("", 0.0, 8000),
                ("libaom-av1", ConversionPreset.Fast) => ("8", 36.0, 4000),
                ("libaom-av1", ConversionPreset.HighQuality) => ("0", 28.0, 12000),
                ("libaom-av1", _) => ("4", 32.0, 8000),
                ("libopenh264", ConversionPreset.Fast) => ("", 0.0, 4000),
                ("libopenh264", ConversionPreset.HighQuality) => ("", 0.0, 12000),
                ("libopenh264", _) => ("", 0.0, 8000),
                _ => (SelectedVideoEncoder.DefaultPreset ?? "", SelectedVideoEncoder.QualityDefault ?? 23.0, 8000)
            };
            if (!string.IsNullOrWhiteSpace(encoderPreset) && EncoderPresets.Contains(encoderPreset, StringComparer.OrdinalIgnoreCase))
                SelectedEncoderPreset = encoderPreset;
            if (SelectedVideoEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) ||
                SelectedVideoEncoder.RateControl.HasFlag(RateControlCapabilities.Quantizer))
            {
                SelectedRateControl = RateControlOptions.First(o => o.Value == VideoRateControlMode.ConstantQuality);
                QualityValue = quality;
            }
            else
            {
                SelectedRateControl = RateControlOptions.First(o => o.Value == VideoRateControlMode.Bitrate);
                VideoBitrate = bitrate > 0 ? bitrate : VideoBitrate;
            }

            RaiseChanged();
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    partial void OnSelectedEncoderPresetChanged(string value)
    {
        if (_updating) return;
        RaiseChanged();
    }

    partial void OnSelectedRateControlChanged(Option<VideoRateControlMode> value)
    {
        RefreshTraits();
        RaiseChanged();
    }

    partial void OnQualityValueChanged(double value) => RaiseChanged();
    partial void OnVideoBitrateChanged(double value) => RaiseChanged();
    partial void OnSelectedAudioBitrateChanged(Option<int> value) => RaiseChanged();
    partial void OnSelectedAudioSampleRateChanged(Option<int> value) => RaiseChanged();
    partial void OnSelectedAudioChannelsChanged(Option<int> value) => RaiseChanged();
    partial void OnEnableResizeChanged(bool value) => RaiseChanged();
    partial void OnWidthChanged(double? value) => RaiseChanged();
    partial void OnHeightChanged(double? value) => RaiseChanged();
    partial void OnSelectedResizeModeChanged(Option<VideoResizeMode> value) => RaiseChanged();
    partial void OnPreserveAspectChanged(bool value) => RaiseChanged();
    partial void OnFrameRateChanged(double value) => RaiseChanged();
    partial void OnSelectedSubtitleModeChanged(Option<SubtitleMode> value) => RaiseChanged();
    partial void OnPreserveChaptersChanged(bool value) => RaiseChanged();
    partial void OnFastStartChanged(bool value) => RaiseChanged();
}
