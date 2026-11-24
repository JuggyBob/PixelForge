using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelForge.Application.Pipelines;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Presentation.Localization;
using PixelForge.Presentation.Services;

namespace PixelForge.Presentation.ViewModels;

public sealed partial class PipelineTemplateItemViewModel : ObservableObject
{
    public PipelineTemplateItemViewModel(PipelineTemplate template)
    {
        Template = template;
        RefreshLocalization();
    }

    public PipelineTemplate Template { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    public string IconGlyph => Template.IconGlyph;

    public void RefreshLocalization()
    {
        Name = Loc.T(Template.NameKey);
        Description = Loc.T(Template.DescriptionKey);
    }
}

public sealed partial class PipelineListItemViewModel : ObservableObject
{
    public PipelineListItemViewModel(PipelineDefinition definition)
    {
        Definition = definition;
        RefreshLocalization();
    }

    public PipelineDefinition Definition { get; }
    public string Name => Definition.Name;
    public Guid Id => Definition.Id;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _modifiedText = "";
    [ObservableProperty] private string _runText = "";

    public void RefreshLocalization()
    {
        var count = Definition.Nodes.Count(n => n.Kind == PipelineNodeKind.Action);
        Summary = Loc.P("Count.Operation", count);
        ModifiedText = LocalizationService.FormatDateTime(Definition.ModifiedAt);
    }
}

public sealed partial class PipelinesViewModel : ObservableObject, IDisposable
{
    private readonly IPipelineStore _store;
    private readonly IPipelineRunService _runs;
    private readonly IOperationSettingsSerializer _serializer;
    private readonly PipelineValidator _validator;
    private readonly IMediaInspector _inspector;
    private readonly IThumbnailService _thumbnails;
    private readonly IFilePickerService _picker;
    private readonly IFolderScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IModelManager _models;
    private readonly IOcrModelManager _ocrModels;
    private readonly ICapabilityRegistry _registry;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public PipelinesViewModel(IPipelineStore store, IPipelineRunService runs,
        IOperationSettingsSerializer serializer, PipelineValidator validator, IMediaInspector inspector,
        IThumbnailService thumbnails, IFilePickerService picker, IFolderScanner scanner, IDialogService dialogs,
        INotificationService notifications, ISettingsService settings, IAppPaths paths, IModelManager models,
        IOcrModelManager ocrModels, ICapabilityRegistry registry)
    {
        _store = store;
        _runs = runs;
        _serializer = serializer;
        _validator = validator;
        _inspector = inspector;
        _thumbnails = thumbnails;
        _picker = picker;
        _scanner = scanner;
        _dialogs = dialogs;
        _notifications = notifications;
        _settings = settings;
        _paths = paths;
        _models = models;
        _ocrModels = ocrModels;
        _registry = registry;
        _dispatcher = App.MainWindow?.DispatcherQueue
                      ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _runs.RunsChanged += OnRunsChanged;
        Templates = PipelineTemplates.All.ToList();
        foreach (var template in Templates)
            TemplateItems.Add(new PipelineTemplateItemViewModel(template));
    }

    public ObservableCollection<PipelineListItemViewModel> Pipelines { get; } = [];
    public ObservableCollection<PipelineListItemViewModel> FilteredPipelines { get; } = [];
    public ObservableCollection<PipelineTemplateItemViewModel> TemplateItems { get; } = [];
    public IReadOnlyList<PipelineTemplate> Templates { get; }

    [ObservableProperty] private PipelineEditorViewModel? _editor;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private PipelineListItemViewModel? _selectedPipeline;

    public bool HasPipelines => Pipelines.Count > 0;

    public event EventHandler? EditorChanged;
    public event EventHandler? ListChanged;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var definitions = await _store.LoadAsync();
            Pipelines.Clear();
            foreach (var definition in definitions.OrderByDescending(d => d.ModifiedAt))
                Pipelines.Add(new PipelineListItemViewModel(definition));
            ApplyFilter();
            OnPropertyChanged(nameof(HasPipelines));
            ListChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _notifications.Notify("Something went wrong", ex.Message, NotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reports an unexpected UI or storage failure without letting an
    /// unhandled async event tear down the process.
    /// </summary>
    public Task ReportErrorAsync(Exception exception) =>
        _dialogs.ShowErrorAsync("Something went wrong", exception.Message, exception.ToString());

    public async Task<PipelineEditorViewModel?> CreatePipelineAsync(string name, string? templateId)
    {
        var template = PipelineTemplates.Find(templateId);
        var effectiveName = string.IsNullOrWhiteSpace(name)
            ? Loc.T(template?.NameKey ?? "Untitled pipeline")
            : name.Trim();
        var definition = template is null
            ? PipelineDefinition.CreateEmpty(effectiveName)
            : PipelineTemplates.Create(template, effectiveName, _serializer);
        definition.OutputNode!.Output.Directory = _settings.Settings.General.DefaultOutputDirectory;
        await _store.SaveAsync(definition);
        await LoadAsync();
        var item = Pipelines.FirstOrDefault(p => p.Id == definition.Id);
        if (item is null) return null;
        return await OpenAsync(item);
    }

    public async Task<PipelineEditorViewModel?> OpenAsync(PipelineListItemViewModel item)
    {
        if (Editor is not null)
        {
            await Editor.FlushAsync();
            Editor.Dispose();
        }

        var definition = (await _store.LoadAsync()).FirstOrDefault(p => p.Id == item.Id)?.Clone()
                         ?? item.Definition.Clone();
        Editor = new PipelineEditorViewModel(definition, _store, _serializer, _validator, _runs, _inspector,
            _thumbnails, _picker, _scanner, _dialogs, _notifications, _settings, _paths, _models, _ocrModels, _registry);
        Editor.PropertyChanged += OnEditorPropertyChanged;
        IsEditing = true;
        await Editor.InitializeAsync();
        EditorChanged?.Invoke(this, EventArgs.Empty);
        return Editor;
    }

    public async Task CloseEditorAsync()
    {
        if (Editor is null)
        {
            IsEditing = false;
            return;
        }

        await Editor.FlushAsync();
        Editor.PropertyChanged -= OnEditorPropertyChanged;
        Editor.Dispose();
        Editor = null;
        IsEditing = false;
        await LoadAsync();
        EditorChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenameAsync(PipelineListItemViewModel item, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var definition = item.Definition.Clone();
        definition.Name = name.Trim();
        definition.ModifiedAt = DateTimeOffset.Now;
        await _store.SaveAsync(definition);
        await LoadAsync();
    }

    public async Task DuplicateAsync(PipelineListItemViewModel item)
    {
        var clone = ClonePipeline(item.Definition, Loc.F("{0} copy", item.Definition.Name));
        await _store.SaveAsync(clone);
        await LoadAsync();
    }

    public async Task DeleteAsync(PipelineListItemViewModel item)
    {
        var confirm = await _dialogs.ConfirmAsync("Delete pipeline?",
            Loc.F("Delete {0}? Generated output files are not removed.", item.Definition.Name),
            "Delete", "Cancel", destructive: true);
        if (!confirm) return;
        if (Editor is not null && Editor.BuildDefinition().Id == item.Id)
        {
            Editor.PropertyChanged -= OnEditorPropertyChanged;
            Editor.Dispose();
            Editor = null;
            IsEditing = false;
        }

        await _store.DeleteAsync(item.Id);
        await LoadAsync();
        EditorChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunAsync(PipelineListItemViewModel item)
    {
        if (_runs.Runs.Any(r => r.PipelineId == item.Id && !r.IsTerminal))
        {
            _notifications.Notify("Pipeline already running", item.Name, NotificationSeverity.Info);
            return;
        }

        var definition = (await _store.LoadAsync()).FirstOrDefault(p => p.Id == item.Id)?.Clone()
                         ?? item.Definition.Clone();
        var (plan, validation) = await _validator.BuildPlanAsync(definition);
        if (plan is null)
        {
            var details = string.Join("\n", validation.Issues
                .Where(i => i.Severity == PipelineIssueSeverity.Error)
                .Take(8)
                .Select(i => Loc.T("Pipeline.Validation." + i.Code)));
            await _dialogs.ShowErrorAsync("Pipeline needs attention",
                "Fix the highlighted operations before running this pipeline.", details);
            return;
        }

        try
        {
            await _runs.StartAsync(definition);
            _notifications.Notify("Pipeline started", item.Definition.Name, NotificationSeverity.Info);
        }
        catch (PipelineValidationException ex)
        {
            var issue = ex.Validation.Issues.FirstOrDefault(i => i.Severity == PipelineIssueSeverity.Error);
            await _dialogs.ShowErrorAsync("Pipeline needs attention",
                issue is null ? "Fix the highlighted operations before running this pipeline."
                    : Loc.T("Pipeline.Validation." + issue.Code));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Pipeline could not start", ex.Message, ex.ToString());
        }
    }

    public void ApplyFilter()
    {
        var query = SearchText.Trim();
        FilteredPipelines.Clear();
        foreach (var pipeline in Pipelines.Where(p => string.IsNullOrWhiteSpace(query) ||
                                                      p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            FilteredPipelines.Add(pipeline);
        OnPropertyChanged(nameof(HasPipelines));
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    public void RefreshLocalization()
    {
        foreach (var pipeline in Pipelines) pipeline.RefreshLocalization();
        foreach (var template in TemplateItems) template.RefreshLocalization();
        UpdateRunTexts();
        Editor?.RefreshLocalization();
    }

    private void UpdateRunTexts()
    {
        foreach (var pipeline in Pipelines)
        {
            var run = _runs.Runs.FirstOrDefault(r => r.PipelineId == pipeline.Id);
            pipeline.RunText = run is null ? "" : run.State switch
            {
                PipelineRunState.Running or PipelineRunState.Preparing =>
                    Loc.F("Step {0} of {1}", Math.Max(1, run.StepIndex), Math.Max(1, run.StepCount)),
                PipelineRunState.Completed => Loc.T("Completed"),
                PipelineRunState.CompletedWithFailures => Loc.T("Completed with failures"),
                PipelineRunState.Cancelled => Loc.T("Cancelled"),
                PipelineRunState.Failed => Loc.T("Failed"),
                _ => ""
            };
        }
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PipelineEditorViewModel.Name) or nameof(PipelineEditorViewModel.IsDirty))
            ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRunsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnRunsChanged(sender, e));
            return;
        }

        UpdateRunTexts();
    }

    private static PipelineDefinition ClonePipeline(PipelineDefinition source, string name)
    {
        var clone = source.Clone();
        clone.Id = Guid.NewGuid();
        clone.Name = name;
        clone.CreatedAt = DateTimeOffset.Now;
        clone.ModifiedAt = DateTimeOffset.Now;
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in clone.Nodes)
        {
            var old = node.Id;
            node.Id = Guid.NewGuid().ToString("N");
            idMap[old] = node.Id;
        }

        foreach (var connection in clone.Connections)
        {
            connection.Id = Guid.NewGuid().ToString("N");
            connection.FromNodeId = idMap.GetValueOrDefault(connection.FromNodeId, connection.FromNodeId);
            connection.ToNodeId = idMap.GetValueOrDefault(connection.ToNodeId, connection.ToNodeId);
        }

        return clone;
    }

    public void Dispose()
    {
        _runs.RunsChanged -= OnRunsChanged;
        if (Editor is not null)
        {
            Editor.PropertyChanged -= OnEditorPropertyChanged;
            Editor.Dispose();
            Editor = null;
        }
    }
}
