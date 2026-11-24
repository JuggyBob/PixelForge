using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ImageMagick;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using PixelForge.Engines.Documents;

namespace PixelForge.Engines.OCR;

public sealed class OcrEngine : IProcessingEngine, IOcrReviewService, IDisposable
{
    private readonly IOcrModelManager _models;
    private readonly DocumentEngine _documents;
    private readonly IAppPaths _paths;
    private readonly ILlamaCppOcrService _llama;
    private readonly ILlamaCppRuntimeManager _llamaRuntime;
    private readonly ILogger<OcrEngine> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private PpOcrV6Runner? _runner;
    private readonly List<PpOcrV6Runner> _retiredRunners = [];
    private string? _runnerKey;
    private bool _disposed;

    public OcrEngine(IOcrModelManager models, DocumentEngine documents,
        IAppPaths paths, ILlamaCppOcrService llama, ILlamaCppRuntimeManager llamaRuntime,
        ILogger<OcrEngine> logger)
    {
        _models = models;
        _documents = documents;
        _paths = paths;
        _llama = llama;
        _llamaRuntime = llamaRuntime;
        _logger = logger;
    }

    public string Id => EngineIds.Ocr;
    public string DisplayName => "OCR engine";
    public MediaCategory[] Categories => [MediaCategory.Image];

    public async Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await _llamaRuntime.InitializeAsync(cancellationToken);
        var formats = new List<MediaFormat>
        {
            ContentFormatCatalog.Docx.ToMediaFormat(Id, false, true),
            ContentFormatCatalog.Pdf.ToMediaFormat(Id, false, true)
        };
        return new EngineCapability
        {
            EngineId = Id,
            DisplayName = DisplayName,
            Version = "PP-OCRv6 · optional llama.cpp OCR",
            IsAvailable = true,
            Formats = formats,
            ExecutionProviders =
            [
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = true,
                    Details = "PP-OCRv6 and llama.cpp advanced OCR run locally on the CPU."
                },
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.DirectML,
                    DisplayName = "DirectML",
                    IsAvailable = true,
                    Details = "DirectML is used when the selected PP-OCRv6 model supports it; CPU fallback is automatic."
                },
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Vulkan,
                    DisplayName = "Vulkan (llama.cpp)",
                    IsAvailable = _llama.IsRuntimeAvailable,
                    Details = _llama.IsRuntimeAvailable
                        ? "Advanced OCR models run through the local llama.cpp Vulkan build; CPU fallback is available."
                        : "Install an advanced OCR model to prepare the llama.cpp runtime."
                }
            ]
        };
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default) => Task.FromResult<MediaInfo?>(null);

    public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public async Task<ValidationResult> ValidateAsync(ProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();
        if (request.Settings is not OcrSettings settings)
        {
            issues.Add(ValidationIssue.Error("ocr.settings", "The OCR settings are invalid."));
            return new ValidationResult { Issues = issues };
        }

        var pages = settings.PagePaths.Count > 0 ? settings.PagePaths : [request.InputPath];
        if (pages.Count == 0)
            issues.Add(ValidationIssue.Error("ocr.pages", "Add at least one image page."));
        for (var i = 0; i < pages.Count; i++)
        {
            var path = pages[i];
            if (!File.Exists(path))
            {
                issues.Add(ValidationIssue.Error($"ocr.page.{i}", $"Page {i + 1} does not exist: {path}"));
                continue;
            }

            try
            {
                using var image = new MagickImage(path);
                if (image.Width < 8 || image.Height < 8)
                    issues.Add(ValidationIssue.Error($"ocr.page.{i}",
                        $"Page {i + 1} is too small to recognize: {Path.GetFileName(path)}"));
            }
            catch (Exception ex)
            {
                issues.Add(ValidationIssue.Error($"ocr.page.{i}",
                    $"Page {i + 1} is not a readable image: {Path.GetFileName(path)} ({ex.Message})"));
            }
        }

        if (string.IsNullOrWhiteSpace(settings.DocumentName))
            issues.Add(ValidationIssue.Error("ocr.name", "Enter a document name."));

        var descriptor = OcrModelCatalog.Find(settings.OcrModelId);
        if (descriptor is null)
            issues.Add(ValidationIssue.Error("ocr.model.unknown", "The selected OCR model is not in the catalogue."));
        else
        {
            var model = await _models.ResolveAsync(settings.OcrModelId, cancellationToken);
            if (model is null)
                issues.Add(ValidationIssue.Error("ocr.model.missing",
                    $"Install {descriptor.Name} before starting OCR."));
            else if (descriptor.Backend == OcrBackendKind.LlamaCpp && !_llama.IsRuntimeAvailable)
                issues.Add(ValidationIssue.Error("ocr.runtime.missing",
                    "The llama.cpp runtime is not installed. Install it from AI Models before using this OCR model."));
        }

        if (settings.GeneratePdf)
        {
            var docCapability = await _documents.ProbeAsync(cancellationToken);
            var pdf = docCapability.Formats.FirstOrDefault(f => f.Id == "pdf" && f.CanWrite);
            if (pdf is null)
                issues.Add(ValidationIssue.Warning("ocr.pdf.unavailable",
                    "PDF generation is unavailable because the bundled Typst PDF engine could not be found. The DOCX will still be generated."));
        }

        return new ValidationResult { Issues = issues };
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request.Settings is not OcrSettings settings)
            return ProcessingResult.Fail("The OCR settings are invalid.");

        var pages = settings.PagePaths.Count > 0 ? settings.PagePaths : [request.InputPath];
        if (!string.IsNullOrWhiteSpace(settings.ReviewedMarkdownPath))
        {
            if (!File.Exists(settings.ReviewedMarkdownPath))
                return ProcessingResult.Fail("The reviewed OCR text is missing.",
                    "The reviewed text file was removed. Run the OCR review again or disable review before export.", stopwatch.Elapsed);
            return await ProcessReviewedAsync(request, settings, progress, cancellationToken);
        }
        var installed = await _models.ResolveAsync(settings.OcrModelId, cancellationToken);
        if (installed is null)
            return ProcessingResult.Fail("The selected OCR model is not installed or failed validation.");
        if (installed.Descriptor.Backend == OcrBackendKind.LlamaCpp)
            return await ProcessLlamaCppAsync(request, settings, installed, pages, progress, cancellationToken);

        var warnings = new List<string>();
        var workDir = Path.Combine(_paths.TempDirectory, "ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var markdownPath = Path.Combine(workDir, "document.md");
        string? pdfTemp = null;
        try
        {
            progress.Report(ProcessingProgress.Percent(0.04, "Loading OCR model…"));
            var loadWatch = Stopwatch.StartNew();
            var runner = await GetRunnerAsync(installed, settings.ExecutionProvider, cancellationToken);
            loadWatch.Stop();
            _logger.LogInformation("OCR {Model} loaded on {Provider} in {Elapsed:0.0} ms",
                installed.Descriptor.Id, runner.ProviderDescription, loadWatch.Elapsed.TotalMilliseconds);
            if (runner.ActualProvider == ExecutionProviderKind.Cpu &&
                settings.ExecutionProvider == ExecutionProviderKind.DirectML)
                warnings.Add("DirectML could not initialize PP-OCRv6; the OCR model ran on the CPU instead.");
            else if (runner.ActualProvider == ExecutionProviderKind.Cpu &&
                     settings.ExecutionProvider == ExecutionProviderKind.Vulkan)
                warnings.Add("Vulkan is used by advanced OCR; PP-OCRv6 ran on the CPU instead.");
            else if (runner.IsMixedProvider)
                warnings.Add("The OCR detector and recognizer did not run on the same provider: " +
                             $"detector {runner.DetectorProvider}, recognizer {runner.RecognizerProvider}.");

            var pageResults = new List<OcrPageLines>();
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pagePath = pages[i];
                progress.Report(ProcessingProgress.Percent(
                    0.1 + 0.65 * i / Math.Max(1, pages.Count),
                    $"Recognizing page {i + 1} of {pages.Count}…"));
                try
                {
                    var pageWatch = Stopwatch.StartNew();
                    var page = await Task.Run(
                        () => runner.Recognize(pagePath, settings.Preprocess, cancellationToken),
                        cancellationToken);
                    pageWatch.Stop();
                    _logger.LogInformation("OCR page {Page}/{Total} took {Elapsed:0.0} ms and produced {Lines} lines",
                        i + 1, pages.Count, pageWatch.Elapsed.TotalMilliseconds, page.Lines.Count);
                    if (page.Lines.Count == 0) warnings.Add($"Page {i + 1} contained no readable text.");
                    pageResults.Add(new OcrPageLines(i + 1, page.Width, page.Height, page.Lines));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Page {i + 1} could not be read and was skipped: {ex.Message}");
                    pageResults.Add(new OcrPageLines(i + 1, 0, 0, []));
                }
            }

            if (pageResults.All(p => p.Lines.Count == 0))
                return ProcessingResult.Fail("No readable text was detected on any page.",
                    "Try a clearer scan, a larger image, or a different OCR model tier.");

            var lowConfidence = pageResults.SelectMany(p => p.Lines)
                .Count(l => l.Confidence > 0 && l.Confidence < 0.55f);
            if (lowConfidence > 0)
                warnings.Add($"{lowConfidence} low-confidence text region{(lowConfidence == 1 ? "" : "s")} " +
                             "were retained in the document. Review the source page where accuracy matters.");

            progress.Report(ProcessingProgress.Percent(0.78, "Reconstructing document…"));
            var markdown = OcrMarkdownBuilder.Build(pageResults);
            await File.WriteAllTextAsync(markdownPath, markdown, new UTF8Encoding(false), cancellationToken);

            progress.Report(ProcessingProgress.Percent(0.84, "Generating DOCX…"));
            var docxRequest = BuildDocumentRequest(request, markdownPath, request.TempOutputPath, "docx", settings);
            var docxResult = await _documents.ProcessAsync(docxRequest, progress, cancellationToken);
            if (!docxResult.Success)
                return ProcessingResult.Fail(docxResult.Error ?? "The DOCX could not be generated.", docxResult.ErrorDetails,
                    stopwatch.Elapsed);

            var docxVerification = await _documents.VerifyOutputAsync(docxRequest, request.TempOutputPath, cancellationToken);
            if (!docxVerification.IsValid)
                return ProcessingResult.Fail("The generated DOCX failed verification.", docxVerification.Details,
                    stopwatch.Elapsed);

            var structuralWarning = VerifyDocxStructure(request.TempOutputPath, pageResults);
            if (structuralWarning is not null) warnings.Add(structuralWarning);
            warnings.AddRange(docxResult.Warnings);

            var additional = new List<string>();
            if (settings.GeneratePdf)
            {
                progress.Report(ProcessingProgress.Percent(0.92, "Generating PDF…"));
                pdfTemp = Path.ChangeExtension(request.TempOutputPath, ".pdf");
                var docxInfo = docxVerification.ActualInfo;
                var pdfRequest = BuildDocumentRequest(request, request.TempOutputPath, pdfTemp, "pdf", settings, docxInfo);
                var pdfResult = await _documents.ProcessAsync(pdfRequest, progress, cancellationToken);
                if (pdfResult.Success)
                {
                    var pdfVerification = await _documents.VerifyOutputAsync(pdfRequest, pdfTemp, cancellationToken);
                    if (pdfVerification.IsValid)
                    {
                        additional.Add(pdfTemp);
                        warnings.Add("PDF also generated beside the DOCX.");
                    }
                    else
                    {
                        warnings.Add("The PDF failed verification and was removed. The DOCX is still available.");
                        TryDelete(pdfTemp);
                        pdfTemp = null;
                    }
                }
                else
                {
                    warnings.Add("The PDF could not be generated. The DOCX is still available. " +
                                 (pdfResult.Error ?? ""));
                    TryDelete(pdfTemp);
                    pdfTemp = null;
                }
            }

            var outputInfo = docxVerification.ActualInfo ?? new MediaInfo
            {
                FilePath = request.TempOutputPath,
                FileName = Path.GetFileName(request.TempOutputPath),
                Extension = ".docx",
                FileSizeBytes = new FileInfo(request.TempOutputPath).Length,
                Category = MediaCategory.Document,
                FormatId = "docx",
                FormatName = "Microsoft Word document",
                EngineId = EngineIds.Documents
            };
            var summary = $"OCR · {runner.ProviderDescription} · {pageResults.Count} page{(pageResults.Count == 1 ? "" : "s")} · " +
                          $"{pageResults.Sum(p => p.Lines.Count)} text lines";
            var verification = VerificationResult.Ok(outputInfo, summary);
            stopwatch.Stop();
            return ProcessingResult.Ok(request.TempOutputPath, outputInfo,
                new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, verification, warnings)
                with
                {
                    AdditionalOutputPaths = additional,
                    RequestedProvider = settings.ExecutionProvider.ToString(),
                    ActualProvider = runner.ProviderDescription,
                    FallbackReason = settings.ExecutionProvider == ExecutionProviderKind.DirectML &&
                                     runner.ActualProvider == ExecutionProviderKind.Cpu
                        ? "DirectML could not initialize the OCR sessions; PP-OCRv6 ran on the CPU."
                        : null,
                    ModelId = settings.OcrModelId,
                    RuntimeVersion = "ONNX Runtime"
                };
        }
        catch (OperationCanceledException)
        {
            TryDelete(pdfTemp);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR processing failed");
            TryDelete(pdfTemp);
            return ProcessingResult.Fail("OCR processing failed.", ex.ToString(), stopwatch.Elapsed);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    public async Task<OcrReviewResult> RecognizeAsync(OcrSettings settings,
        IProgress<ProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var pages = settings.PagePaths.Count > 0 ? settings.PagePaths : [];
        if (pages.Count == 0)
            return new OcrReviewResult { Error = "Add at least one page before reviewing OCR text." };

        var installed = await _models.ResolveAsync(settings.OcrModelId, cancellationToken);
        if (installed is null)
            return new OcrReviewResult { Error = "The selected OCR model is not installed or failed validation." };

        try
        {
            if (installed.Descriptor.Backend == OcrBackendKind.LlamaCpp)
            {
                var useGpu = settings.ExecutionProvider != ExecutionProviderKind.Cpu;
                var result = await _llama.RecognizeAsync(installed, pages,
                    installed.Descriptor.Prompt ?? "Extract all text in reading order. Preserve tables as Markdown.",
                    useGpu, progress ?? new Progress<ProcessingProgress>(), cancellationToken);
                if (!result.Success)
                    return new OcrReviewResult
                    {
                        Error = result.Error ?? "Advanced OCR inference failed.",
                        ErrorDetails = result.Details
                    };
                return new OcrReviewResult
                {
                    Success = true,
                    Markdown = result.Markdown,
                    Provider = result.Provider,
                    ModelId = settings.OcrModelId,
                    RuntimeVersion = "llama.cpp " + LlamaCppRuntimeCatalog.Vulkan.Version,
                    Warnings = string.IsNullOrWhiteSpace(result.Warning) ? [] : [result.Warning]
                };
            }

            var warnings = new List<string>();
            var runner = await GetRunnerAsync(installed, settings.ExecutionProvider, cancellationToken);
            if (runner.ActualProvider == ExecutionProviderKind.Cpu &&
                settings.ExecutionProvider == ExecutionProviderKind.DirectML)
                warnings.Add("DirectML could not initialize PP-OCRv6; the preview ran on the CPU instead.");
            else if (runner.IsMixedProvider)
                warnings.Add("The OCR detector and recognizer did not run on the same provider: " +
                             $"detector {runner.DetectorProvider}, recognizer {runner.RecognizerProvider}.");

            var pageResults = new List<OcrPageLines>();
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(ProcessingProgress.Percent(
                    0.05 + 0.9 * i / Math.Max(1, pages.Count),
                    $"Recognizing page {i + 1} of {pages.Count}…"));
                var page = await Task.Run(
                    () => runner.Recognize(pages[i], settings.Preprocess, cancellationToken),
                    cancellationToken);
                pageResults.Add(new OcrPageLines(i + 1, page.Width, page.Height, page.Lines));
            }

            if (pageResults.All(p => p.Lines.Count == 0))
                return new OcrReviewResult { Error = "No readable text was detected on any page." };

            var lowConfidence = pageResults.SelectMany(p => p.Lines)
                .Count(l => l.Confidence > 0 && l.Confidence < 0.55f);
            if (lowConfidence > 0)
                warnings.Add($"{lowConfidence} low-confidence text region{(lowConfidence == 1 ? "" : "s")} " +
                             "were retained. Correct them in the review before export.");

            return new OcrReviewResult
            {
                Success = true,
                Markdown = OcrMarkdownBuilder.Build(pageResults),
                Provider = runner.ProviderDescription,
                ModelId = settings.OcrModelId,
                RuntimeVersion = "ONNX Runtime",
                Warnings = warnings
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR review failed");
            return new OcrReviewResult
            {
                Error = "OCR review failed.",
                ErrorDetails = ex.ToString()
            };
        }
    }

    private async Task<ProcessingResult> ProcessReviewedAsync(ProcessingRequest request, OcrSettings settings,
        IProgress<ProcessingProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var reviewedPath = settings.ReviewedMarkdownPath!;
        string? pdfTemp = null;
        try
        {
            var warnings = new List<string>
            {
                "The reviewed OCR text was used instead of running recognition again."
            };
            if (!string.IsNullOrWhiteSpace(settings.ReviewedProvider))
                warnings.Add($"Reviewed text was produced by {settings.ReviewedProvider}.");

            progress.Report(ProcessingProgress.Percent(0.6, "Using reviewed text…"));
            var docxRequest = BuildDocumentRequest(request, reviewedPath, request.TempOutputPath, "docx", settings);
            var docxResult = await _documents.ProcessAsync(docxRequest, progress, cancellationToken);
            if (!docxResult.Success)
                return ProcessingResult.Fail(docxResult.Error ?? "The DOCX could not be generated.", docxResult.ErrorDetails,
                    stopwatch.Elapsed);

            var docxVerification = await _documents.VerifyOutputAsync(docxRequest, request.TempOutputPath, cancellationToken);
            if (!docxVerification.IsValid)
                return ProcessingResult.Fail("The generated DOCX failed verification.", docxVerification.Details,
                    stopwatch.Elapsed);

            var structuralWarning = VerifyDocxStructure(request.TempOutputPath, settings.PagePaths.Count);
            if (structuralWarning is not null) warnings.Add(structuralWarning);
            warnings.AddRange(docxResult.Warnings);

            var additional = new List<string>();
            if (settings.GeneratePdf)
            {
                progress.Report(ProcessingProgress.Percent(0.92, "Generating PDF…"));
                pdfTemp = Path.ChangeExtension(request.TempOutputPath, ".pdf");
                var pdfRequest = BuildDocumentRequest(request, request.TempOutputPath, pdfTemp, "pdf", settings,
                    docxVerification.ActualInfo);
                var pdfResult = await _documents.ProcessAsync(pdfRequest, progress, cancellationToken);
                if (pdfResult.Success)
                {
                    var pdfVerification = await _documents.VerifyOutputAsync(pdfRequest, pdfTemp, cancellationToken);
                    if (pdfVerification.IsValid)
                    {
                        additional.Add(pdfTemp);
                        warnings.Add("PDF also generated beside the DOCX.");
                    }
                    else
                    {
                        warnings.Add("The PDF failed verification and was removed. The DOCX is still available.");
                        TryDelete(pdfTemp);
                        pdfTemp = null;
                    }
                }
                else
                {
                    warnings.Add("The PDF could not be generated. The DOCX is still available. " +
                                 (pdfResult.Error ?? ""));
                    TryDelete(pdfTemp);
                    pdfTemp = null;
                }
            }

            var outputInfo = docxVerification.ActualInfo ?? new MediaInfo
            {
                FilePath = request.TempOutputPath,
                FileName = Path.GetFileName(request.TempOutputPath),
                Extension = ".docx",
                FileSizeBytes = new FileInfo(request.TempOutputPath).Length,
                Category = MediaCategory.Document,
                FormatId = "docx",
                FormatName = "Microsoft Word document",
                EngineId = EngineIds.Documents
            };
            var summary = $"OCR · reviewed text · {settings.PagePaths.Count} page" +
                          $"{(settings.PagePaths.Count == 1 ? "" : "s")}";
            stopwatch.Stop();
            return ProcessingResult.Ok(request.TempOutputPath, outputInfo,
                    new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed,
                    VerificationResult.Ok(outputInfo, summary), warnings)
                with
                {
                    AdditionalOutputPaths = additional,
                    RequestedProvider = settings.ExecutionProvider.ToString(),
                    ActualProvider = settings.ReviewedProvider ?? "Reviewed text",
                    ModelId = settings.OcrModelId,
                    RuntimeVersion = settings.ReviewedRuntimeVersion ?? "Reviewed text"
                };
        }
        catch (OperationCanceledException)
        {
            TryDelete(pdfTemp);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reviewed OCR document generation failed");
            TryDelete(pdfTemp);
            return ProcessingResult.Fail("Reviewed OCR document generation failed.", ex.ToString(), stopwatch.Elapsed);
        }
    }

    private async Task<ProcessingResult> ProcessLlamaCppAsync(ProcessingRequest request, OcrSettings settings,
        InstalledOcrModel installed, IReadOnlyList<string> pages, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var workDir = Path.Combine(_paths.TempDirectory, "ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var markdownPath = Path.Combine(workDir, "document.md");
        string? pdfTemp = null;
        try
        {
            progress.Report(ProcessingProgress.Percent(0.04, $"Starting {installed.Descriptor.Name}…"));
            var useGpu = settings.ExecutionProvider != ExecutionProviderKind.Cpu;
            var result = await _llama.RecognizeAsync(installed, pages,
                installed.Descriptor.Prompt ?? "Extract all text in reading order. Preserve tables as Markdown.",
                useGpu, progress, cancellationToken);
            if (!result.Success)
                return ProcessingResult.Fail(result.Error ?? "Advanced OCR inference failed.", result.Details,
                    stopwatch.Elapsed);

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Warning)) warnings.Add(result.Warning);
            warnings.Add($"Advanced OCR executed through {result.Provider} in {result.Duration.TotalSeconds:0.0} s.");

            progress.Report(ProcessingProgress.Percent(0.78, "Reconstructing document…"));
            await File.WriteAllTextAsync(markdownPath, result.Markdown, new UTF8Encoding(false), cancellationToken);

            progress.Report(ProcessingProgress.Percent(0.84, "Generating DOCX…"));
            var docxRequest = BuildDocumentRequest(request, markdownPath, request.TempOutputPath, "docx", settings);
            var docxResult = await _documents.ProcessAsync(docxRequest, progress, cancellationToken);
            if (!docxResult.Success)
                return ProcessingResult.Fail(docxResult.Error ?? "The DOCX could not be generated.", docxResult.ErrorDetails,
                    stopwatch.Elapsed);

            var docxVerification = await _documents.VerifyOutputAsync(docxRequest, request.TempOutputPath, cancellationToken);
            if (!docxVerification.IsValid)
                return ProcessingResult.Fail("The generated DOCX failed verification.", docxVerification.Details,
                    stopwatch.Elapsed);

            var structuralWarning = VerifyDocxStructure(request.TempOutputPath, pages.Count);
            if (structuralWarning is not null) warnings.Add(structuralWarning);
            warnings.AddRange(docxResult.Warnings);

            var additional = new List<string>();
            if (settings.GeneratePdf)
            {
                progress.Report(ProcessingProgress.Percent(0.92, "Generating PDF…"));
                pdfTemp = Path.ChangeExtension(request.TempOutputPath, ".pdf");
                var docxInfo = docxVerification.ActualInfo;
                var pdfRequest = BuildDocumentRequest(request, request.TempOutputPath, pdfTemp, "pdf", settings, docxInfo);
                var pdfResult = await _documents.ProcessAsync(pdfRequest, progress, cancellationToken);
                if (pdfResult.Success)
                {
                    var pdfVerification = await _documents.VerifyOutputAsync(pdfRequest, pdfTemp, cancellationToken);
                    if (pdfVerification.IsValid)
                    {
                        additional.Add(pdfTemp);
                        warnings.Add("PDF also generated beside the DOCX.");
                    }
                    else
                    {
                        warnings.Add("The PDF failed verification and was removed. The DOCX is still available.");
                        TryDelete(pdfTemp);
                        pdfTemp = null;
                    }
                }
                else
                {
                    warnings.Add("The PDF could not be generated. The DOCX is still available. " +
                                 (pdfResult.Error ?? ""));
                    TryDelete(pdfTemp);
                    pdfTemp = null;
                }
            }

            var outputInfo = docxVerification.ActualInfo ?? new MediaInfo
            {
                FilePath = request.TempOutputPath,
                FileName = Path.GetFileName(request.TempOutputPath),
                Extension = ".docx",
                FileSizeBytes = new FileInfo(request.TempOutputPath).Length,
                Category = MediaCategory.Document,
                FormatId = "docx",
                FormatName = "Microsoft Word document",
                EngineId = EngineIds.Documents
            };
            var summary = $"OCR · {result.Provider} · {pages.Count} page{(pages.Count == 1 ? "" : "s")}";
            var verification = VerificationResult.Ok(outputInfo, summary);
            stopwatch.Stop();
            return ProcessingResult.Ok(request.TempOutputPath, outputInfo,
                    new FileInfo(request.TempOutputPath).Length, stopwatch.Elapsed, verification, warnings)
                with
                {
                    AdditionalOutputPaths = additional,
                    RequestedProvider = settings.ExecutionProvider.ToString(),
                    ActualProvider = result.Provider,
                    FallbackReason = settings.ExecutionProvider is ExecutionProviderKind.DirectML or ExecutionProviderKind.Vulkan &&
                                     result.Provider.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                        ? "The requested GPU provider was unavailable; advanced OCR ran on the CPU."
                        : null,
                    ModelId = settings.OcrModelId,
                    RuntimeVersion = "llama.cpp " + LlamaCppRuntimeCatalog.Vulkan.Version
                };
        }
        catch (OperationCanceledException)
        {
            TryDelete(pdfTemp);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advanced OCR processing failed");
            TryDelete(pdfTemp);
            return ProcessingResult.Fail("Advanced OCR processing failed.", ex.ToString(), stopwatch.Elapsed);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    public async Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (request.Settings is not OcrSettings settings)
            return VerificationResult.Fail("The OCR settings are invalid.");
        var docxRequest = BuildDocumentRequest(request, request.InputPath, outputPath, "docx", settings);
        var verification = await _documents.VerifyOutputAsync(docxRequest, outputPath, cancellationToken);
        if (!verification.IsValid) return verification;
        if (request.Operation == OperationKind.OcrDocument)
        {
            using var zip = ZipFile.OpenRead(outputPath);
            var document = zip.GetEntry("word/document.xml");
            if (document is null) return VerificationResult.Fail("The DOCX is missing word/document.xml.");
            using var stream = document.Open();
            var text = XDocument.Load(stream).ToString();
            if (text.Length < 200) return VerificationResult.Fail("The DOCX does not contain meaningful text.");
        }

        return verification;
    }

    private async Task<PpOcrV6Runner> GetRunnerAsync(InstalledOcrModel model, ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var key = $"{model.Descriptor.Id}|{provider}";
            if (_runner is not null && string.Equals(_runnerKey, key, StringComparison.Ordinal))
                return _runner;
            if (_runner is not null)
            {
                _retiredRunners.Add(_runner);
                _runner = null;
            }
            _runner = new PpOcrV6Runner(model.DetectorPath, model.RecognizerPath, model.DictionaryPath, provider);
            _runnerKey = key;
            _logger.LogInformation("Loaded OCR model {Model} on {Provider}", model.Descriptor.Id, _runner.ActualProvider);
            return _runner;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static ProcessingRequest BuildDocumentRequest(ProcessingRequest request, string inputPath,
        string tempOutputPath, string formatId, OcrSettings settings, MediaInfo? inputInfo = null)
    {
        return new ProcessingRequest
        {
            JobId = request.JobId,
            ItemId = request.ItemId,
            Operation = OperationKind.DocumentConvert,
            Settings = new DocumentConversionSettings
            {
                OutputFormatId = formatId,
                Title = settings.DocumentName,
                Author = "PixelForge OCR"
            },
            InputPath = inputPath,
            TempOutputPath = tempOutputPath,
            OutputFormatId = formatId,
            TempDirectory = request.TempDirectory,
            InputInfo = inputInfo
        };
    }

    private static string? VerifyDocxStructure(string path, IReadOnlyList<OcrPageLines> pages) =>
        VerifyDocxStructure(path, pages.Count);

    private static string? VerifyDocxStructure(string path, int pageCount)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return "The generated DOCX has no word/document.xml part.";
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            if (xml.Length < 200) return "The generated DOCX contains almost no text.";
            if (pageCount > 1)
            {
                var breaks = Regex.Matches(xml, "<w:br[^>]*w:type=\"page\"", RegexOptions.IgnoreCase).Count;
                if (breaks < pageCount - 1)
                    return $"The DOCX contains {breaks} page break(s) for {pageCount} pages; text order is still preserved.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return "The DOCX package could not be inspected: " + ex.Message;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner?.Dispose();
        foreach (var retired in _retiredRunners) retired.Dispose();
        _retiredRunners.Clear();
        _sessionGate.Dispose();
    }
}
