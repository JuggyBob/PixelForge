using PixelForge.Presentation.Services;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;

namespace PixelForge.Presentation.ViewModels;

/// <summary>
/// Owns the six Converter workspaces for the lifetime of the application. The
/// Converter page is transient, so keeping the staged files and category
/// settings here prevents navigation from discarding work.
/// </summary>
public sealed class ConverterStagingSession : IDisposable
{
    private bool _disposed;

    public ConverterStagingSession(ICapabilityRegistry registry, ISettingsService settings, IAppPaths paths,
        IFilePickerService filePicker)
    {
        Image = new ImageWorkspaceViewModel(registry, settings, paths, filePicker);
        Audio = new AudioWorkspaceViewModel(registry, settings, paths, filePicker);
        Video = new VideoWorkspaceViewModel(registry, settings, paths, filePicker);
        Documents = new DocumentWorkspaceViewModel(registry, settings, paths, filePicker);
        Ebooks = new EbookWorkspaceViewModel(registry, settings, paths, filePicker);
        Archives = new ArchiveWorkspaceViewModel(registry, settings, paths, filePicker);
        Workspaces = [Image, Audio, Video, Documents, Ebooks, Archives];
        foreach (var workspace in Workspaces)
            workspace.Changed += OnWorkspaceChanged;
    }

    public ImageWorkspaceViewModel Image { get; }
    public AudioWorkspaceViewModel Audio { get; }
    public VideoWorkspaceViewModel Video { get; }
    public DocumentWorkspaceViewModel Documents { get; }
    public EbookWorkspaceViewModel Ebooks { get; }
    public ArchiveWorkspaceViewModel Archives { get; }
    public IReadOnlyList<WorkspaceViewModel> Workspaces { get; }

    public int TotalFileCount => Workspaces.Sum(w => w.Files.Count);
    public bool HasFiles => TotalFileCount > 0;
    public bool HasQueuedFiles { get; private set; }
    public bool FormatsLoaded { get; set; }

    public event EventHandler? Changed;

    public WorkspaceViewModel WorkspaceFor(MediaCategory category) => category switch
    {
        MediaCategory.Audio => Audio,
        MediaCategory.Video => Video,
        MediaCategory.Document => Documents,
        MediaCategory.Ebook => Ebooks,
        MediaCategory.Archive => Archives,
        _ => Image
    };

    public IReadOnlyList<WorkspaceViewModel> WorkspacesWithFiles =>
        Workspaces.Where(w => w.Files.Count > 0).ToList();

    public void MarkQueued(IEnumerable<WorkspaceViewModel> queued)
    {
        var any = false;
        foreach (var workspace in queued)
        {
            if (workspace.Files.Count == 0) continue;
            workspace.ClearFiles();
            any = true;
        }

        if (!any) return;
        HasQueuedFiles = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAll()
    {
        foreach (var workspace in Workspaces)
            workspace.ClearFiles();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var workspace in Workspaces)
            workspace.Changed -= OnWorkspaceChanged;
    }
}
