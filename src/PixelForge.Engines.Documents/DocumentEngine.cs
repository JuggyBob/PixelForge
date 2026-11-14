using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Capabilities;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;
using UglyToad.PdfPig;

namespace PixelForge.Engines.Documents;

public sealed class DocumentToolLocator
{
    private DocumentToolLocator(string? pandoc, string? typst, string? error)
    {
        PandocPath = pandoc;
        TypstPath = typst;
        Error = error;
    }

    public string? PandocPath { get; }
    public string? TypstPath { get; }
    public string? Error { get; }
    public bool HasPandoc => PandocPath is not null;
    public bool HasTypst => TypstPath is not null;

    public static DocumentToolLocator Discover()
    {
        var baseDir = AppContext.BaseDirectory;
        var pandocCandidates = new List<string>
        {
            Path.Combine(baseDir, "documents", "pandoc", "pandoc.exe"),
            Path.Combine(baseDir, "pandoc", "pandoc.exe")
        };
        var typstCandidates = new List<string>
        {
            Path.Combine(baseDir, "documents", "typst", "typst.exe"),
            Path.Combine(baseDir, "typst", "typst.exe")
        };

        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            pandocCandidates.Add(Path.Combine(current.FullName, "third_party", "documents", "pandoc", "pandoc.exe"));
            typstCandidates.Add(Path.Combine(current.FullName, "third_party", "documents", "typst", "typst.exe"));
        }

        var pandoc = pandocCandidates.FirstOrDefault(File.Exists);
        var typst = typstCandidates.FirstOrDefault(File.Exists);
        var error = pandoc is null
            ? "The document converter engine is unavailable. Pandoc was not found in the application dependencies."
            : null;
        return new DocumentToolLocator(pandoc, typst, error);
    }
}

public sealed class DocumentEngine : IProcessingEngine
{
    private readonly IProcessRunner _runner;
    private readonly IAppPaths _paths;
    private readonly ILogger<DocumentEngine> _logger;
    private readonly DocumentToolLocator _tools;

    public DocumentEngine(IProcessRunner runner, IAppPaths paths, ILogger<DocumentEngine> logger)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
        _tools = DocumentToolLocator.Discover();
    }

    public string Id => EngineIds.Documents;
    public string DisplayName => "Document engine";
    public MediaCategory[] Categories => [MediaCategory.Document, MediaCategory.Ebook];

    public Task<EngineCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_tools.HasPandoc)
            return Task.FromResult(new EngineCapability
            {
                EngineId = Id,
                DisplayName = DisplayName,
                IsAvailable = false,
                Error = _tools.Error
            });

        var formats = new List<MediaFormat>();
        foreach (var definition in ContentFormatCatalog.DocumentFormats)
        {
            var canWrite = definition.Id != "pdf" || _tools.HasTypst;
            formats.Add(definition.ToMediaFormat(Id, true, canWrite));
        }

        foreach (var definition in ContentFormatCatalog.EbookFormats)
        {
            var canWrite = definition.Id != "pdf" || _tools.HasTypst;
            formats.Add(definition.ToMediaFormat(Id, true, canWrite) with { Category = MediaCategory.Ebook });
        }

        var version = FileVersionInfo.GetVersionInfo(_tools.PandocPath!).ProductVersion ?? "Pandoc";
        return Task.FromResult(new EngineCapability
        {
            EngineId = Id,
            DisplayName = DisplayName,
            Version = $"Pandoc {version}" + (_tools.HasTypst ? " + Typst" : " (PDF output unavailable)"),
            IsAvailable = true,
            Formats = formats,
            ExecutionProviders =
            [
                new ExecutionProviderInfo
                {
                    Kind = ExecutionProviderKind.Cpu,
                    DisplayName = "CPU",
                    IsAvailable = true,
                    Details = "Document conversion runs locally on the CPU."
                }
            ]
        });
    }

    public Task<MediaInfo?> TryInspectAsync(string path, MediaCategory? categoryHint = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return Task.FromResult<MediaInfo?>(null);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var detected = FormatCatalog.DetectCategoryByExtension(extension);
        var category = categoryHint is MediaCategory.Document or MediaCategory.Ebook
            ? categoryHint.Value
            : detected;
        if (category is not (MediaCategory.Document or MediaCategory.Ebook))
            return Task.FromResult<MediaInfo?>(null);

        var definition = ContentFormatCatalog.FindByExtension(extension);
        if (definition is null) return Task.FromResult<MediaInfo?>(null);

        try
        {
            var file = new FileInfo(fullPath);
            var info = new MediaInfo
            {
                FilePath = fullPath,
                FileName = file.Name,
                Extension = extension,
                FileSizeBytes = file.Length,
                Category = category,
                FormatId = definition.Id,
                FormatName = definition.DisplayName,
                EngineId = Id
            };

            switch (definition.Id)
            {
                case "pdf":
                {
                    using var pdf = PdfDocument.Open(fullPath);
                    var pages = pdf.NumberOfPages;
                    if (pages <= 0) return Task.FromResult<MediaInfo?>(info with { Error = "The PDF has no pages." });
                    return Task.FromResult<MediaInfo?>(info with
                    {
                        FrameCount = pages,
                        IsMultipage = pages > 1,
                        FormatName = "PDF document"
                    });
                }
                case "docx":
                case "odt":
                case "epub":
                {
                    using var archive = ZipFile.OpenRead(fullPath);
                    var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (definition.Id == "docx" && !names.Contains("word/document.xml"))
                        return Task.FromResult<MediaInfo?>(info with { Error = "The file is not a valid DOCX document." });
                    if (definition.Id == "odt" && !names.Contains("content.xml"))
                        return Task.FromResult<MediaInfo?>(info with { Error = "The file is not a valid ODT document." });
                    if (definition.Id == "epub" &&
                        (!names.Contains("META-INF/container.xml") || !names.Contains("mimetype")))
                        return Task.FromResult<MediaInfo?>(info with { Error = "The file is not a valid EPUB e-book." });
                    return Task.FromResult<MediaInfo?>(info);
                }
                default:
                {
                    // HTML, Markdown, RTF and text inputs are validated as non-empty UTF-8/UTF-16 text.
                    var bytes = File.ReadAllBytes(fullPath);
                    if (bytes.Length == 0)
                        return Task.FromResult<MediaInfo?>(info with { Error = "The document is empty." });
                    var sample = bytes.Length > 4096 ? bytes[..4096] : bytes;
                    var text = Encoding.UTF8.GetString(sample);
                    if (!IsPlausibleText(text))
                        return Task.FromResult<MediaInfo?>(info with { Error = "The file does not look like a valid text document." });
                    return Task.FromResult<MediaInfo?>(info);
                }
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult<MediaInfo?>(new MediaInfo
            {
                FilePath = fullPath,
                FileName = Path.GetFileName(fullPath),
                Extension = extension,
                FileSizeBytes = new FileInfo(fullPath).Length,
                Category = category,
                FormatId = definition.Id,
                FormatName = definition.DisplayName,
                EngineId = Id,
                Error = ex.Message
            });
        }
    }

    public Task<ValidationResult> ValidateAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();
        var settings = request.Settings switch
        {
            DocumentConversionSettings document => (document.OutputFormatId, ContentFormatCatalog.DocumentFormats),
            EbookConversionSettings ebook => (ebook.OutputFormatId, ContentFormatCatalog.EbookFormats),
            _ => (null, null)
        };
        if (settings.Item1 is null || settings.Item2 is null)
            return Task.FromResult(ValidationResult.Failure("documents.settings",
                "The document settings do not match the operation."));

        var target = settings.Item2.FirstOrDefault(f =>
            string.Equals(f.Id, settings.Item1, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return Task.FromResult(ValidationResult.Failure("documents.format",
                "The selected document target format is not supported."));
        if (target.Id == "pdf" && !_tools.HasTypst)
            issues.Add(ValidationIssue.Error("documents.pdf.engine",
                "PDF output requires the bundled Typst PDF engine, which is unavailable."));
        if (!_tools.HasPandoc)
            issues.Add(ValidationIssue.Error("documents.pandoc",
                _tools.Error ?? "The document engine is unavailable."));

        return Task.FromResult(new ValidationResult { Issues = issues });
    }

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_tools.HasPandoc)
            return ProcessingResult.Fail(_tools.Error ?? "The document engine is unavailable.");
        if (request.Settings is not (DocumentConversionSettings or EbookConversionSettings))
            return ProcessingResult.Fail("The document settings are invalid.");

        var outputFormatId = request.Settings switch
        {
            DocumentConversionSettings d => d.OutputFormatId,
            EbookConversionSettings e => e.OutputFormatId,
            _ => ""
        };
        var target = ContentFormatCatalog.FindById(outputFormatId);
        if (target is null) return ProcessingResult.Fail("The selected target format is not supported.");
        if (target.Id == "pdf" && !_tools.HasTypst)
            return ProcessingResult.Fail("PDF output requires the bundled Typst engine, which is unavailable.");

        var workDir = Path.Combine(_paths.TempDirectory, "documents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var sourceExtension = Path.GetExtension(request.InputPath).ToLowerInvariant();
        var sourceCopy = Path.Combine(workDir, "input" + sourceExtension);
        var outputCopy = Path.Combine(workDir, "output" + target.Extension);
        var markdownFallback = Path.Combine(workDir, "input.md");

        try
        {
            progress.Report(ProcessingProgress.Percent(0.05, "Preparing document."));
            if (string.Equals(sourceExtension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractPdfText(request.InputPath);
                if (string.IsNullOrWhiteSpace(text))
                    return ProcessingResult.Fail("The PDF contains no extractable text. Scanned PDFs require OCR, which is not included.");
                await File.WriteAllTextAsync(markdownFallback, text, new UTF8Encoding(false), cancellationToken);
                sourceCopy = markdownFallback;
                progress.Report(ProcessingProgress.Percent(0.25, "PDF text extracted."));
            }
            else
            {
                File.Copy(request.InputPath, sourceCopy, overwrite: true);
            }

            var arguments = new List<string>
            {
                Path.GetFileName(sourceCopy),
                "-o",
                Path.GetFileName(outputCopy),
                "--resource-path", workDir
            };
            if (target.Id == "pdf")
            {
                arguments.AddRange(["--pdf-engine", "typst", "--extract-media", "media"]);
            }
            else if (target.Id is "html")
            {
                arguments.AddRange(["--standalone", "--embed-resources"]);
            }
            else if (target.Id is "md")
            {
                arguments.AddRange(["--to", "gfm"]);
            }
            else if (target.Id is "txt")
            {
                arguments.AddRange(["--to", "plain"]);
            }
            else if (target.Id is "rtf")
            {
                arguments.AddRange(["--to", "rtf"]);
            }

            var metadata = request.Settings switch
            {
                DocumentConversionSettings d => (d.Title, d.Author),
                EbookConversionSettings e => (e.Title, e.Author),
                _ => (null, null)
            };
            if (!string.IsNullOrWhiteSpace(metadata.Item1))
                arguments.AddRange(["--metadata", "title=" + metadata.Item1]);
            if (!string.IsNullOrWhiteSpace(metadata.Item2))
                arguments.AddRange(["--metadata", "author=" + metadata.Item2]);

            var env = new Dictionary<string, string>();
            if (_tools.HasTypst)
            {
                var typstDir = Path.GetDirectoryName(_tools.TypstPath!)!;
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                env["PATH"] = typstDir + Path.PathSeparator + path;
            }

            progress.Report(ProcessingProgress.Percent(0.35, "Converting document."));
            var result = await _runner.RunAsync(new ProcessRequest
            {
                FileName = _tools.PandocPath!,
                Arguments = arguments,
                WorkingDirectory = workDir,
                Environment = env,
                Timeout = TimeSpan.FromMinutes(10),
                MaxCapturedLines = 300
            }, cancellationToken);
            if (!result.Succeeded)
            {
                var message = LastLine(result.StandardError) ?? LastLine(result.StandardOutput) ?? "Document conversion failed.";
                return ProcessingResult.Fail(message, result.StandardError, stopwatch.Elapsed);
            }

            progress.Report(ProcessingProgress.Percent(0.9, "Validating document."));
            if (!File.Exists(outputCopy) || new FileInfo(outputCopy).Length == 0)
                return ProcessingResult.Fail("The document converter produced no output.");
            var verification = VerifyOutput(request, outputCopy, target, request.InputPath, sourceExtension);
            if (!verification.IsValid)
                return ProcessingResult.Fail($"Output validation failed: {verification.Summary}", verification.Details, stopwatch.Elapsed);

            if (File.Exists(request.TempOutputPath)) File.Delete(request.TempOutputPath);
            File.Copy(outputCopy, request.TempOutputPath, overwrite: true);
            stopwatch.Stop();
            progress.Report(ProcessingProgress.Percent(1, "Document ready."));
            var outputInfo = BuildOutputInfo(request.TempOutputPath, target, request.Operation);
            return ProcessingResult.Ok(request.TempOutputPath, outputInfo, new FileInfo(request.TempOutputPath).Length,
                stopwatch.Elapsed, verification);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document conversion failed for {Path}", request.InputPath);
            return ProcessingResult.Fail("The document could not be converted.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    public Task<VerificationResult> VerifyOutputAsync(ProcessingRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var id = request.Settings switch
        {
            DocumentConversionSettings d => d.OutputFormatId,
            EbookConversionSettings e => e.OutputFormatId,
            _ => ""
        };
        var target = ContentFormatCatalog.FindById(id);
        if (target is null) return Task.FromResult(VerificationResult.Fail("Unknown document output format."));
        return Task.FromResult(VerifyOutput(request, outputPath, target, request.InputPath, Path.GetExtension(request.InputPath)));
    }

    public Task<string?> CreateThumbnailAsync(string inputPath, string outputPath, int maxSize,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    private VerificationResult VerifyOutput(ProcessingRequest request, string path, FormatDefinition target,
        string inputPath, string inputExtension)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                return VerificationResult.Fail("The output document is empty.");
            switch (target.Id)
            {
                case "pdf":
                {
                    using var pdf = PdfDocument.Open(path);
                    if (pdf.NumberOfPages <= 0) return VerificationResult.Fail("The PDF has no pages.");
                    var info = new MediaInfo
                    {
                        FilePath = path, FileName = Path.GetFileName(path), Extension = ".pdf", FileSizeBytes = file.Length,
                        Category = MediaCategory.Document, FormatId = "pdf", FormatName = "PDF document",
                        FrameCount = pdf.NumberOfPages, IsMultipage = pdf.NumberOfPages > 1
                    };
                    return VerificationResult.Ok(info, $"PDF · {pdf.NumberOfPages} page(s) · {file.Length / 1024d:0.0} KB");
                }
                case "docx":
                case "odt":
                case "epub":
                {
                    using var zip = ZipFile.OpenRead(path);
                    var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (target.Id == "docx" && !names.Contains("word/document.xml"))
                        return VerificationResult.Fail("The DOCX output is missing its document part.");
                    if (target.Id == "odt" && !names.Contains("content.xml"))
                        return VerificationResult.Fail("The ODT output is missing its content part.");
                    if (target.Id == "epub" &&
                        (!names.Contains("META-INF/container.xml") || !names.Contains("mimetype")))
                        return VerificationResult.Fail("The EPUB output is missing required structure.");
                    var info = new MediaInfo
                    {
                        FilePath = path, FileName = Path.GetFileName(path), Extension = target.Extension,
                        FileSizeBytes = file.Length, Category = target.Category, FormatId = target.Id,
                        FormatName = target.DisplayName
                    };
                    return VerificationResult.Ok(info, $"{target.DisplayName} · {file.Length / 1024d:0.0} KB");
                }
                default:
                {
                    if (file.Length < 1) return VerificationResult.Fail("The output text is empty.");
                    var info = new MediaInfo
                    {
                        FilePath = path, FileName = Path.GetFileName(path), Extension = target.Extension,
                        FileSizeBytes = file.Length, Category = target.Category, FormatId = target.Id,
                        FormatName = target.DisplayName
                    };
                    var warnings = string.Equals(inputExtension, ".pdf", StringComparison.OrdinalIgnoreCase)
                        ? "PDF text was extracted; original PDF layout is not preserved."
                        : null;
                    return VerificationResult.Ok(info, $"{target.DisplayName} · {file.Length / 1024d:0.0} KB")
                        with { Details = warnings };
                }
            }
        }
        catch (Exception ex)
        {
            return VerificationResult.Fail("The output document could not be reopened.", details: ex.ToString());
        }
    }

    private static MediaInfo BuildOutputInfo(string path, FormatDefinition target, OperationKind operation)
    {
        var file = new FileInfo(path);
        return new MediaInfo
        {
            FilePath = path,
            FileName = file.Name,
            Extension = target.Extension,
            FileSizeBytes = file.Length,
            Category = target.Category,
            FormatId = target.Id,
            FormatName = target.DisplayName,
            EngineId = EngineIds.Documents
        };
    }

    private static string ExtractPdfText(string path)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(path);
        for (var i = 1; i <= pdf.NumberOfPages; i++)
        {
            var text = pdf.GetPage(i).Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(text.Trim());
        }

        return sb.ToString().Trim();
    }

    private static bool IsPlausibleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var bad = text.Count(c => c == '\uFFFD' || c == '\0');
        return bad <= Math.Max(2, text.Length / 100);
    }

    private static string? LastLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
    }
}
