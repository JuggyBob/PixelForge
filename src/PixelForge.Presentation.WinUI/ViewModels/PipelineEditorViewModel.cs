using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PixelForge.Application.Pipelines;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class PipelineConnectionViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PipelineEditorViewModel : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IPipelineStore _store;
    private readonly IOperationSettingsSerializer _serializer;
    private readonly PipelineValidator _validator;
    private readonly IPipelineRunService _runs;
    private readonly IMediaInspector _inspector;
    private readonly IThumbnailService _thumbnails;
    private readonly IFilePickerService _picker;
    private readonly IFolderScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly DispatcherQueue _dispatcher;
    private readonly Guid _pipelineId;
    private readonly DateTimeOffset _createdAt;
    private readonly string? _templateId;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private readonly object _historyGate = new();
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _validationCts;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _suppressNodeEvents;
    private bool _disposed;
    private DateTimeOffset _lastUndoAt;
    private string? _lastUndoKey;
    private string _saveStatusKey = "Saved";
    private PipelineValidationResult? _lastValidation;
    private PipelineRunSnapshot? _lastRun;
    private bool _showingRunStatus;
    private string _outputTypeKey = "Output";
    private int _revision;

    public PipelineEditorViewModel(PipelineDefinition definition, IPipelineStore store,
        IOperationSettingsSerializer serializer, PipelineValidator validator, IPipelineRunService runs,
        IMediaInspector inspector, IThumbnailService thumbnails, IFilePickerService picker, IFolderScanner scanner,
        IDialogService dialogs, INotificationService notifications, ISettingsService settings, IAppPaths paths,
        IModelManager models, IOcrModelManager ocrModels, ICapabilityRegistry registry)
    {
        _store = store;
        _serializer = serializer;
        _validator = validator;
        _runs = runs;
        _inspector = inspector;
        _thumbnails = thumbnails;
        _picker = picker;
        _scanner = scanner;
        _dialogs = dialogs;
        _notifications = notifications;
        _settings = settings;
        _paths = paths;
        Models = models;
        OcrModels = ocrModels;
        Registry = registry;
        _pipelineId = definition.Id;
        _createdAt = definition.CreatedAt;
        _templateId = definition.TemplateId;
        _name = definition.Name;
        _dispatcher = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        LoadNodes(definition);
        _runs.RunsChanged += OnRunsChanged;
        RefreshLocalization();
        UpdateSaveStatus();
        _ = RefreshValidationAsync();
    }

    public IModelManager Models { get; }
    public IOcrModelManager OcrModels { get; }
    public ICapabilityRegistry Registry { get; }
    public IOperationSettingsSerializer Serializer => _serializer;
    public ObservableCollection<PipelineNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<PipelineConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<PipelineValidationIssue> ValidationIssues { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _saveStatus = "Saved";
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _saveFailed;
    [ObservableProperty] private bool _isSaved = true;
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private PipelineNodeViewModel? _selectedNode;
    [ObservableProperty] private PipelineConnectionViewModel? _selectedConnection;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _runProgress;
    [ObservableProperty] private string _runMessage = "";
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private string _outputTypeText = "";
    [ObservableProperty] private string _primaryValidationMessage = "";

    public event EventHandler? GraphChanged;
    public event EventHandler? FitRequested;
    public event EventHandler<double>? ZoomRequested;

    public bool ShowSavedIndicator => IsSaved && !IsSaving && !SaveFailed;
    public bool ShowUnsavedIndicator => !IsSaved && !IsSaving && !SaveFailed;

    public PipelineInputNodeViewModel InputNode =>
        (PipelineInputNodeViewModel)Nodes.First(n => n.Kind == PipelineNodeKind.Input);

    public PipelineOutputNodeViewModel OutputNode =>
        (PipelineOutputNodeViewModel)Nodes.First(n => n.Kind == PipelineNodeKind.Output);

    public IReadOnlyList<PipelineActionNodeViewModel> ActionNodes =>
        Nodes.OfType<PipelineActionNodeViewModel>().OrderBy(n => n.X).ThenBy(n => n.Y).ToList();

    /// <summary>
    /// Every action-kind node in graph order, including unavailable operations
    /// loaded from disk. Keeping them in this sequence lets load/save and
    /// connection repair preserve a pipeline from a newer build.
    /// </summary>
    public IReadOnlyList<PipelineNodeViewModel> ActionChainNodes =>
        Nodes.Where(n => n.Kind == PipelineNodeKind.Action).OrderBy(n => n.X).ThenBy(n => n.Y).ToList();

    public IReadOnlyList<PipelineNodeViewModel> OrderedNodes
    {
        get
        {
            var result = new List<PipelineNodeViewModel> { InputNode };
            result.AddRange(ActionChainNodes);
            result.Add(OutputNode);
            return result;
        }
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(InputNode.FolderPath))
        {
            await LoadFolderFilesAsync(InputNode.FolderPath, InputNode.Recursive);
        }

        await RefreshValidationAsync(force: true);
        OnRunsChanged(this, EventArgs.Empty);
        RaiseGraphChanged();
    }

    public void SetName(string name)
    {
        if (string.Equals(Name, name, StringComparison.Ordinal)) return;
        RecordUndo("name");
        Name = name;
        MarkDirty();
        ScheduleSave();
    }

    /// <summary>
    /// Adds an independent action node at the requested world position. The
    /// node is deliberately not connected: the user builds the graph through
    /// the connector ports, and validation reports the temporary gap.
    /// </summary>
    public async Task InsertActionAtAsync(string actionId, double x, double y)
    {
        var descriptor = PipelineActionRegistry.Find(actionId);
        if (descriptor is null) return;

        RecordUndo("add:" + actionId);
        var settings = descriptor.CreateSettings();
        var definition = new PipelineNodeDefinition
        {
            Kind = PipelineNodeKind.Action,
            ActionId = descriptor.Id,
            X = x,
            Y = y,
            ConfigurationJson = _serializer.Serialize(settings)
        };
        var vm = CreateActionNode(definition, descriptor, settings);
        vm.ConfigurationChanged += OnNodeConfigurationChanged;
        Nodes.Add(vm);
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    public Task AddActionAsync(string actionId, double? x = null, double? y = null)
    {
        var previous = ActionNodes.LastOrDefault();
        var nodeX = x ?? (previous is null ? InputNode.X + 340 : previous.X + LayoutWidth(previous) + LayoutGap);
        var nodeY = y ?? (previous?.Y ?? InputNode.Y);
        return InsertActionAtAsync(actionId, nodeX, nodeY);
    }

    public async Task<bool> AddInputPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return false;
        RecordUndo("input:add");
        if (InputNode.IsFolderMode) InputNode.SetFolder(null, InputNode.Recursive);
        await InputNode.AddPathsAsync(list);
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
        return true;
    }

    public async Task<bool> ChooseInputFilesAsync()
    {
        var categories = ActionNodes.Count == 0
            ? new[] { MediaCategory.Image, MediaCategory.Audio, MediaCategory.Video,
                MediaCategory.Document, MediaCategory.Ebook, MediaCategory.Archive }
            : ActionNodes[0].Descriptor.InputTypes
                .Where(t => t is not PipelineMediaType.Folder and not PipelineMediaType.Unknown)
                .Select(PipelineActionRegistry.ToMediaCategory)
                .Where(c => c != MediaCategory.Unknown)
                .Distinct()
                .ToArray();
        var paths = await _picker.PickFilesAsync(categories);
        return await AddInputPathsAsync(paths);
    }

    public async Task RemoveInputFileAsync(PipelineInputFileViewModel file)
    {
        RecordUndo("input:remove:" + file.Path);
        InputNode.RemoveFile(file);
        MarkDirty();
        await ValidateAndSaveAsync();
    }

    public async Task InputConfigurationChangedAsync()
    {
        RecordUndo("input:config");
        MarkDirty();
        await ValidateAndSaveAsync();
    }

    public async Task<bool> ChooseInputFolderAsync()
    {
        var folder = await _picker.PickFolderAsync(InputNode.FolderPath);
        if (string.IsNullOrWhiteSpace(folder)) return false;
        await SetInputFolderAsync(folder, InputNode.Recursive);
        return true;
    }

    public async Task SetInputFolderAsync(string folder, bool recursive = true)
    {
        if (!Directory.Exists(folder)) return;
        RecordUndo("input:folder");
        InputNode.ClearFiles();
        InputNode.SetFolder(folder, recursive);
        await LoadFolderFilesAsync(folder, recursive);
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    public async Task ClearInputAsync()
    {
        RecordUndo("input:clear");
        InputNode.SetFolder(null, InputNode.Recursive);
        InputNode.ClearFiles();
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    private async Task LoadFolderFilesAsync(string folder, bool recursive)
    {
        if (!Directory.Exists(folder)) return;
        var categories = new[]
        {
            MediaCategory.Image, MediaCategory.Audio, MediaCategory.Video,
            MediaCategory.Document, MediaCategory.Ebook, MediaCategory.Archive
        };
        var paths = new List<string>();
        foreach (var category in categories)
        {
            var result = await _scanner.ScanAsync(new FolderScanRequest
            {
                FolderPath = folder,
                Recursive = recursive,
                TargetCategory = category
            });
            paths.AddRange(result.IncludedFiles.Select(f => f.Path));
        }

        await InputNode.AddPathsAsync(paths.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public void BeginNodeMove(PipelineNodeViewModel node) => RecordUndo("move:" + node.Id);

    public async Task CompleteNodeMoveAsync(PipelineNodeViewModel node)
    {
        node.X = Snap(node.X);
        node.Y = Snap(node.Y);
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    public void UpdateNodePosition(PipelineNodeViewModel node, double x, double y)
    {
        node.X = x;
        node.Y = y;
    }

    public async Task RemoveSelectedNodeAsync()
    {
        var node = SelectedNode;
        if (node is null || node.IsBoundary) return;
        var confirm = await _dialogs.ConfirmAsync("Remove operation?",
            Loc.F("Remove {0} from this pipeline? Its configuration will be lost.", node.Title),
            "Remove", "Cancel", destructive: true);
        if (!confirm) return;
        RecordUndo("remove:" + node.Id);
        node.ConfigurationChanged -= OnNodeConfigurationChanged;
        foreach (var connection in Connections.Where(c =>
                     c.FromNodeId == node.Id || c.ToNodeId == node.Id).ToList())
            Connections.Remove(connection);
        Nodes.Remove(node);
        SelectedNode = null;
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    public async Task DeleteConnectionAsync(PipelineConnectionViewModel connection)
    {
        RecordUndo("connection:remove");
        Connections.Remove(connection);
        if (ReferenceEquals(SelectedConnection, connection)) SelectedConnection = null;
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    public async Task<bool> ConnectNodesAsync(string fromId, string toId)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal)) return false;
        var fromNode = Nodes.FirstOrDefault(n => n.Id == fromId);
        var toNode = Nodes.FirstOrDefault(n => n.Id == toId);
        if (fromNode is null || toNode is null) return false;
        if (fromNode.Kind == PipelineNodeKind.Output || toNode.Kind == PipelineNodeKind.Input) return false;

        PipelineMediaType produced;
        if (fromNode.Kind == PipelineNodeKind.Input)
        {
            produced = InputNode.IsFolderMode
                ? PipelineMediaType.Folder
                : InputNode.Files.FirstOrDefault() is { } file
                    ? PipelineActionRegistry.ToPipelineType(file.Category)
                    : PipelineMediaType.Unknown;
        }
        else if (fromNode is PipelineActionNodeViewModel fromAction)
        {
            if (fromAction.IsTerminal && toNode.Kind == PipelineNodeKind.Action)
            {
                _notifications.Notify("Operation is not compatible",
                    Loc.T("Pipeline.Validation.TerminalActionNotLast"), NotificationSeverity.Warning);
                return false;
            }

            produced = PipelineActionRegistry.OutputTypeFor(fromAction.Descriptor, fromAction.Settings);
        }
        else
        {
            return false;
        }

        if (toNode is PipelineActionNodeViewModel toAction &&
            produced != PipelineMediaType.Unknown &&
            !toAction.Descriptor.InputTypes.Contains(produced))
        {
            _notifications.Notify("Operation is not compatible",
                Loc.F("{0} cannot accept {1} input at this position.",
                    Loc.T(toAction.Descriptor.TitleKey), Loc.T(OutputTypeKey(produced))),
                NotificationSeverity.Warning);
            return false;
        }

        var adjacent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var connection in Connections)
        {
            if (string.Equals(connection.FromNodeId, fromId, StringComparison.Ordinal) ||
                string.Equals(connection.ToNodeId, toId, StringComparison.Ordinal))
                continue;
            if (!adjacent.TryGetValue(connection.FromNodeId, out var targets))
            {
                targets = [];
                adjacent[connection.FromNodeId] = targets;
            }

            targets.Add(connection.ToNodeId);
        }

        var queue = new Queue<string>();
        queue.Enqueue(toId);
        var visited = new HashSet<string>(StringComparer.Ordinal) { toId };
        while (queue.Count > 0)
        {
            var cursor = queue.Dequeue();
            if (!adjacent.TryGetValue(cursor, out var targets)) continue;
            foreach (var next in targets)
            {
                if (string.Equals(next, fromId, StringComparison.Ordinal))
                {
                    _notifications.Notify("Operation is not compatible",
                        Loc.T("Pipeline.Validation.Cycle"), NotificationSeverity.Warning);
                    return false;
                }

                if (visited.Add(next)) queue.Enqueue(next);
            }
        }

        RecordUndo("connection:create");
        var replaced = Connections
            .Where(c => c.FromNodeId == fromId || c.ToNodeId == toId)
            .ToList();
        foreach (var existing in replaced) Connections.Remove(existing);
        Connections.Add(new PipelineConnectionViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNodeId = fromId,
            ToNodeId = toId
        });
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
        return true;
    }

    public async Task RepairConnectionsAsync()
    {
        RecordUndo("connection:repair");
        RebuildConnections();
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
    }

    private const double LayoutGap = 60;
    private const double LayoutStartX = 80;
    private const double LayoutY = 280;

    private static double LayoutWidth(PipelineNodeViewModel node) => node.IsExpanded ? 380 : 280;

    private List<PipelineActionNodeViewModel> LayoutOrder()
    {
        var ordered = new List<PipelineActionNodeViewModel>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { InputNode.Id };
        PipelineNodeViewModel current = InputNode;
        while (true)
        {
            var nextId = Connections.FirstOrDefault(c => c.FromNodeId == current.Id)?.ToNodeId;
            if (nextId is null) break;
            var next = Nodes.FirstOrDefault(n => n.Id == nextId);
            if (next is null || !visited.Add(next.Id)) break;
            if (next.Kind == PipelineNodeKind.Output) break;
            if (next is PipelineActionNodeViewModel action) ordered.Add(action);
            current = next;
        }

        foreach (var action in ActionNodes.Where(a => !visited.Contains(a.Id)))
            ordered.Add(action);
        return ordered;
    }

    [RelayCommand]
    public async Task AutoLayoutAsync()
    {
        RecordUndo("layout");
        var x = LayoutStartX;
        InputNode.X = x;
        InputNode.Y = LayoutY;
        x += LayoutWidth(InputNode) + LayoutGap;
        foreach (var action in LayoutOrder())
        {
            action.X = x;
            action.Y = LayoutY;
            x += LayoutWidth(action) + LayoutGap;
        }

        OutputNode.X = x;
        OutputNode.Y = LayoutY;
        MarkDirty();
        await ValidateAndSaveAsync();
        RaiseGraphChanged();
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleNodeExpansion(PipelineNodeViewModel node)
    {
        RecordUndo("expand:" + node.Id);
        node.IsExpanded = !node.IsExpanded;
        MarkDirty();
        ScheduleSave();
    }

    [RelayCommand]
    private void Undo()
    {
        string? snapshot;
        lock (_historyGate)
        {
            if (_undo.Count == 0) return;
            snapshot = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(SerializeDefinition(BuildDefinition()));
            _lastUndoKey = null;
            _lastUndoAt = default;
        }

        ApplySnapshot(snapshot);
    }

    [RelayCommand]
    private void Redo()
    {
        string? snapshot;
        lock (_historyGate)
        {
            if (_redo.Count == 0) return;
            snapshot = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(SerializeDefinition(BuildDefinition()));
            _lastUndoKey = null;
            _lastUndoAt = default;
        }

        ApplySnapshot(snapshot);
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveNowAsync(force: true);

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedConnection is not null)
        {
            await DeleteConnectionAsync(SelectedConnection);
            return;
        }

        if (SelectedNode is { IsBoundary: false }) await RemoveSelectedNodeAsync();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        if (_runs.Runs.Any(r => r.PipelineId == _pipelineId && !r.IsTerminal))
        {
            _notifications.Notify("Pipeline already running", Name, NotificationSeverity.Info);
            return;
        }

        await SaveNowAsync(force: true);
        var (plan, validation) = await _validator.BuildPlanAsync(BuildDefinition());
        if (plan is null)
        {
            ShowValidation(validation);
            return;
        }

        try
        {
            await _runs.StartAsync(BuildDefinition());
            HasRun = true;
            IsRunning = true;
            RunProgress = 0;
            RunMessage = Loc.T("Preparing");
            _notifications.Notify("Pipeline started", Name, NotificationSeverity.Info);
        }
        catch (PipelineValidationException ex)
        {
            ShowValidation(ex.Validation);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Pipeline could not start", ex.Message, ex.ToString());
        }
    }

    [RelayCommand]
    private async Task CancelRunAsync()
    {
        var run = _runs.Runs.FirstOrDefault(r => r.PipelineId == _pipelineId && !r.IsTerminal);
        if (run is not null) await _runs.CancelAsync(run.Id);
    }

    [RelayCommand]
    private void FitToView() => FitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ZoomIn() => ZoomRequested?.Invoke(this, 1.2);

    [RelayCommand]
    private void ZoomOut() => ZoomRequested?.Invoke(this, 1 / 1.2);

    public async Task RefreshValidationAsync(bool force = false)
    {
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var token = _validationCts.Token;
        try
        {
            if (!force) await Task.Delay(180, token);
            var result = _validator.ValidateStructure(BuildDefinition());
            if (token.IsCancellationRequested) return;
            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(() => ApplyValidation(result));
                return;
            }

            ApplyValidation(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit.
        }
    }

    private void ApplyValidation(PipelineValidationResult result)
    {
        _lastValidation = result;
        ValidationIssues.Clear();
        foreach (var issue in result.Issues) ValidationIssues.Add(issue);
        IsValid = result.IsValid;
        UpdateValidationSummary();

        if (!_showingRunStatus)
        {
            var invalidIds = result.Issues
                .Where(i => i.Severity == PipelineIssueSeverity.Error && i.NodeId is not null)
                .Select(i => i.NodeId!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var node in Nodes)
            {
                node.Status = invalidIds.Contains(node.Id)
                    ? PipelineNodeStatus.Invalid
                    : node.Kind == PipelineNodeKind.Input && InputNode.Files.Count == 0 && !InputNode.IsFolderMode
                        ? PipelineNodeStatus.NotConfigured
                        : node.Kind == PipelineNodeKind.Output && string.IsNullOrWhiteSpace(OutputNode.Directory)
                            ? PipelineNodeStatus.NotConfigured
                            : PipelineNodeStatus.Ready;
            }
        }

        _outputTypeKey = OutputTypeKey(CurrentOutputType);
        OutputTypeText = result.IsValid ? Loc.T(_outputTypeKey) : "";
        OutputNode.IsFolderOutput = CurrentOutputType == PipelineMediaType.Folder;
        UpdatePrimaryValidationMessage();
        UpdateCanUndoRedo();
        RaiseGraphChanged();
    }

    private PipelineMediaType CurrentOutputType
    {
        get
        {
            var actions = ActionNodes;
            if (actions.Count > 0)
                return PipelineActionRegistry.OutputTypeFor(actions[^1].Descriptor, actions[^1].Settings);
            return InputNode.Files.FirstOrDefault() is { } file
                ? PipelineActionRegistry.ToPipelineType(file.Category)
                : PipelineMediaType.Unknown;
        }
    }

    private void UpdateValidationSummary()
    {
        if (_lastValidation is null)
        {
            ValidationSummary = "";
            return;
        }

        ValidationSummary = _lastValidation.IsValid
            ? (_lastValidation.WarningCount > 0
                ? Loc.P("Count.Warning", _lastValidation.WarningCount)
                : Loc.T("Ready"))
            : Loc.P("Count.Error", _lastValidation.ErrorCount);
    }

    private void UpdatePrimaryValidationMessage() =>
        PrimaryValidationMessage = _lastValidation?.Issues
            .Where(i => i.Severity == PipelineIssueSeverity.Error)
            .Select(LocalizeIssue)
            .FirstOrDefault() ?? "";

    private void ShowValidation(PipelineValidationResult validation)
    {
        var details = string.Join("\n", validation.Issues
            .Where(i => i.Severity == PipelineIssueSeverity.Error)
            .Take(8)
            .Select(LocalizeIssue));
        _ = _dialogs.ShowErrorAsync("Pipeline needs attention",
            "Fix the highlighted operations before running this pipeline.", details);
    }

    private string LocalizeIssue(PipelineValidationIssue issue)
    {
        var node = issue.NodeId is null ? null : Nodes.FirstOrDefault(n => n.Id == issue.NodeId);
        var prefix = node is null ? "" : $"{node.Title}: ";
        var text = Loc.T("Pipeline.Validation." + issue.Code);
        return prefix + text + (string.IsNullOrWhiteSpace(issue.Detail) ? "" : $" ({issue.Detail})");
    }

    private static string OutputTypeKey(PipelineMediaType type) => type switch
    {
        PipelineMediaType.Image => "Images",
        PipelineMediaType.Audio => "Audio",
        PipelineMediaType.Video => "Video",
        PipelineMediaType.Document => "Documents",
        PipelineMediaType.Ebook => "E-books",
        PipelineMediaType.Archive => "Archives",
        PipelineMediaType.Folder => "Folders",
        _ => "Output"
    };

    public PipelineMediaType InputTypeAtEnd => GetTypeAtInsertion(ActionNodes.Count);

    public bool CanAddAction(PipelineActionDescriptor descriptor)
    {
        if (ActionNodes.LastOrDefault()?.IsTerminal == true) return false;
        if (ActionNodes.Count == 0 && InputNode.IsFolderMode &&
            descriptor.InputTypes.Contains(PipelineMediaType.Folder))
            return true;
        var type = InputTypeAtEnd;
        return type == PipelineMediaType.Unknown || descriptor.InputTypes.Contains(type);
    }

    private PipelineMediaType GetTypeAtInsertion(int actionIndex)
    {
        var actions = ActionNodes;
        var index = Math.Clamp(actionIndex, 0, actions.Count);
        if (actions.Count == 0)
            return InputNode.Files.FirstOrDefault() is { } file
                ? PipelineActionRegistry.ToPipelineType(file.Category)
                : PipelineMediaType.Unknown;
        if (index == 0)
            return InputNode.Files.FirstOrDefault() is { } input
                ? PipelineActionRegistry.ToPipelineType(input.Category)
                : PipelineMediaType.Unknown;
        var previous = actions[index - 1];
        return PipelineActionRegistry.OutputTypeFor(previous.Descriptor, previous.Settings);
    }

    private async Task ValidateAndSaveAsync()
    {
        await RefreshValidationAsync(force: true);
        ScheduleSave();
    }

    private void RebuildConnections()
    {
        var oldIds = new Dictionary<(string From, string To), string>();
        foreach (var connection in Connections)
            oldIds.TryAdd((connection.FromNodeId, connection.ToNodeId), connection.Id);
        Connections.Clear();
        var ordered = OrderedNodes;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var from = ordered[i];
            var to = ordered[i + 1];
            var id = oldIds.TryGetValue((from.Id, to.Id), out var existing) ? existing : Guid.NewGuid().ToString("N");
            Connections.Add(new PipelineConnectionViewModel { Id = id, FromNodeId = from.Id, ToNodeId = to.Id });
        }
    }

    private void LoadNodes(PipelineDefinition definition)
    {
        _suppressNodeEvents = true;
        try
        {
            Nodes.Clear();
            Connections.Clear();
            foreach (var nodeDefinition in definition.Nodes)
            {
                var node = CreateNodeViewModel(nodeDefinition);
                if (node is null) continue;
                node.ConfigurationChanged += OnNodeConfigurationChanged;
                Nodes.Add(node);
            }

            if (Nodes.All(n => n.Kind != PipelineNodeKind.Input))
            {
                var input = new PipelineInputNodeViewModel(new PipelineNodeDefinition
                {
                    Kind = PipelineNodeKind.Input,
                    X = 80,
                    Y = 280
                }, _inspector, _thumbnails);
                input.ConfigurationChanged += OnNodeConfigurationChanged;
                Nodes.Add(input);
            }

            if (Nodes.All(n => n.Kind != PipelineNodeKind.Output))
            {
                var output = new PipelineOutputNodeViewModel(new PipelineNodeDefinition
                {
                    Kind = PipelineNodeKind.Output,
                    X = 560,
                    Y = 280
                }, _picker, _settings.Settings.General.DefaultOutputDirectory);
                output.ConfigurationChanged += OnNodeConfigurationChanged;
                Nodes.Add(output);
            }

            var seen = new HashSet<(string From, string To)>();
            foreach (var connection in definition.Connections)
            {
                if (!seen.Add((connection.FromNodeId, connection.ToNodeId))) continue;
                if (Nodes.Any(n => n.Id == connection.FromNodeId) &&
                    Nodes.Any(n => n.Id == connection.ToNodeId))
                {
                    Connections.Add(new PipelineConnectionViewModel
                    {
                        Id = connection.Id,
                        FromNodeId = connection.FromNodeId,
                        ToNodeId = connection.ToNodeId
                    });
                }
            }

        }
        finally
        {
            _suppressNodeEvents = false;
        }
    }

    private PipelineNodeViewModel? CreateNodeViewModel(PipelineNodeDefinition definition)
    {
        switch (definition.Kind)
        {
            case PipelineNodeKind.Input:
                return new PipelineInputNodeViewModel(definition, _inspector, _thumbnails);
            case PipelineNodeKind.Output:
                return new PipelineOutputNodeViewModel(definition, _picker,
                    _settings.Settings.General.DefaultOutputDirectory);
            case PipelineNodeKind.Action:
                var descriptor = PipelineActionRegistry.Find(definition.ActionId);
                if (descriptor is null)
                {
                    return new PipelineUnavailableNodeViewModel(definition);
                }

                OperationSettings settings;
                try
                {
                    settings = string.IsNullOrWhiteSpace(definition.ConfigurationJson)
                        ? descriptor.CreateSettings()
                        : _serializer.Deserialize(descriptor.Operation, definition.ConfigurationJson);
                }
                catch
                {
                    settings = descriptor.CreateSettings();
                }

                return CreateActionNode(definition, descriptor, settings);
        }

        return null;
    }

    private PipelineActionNodeViewModel CreateActionNode(PipelineNodeDefinition definition,
        PipelineActionDescriptor descriptor, OperationSettings settings)
    {
        var vm = new PipelineActionNodeViewModel(definition, descriptor, settings, _serializer);
        return vm;
    }

    private void OnNodeConfigurationChanged(object? sender, EventArgs e)
    {
        if (_suppressNodeEvents) return;
        MarkDirty();
        _ = RefreshValidationAsync();
        ScheduleSave();
    }

    private void MarkDirty()
    {
        _showingRunStatus = false;
        _revision++;
        IsDirty = true;
        _saveStatusKey = "Unsaved changes";
        UpdateSaveStatus();
    }

    private void UpdateSaveStatus()
    {
        SaveStatus = Loc.T(_saveStatusKey);
        IsSaving = _saveStatusKey == "Saving…";
        SaveFailed = _saveStatusKey == "Save failed";
        IsSaved = _saveStatusKey == "Saved" && !IsSaving && !SaveFailed;
        OnPropertyChanged(nameof(ShowSavedIndicator));
        OnPropertyChanged(nameof(ShowUnsavedIndicator));
    }

    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        _ = SaveDebouncedAsync(_saveCts.Token);
    }

    private async Task SaveDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(650, token);
            if (!token.IsCancellationRequested) await SaveNowAsync(force: false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit.
        }
    }

    public async Task SaveNowAsync(bool force)
    {
        if (!IsDirty && !force) return;
        await _saveGate.WaitAsync();
        try
        {
            if (!IsDirty && !force) return;
            var revision = _revision;
            _saveStatusKey = "Saving…";
            UpdateSaveStatus();
            var definition = BuildDefinition();
            await _store.SaveAsync(definition);
            UpdateCanUndoRedo();
            OnPropertyChanged(nameof(Name));
            if (_revision != revision)
            {
                _saveStatusKey = "Unsaved changes";
                UpdateSaveStatus();
                ScheduleSave();
                return;
            }

            IsDirty = false;
            _saveStatusKey = "Saved";
            UpdateSaveStatus();
        }
        catch (Exception ex)
        {
            _saveStatusKey = "Save failed";
            UpdateSaveStatus();
            await _dialogs.ShowErrorAsync("Pipeline could not be saved", ex.Message, ex.ToString());
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task FlushAsync()
    {
        _saveCts?.Cancel();
        if (IsDirty) await SaveNowAsync(force: true);
    }

    public PipelineDefinition BuildDefinition() => new()
    {
        SchemaVersion = PipelineDefinition.CurrentSchemaVersion,
        Id = _pipelineId,
        Name = Name,
        TemplateId = _templateId,
        CreatedAt = _createdAt,
        ModifiedAt = DateTimeOffset.Now,
        Nodes = Nodes.Select(n => n.ToDefinition()).ToList(),
        Connections = Connections.Select(c => new PipelineConnectionDefinition
        {
            Id = c.Id,
            FromNodeId = c.FromNodeId,
            ToNodeId = c.ToNodeId
        }).ToList()
    };

    public void RecordUndo(string key)
    {
        if (_suppressNodeEvents) return;
        lock (_historyGate)
        {
            var now = DateTimeOffset.Now;
            if (string.Equals(key, _lastUndoKey, StringComparison.Ordinal) &&
                now - _lastUndoAt < TimeSpan.FromSeconds(1))
            {
                _lastUndoAt = now;
                return;
            }

            _undo.Add(SerializeDefinition(BuildDefinition()));
            while (_undo.Count > 120) _undo.RemoveAt(0);
            _redo.Clear();
            _lastUndoKey = key;
            _lastUndoAt = now;
        }

        UpdateCanUndoRedo();
    }

    public void RecordSettingChange(PipelineActionNodeViewModel node, string property) =>
        RecordUndo($"setting:{node.Id}:{property}");

    private string SerializeDefinition(PipelineDefinition definition) =>
        JsonSerializer.Serialize(definition, SnapshotJson);

    private void ApplySnapshot(string json)
    {
        var definition = JsonSerializer.Deserialize<PipelineDefinition>(json, SnapshotJson);
        if (definition is null) return;
        _suppressNodeEvents = true;
        try
        {
            foreach (var node in Nodes) node.ConfigurationChanged -= OnNodeConfigurationChanged;
            Nodes.Clear();
            Connections.Clear();
            foreach (var nodeDefinition in definition.Nodes)
            {
                var node = CreateNodeViewModel(nodeDefinition);
                if (node is null) continue;
                node.ConfigurationChanged += OnNodeConfigurationChanged;
                Nodes.Add(node);
            }

            foreach (var connection in definition.Connections)
            {
                Connections.Add(new PipelineConnectionViewModel
                {
                    Id = connection.Id,
                    FromNodeId = connection.FromNodeId,
                    ToNodeId = connection.ToNodeId
                });
            }

            Name = definition.Name;
            SelectedNode = null;
            SelectedConnection = null;
            _lastUndoKey = null;
            _lastUndoAt = default;
        }
        finally
        {
            _suppressNodeEvents = false;
        }

        _showingRunStatus = false;
        MarkDirty();
        _ = RefreshValidationAsync(force: true);
        ScheduleSave();
        RaiseGraphChanged();
    }

    private void UpdateCanUndoRedo()
    {
        lock (_historyGate)
        {
            CanUndo = _undo.Count > 0;
            CanRedo = _redo.Count > 0;
        }
    }

    public void SelectNode(PipelineNodeViewModel? node)
    {
        if (SelectedNode is not null) SelectedNode.IsSelected = false;
        if (SelectedConnection is not null)
        {
            SelectedConnection.IsSelected = false;
            SelectedConnection = null;
        }

        SelectedNode = node;
        if (node is not null) node.IsSelected = true;
        // Selection is a visual-only change: the node control updates its own
        // border and the canvas listens to IsSelected. Raising GraphChanged
        // here rebuilt every edge and the action palette on each pan start.
    }

    public void SelectConnection(PipelineConnectionViewModel? connection)
    {
        if (SelectedConnection is not null) SelectedConnection.IsSelected = false;
        if (SelectedNode is not null) SelectedNode.IsSelected = false;
        SelectedNode = null;
        SelectedConnection = connection;
        if (connection is not null) connection.IsSelected = true;
        RaiseGraphChanged();
    }

    public void RefreshLocalization()
    {
        foreach (var node in Nodes) node.RefreshLocalization();
        UpdateSaveStatus();
        UpdateValidationSummary();
        UpdatePrimaryValidationMessage();
        UpdateRunMessage();
        OutputTypeText = IsValid ? Loc.T(_outputTypeKey) : "";
    }

    private void OnRunsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnRunsChanged(sender, e));
            return;
        }

        var run = _runs.Runs.FirstOrDefault(r => r.PipelineId == _pipelineId);
        if (run is null)
        {
            IsRunning = false;
            return;
        }

        _lastRun = run;
        HasRun = true;
        IsRunning = !run.IsTerminal;
        RunProgress = run.Progress;
        UpdateRunMessage();
        ApplyRunStatus(run);
    }

    private void UpdateRunMessage()
    {
        if (_lastRun is null)
        {
            RunMessage = "";
            return;
        }

        RunMessage = _lastRun.State switch
        {
            PipelineRunState.Completed => Loc.T("Completed"),
            PipelineRunState.CompletedWithFailures => Loc.T("Completed with failures"),
            PipelineRunState.Cancelled => Loc.T("Cancelled"),
            PipelineRunState.Failed => Loc.T("Failed"),
            _ => Loc.F("Step {0} of {1}", Math.Max(1, _lastRun.StepIndex), Math.Max(1, _lastRun.StepCount))
        };
    }

    private void ApplyRunStatus(PipelineRunSnapshot run)
    {
        _showingRunStatus = true;
        var actions = ActionNodes;
        for (var i = 0; i < actions.Count; i++)
        {
            actions[i].Status = run.State switch
            {
                PipelineRunState.Completed or PipelineRunState.CompletedWithFailures when i < run.StepCount =>
                    PipelineNodeStatus.Completed,
                _ when i + 1 < run.StepIndex => PipelineNodeStatus.Completed,
                _ when i + 1 == run.StepIndex && !run.IsTerminal => PipelineNodeStatus.Running,
                _ when i + 1 == run.StepIndex && run.State == PipelineRunState.Failed => PipelineNodeStatus.Failed,
                _ => actions[i].Status
            };
        }

        if (run.IsTerminal) _ = RefreshValidationAsync(force: true);
        RaiseGraphChanged();
    }

    private void RaiseGraphChanged() => GraphChanged?.Invoke(this, EventArgs.Empty);

    private static double Snap(double value) => Math.Round(value / 20d) * 20d;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runs.RunsChanged -= OnRunsChanged;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _validationCts?.Cancel();
        _validationCts?.Dispose();
    }
}
