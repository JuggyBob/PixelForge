using PixelForge.Domain.Models;

namespace PixelForge.Domain.Capabilities;

/// <summary>
/// Format definitions for the content engines. These are kept separate from the
/// media catalogues so adding document/archive support cannot change the media
/// conversion matrix accidentally.
/// </summary>
public static class ContentFormatCatalog
{
    public static readonly FormatDefinition Pdf = new()
    {
        Id = "pdf", Extension = ".pdf", DisplayName = "PDF document", Category = MediaCategory.Document,
        KnownExtensions = [".pdf"], SupportsMetadata = true, SortOrder = 10, TechnicalId = "pdf"
    };

    public static readonly FormatDefinition Docx = new()
    {
        Id = "docx", Extension = ".docx", DisplayName = "Microsoft Word document", Category = MediaCategory.Document,
        KnownExtensions = [".docx"], SupportsMetadata = true, SortOrder = 20, TechnicalId = "docx"
    };

    public static readonly FormatDefinition Odt = new()
    {
        Id = "odt", Extension = ".odt", DisplayName = "OpenDocument text", Category = MediaCategory.Document,
        KnownExtensions = [".odt"], SupportsMetadata = true, SortOrder = 30, TechnicalId = "odt"
    };

    public static readonly FormatDefinition Rtf = new()
    {
        Id = "rtf", Extension = ".rtf", DisplayName = "Rich Text Format", Category = MediaCategory.Document,
        KnownExtensions = [".rtf"], SupportsMetadata = true, SortOrder = 40, TechnicalId = "rtf"
    };

    public static readonly FormatDefinition Txt = new()
    {
        Id = "txt", Extension = ".txt", DisplayName = "Plain text", Category = MediaCategory.Document,
        KnownExtensions = [".txt", ".text"], SupportsMetadata = false, SortOrder = 50, TechnicalId = "plain"
    };

    public static readonly FormatDefinition Html = new()
    {
        Id = "html", Extension = ".html", DisplayName = "HTML document", Category = MediaCategory.Document,
        KnownExtensions = [".html", ".htm"], SupportsMetadata = true, SortOrder = 60, TechnicalId = "html"
    };

    public static readonly FormatDefinition Markdown = new()
    {
        Id = "md", Extension = ".md", DisplayName = "Markdown document", Category = MediaCategory.Document,
        KnownExtensions = [".md", ".markdown"], SupportsMetadata = true, SortOrder = 70, TechnicalId = "markdown"
    };

    public static readonly FormatDefinition Epub = new()
    {
        Id = "epub", Extension = ".epub", DisplayName = "EPUB e-book", Category = MediaCategory.Ebook,
        KnownExtensions = [".epub"], SupportsMetadata = true, SortOrder = 10, TechnicalId = "epub"
    };

    public static readonly FormatDefinition Zip = new()
    {
        Id = "zip", Extension = ".zip", DisplayName = "ZIP archive", Category = MediaCategory.Archive,
        KnownExtensions = [".zip"], SupportsMetadata = true, SortOrder = 10, TechnicalId = "zip"
    };

    public static readonly FormatDefinition SevenZip = new()
    {
        Id = "7z", Extension = ".7z", DisplayName = "7-Zip archive", Category = MediaCategory.Archive,
        KnownExtensions = [".7z"], SupportsMetadata = true, SortOrder = 20, TechnicalId = "7z"
    };

    public static readonly FormatDefinition Tar = new()
    {
        Id = "tar", Extension = ".tar", DisplayName = "TAR archive", Category = MediaCategory.Archive,
        KnownExtensions = [".tar"], SupportsMetadata = true, SortOrder = 30, TechnicalId = "tar"
    };

    public static readonly FormatDefinition Gzip = new()
    {
        Id = "gz", Extension = ".gz", DisplayName = "Gzip compressed file", Category = MediaCategory.Archive,
        KnownExtensions = [".gz", ".gzip"], SupportsMetadata = true, SortOrder = 40, TechnicalId = "gzip"
    };

    public static readonly FormatDefinition Bzip2 = new()
    {
        Id = "bz2", Extension = ".bz2", DisplayName = "Bzip2 compressed file", Category = MediaCategory.Archive,
        KnownExtensions = [".bz2", ".bzip2"], SupportsMetadata = true, SortOrder = 50, TechnicalId = "bzip2"
    };

    public static readonly FormatDefinition Xz = new()
    {
        Id = "xz", Extension = ".xz", DisplayName = "XZ compressed file", Category = MediaCategory.Archive,
        KnownExtensions = [".xz"], SupportsMetadata = true, SortOrder = 60, TechnicalId = "xz"
    };

    public static readonly FormatDefinition Zstd = new()
    {
        Id = "zst", Extension = ".zst", DisplayName = "Zstandard compressed file", Category = MediaCategory.Archive,
        KnownExtensions = [".zst", ".zstd"], SupportsMetadata = true, SortOrder = 70, TechnicalId = "zstd"
    };

    public static readonly IReadOnlyList<FormatDefinition> DocumentFormats =
        [Pdf, Docx, Odt, Rtf, Txt, Html, Markdown];

    public static readonly IReadOnlyList<FormatDefinition> EbookFormats =
        [Epub, Docx, Odt, Html, Markdown, Txt, Pdf];

    public static readonly IReadOnlyList<FormatDefinition> ArchiveFormats =
        [Zip, SevenZip, Tar, Gzip, Bzip2, Xz, Zstd];

    public static readonly IReadOnlyList<FormatDefinition> All =
        [.. DocumentFormats, .. EbookFormats, .. ArchiveFormats];

    public static FormatDefinition? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var value = id.Trim().TrimStart('.');
        return All.FirstOrDefault(f => string.Equals(f.Id, value, StringComparison.OrdinalIgnoreCase));
    }

    public static FormatDefinition? FindByExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;
        return All.FirstOrDefault(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase) ||
                                       f.KnownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    public static MediaCategory DetectCategoryByExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return MediaCategory.Unknown;
        var ext = extension.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;
        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase)) return MediaCategory.Ebook;
        if (ArchiveFormats.Any(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase) ||
                                    f.KnownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
            return MediaCategory.Archive;
        if (DocumentFormats.Any(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase) ||
                                     f.KnownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
            return MediaCategory.Document;
        return MediaCategory.Unknown;
    }

    public static IReadOnlyList<string> GetInputExtensions(MediaCategory category) => category switch
    {
        MediaCategory.Document => DocumentFormats.SelectMany(f => new[] { f.Extension }.Concat(f.KnownExtensions))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e).ToList(),
        MediaCategory.Ebook => EbookFormats.SelectMany(f => new[] { f.Extension }.Concat(f.KnownExtensions))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e).ToList(),
        MediaCategory.Archive => ArchiveFormats.SelectMany(f => new[] { f.Extension }.Concat(f.KnownExtensions))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e).ToList(),
        _ => []
    };
}
