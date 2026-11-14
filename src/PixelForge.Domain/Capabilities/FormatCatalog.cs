using PixelForge.Domain.Models;

namespace PixelForge.Domain.Capabilities;

public sealed record FormatDefinition
{
    public required string Id { get; init; }
    public required string Extension { get; init; }
    public required string DisplayName { get; init; }
    public required MediaCategory Category { get; init; }
    public string? TechnicalId { get; init; }
    public string? MuxerName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> KnownExtensions { get; init; } = [];
    public bool IsLossy { get; init; }
    public bool SupportsMultipage { get; init; }
    public bool SupportsAlpha { get; init; }
    public bool SupportsAnimation { get; init; }
    public bool SupportsMetadata { get; init; } = true;
    public bool SupportsIccProfile { get; init; }
    public bool IsVector { get; init; }
    public bool IsContainer { get; init; }
    public int SortOrder { get; init; }

    public MediaFormat ToMediaFormat(string engineId, bool canRead, bool canWrite,
        string? technicalId = null, string? unavailableReason = null) => new()
    {
        Id = Id,
        Extension = Extension,
        DisplayName = DisplayName,
        Category = Category,
        EngineId = engineId,
        TechnicalId = technicalId ?? TechnicalId,
        MuxerName = MuxerName,
        Aliases = Aliases,
        CanRead = canRead,
        CanWrite = canWrite,
        SupportsAlpha = SupportsAlpha,
        SupportsAnimation = SupportsAnimation,
        SupportsMultipage = SupportsMultipage,
        SupportsMetadata = SupportsMetadata,
        SupportsIccProfile = SupportsIccProfile,
        IsLossy = IsLossy,
        IsVector = IsVector,
        IsContainer = IsContainer,
        SortOrder = SortOrder,
        UnavailableReason = unavailableReason
    };
}

public static class FormatCatalog
{
    public static readonly FormatDefinition Png = new()
    {
        Id = "png", Extension = ".png", DisplayName = "Portable Network Graphics", Category = MediaCategory.Image,
        Aliases = ["png", "portable network graphics"], SupportsAlpha = true, SupportsAnimation = true,
        SupportsIccProfile = true, SupportsMetadata = true, SortOrder = 10, TechnicalId = "PNG"
    };

    public static readonly FormatDefinition Jpeg = new()
    {
        Id = "jpg", Extension = ".jpg", DisplayName = "JPEG Image", Category = MediaCategory.Image,
        Aliases = ["jpeg", "jpg", "joint photographic experts group"], KnownExtensions = [".jpg", ".jpeg", ".jpe", ".jfif"],
        IsLossy = true, SupportsIccProfile = true, SupportsMetadata = true, SortOrder = 20, TechnicalId = "JPEG"
    };

    public static readonly FormatDefinition WebP = new()
    {
        Id = "webp", Extension = ".webp", DisplayName = "WebP Image", Category = MediaCategory.Image,
        Aliases = ["webp", "web picture"], KnownExtensions = [".webp"], IsLossy = true, SupportsAlpha = true,
        SupportsAnimation = true, SupportsIccProfile = true, SupportsMetadata = true, SortOrder = 30, TechnicalId = "WEBP"
    };

    public static readonly FormatDefinition Avif = new()
    {
        Id = "avif", Extension = ".avif", DisplayName = "AVIF Image", Category = MediaCategory.Image,
        Aliases = ["avif", "av1 image file format"], KnownExtensions = [".avif"], IsLossy = true, SupportsAlpha = true,
        SupportsAnimation = true, SupportsIccProfile = true, SupportsMetadata = true, SortOrder = 40, TechnicalId = "AVIF"
    };

    public static readonly FormatDefinition Tiff = new()
    {
        Id = "tif", Extension = ".tif", DisplayName = "Tagged Image File Format", Category = MediaCategory.Image,
        Aliases = ["tif", "tiff", "tagged image file format"], KnownExtensions = [".tif", ".tiff"],
        SupportsAlpha = true, SupportsAnimation = false, SupportsMultipage = true, SupportsIccProfile = true, SupportsMetadata = true,
        SortOrder = 50, TechnicalId = "TIFF"
    };

    public static readonly FormatDefinition Bmp = new()
    {
        Id = "bmp", Extension = ".bmp", DisplayName = "Windows Bitmap", Category = MediaCategory.Image,
        Aliases = ["bmp", "bitmap"], KnownExtensions = [".bmp", ".dib"], SupportsAlpha = true, SortOrder = 60,
        TechnicalId = "BMP"
    };

    public static readonly FormatDefinition Gif = new()
    {
        Id = "gif", Extension = ".gif", DisplayName = "Graphics Interchange Format", Category = MediaCategory.Image,
        Aliases = ["gif", "graphics interchange format"], KnownExtensions = [".gif"], SupportsAlpha = true,
        SupportsAnimation = true, SupportsMetadata = true, SortOrder = 70, TechnicalId = "GIF"
    };

    public static readonly FormatDefinition Ico = new()
    {
        Id = "ico", Extension = ".ico", DisplayName = "Windows Icon", Category = MediaCategory.Image,
        Aliases = ["ico", "icon", "windows icon"], KnownExtensions = [".ico"], SupportsAlpha = true,
        SupportsMultipage = true, SupportsMetadata = false, SortOrder = 80, TechnicalId = "ICO"
    };

    public static readonly FormatDefinition Heic = new()
    {
        Id = "heic", Extension = ".heic", DisplayName = "High Efficiency Image Container", Category = MediaCategory.Image,
        Aliases = ["heic", "heif", "high efficiency image format"], KnownExtensions = [".heic", ".heif"],
        IsLossy = true, SupportsAlpha = true, SupportsIccProfile = true, SupportsMetadata = true,
        SortOrder = 90, TechnicalId = "HEIC"
    };

    public static readonly FormatDefinition Jxl = new()
    {
        Id = "jxl", Extension = ".jxl", DisplayName = "JPEG XL", Category = MediaCategory.Image,
        Aliases = ["jxl", "jpeg xl"], KnownExtensions = [".jxl"], IsLossy = true, SupportsAlpha = true,
        SupportsAnimation = true, SupportsIccProfile = true, SupportsMetadata = true, SortOrder = 100, TechnicalId = "JXL"
    };

    public static readonly FormatDefinition Tga = new()
    {
        Id = "tga", Extension = ".tga", DisplayName = "Truevision TGA", Category = MediaCategory.Image,
        Aliases = ["tga", "targa"], KnownExtensions = [".tga", ".targa"], SupportsAlpha = true,
        SupportsMetadata = false, SortOrder = 110, TechnicalId = "TGA"
    };

    public static readonly FormatDefinition Pnm = new()
    {
        Id = "pnm", Extension = ".pnm", DisplayName = "Portable Anymap", Category = MediaCategory.Image,
        Aliases = ["pnm", "ppm", "pgm", "pbm", "portable pixmap"], KnownExtensions = [".pnm", ".ppm", ".pgm", ".pbm"],
        SupportsMetadata = false, SortOrder = 120, TechnicalId = "PNM"
    };

    public static readonly FormatDefinition Svg = new()
    {
        Id = "svg", Extension = ".svg", DisplayName = "Scalable Vector Graphics", Category = MediaCategory.Image,
        Aliases = ["svg", "scalable vector graphics"], KnownExtensions = [".svg", ".svgz"], SupportsAlpha = true,
        IsVector = true, SortOrder = 130, TechnicalId = "SVG"
    };

    public static readonly FormatDefinition Psd = new()
    {
        Id = "psd", Extension = ".psd", DisplayName = "Adobe Photoshop Document", Category = MediaCategory.Image,
        Aliases = ["psd", "photoshop"], KnownExtensions = [".psd", ".psb"], SupportsAlpha = true,
        SupportsMultipage = true, SupportsMetadata = true, SortOrder = 140, TechnicalId = "PSD"
    };

    public static readonly FormatDefinition Exr = new()
    {
        Id = "exr", Extension = ".exr", DisplayName = "OpenEXR", Category = MediaCategory.Image,
        Aliases = ["exr", "openexr"], KnownExtensions = [".exr"], SupportsAlpha = true, SupportsMetadata = false,
        SortOrder = 150, TechnicalId = "EXR"
    };

    public static readonly FormatDefinition Hdr = new()
    {
        Id = "hdr", Extension = ".hdr", DisplayName = "Radiance HDR", Category = MediaCategory.Image,
        Aliases = ["hdr", "radiance"], KnownExtensions = [".hdr", ".pic"], SupportsMetadata = false,
        SortOrder = 160, TechnicalId = "HDR"
    };

    public static readonly FormatDefinition Qoi = new()
    {
        Id = "qoi", Extension = ".qoi", DisplayName = "Quite OK Image", Category = MediaCategory.Image,
        Aliases = ["qoi", "quite ok image"], KnownExtensions = [".qoi"], SupportsAlpha = true,
        SupportsMetadata = false, SortOrder = 170, TechnicalId = "QOI"
    };

    public static readonly FormatDefinition Dds = new()
    {
        Id = "dds", Extension = ".dds", DisplayName = "DirectDraw Surface", Category = MediaCategory.Image,
        Aliases = ["dds", "directdraw surface"], KnownExtensions = [".dds"], SupportsAlpha = true,
        SupportsMetadata = false, SortOrder = 180, TechnicalId = "DDS"
    };

    public static readonly FormatDefinition Mp3 = new()
    {
        Id = "mp3", Extension = ".mp3", DisplayName = "MPEG Audio Layer III", Category = MediaCategory.Audio,
        Aliases = ["mp3", "mpeg audio layer 3"], KnownExtensions = [".mp3"], IsLossy = true, IsContainer = true,
        SortOrder = 10, TechnicalId = "mp3", MuxerName = "mp3"
    };

    public static readonly FormatDefinition Wav = new()
    {
        Id = "wav", Extension = ".wav", DisplayName = "Waveform Audio", Category = MediaCategory.Audio,
        Aliases = ["wav", "wave", "waveform audio"], KnownExtensions = [".wav", ".wave"], IsContainer = true,
        SortOrder = 20, TechnicalId = "wav", MuxerName = "wav"
    };

    public static readonly FormatDefinition Flac = new()
    {
        Id = "flac", Extension = ".flac", DisplayName = "Free Lossless Audio Codec", Category = MediaCategory.Audio,
        Aliases = ["flac", "free lossless audio codec"], KnownExtensions = [".flac"], IsContainer = true,
        SortOrder = 30, TechnicalId = "flac", MuxerName = "flac"
    };

    public static readonly FormatDefinition M4a = new()
    {
        Id = "m4a", Extension = ".m4a", DisplayName = "MPEG-4 Audio", Category = MediaCategory.Audio,
        Aliases = ["m4a", "mpeg 4 audio", "mp4 audio"], KnownExtensions = [".m4a"], IsLossy = true, IsContainer = true,
        SortOrder = 40, TechnicalId = "ipod", MuxerName = "ipod"
    };

    public static readonly FormatDefinition Aac = new()
    {
        Id = "aac", Extension = ".aac", DisplayName = "AAC Audio", Category = MediaCategory.Audio,
        Aliases = ["aac", "advanced audio coding", "adts"], KnownExtensions = [".aac", ".adts"], IsLossy = true,
        IsContainer = true, SortOrder = 50, TechnicalId = "adts", MuxerName = "adts"
    };

    public static readonly FormatDefinition Ogg = new()
    {
        Id = "ogg", Extension = ".ogg", DisplayName = "Ogg Audio", Category = MediaCategory.Audio,
        Aliases = ["ogg", "ogg vorbis", "vorbis"], KnownExtensions = [".ogg", ".oga"], IsLossy = true, IsContainer = true,
        SortOrder = 60, TechnicalId = "ogg", MuxerName = "ogg"
    };

    public static readonly FormatDefinition Opus = new()
    {
        Id = "opus", Extension = ".opus", DisplayName = "Opus Audio", Category = MediaCategory.Audio,
        Aliases = ["opus", "ogg opus"], KnownExtensions = [".opus"], IsLossy = true, IsContainer = true,
        SortOrder = 70, TechnicalId = "ogg", MuxerName = "ogg"
    };

    public static readonly FormatDefinition Mp4 = new()
    {
        Id = "mp4", Extension = ".mp4", DisplayName = "MPEG-4 Video", Category = MediaCategory.Video,
        Aliases = ["mp4", "mpeg 4", "mpeg4 video"], KnownExtensions = [".mp4", ".m4v"], IsLossy = true, IsContainer = true,
        SortOrder = 10, TechnicalId = "mp4", MuxerName = "mp4"
    };

    public static readonly FormatDefinition Mkv = new()
    {
        Id = "mkv", Extension = ".mkv", DisplayName = "Matroska Video", Category = MediaCategory.Video,
        Aliases = ["mkv", "matroska"], KnownExtensions = [".mkv", ".mka"], IsContainer = true,
        SortOrder = 20, TechnicalId = "matroska", MuxerName = "matroska"
    };

    public static readonly FormatDefinition Mov = new()
    {
        Id = "mov", Extension = ".mov", DisplayName = "QuickTime Movie", Category = MediaCategory.Video,
        Aliases = ["mov", "quicktime"], KnownExtensions = [".mov", ".qt"], IsLossy = true, IsContainer = true,
        SortOrder = 30, TechnicalId = "mov", MuxerName = "mov"
    };

    public static readonly FormatDefinition WebM = new()
    {
        Id = "webm", Extension = ".webm", DisplayName = "WebM Video", Category = MediaCategory.Video,
        Aliases = ["webm"], KnownExtensions = [".webm"], IsLossy = true, IsContainer = true,
        SortOrder = 40, TechnicalId = "webm", MuxerName = "webm"
    };

    public static readonly FormatDefinition Avi = new()
    {
        Id = "avi", Extension = ".avi", DisplayName = "Audio Video Interleaved", Category = MediaCategory.Video,
        Aliases = ["avi", "audio video interleaved"], KnownExtensions = [".avi"], IsLossy = true, IsContainer = true,
        SortOrder = 50, TechnicalId = "avi", MuxerName = "avi"
    };

    public static readonly FormatDefinition AnimatedGif = new()
    {
        Id = "gif", Extension = ".gif", DisplayName = "Animated GIF", Category = MediaCategory.Video,
        Aliases = ["gif", "animated gif", "graphics interchange format"], KnownExtensions = [".gif"],
        IsLossy = true, IsContainer = true, SupportsAnimation = true, SortOrder = 60, TechnicalId = "gif", MuxerName = "gif"
    };

    public static readonly IReadOnlyList<FormatDefinition> ImageFormats =
        [Png, Jpeg, WebP, Avif, Tiff, Bmp, Gif, Ico, Heic, Jxl, Tga, Pnm, Svg, Psd, Exr, Hdr, Qoi, Dds];

    public static readonly IReadOnlyList<FormatDefinition> AudioFormats =
        [Mp3, Wav, Flac, M4a, Aac, Ogg, Opus];

    public static readonly IReadOnlyList<FormatDefinition> VideoFormats =
        [Mp4, Mkv, Mov, WebM, Avi, AnimatedGif];

    public static readonly IReadOnlyList<FormatDefinition> All =
        [.. ImageFormats, .. AudioFormats, .. VideoFormats];

    private static readonly Dictionary<string, FormatDefinition> ById =
        All.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FormatDefinition> ByExtension = BuildExtensionMap();

    private static Dictionary<string, FormatDefinition> BuildExtensionMap()
    {
        var map = new Dictionary<string, FormatDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in All)
        {
            map[format.Extension] = format;
            foreach (var ext in format.KnownExtensions)
            {
                map.TryAdd(ext, format);
            }
        }
        // GIF is an image in the conversion category by default; video GIF output is explicit.
        map[".gif"] = Gif;
        return map;
    }

    public static FormatDefinition? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return ById.GetValueOrDefault(id.Trim().TrimStart('.'));
    }

    public static FormatDefinition? FindByExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;
        return ByExtension.GetValueOrDefault(ext);
    }

    public static MediaCategory DetectCategoryByExtension(string? extension)
    {
        var format = FindByExtension(extension);
        if (format is not null) return format.Category;
        var contentCategory = ContentFormatCatalog.DetectCategoryByExtension(extension);
        return contentCategory;
    }

    public static IReadOnlyList<string> GetInputExtensions(MediaCategory category)
    {
        if (category is MediaCategory.Document or MediaCategory.Ebook or MediaCategory.Archive)
            return ContentFormatCatalog.GetInputExtensions(category);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formats = category switch
        {
            MediaCategory.Image => ImageFormats,
            MediaCategory.Audio => AudioFormats,
            MediaCategory.Video => VideoFormats,
            _ => All
        };
        foreach (var f in formats)
        {
            set.Add(f.Extension);
            foreach (var e in f.KnownExtensions) set.Add(e);
        }

        if (category == MediaCategory.Image)
        {
            // Common image inputs that are readable but not offered as output targets.
            foreach (var e in new[] { ".psd", ".exr", ".hdr", ".dds", ".pcx", ".xcf", ".dng", ".cr2", ".nef", ".arw" })
                set.Add(e);
        }
        else if (category == MediaCategory.Audio)
        {
            foreach (var e in new[] { ".wma", ".aiff", ".aif", ".aifc", ".mka", ".amr", ".ac3", ".dts", ".caf", ".ape", ".wv" })
                set.Add(e);
        }
        else if (category == MediaCategory.Video)
        {
            foreach (var e in new[] { ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".3g2", ".ogv", ".flv", ".vob", ".mxf", ".f4v" })
                set.Add(e);
        }

        return set.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
