using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Application.Pipelines;

public sealed record PipelinePlanStep(
    PipelineNodeDefinition Node,
    PipelineActionDescriptor Action,
    OperationSettings Settings,
    PipelineMediaType InputType,
    PipelineMediaType OutputType);

public sealed class PipelineExecutionPlan
{
    public required PipelineDefinition Pipeline { get; init; }
    public required IReadOnlyList<PipelinePlanStep> Steps { get; init; }
    public required IReadOnlyList<string> InputPaths { get; init; }
    public required PipelineMediaType InputType { get; init; }
    public required PipelineMediaType OutputType { get; init; }
    public required PipelineOutputConfiguration Output { get; init; }
}

public sealed class PipelineValidator
{
    private readonly IOperationSettingsSerializer _settingsSerializer;
    private readonly ICapabilityRegistry _registry;
    private readonly IModelManager _models;
    private readonly IOcrModelManager _ocrModels;
    private readonly IMediaInspector _inspector;
    private readonly IFolderScanner _scanner;
    private readonly IFileNameService _fileNames;

    public PipelineValidator(IOperationSettingsSerializer settingsSerializer, ICapabilityRegistry registry,
        IModelManager models, IOcrModelManager ocrModels, IMediaInspector inspector, IFolderScanner scanner,
        IFileNameService fileNames)
    {
        _settingsSerializer = settingsSerializer;
        _registry = registry;
        _models = models;
        _ocrModels = ocrModels;
        _inspector = inspector;
        _scanner = scanner;
        _fileNames = fileNames;
    }

    /// <summary>
    /// Validates the graph and configuration without touching the selected
    /// input files. Safe to call on every editor change.
    /// </summary>
    public PipelineValidationResult ValidateStructure(PipelineDefinition pipeline)
    {
        var issues = new List<PipelineValidationIssue>();
        if (pipeline.SchemaVersion > PipelineDefinition.CurrentSchemaVersion)
        {
            issues.Add(Error("UnsupportedSchema", null, pipeline.SchemaVersion.ToString()));
            return new PipelineValidationResult { Issues = issues };
        }

        if (pipeline.Nodes.Count == 0)
        {
            issues.Add(Error("EmptyPipeline"));
            return new PipelineValidationResult { Issues = issues };
        }

        var inputNodes = pipeline.Nodes.Where(n => n.Kind == PipelineNodeKind.Input).ToList();
        var outputNodes = pipeline.Nodes.Where(n => n.Kind == PipelineNodeKind.Output).ToList();
        if (inputNodes.Count == 0) issues.Add(Error("MissingInput"));
        if (outputNodes.Count == 0) issues.Add(Error("MissingOutput"));
        if (inputNodes.Count > 1) issues.Add(Error("DuplicateInput", inputNodes[1].Id));
        if (outputNodes.Count > 1) issues.Add(Error("DuplicateOutput", outputNodes[1].Id));
        if (inputNodes.Count != 1 || outputNodes.Count != 1)
            return new PipelineValidationResult { Issues = issues };

        var byId = new Dictionary<string, PipelineNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in pipeline.Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                issues.Add(Error("DuplicateNodeId", node.Id));
                return new PipelineValidationResult { Issues = issues };
            }
        }

        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in pipeline.Nodes)
        {
            outgoing[node.Id] = [];
            incoming[node.Id] = [];
        }

        foreach (var connection in pipeline.Connections)
        {
            if (!byId.ContainsKey(connection.FromNodeId) || !byId.ContainsKey(connection.ToNodeId))
            {
                issues.Add(Error("ConnectionTargetMissing"));
                continue;
            }

            outgoing[connection.FromNodeId].Add(connection.ToNodeId);
            incoming[connection.ToNodeId].Add(connection.FromNodeId);
        }

        foreach (var node in pipeline.Nodes)
        {
            var ins = incoming[node.Id];
            var outs = outgoing[node.Id];
            switch (node.Kind)
            {
                case PipelineNodeKind.Input:
                    if (ins.Count != 0) issues.Add(Error("InputHasIncoming", node.Id));
                    if (outs.Count != 1) issues.Add(Error("InputNeedsOneOutput", node.Id));
                    break;
                case PipelineNodeKind.Output:
                    if (ins.Count != 1) issues.Add(Error("OutputNeedsOneInput", node.Id));
                    if (outs.Count != 0) issues.Add(Error("OutputHasOutgoing", node.Id));
                    break;
                case PipelineNodeKind.Action:
                    if (ins.Count != 1) issues.Add(Error("ActionNeedsOneInput", node.Id));
                    if (outs.Count != 1) issues.Add(Error("ActionNeedsOneOutput", node.Id));
                    break;
            }
        }

        if (issues.Any(i => i.Severity == PipelineIssueSeverity.Error))
            return new PipelineValidationResult { Issues = issues };

        // Follow the only possible chain from Input to Output. A cycle or a
        // second path would have tripped the degree checks above, but walking
        // also gives us the deterministic execution order.
        var order = new List<PipelineNodeDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = inputNodes[0];
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                issues.Add(Error("Cycle", current.Id));
                break;
            }

            order.Add(current);
            if (current.Kind == PipelineNodeKind.Output) break;
            var nextId = outgoing[current.Id].FirstOrDefault();
            if (nextId is null || !byId.TryGetValue(nextId, out var next))
            {
                issues.Add(Error("DisconnectedNode", current.Id));
                break;
            }

            current = next;
        }

        if (order.Count != pipeline.Nodes.Count)
            issues.Add(Error("DisconnectedNode"));

        if (order.LastOrDefault()?.Kind != PipelineNodeKind.Output)
            issues.Add(Error("MissingOutput"));

        var actionNodes = order.Where(n => n.Kind == PipelineNodeKind.Action).ToList();
        if (actionNodes.Count == 0)
            issues.Add(Error("NoOperations"));

        foreach (var node in actionNodes)
        {
            var descriptor = PipelineActionRegistry.Find(node.ActionId);
            if (descriptor is null)
            {
                issues.Add(Error("UnknownAction", node.Id, node.ActionId));
                continue;
            }

            var settings = ReadSettings(node, descriptor, issues);
            if (settings is null) continue;
            ValidateSettings(descriptor, settings, node, issues);
        }

        var outputNode = outputNodes[0];
        var output = outputNode.Output ?? new PipelineOutputConfiguration();
        if (string.IsNullOrWhiteSpace(output.Directory))
            issues.Add(Error("OutputDirectoryMissing", outputNode.Id));
        if (!string.IsNullOrEmpty(output.FileNameSuffix) &&
            !string.Equals(_fileNames.SanitizeSuffix(output.FileNameSuffix), output.FileNameSuffix,
                StringComparison.Ordinal))
            issues.Add(Error("InvalidSuffix", outputNode.Id));

        if (issues.All(i => i.Severity != PipelineIssueSeverity.Error))
            ValidateTypes(order, issues);

        return new PipelineValidationResult { Issues = issues };
    }

    /// <summary>
    /// Validates the structure, every action configuration and the actual input
    /// files. This is the gate used by Run.
    /// </summary>
    public async Task<(PipelineExecutionPlan? Plan, PipelineValidationResult Validation)> BuildPlanAsync(
        PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        if (!_registry.IsInitialized) await _registry.InitializeAsync(cancellationToken);
        var structure = ValidateStructure(pipeline);
        var issues = structure.Issues.ToList();
        if (issues.Any(i => i.Severity == PipelineIssueSeverity.Error))
            return (null, new PipelineValidationResult { Issues = issues });

        var outputNode = pipeline.OutputNode!;
        var inputNode = pipeline.InputNode!;
        var output = outputNode.Output ?? new PipelineOutputConfiguration();
        var order = OrderNodes(pipeline);
        var actionNodes = order.Where(n => n.Kind == PipelineNodeKind.Action).ToList();
        var firstAction = PipelineActionRegistry.Find(actionNodes[0].ActionId)!;

        var inputPaths = await ResolveInputPathsAsync(inputNode, firstAction, issues, cancellationToken);
        if (inputPaths.Count == 0)
            issues.Add(Error("NoInputFiles", inputNode.Id));

        var inputType = PipelineMediaType.Unknown;
        if (inputPaths.Count > 0)
        {
            var typeSet = new HashSet<PipelineMediaType>();
            foreach (var path in inputPaths)
            {
                if (Directory.Exists(path) && firstAction.InputTypes.Contains(PipelineMediaType.Folder))
                {
                    typeSet.Add(PipelineMediaType.Folder);
                    continue;
                }

                var info = await _inspector.InspectAsync(path, null, cancellationToken);
                if (!info.IsReadable)
                {
                    issues.Add(Error("InputFileUnreadable", inputNode.Id,
                        $"{Path.GetFileName(path)}: {info.Error}"));
                    continue;
                }

                var type = PipelineActionRegistry.ToPipelineType(info.Category);
                typeSet.Add(type);
                if (!firstAction.InputTypes.Contains(type))
                    issues.Add(Error("InputTypeUnsupported", inputNode.Id,
                        $"{Path.GetFileName(path)}: {type}"));
            }

            if (typeSet.Count > 1)
                issues.Add(Error("MixedInputTypes", inputNode.Id));
            else if (typeSet.Count == 1)
                inputType = typeSet.First();
        }

        if (issues.Any(i => i.Severity == PipelineIssueSeverity.Error))
            return (null, new PipelineValidationResult { Issues = issues });

        var steps = new List<PipelinePlanStep>();
        var currentType = inputType;
        foreach (var node in actionNodes)
        {
            var descriptor = PipelineActionRegistry.Find(node.ActionId)!;
            var settings = ReadSettings(node, descriptor, issues);
            if (settings is null) continue;
            var outputType = PipelineActionRegistry.OutputTypeFor(descriptor, settings);
            steps.Add(new PipelinePlanStep(node, descriptor, settings, currentType, outputType));
            currentType = outputType;
        }

        if (currentType == PipelineMediaType.Folder && steps.Count > 1)
            issues.Add(Error("FolderMustBeTerminal", steps[^1].Node.Id));

        if (inputPaths.Count > 1 && steps.Any(s => s.Action.Id == PipelineActionIds.Ocr))
            issues.Add(Error("OcrBatchNotSupported", inputNode.Id));

        if (issues.Any(i => i.Severity == PipelineIssueSeverity.Error))
            return (null, new PipelineValidationResult { Issues = issues });

        return (new PipelineExecutionPlan
        {
            Pipeline = pipeline,
            Steps = steps,
            InputPaths = inputPaths,
            InputType = inputType,
            OutputType = currentType,
            Output = output
        }, new PipelineValidationResult { Issues = issues });
    }

    public IReadOnlyList<PipelineNodeDefinition> OrderNodes(PipelineDefinition pipeline)
    {
        var input = pipeline.InputNode;
        if (input is null) return [];
        var byId = new Dictionary<string, PipelineNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in pipeline.Nodes) byId.TryAdd(node.Id, node);
        var next = pipeline.Connections
            .GroupBy(c => c.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ToNodeId, StringComparer.Ordinal);
        var result = new List<PipelineNodeDefinition>();
        var current = input;
        var guard = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && guard.Add(current.Id))
        {
            result.Add(current);
            if (current.Kind == PipelineNodeKind.Output) break;
            current = next.TryGetValue(current.Id, out var id) && byId.TryGetValue(id, out var node) ? node : null;
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> ResolveInputPathsAsync(PipelineNodeDefinition inputNode,
        PipelineActionDescriptor firstAction, List<PipelineValidationIssue> issues, CancellationToken cancellationToken)
    {
        var config = inputNode.Input ?? new PipelineInputConfiguration();
        if (!string.IsNullOrWhiteSpace(config.FolderPath))
        {
            if (!Directory.Exists(config.FolderPath))
            {
                issues.Add(Error("InputFolderMissing", inputNode.Id, config.FolderPath));
                return [];
            }

            // Folder consumers (archive creation) receive the folder itself
            // rather than a flattened list of files.
            if (firstAction.InputTypes.Contains(PipelineMediaType.Folder))
                return [config.FolderPath];

            var accepted = firstAction.InputTypes
                .Where(t => t is not PipelineMediaType.Folder and not PipelineMediaType.Unknown)
                .Select(PipelineActionRegistry.ToMediaCategory)
                .Where(c => c != MediaCategory.Unknown)
                .Distinct()
                .ToList();
            var paths = new List<string>();
            foreach (var category in accepted)
            {
                var scan = await _scanner.ScanAsync(new FolderScanRequest
                {
                    FolderPath = config.FolderPath,
                    Recursive = config.Recursive,
                    PreserveFolderStructure = config.PreserveFolderStructure,
                    TargetCategory = category
                }, cancellationToken: cancellationToken);
                paths.AddRange(scan.IncludedFiles.Select(f => f.Path));
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return config.Paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private OperationSettings? ReadSettings(PipelineNodeDefinition node, PipelineActionDescriptor descriptor,
        List<PipelineValidationIssue> issues)
    {
        try
        {
            return string.IsNullOrWhiteSpace(node.ConfigurationJson)
                ? descriptor.CreateSettings()
                : _settingsSerializer.Deserialize(descriptor.Operation, node.ConfigurationJson);
        }
        catch (Exception ex)
        {
            issues.Add(Error("InvalidConfiguration", node.Id, ex.Message));
            return null;
        }
    }

    private void ValidateSettings(PipelineActionDescriptor descriptor, OperationSettings settings,
        PipelineNodeDefinition node, List<PipelineValidationIssue> issues)
    {
        switch (settings)
        {
            case ImageConversionSettings image:
                RequireFormat(image.OutputFormatId, MediaCategory.Image, node, "ImageFormatUnavailable", issues);
                if (image.Quality is < 1 or > 100) issues.Add(Error("QualityOutOfRange", node.Id));
                if (image.ResizeWidth is <= 0 or > 32768 || image.ResizeHeight is <= 0 or > 32768)
                    issues.Add(Error("DimensionsOutOfRange", node.Id));
                break;
            case UpscaleSettings upscale:
                if (upscale.Method == UpscaleMethod.Ai)
                {
                    if (string.IsNullOrWhiteSpace(upscale.ModelId))
                        issues.Add(Error("ModelMissing", node.Id));
                    else if (_models.GetInstalled(upscale.ModelId) is not { IsValid: true })
                        issues.Add(Error("ModelNotInstalled", node.Id, upscale.ModelId));
                }

                if (upscale.ScaleFactor is < 2 or > 4) issues.Add(Error("ScaleOutOfRange", node.Id));
                if (!string.Equals(upscale.OutputFormatId, FormatIds.Auto, StringComparison.OrdinalIgnoreCase))
                    RequireFormat(upscale.OutputFormatId, MediaCategory.Image, node, "ImageFormatUnavailable", issues);
                break;
            case VideoConversionSettings video:
                RequireFormat(video.ContainerFormatId, MediaCategory.Video, node, "VideoFormatUnavailable", issues);
                if (video.Width is <= 0 || video.Height is <= 0) issues.Add(Error("DimensionsOutOfRange", node.Id));
                if ((video.ResizeMode != VideoResizeMode.None || video.FrameRate > 0) &&
                    video.VideoCodecId == FormatIds.Copy)
                    issues.Add(Error("StreamCopyCannotTransform", node.Id));
                if (!string.IsNullOrWhiteSpace(video.VideoCodecId) && video.VideoCodecId != FormatIds.Copy &&
                    !_registry.IsVideoCodecCompatible(video.ContainerFormatId, video.VideoCodecId))
                    issues.Add(Error("CodecIncompatible", node.Id, video.VideoCodecId));
                if (!string.IsNullOrWhiteSpace(video.AudioCodecId) && video.AudioCodecId is not (FormatIds.Copy
                        or FormatIds.None) &&
                    !_registry.IsAudioCodecCompatible(video.ContainerFormatId, video.AudioCodecId))
                    issues.Add(Error("CodecIncompatible", node.Id, video.AudioCodecId));
                break;
            case VideoToolSettings videoTool:
                RequireFormat(videoTool.ContainerFormatId, MediaCategory.Video, node, "VideoFormatUnavailable", issues);
                if (videoTool.Tool == VideoToolOperation.Trim && videoTool.TrimEndSeconds > 0 &&
                    videoTool.TrimEndSeconds <= videoTool.TrimStartSeconds)
                    issues.Add(Error("TrimRangeInvalid", node.Id));
                if (videoTool.Tool == VideoToolOperation.Gif && videoTool.GifEndSeconds <= videoTool.GifStartSeconds)
                    issues.Add(Error("TrimRangeInvalid", node.Id));
                if (videoTool.Tool is VideoToolOperation.Thumbnail or VideoToolOperation.Gif)
                {
                    var format = videoTool.Tool == VideoToolOperation.Thumbnail
                        ? videoTool.ThumbnailFormatId
                        : "gif";
                    RequireFormat(format, MediaCategory.Image, node, "ImageFormatUnavailable", issues);
                }
                break;
            case VideoUpscaleSettings videoUpscale:
                RequireFormat(videoUpscale.ContainerFormatId, MediaCategory.Video, node, "VideoFormatUnavailable", issues);
                if (!videoUpscale.EnableSuperResolution && !videoUpscale.EnableInterpolation)
                {
                    issues.Add(Error("NoVideoUpscaleMode", node.Id));
                }
                else
                {
                    if (videoUpscale.EnableSuperResolution)
                    {
                        if (string.IsNullOrWhiteSpace(videoUpscale.ImageModelId))
                            issues.Add(Error("ModelMissing", node.Id));
                        else if (_models.GetInstalled(videoUpscale.ImageModelId) is not { IsValid: true })
                            issues.Add(Error("ModelNotInstalled", node.Id, videoUpscale.ImageModelId));
                        if (videoUpscale.ScaleFactor is < 2 or > 4) issues.Add(Error("ScaleOutOfRange", node.Id));
                    }

                    if (videoUpscale.EnableInterpolation)
                    {
                        if (string.IsNullOrWhiteSpace(videoUpscale.InterpolationModelId))
                            issues.Add(Error("ModelMissing", node.Id));
                        else if (_models.GetInstalled(videoUpscale.InterpolationModelId) is not { IsValid: true })
                            issues.Add(Error("ModelNotInstalled", node.Id, videoUpscale.InterpolationModelId));
                    }
                }
                break;
            case AudioConversionSettings audio:
                RequireFormat(audio.OutputFormatId, MediaCategory.Audio, node, "AudioFormatUnavailable", issues);
                if (audio.BitrateKbps is < 32 or > 512) issues.Add(Error("BitrateOutOfRange", node.Id));
                break;
            case AudioToolSettings audioTool:
                RequireFormat(audioTool.OutputFormatId, MediaCategory.Audio, node, "AudioFormatUnavailable", issues);
                if (audioTool.Tool == AudioToolOperation.Trim && audioTool.TrimEndSeconds > 0 &&
                    audioTool.TrimEndSeconds <= audioTool.TrimStartSeconds)
                    issues.Add(Error("TrimRangeInvalid", node.Id));
                break;
            case ImageToolSettings imageTool:
                RequireFormat(imageTool.OutputFormatId, MediaCategory.Image, node, "ImageFormatUnavailable", issues);
                if (imageTool.Tool == ImageToolOperation.BatchEdit &&
                    (imageTool.CropWidth is <= 0 || imageTool.CropHeight is <= 0))
                    issues.Add(Error("DimensionsOutOfRange", node.Id));
                break;
            case DocumentConversionSettings document:
                RequireFormat(document.OutputFormatId, MediaCategory.Document, node, "DocumentFormatUnavailable", issues);
                break;
            case EbookConversionSettings ebook:
                RequireFormat(ebook.OutputFormatId, MediaCategory.Ebook, node, "EbookFormatUnavailable", issues);
                break;
            case ArchiveConversionSettings archive:
                if (archive.Action != ArchiveAction.Extract)
                    RequireFormat(archive.OutputFormatId, MediaCategory.Archive, node, "ArchiveFormatUnavailable", issues);
                break;
            case OcrSettings ocr:
                if (string.IsNullOrWhiteSpace(ocr.OcrModelId))
                    issues.Add(Error("ModelMissing", node.Id));
                else if (_ocrModels.GetInstalled(ocr.OcrModelId) is not { IsValid: true })
                    issues.Add(Error("OcrModelNotInstalled", node.Id, ocr.OcrModelId));
                if (string.IsNullOrWhiteSpace(ocr.DocumentName))
                    issues.Add(Error("DocumentNameMissing", node.Id));
                break;
        }
    }

    private void RequireFormat(string id, MediaCategory category, PipelineNodeDefinition node, string code,
        List<PipelineValidationIssue> issues)
    {
        var format = _registry.FindFormat(id, category);
        if (format is null || !format.CanWrite)
            issues.Add(Error(code, node.Id, id));
    }

    private void ValidateTypes(IReadOnlyList<PipelineNodeDefinition> order, List<PipelineValidationIssue> issues)
    {
        PipelineActionDescriptor? previous = null;
        OperationSettings? previousSettings = null;
        foreach (var node in order.Where(n => n.Kind == PipelineNodeKind.Action))
        {
            var descriptor = PipelineActionRegistry.Find(node.ActionId);
            if (descriptor is null) continue;
            var settings = ReadSettings(node, descriptor, issues);
            if (settings is null) continue;

            if (previous is not null && previousSettings is not null)
            {
                var produced = PipelineActionRegistry.OutputTypeFor(previous, previousSettings);
                if (!descriptor.InputTypes.Contains(produced))
                    issues.Add(Error("TypeMismatch", node.Id));
                if (previous.IsTerminal)
                    issues.Add(Error("TerminalActionNotLast", node.Id));
            }

            if (descriptor.IsTerminal && node != order.Where(n => n.Kind == PipelineNodeKind.Action).Last())
                issues.Add(Error("TerminalActionNotLast", node.Id));

            previous = descriptor;
            previousSettings = settings;
        }
    }

    private static PipelineValidationIssue Error(string code, string? nodeId = null, string? detail = null) =>
        new(PipelineIssueSeverity.Error, code, nodeId, detail);
}
