using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.ViewModels;

public sealed record AudioCodecItem(string Id, string DisplayName, string TechnicalId,
    IReadOnlyList<EncoderCapability> Encoders);

public sealed partial class AudioWorkspaceViewModel : WorkspaceViewModel
{
    private bool _updating;

    public AudioWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Audio, registry, settings, paths, filePicker)
    {
        SelectedBitrate = BitrateOptions.FirstOrDefault(o => o.Value == settings.Settings.Converter.AudioBitrateKbps)
                          ?? BitrateOptions.First(o => o.Value == 192);
        SelectedSampleRate = SampleRateOptions[0];
        SelectedChannels = ChannelOptions[0];
        SelectedRateControl = RateControlOptions[0];
    }

    public override OperationKind Operation => OperationKind.AudioConvert;
    public override string CategoryTitle => LocalizationService.T("Audio");
    public override string CategorySubtitle => "Transcode audio with verified codecs, sample rates and metadata.";
    public override string CategoryGlyph => "\uE8D6";

    public IReadOnlyList<Option<int>> BitrateOptions { get; } =
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

    public IReadOnlyList<Option<AudioRateControlMode>> RateControlOptions { get; } =
    [
        new(AudioRateControlMode.ConstantBitrate, "Constant bitrate"),
        new(AudioRateControlMode.VariableQuality, "Variable quality")
    ];

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var option in BitrateOptions) option.RefreshLocalization();
        foreach (var option in SampleRateOptions) option.RefreshLocalization();
        foreach (var option in ChannelOptions) option.RefreshLocalization();
        foreach (var option in RateControlOptions) option.RefreshLocalization();
        SelectedBitrate.RefreshLocalization();
        SelectedRateControl.RefreshLocalization();
        SelectedSampleRate.RefreshLocalization();
        SelectedChannels.RefreshLocalization();
    }

    [ObservableProperty] private ObservableCollection<AudioCodecItem> _codecs = [];
    [ObservableProperty] private AudioCodecItem? _selectedCodec;
    [ObservableProperty] private ObservableCollection<EncoderCapability> _encoders = [];
    [ObservableProperty] private EncoderCapability? _selectedEncoder;
    [ObservableProperty] private Option<int> _selectedBitrate;
    [ObservableProperty] private Option<AudioRateControlMode> _selectedRateControl;
    [ObservableProperty] private double _variableQuality = 5;
    [ObservableProperty] private Option<int> _selectedSampleRate;
    [ObservableProperty] private Option<int> _selectedChannels;
    [ObservableProperty] private double _compressionLevel = 5;

    public bool ShowRateControl => SelectedEncoder is not null &&
                                   SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                                   SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.Bitrate);
    public bool ShowBitrate => SelectedEncoder is not null &&
                               SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                               (!ShowRateControl || SelectedRateControl?.Value == AudioRateControlMode.ConstantBitrate);
    public bool ShowVariableQuality => SelectedEncoder is not null &&
                                       SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality) &&
                                       (!ShowRateControl || SelectedRateControl?.Value == AudioRateControlMode.VariableQuality);
    public bool ShowCompressionLevel => SelectedEncoder?.Id == "flac";
    public bool ShowSampleRate => true;
    public bool ShowChannels => true;
    public string EncoderHint => SelectedEncoder?.Notes ?? SelectedEncoder?.DisplayName ?? "Choose an encoder";

    protected override void OnFormatChanged()
    {
        _updating = true;
        var codecs = Registry.GetAudioCodecs(SelectedFormat?.Id ?? "", false)
            .Select(c => new AudioCodecItem(c.Id, c.DisplayName, c.TechnicalId, c.Encoders))
            .ToList();
        Codecs = new ObservableCollection<AudioCodecItem>(codecs);
        var defaultCodec = CompatibilityMatrix.GetDefaultAudioCodec(SelectedFormat?.Id ?? "");
        SelectedCodec = Codecs.FirstOrDefault(c => string.Equals(c.Id, defaultCodec, StringComparison.OrdinalIgnoreCase))
                        ?? Codecs.FirstOrDefault();
        _updating = false;
        LoadEncoders();
        RaiseChanged();
    }

    private void LoadEncoders()
    {
        _updating = true;
        Encoders = new ObservableCollection<EncoderCapability>((SelectedCodec?.Encoders
            .Where(e => e.IsAvailable)
            .OrderBy(e => e.Priority) ?? Enumerable.Empty<EncoderCapability>()).ToList());
        SelectedEncoder = Encoders.FirstOrDefault();
        _updating = false;
        RefreshEncoderState();
        RaiseChanged();
    }

    private void RefreshEncoderState()
    {
        OnPropertyChanged(nameof(ShowRateControl));
        OnPropertyChanged(nameof(ShowBitrate));
        OnPropertyChanged(nameof(ShowVariableQuality));
        OnPropertyChanged(nameof(ShowCompressionLevel));
        OnPropertyChanged(nameof(EncoderHint));
        if (SelectedEncoder is not null)
        {
            if (!SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality))
                SelectedRateControl = RateControlOptions.First(o => o.Value == AudioRateControlMode.VariableQuality);
            else if (SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.Bitrate) &&
                     !SelectedEncoder.RateControl.HasFlag(RateControlCapabilities.ConstantQuality))
                SelectedRateControl = RateControlOptions.First(o => o.Value == AudioRateControlMode.ConstantBitrate);
        }
    }

    protected override string BuildSummary()
    {
        if (SelectedFormat is null || SelectedCodec is null) return "Choose an audio format";
        var rate = ShowVariableQuality
            ? LocalizationService.F("VBR {0}", VariableQuality.ToString("0", LocalizationService.CurrentCulture))
            : LocalizationService.F("{0} kbps", SelectedBitrate.Value);
        return $"{FileCountText} → {SelectedFormat.ExtensionUpper} · {SelectedCodec.DisplayName} · {rate}";
    }

    protected override string BuildDefaultTitle() =>
        LocalizationService.F("Convert {0} to {1}", FileCountText,
            SelectedFormat?.ExtensionUpper ?? LocalizationService.T("audio"));

    public override bool Accepts(MediaInfo info) =>
        info.Category == MediaCategory.Audio || (info.Category == MediaCategory.Video && info.AudioCodec is not null);

    public override OperationSettings BuildSettings()
    {
        var settings = new AudioConversionSettings
        {
            OutputFormatId = SelectedFormat?.Id ?? "mp3",
            CodecId = SelectedCodec?.Id ?? "mp3",
            EncoderId = SelectedEncoder?.Id ?? "",
            RateControlMode = SelectedRateControl?.Value ?? AudioRateControlMode.ConstantBitrate,
            BitrateKbps = SelectedBitrate.Value,
            Quality = (int)Math.Clamp(VariableQuality, 0, 10),
            SampleRate = SelectedSampleRate.Value,
            Channels = SelectedChannels.Value,
            CompressionLevel = (int)Math.Clamp(CompressionLevel, 0, 12)
        };
        CommonSettings(settings);
        return settings;
    }

    partial void OnSelectedCodecChanged(AudioCodecItem? value)
    {
        if (_updating) return;
        LoadEncoders();
    }

    partial void OnSelectedEncoderChanged(EncoderCapability? value)
    {
        if (_updating) return;
        RefreshEncoderState();
        RaiseChanged();
    }

    partial void OnSelectedBitrateChanged(Option<int> value) => RaiseChanged();
    partial void OnSelectedRateControlChanged(Option<AudioRateControlMode> value)
    {
        OnPropertyChanged(nameof(ShowBitrate));
        OnPropertyChanged(nameof(ShowVariableQuality));
        RaiseChanged();
    }
    partial void OnVariableQualityChanged(double value) => RaiseChanged();
    partial void OnSelectedSampleRateChanged(Option<int> value) => RaiseChanged();
    partial void OnSelectedChannelsChanged(Option<int> value) => RaiseChanged();
    partial void OnCompressionLevelChanged(double value) => RaiseChanged();
}
