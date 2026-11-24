using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class DocumentWorkspaceViewModel : WorkspaceViewModel
{
    public DocumentWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Document, registry, settings, paths, filePicker)
    {
    }

    public override OperationKind Operation => OperationKind.DocumentConvert;
    public override string CategoryTitle => LocalizationService.T("Documents");
    public override string CategorySubtitle => "Convert documents locally while preserving structure where the engine allows.";
    public override string CategoryGlyph => "\uE8A5";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";

    public override OperationSettings BuildSettings()
    {
        var settings = new DocumentConversionSettings
        {
            OutputFormatId = SelectedFormat?.Id ?? "pdf",
            Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
            Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim()
        };
        CommonSettings(settings);
        return settings;
    }

    public override bool Accepts(MediaInfo info) =>
        info.Category is MediaCategory.Document or MediaCategory.Ebook;

    protected override string BuildSummary()
    {
        if (Files.Count == 0)
            return LocalizationService.F("No documents selected · output {0}", SelectedFormatExtension);
        return LocalizationService.F("{0} → {1}",
            LocalizationService.P("Count.Document", Files.Count), SelectedFormat?.DisplayName ?? "PDF");
    }

    protected override string BuildDefaultTitle() =>
        LocalizationService.F("Convert {0}", Files.Count == 1
            ? Files[0].FileName
            : LocalizationService.P("Count.Document", Files.Count));
}

public sealed partial class EbookWorkspaceViewModel : WorkspaceViewModel
{
    public EbookWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Ebook, registry, settings, paths, filePicker)
    {
    }

    public override OperationKind Operation => OperationKind.EbookConvert;
    public override string CategoryTitle => LocalizationService.T("E-books");
    public override string CategorySubtitle => "Convert reflowable e-books, chapters and metadata locally.";
    public override string CategoryGlyph => "\uE736";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _language = "";

    public override OperationSettings BuildSettings()
    {
        var settings = new EbookConversionSettings
        {
            OutputFormatId = SelectedFormat?.Id ?? "epub",
            Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
            Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim(),
            Language = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim()
        };
        CommonSettings(settings);
        return settings;
    }

    public override bool Accepts(MediaInfo info) =>
        info.Category is MediaCategory.Document or MediaCategory.Ebook;

    protected override string BuildSummary()
    {
        if (Files.Count == 0)
            return LocalizationService.F("No e-books selected · output {0}", SelectedFormatExtension);
        return LocalizationService.F("{0} → {1}",
            LocalizationService.P("Count.Ebook", Files.Count), SelectedFormat?.DisplayName ?? "EPUB");
    }

    protected override string BuildDefaultTitle() =>
        LocalizationService.F("Convert {0}", Files.Count == 1
            ? Files[0].FileName
            : LocalizationService.P("Count.Ebook", Files.Count));
}

public sealed partial class ArchiveWorkspaceViewModel : WorkspaceViewModel
{
    public ArchiveWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Archive, registry, settings, paths, filePicker)
    {
        SelectedAction = Actions[0];
        SelectedCompressionLevel = CompressionLevels[4];
    }

    public override OperationKind Operation => OperationKind.ArchiveConvert;
    public override string CategoryTitle => LocalizationService.T("Archives");
    public override string CategorySubtitle => "Create, repackage and validate archives with safe path handling.";
    public override string CategoryGlyph => "\uE8B7";

    public IReadOnlyList<Option<ArchiveAction>> Actions { get; } =
    [
        new(ArchiveAction.Create, "Create archive"),
        new(ArchiveAction.Repackage, "Repackage archive"),
        new(ArchiveAction.Extract, "Extract to folder")
    ];

    public IReadOnlyList<Option<int>> CompressionLevels { get; } =
    [
        new(1, "Fast (1)"), new(3, "Light (3)"), new(5, "Balanced (5)"),
        new(7, "Strong (7)"), new(9, "Maximum (9)")
    ];

    [ObservableProperty] private Option<ArchiveAction> _selectedAction;
    [ObservableProperty] private Option<int> _selectedCompressionLevel;
    [ObservableProperty] private bool _preserveStructure = true;

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var option in Actions) option.RefreshLocalization();
        foreach (var option in CompressionLevels) option.RefreshLocalization();
        SelectedAction.RefreshLocalization();
        SelectedCompressionLevel.RefreshLocalization();
    }

    public bool IsRepackage => SelectedAction.Value == ArchiveAction.Repackage;
    public bool IsExtract => SelectedAction.Value == ArchiveAction.Extract;
    public bool IsCreate => SelectedAction.Value == ArchiveAction.Create;

    public override OperationSettings BuildSettings()
    {
        var settings = new ArchiveConversionSettings
        {
            OutputFormatId = SelectedFormat?.Id ?? "zip",
            Action = SelectedAction.Value,
            CompressionLevel = SelectedCompressionLevel.Value,
            PreserveDirectoryStructure = PreserveStructure
        };
        CommonSettings(settings);
        return settings;
    }

    public override bool Accepts(MediaInfo info) =>
        SelectedAction.Value == ArchiveAction.Create ||
        (info.Category == MediaCategory.Archive && info.FormatId != "folder");

    protected override string BuildSummary()
    {
        var actionLabel = SelectedAction?.DisplayLabel ?? LocalizationService.T("Create archive");
        if (Files.Count == 0)
            return LocalizationService.F("No files selected · {0}", actionLabel);
        var action = LocalizationService.T(SelectedAction?.Value switch
        {
            ArchiveAction.Repackage => "repackage",
            ArchiveAction.Extract => "extract",
            _ => "compress"
        });
        var target = SelectedAction?.Value == ArchiveAction.Extract
            ? LocalizationService.T("a folder")
            : SelectedFormat?.DisplayName ?? "ZIP";
        return LocalizationService.F("{0} · {1} → {2}",
            LocalizationService.P("Count.File", Files.Count), action, target);
    }

    protected override string BuildDefaultTitle()
    {
        var actionLabel = SelectedAction?.DisplayLabel ?? LocalizationService.T("Archive");
        return LocalizationService.F("{0}: {1}", actionLabel,
            Files.Count == 1 ? Files[0].FileName : LocalizationService.P("Count.File", Files.Count));
    }

    partial void OnSelectedActionChanged(Option<ArchiveAction> value)
    {
        OnPropertyChanged(nameof(IsRepackage));
        OnPropertyChanged(nameof(IsExtract));
        OnPropertyChanged(nameof(IsCreate));
        RaiseChanged();
    }

    partial void OnSelectedCompressionLevelChanged(Option<int> value) => RaiseChanged();
    partial void OnPreserveStructureChanged(bool value) => RaiseChanged();
}
