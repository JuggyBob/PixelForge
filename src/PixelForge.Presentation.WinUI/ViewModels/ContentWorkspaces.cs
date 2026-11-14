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
    public override string CategoryTitle => "Documents";
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
        if (Files.Count == 0) return $"No documents selected · output {SelectedFormatExtension}";
        return $"{Files.Count} document{(Files.Count == 1 ? "" : "s")} → {SelectedFormat?.DisplayName ?? "PDF"}";
    }

    protected override string BuildDefaultTitle() =>
        Files.Count == 1 ? $"Convert {Files[0].FileName}" : $"Convert {Files.Count} documents";
}

public sealed partial class EbookWorkspaceViewModel : WorkspaceViewModel
{
    public EbookWorkspaceViewModel(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
        : base(MediaCategory.Ebook, registry, settings, paths, filePicker)
    {
    }

    public override OperationKind Operation => OperationKind.EbookConvert;
    public override string CategoryTitle => "E-books";
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
        if (Files.Count == 0) return $"No e-books selected · output {SelectedFormatExtension}";
        return $"{Files.Count} e-book{(Files.Count == 1 ? "" : "s")} → {SelectedFormat?.DisplayName ?? "EPUB"}";
    }

    protected override string BuildDefaultTitle() =>
        Files.Count == 1 ? $"Convert {Files[0].FileName}" : $"Convert {Files.Count} e-books";
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
    public override string CategoryTitle => "Archives";
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
        var actionLabel = SelectedAction?.Label ?? "Create archive";
        if (Files.Count == 0) return $"No files selected · {actionLabel}";
        var action = SelectedAction?.Value switch
        {
            ArchiveAction.Repackage => "repackage",
            ArchiveAction.Extract => "extract",
            _ => "compress"
        };
        var target = SelectedAction?.Value == ArchiveAction.Extract
            ? "a folder"
            : SelectedFormat?.DisplayName ?? "ZIP";
        return $"{Files.Count} file{(Files.Count == 1 ? "" : "s")} · {action} → {target}";
    }

    protected override string BuildDefaultTitle()
    {
        var actionLabel = SelectedAction?.Label ?? "Archive";
        return Files.Count == 1 ? $"{actionLabel}: {Files[0].FileName}" : $"{actionLabel}: {Files.Count} files";
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
