using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Presentation.Localization;
using Windows.Storage.Pickers;

namespace PixelForge.Presentation.Services;

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickFilesAsync(MediaCategory category);
    Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<MediaCategory> categories);
    Task<IReadOnlyList<string>> PickUpscaleImagesAsync();
    Task<string?> PickFolderAsync(string? initialDirectory = null);
    Task<string?> PickModelFileAsync();
    Task<string?> PickFontFileAsync();
    Task<string?> PickJsonSaveFileAsync(string suggestedName);
    Task<string?> PickJsonOpenFileAsync();
    Task<string?> PickTextSaveFileAsync(string suggestedName);
}

public sealed class FilePickerService : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(MediaCategory category)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = category switch
            {
                MediaCategory.Audio => PickerLocationId.MusicLibrary,
                MediaCategory.Video => PickerLocationId.VideosLibrary,
                _ => PickerLocationId.PicturesLibrary
            }
        };
        foreach (var extension in FormatCatalog.GetInputExtensions(category))
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (category == MediaCategory.Unknown)
        {
            foreach (var extension in FormatCatalog.GetInputExtensions(MediaCategory.Image)
                         .Concat(FormatCatalog.GetInputExtensions(MediaCategory.Audio))
                         .Concat(FormatCatalog.GetInputExtensions(MediaCategory.Video)))
                picker.FileTypeFilter.Add(extension);
        }

        if (category == MediaCategory.Audio)
        {
            foreach (var extension in FormatCatalog.GetInputExtensions(MediaCategory.Video))
                picker.FileTypeFilter.Add(extension);
        }

        Initialize(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<MediaCategory> categories)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        var extensions = categories
            .Where(c => c != MediaCategory.Unknown)
            .SelectMany(FormatCatalog.GetInputExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extensions.Count == 0)
            extensions.Add("*");
        foreach (var extension in extensions)
            picker.FileTypeFilter.Add(extension);
        Initialize(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<string>> PickUpscaleImagesAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        foreach (var extension in FormatCatalog.GetInputExtensions(MediaCategory.Image))
            picker.FileTypeFilter.Add(extension);
        Initialize(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
    }

    public async Task<string?> PickFolderAsync(string? initialDirectory = null)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = Loc.T("Use this folder")
        };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<string?> PickModelFileAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".onnx");
        Initialize(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickFontFileAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".ttf");
        picker.FileTypeFilter.Add(".otf");
        Initialize(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickJsonSaveFileAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        Initialize(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickTextSaveFileAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        Initialize(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickJsonOpenFileAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".json");
        Initialize(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static void Initialize(object picker)
    {
        var window = App.MainWindow;
        if (window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
