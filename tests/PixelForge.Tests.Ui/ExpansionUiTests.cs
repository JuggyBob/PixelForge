using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PixelForge.Tests.Ui;

public sealed class ExpansionUiTests
{
    private readonly ITestOutputHelper _output;

    public ExpansionUiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Minimum_window_width_keeps_right_sidebars_visible()
    {
        using var app = UiAppContext.Launch(settings =>
        {
            settings.General.Theme = ThemeMode.Dark;
            settings.General.ShowNotifications = false;
            settings.Window.Width = 900;
            settings.Window.Height = 700;
            settings.Upscaler.Method = UpscaleMethod.Ai;
        });
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);
        Thread.Sleep(9000);
        var bounds = window.BoundingRectangle;
        Assert.True(bounds.Width >= 1280,
            $"The window width {bounds.Width}px is below the enforced 1280 DIP minimum.");
        _output.WriteLine($"Minimum-width window bounds: {bounds}");

        app.Navigate(window, "NavConverter", "ConverterTitle");
        AssertInsideWindow(window, "CategoryArchives");
        AssertInsideWindow(window, "ConverterAddToQueue");
        app.Capture(window, "22-min-width-converter");

        app.Navigate(window, "NavUpscaler", "UpscalerTitle");
        AssertInsideWindow(window, "UpscalerManageModels");
        AssertInsideWindow(window, "UpscalerAddToQueue");
        app.Capture(window, "23-min-width-upscaler");
    }

    [Fact]
    public void Expansion_pages_and_model_library_are_visible()
    {
        RequireExpansionModels();
        using var app = UiAppContext.Launch(
            settings => ConfigureExpansion(settings),
            UiModelSeed.SeedExpansionModels);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);
        Thread.Sleep(9000);
        _output.WriteLine($"Window bounds: {window.BoundingRectangle}");

        var routes = new (string Nav, string Page, string Screenshot)[]
        {
            ("NavHome", "HomeTitle", "01-home-dark"),
            ("NavConverter", "ConverterTitle", "02-converter-dark"),
            ("NavTools", "ToolsTitle", "03-tools-dark"),
            ("NavUpscaler", "UpscalerTitle", "04-upscaler-waifu2x"),
            ("NavOcr", "OcrTitle", "05-ocr-advanced"),
            ("NavVideoUpscaler", "VideoUpscalerTitle", "06-video-upscaler"),
            ("NavWatch", "WatchTitle", "07-watch-folders"),
            ("NavQueue", "QueueTitle", "08-queue"),
            ("NavHistory", "HistoryTitle", "09-history"),
            ("NavSettings", "SettingsTitle", "10-settings")
        };

        foreach (var (nav, page, screenshot) in routes)
        {
            app.Navigate(window, nav, page);
            Thread.Sleep(700);
            var path = app.Capture(window, screenshot);
            _output.WriteLine($"{screenshot}: {path}");
            if (nav == "NavConverter")
            {
                app.Click(window, "CategoryAudio");
                Thread.Sleep(500);
                _output.WriteLine("converter audio: " + app.Capture(window, "02b-converter-audio-dark"));
                app.Click(window, "CategoryVideo");
                Thread.Sleep(500);
                _output.WriteLine("converter video: " + app.Capture(window, "02c-converter-video-dark"));
                app.Click(window, "CategoryDocuments");
                Thread.Sleep(500);
                _output.WriteLine("converter documents: " + app.Capture(window, "02d-converter-documents-dark"));
                app.Click(window, "CategoryEbooks");
                Thread.Sleep(500);
                _output.WriteLine("converter ebooks: " + app.Capture(window, "02e-converter-ebooks-dark"));
                app.Click(window, "CategoryArchives");
                Thread.Sleep(500);
                _output.WriteLine("converter archives: " + app.Capture(window, "02f-converter-archives-dark"));
                app.Click(window, "CategoryImages");
                Thread.Sleep(300);
            }
        }

        app.Navigate(window, "NavVideoUpscaler", "VideoUpscalerTitle");
        Assert.True(Find(window, "VideoUpscalerAddVideos").IsEnabled, "Video Upscaler Add videos is disabled.");
        Assert.True(Find(window, "VideoUpscalerAddFolder").IsEnabled, "Video Upscaler Scan folder is disabled.");

        app.Navigate(window, "NavUpscaler", "UpscalerTitle");
        app.WaitForNameContaining(window, "Waifu2x CUnet", TimeSpan.FromSeconds(20));
        Assert.Equal("Waifu2x CUnet", app.GetComboSelection(window, "UpscalerModelPicker"));
        Assert.Contains("Vulkan", app.GetComboSelection(window, "UpscalerProviderPicker"));
        _output.WriteLine("Upscaler shows Waifu2x CUnet with the Vulkan provider.");

        app.Click(window, "UpscalerManageModels");
        app.WaitForId(window, "ModelsTitle", TimeSpan.FromSeconds(15));
        app.SelectCombo(window, "ModelCategoryFilter", "Image upscaling + restoration");
        Thread.Sleep(800);
        app.WaitForName(window, "Waifu2x CUnet", TimeSpan.FromSeconds(15));
        var modelTopScreenshot = app.Capture(window, "11-models-image-restoration-top");
        _output.WriteLine($"models top: {modelTopScreenshot}");

        foreach (var name in new[] { "SwinIR Real-SR x4", "SPAN 4× (Nomos Uni)", "NAFNet GoPro (Deblur)" })
        {
            Assert.True(app.ScrollToName(window, "ModelList", name), $"Model '{name}' was not reachable in the list.");
            Thread.Sleep(500);
        }
        var modelScreenshot = app.Capture(window, "12-models-image-restoration-list");
        _output.WriteLine($"models lower: {modelScreenshot}");
        app.Click(window, "ModelsClosePanel");
        Thread.Sleep(400);

        app.Navigate(window, "NavOcr", "OcrTitle");
        app.WaitForNameContaining(window, "Xiaomi-OCR-0", TimeSpan.FromSeconds(20));
        app.WaitForNameContaining(window, "installed", TimeSpan.FromSeconds(10));
        Assert.Equal("Xiaomi-OCR-0", app.GetComboSelection(window, "OcrModelPicker"));
        var ocrScreenshot = app.Capture(window, "13-ocr-xiaomi-installed");
        _output.WriteLine($"ocr installed: {ocrScreenshot}");

        app.Navigate(window, "NavSettings", "SettingsTitle");
        SelectSettingsSection(window, "OCR");
        app.WaitForName(window, "OCR documents", TimeSpan.FromSeconds(10));
        app.WaitForName(window, "OCR execution provider", TimeSpan.FromSeconds(10));
        var settingsScreenshot = app.Capture(window, "14-settings-ocr-provider");
        _output.WriteLine($"settings ocr: {settingsScreenshot}");
    }

    [Fact]
    public void Waifu2x_runs_through_the_image_upscaler_ui()
    {
        RequireExpansionModels();
        using var app = UiAppContext.Launch(
            settings =>
            {
                ConfigureExpansion(settings);
                settings.Upscaler.ModelId = "waifu2x-cunet";
                settings.Upscaler.ScaleFactor = 2;
                settings.Upscaler.ExecutionProvider = ExecutionProviderKind.Vulkan;
            },
            UiModelSeed.SeedExpansionModels);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);
        Thread.Sleep(9000);

        app.Navigate(window, "NavUpscaler", "UpscalerTitle");
        app.WaitForNameContaining(window, "Waifu2x CUnet", TimeSpan.FromSeconds(20));
        AddFile(app, automation, window, "UpscalerAddImages", Path.Combine(RepoPaths.Root(), "testdata", "fixtures", "illustration.webp"));
        app.WaitForName(window, "illustration.webp", TimeSpan.FromSeconds(20));
        app.WaitForEnabled(window, "UpscalerAddToQueue", TimeSpan.FromSeconds(20));
        app.Capture(window, "15-upscaler-waifu2x-ready");
        app.Click(window, "UpscalerAddToQueue");
        app.WaitForId(window, "QueueTitle", TimeSpan.FromSeconds(20));

        var outputDirectory = Path.Combine(app.OutputDirectory, "upscaled");
        var output = WaitForFile(outputDirectory, "*.png", TimeSpan.FromSeconds(90));
        using (var image = System.Drawing.Image.FromFile(output))
        {
            Assert.Equal(1800, image.Width);
            Assert.Equal(1200, image.Height);
        }
        app.Capture(window, "16-queue-waifu2x-complete");
        Thread.Sleep(6000);
        app.Navigate(window, "NavHome", "HomeTitle");
        Thread.Sleep(800);
        app.Capture(window, "16b-home-waifu2x-recent");
        Thread.Sleep(800);
        app.Navigate(window, "NavHistory", "HistoryTitle");
        Thread.Sleep(800);
        app.Capture(window, "16c-history-waifu2x");
        _output.WriteLine($"Waifu2x output: {output} ({new FileInfo(output).Length} bytes)");
    }

    [Fact]
    public void Nafnet_restoration_runs_through_the_image_upscaler_ui()
    {
        RequireExpansionModels();
        using var app = UiAppContext.Launch(
            settings =>
            {
                ConfigureExpansion(settings);
                settings.Upscaler.ModelId = "nafnet-gopro";
                settings.Upscaler.ScaleFactor = 1;
                settings.Upscaler.ExecutionProvider = ExecutionProviderKind.DirectML;
            },
            UiModelSeed.SeedExpansionModels);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);
        Thread.Sleep(9000);

        app.Navigate(window, "NavUpscaler", "UpscalerTitle");
        app.WaitForNameContaining(window, "NAFNet GoPro", TimeSpan.FromSeconds(20));
        app.WaitForNameContaining(window, "Restoration · 1×", TimeSpan.FromSeconds(10));
        app.WaitForNameContaining(window, "Restoration model · output stays at the source resolution.",
            TimeSpan.FromSeconds(10));
        app.Capture(window, "17-upscaler-nafnet-restoration");
        AddFile(app, automation, window, "UpscalerAddImages", Path.Combine(RepoPaths.Root(), "testdata", "fixtures", "swinir-test.jpg"));
        app.WaitForName(window, "swinir-test.jpg", TimeSpan.FromSeconds(20));
        app.WaitForEnabled(window, "UpscalerAddToQueue", TimeSpan.FromSeconds(20));
        app.Click(window, "UpscalerAddToQueue");
        app.WaitForId(window, "QueueTitle", TimeSpan.FromSeconds(20));

        var outputDirectory = Path.Combine(app.OutputDirectory, "upscaled");
        var output = WaitForFile(outputDirectory, "*.png", TimeSpan.FromSeconds(90));
        using (var image = System.Drawing.Image.FromFile(output))
        {
            Assert.Equal(128, image.Width);
            Assert.Equal(96, image.Height);
        }
        app.Capture(window, "18-queue-nafnet-complete");
        _output.WriteLine($"NAFNet output: {output} ({new FileInfo(output).Length} bytes)");
    }

    [Fact]
    public void Xiaomi_advanced_ocr_runs_through_the_ocr_ui()
    {
        RequireExpansionModels();
        using var app = UiAppContext.Launch(
            settings =>
            {
                ConfigureExpansion(settings);
                settings.Ocr.ModelId = "xiaomi-ocr-0";
                settings.Ocr.GeneratePdf = true;
            },
            UiModelSeed.SeedExpansionModels);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);
        Thread.Sleep(9000);

        app.Navigate(window, "NavOcr", "OcrTitle");
        app.WaitForNameContaining(window, "Xiaomi-OCR-0", TimeSpan.FromSeconds(20));
        app.WaitForNameContaining(window, "installed", TimeSpan.FromSeconds(10));
        app.SetText(window, "OcrDocumentName", "UI Advanced OCR Check");

        var fixtures = Path.Combine(RepoPaths.Root(), "testdata", "fixtures");
        foreach (var file in new[] { "ocr-sample-1.png", "ocr-sample-2.png", "ocr-two-column.png" })
        {
            AddFile(app, automation, window, "OcrAddImages", Path.Combine(fixtures, file));
            app.WaitForName(window, file, TimeSpan.FromSeconds(20));
        }
        Assert.Equal(ToggleState.On, Find(window, "OcrGeneratePdf").Patterns.Toggle.Pattern.ToggleState);
        app.WaitForEnabled(window, "OcrAddToQueue", TimeSpan.FromSeconds(20));
        app.Capture(window, "19-ocr-xiaomi-pages-ready");
        app.Click(window, "OcrAddToQueue");
        app.WaitForId(window, "QueueTitle", TimeSpan.FromSeconds(20));
        try
        {
            Thread.Sleep(12000);
            app.Capture(window, "20-queue-advanced-ocr-queued");
            var outputDirectory = Path.Combine(app.OutputDirectory, "ocr");
            var docx = WaitForFile(outputDirectory, "*.docx", TimeSpan.FromSeconds(180));
            var pdf = WaitForFile(outputDirectory, "*.pdf", TimeSpan.FromSeconds(180));
            var text = ReadDocxText(docx);
            Assert.Contains("PixelForge OCR Test Document", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("##", text, StringComparison.Ordinal);
            app.Capture(window, "21-queue-advanced-ocr-complete");
            _output.WriteLine($"Advanced OCR DOCX: {docx} ({new FileInfo(docx).Length} bytes)");
            _output.WriteLine($"Advanced OCR PDF: {pdf} ({new FileInfo(pdf).Length} bytes)");
        }
        catch
        {
            app.KeepData = true;
            throw;
        }
    }

    private static void RequireExpansionModels()
    {
        if (UiModelSeed.HasExpansionModels) return;
        throw SkipException.ForSkip("Expansion UI tests require the verified models in third_party/models/downloads and a testdata run containing the NCNN, llama.cpp, and Xiaomi OCR assets.");
    }

    private static void ConfigureExpansion(AppSettings settings)
    {
        var output = settings.General.DefaultOutputDirectory;
        settings.General.Theme = ThemeMode.Dark;
        settings.General.LastPage = "Home";
        settings.General.ShowNotifications = false;
        settings.Window.Width = 1440;
        settings.Window.Height = 900;
        settings.Upscaler.Method = UpscaleMethod.Ai;
        settings.Upscaler.ModelId = "waifu2x-cunet";
        settings.Upscaler.ScaleFactor = 2;
        settings.Upscaler.ExecutionProvider = ExecutionProviderKind.Vulkan;
        settings.Upscaler.OutputFormatId = "png";
        settings.Upscaler.FileNameSuffix = "_ui";
        settings.Upscaler.DefaultOutputDirectory = Path.Combine(output, "upscaled");
        settings.VideoUpscaler.ImageModelId = "realesrgan-x4plus";
        settings.VideoUpscaler.InterpolationModelId = "rife-4.25-lite";
        settings.VideoUpscaler.DefaultOutputDirectory = Path.Combine(output, "video-upscaled");
        settings.Ocr.ModelId = "xiaomi-ocr-0";
        settings.Ocr.GeneratePdf = true;
        settings.Ocr.DefaultOutputDirectory = Path.Combine(output, "ocr");
    }

    private static void SelectSettingsSection(AutomationElement window, string label)
    {
        var list = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSections"));
        Assert.NotNull(list);
        var item = list!.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(e => string.Equals(Safe(() => e.Name), label, StringComparison.Ordinal));
        if (item is null)
        {
            item = list.FindAllDescendants(cf => cf.ByName(label))
                .FirstOrDefault(e => Safe(() => e.ControlType.ToString()) == "ListItem");
        }
        Assert.NotNull(item);
        item!.Click();
        Thread.Sleep(600);
    }

    private static AutomationElement Find(AutomationElement window, string automationId)
    {
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        Assert.NotNull(element);
        return element!;
    }

    private static void AssertInsideWindow(AutomationElement window, string automationId)
    {
        var element = Find(window, automationId);
        var windowBounds = window.BoundingRectangle;
        var elementBounds = element.BoundingRectangle;
        Assert.False(element.Properties.IsOffscreen.Value, $"'{automationId}' is offscreen.");
        Assert.True(elementBounds.Right <= windowBounds.Right,
            $"'{automationId}' extends past the window right edge ({elementBounds.Right} > {windowBounds.Right}).");
        Assert.True(elementBounds.Left >= windowBounds.Left,
            $"'{automationId}' starts left of the window ({elementBounds.Left} < {windowBounds.Left}).");
    }

    private static string ReadDocxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AddFile(UiAppContext app, UIA3Automation automation, AutomationElement window,
        string buttonId, string path)
    {
        app.Click(window, buttonId);
        var dialog = WaitForDialog(TimeSpan.FromSeconds(20));
        Assert.NotEqual(IntPtr.Zero, dialog);
        var element = automation.FromHandle(dialog);
        Assert.NotNull(element);
        var edit = element!.FindAllDescendants(cf => cf.ByAutomationId("1148"))
                       .FirstOrDefault(e => Safe(() => e.ControlType.ToString()) == "Edit")
                   ?? element.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)
                       .And(cf.ByName("File name:")));
        Assert.NotNull(edit);
        edit!.Focus();
        Thread.Sleep(250);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(path);
        Thread.Sleep(400);
        NativeWindows.SetForegroundWindow(dialog);
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.RETURN);
        WaitForDialogClose(dialog, TimeSpan.FromSeconds(20));
    }

    private static IntPtr WaitForDialog(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var handle = NativeWindows.FindOpenDialog();
            if (handle != IntPtr.Zero) return handle;
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }

    private static void WaitForDialogClose(IntPtr handle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeWindows.IsWindow(handle)) return;
            Thread.Sleep(150);
        }
        Assert.Fail("The file-picker dialog did not close after choosing a file.");
    }

    private static string WaitForFile(string directory, string pattern, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory))
            {
                var file = Directory.GetFiles(directory, pattern)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (file is not null && new FileInfo(file).Length > 0) return file;
            }
            Thread.Sleep(300);
        }
        Assert.Fail($"No {pattern} file appeared in {directory} within {timeout.TotalSeconds:0}s.");
        return "";
    }

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }
}

internal static class UiModelSeed
{
    public static bool HasExpansionModels
    {
        get
        {
            try
            {
                var downloads = Path.Combine(RepoPaths.Root(), "third_party", "models", "downloads");
                foreach (var file in new[]
                         {
                             "real_esrgan_x4.onnx", "swinir_realsr_x4.onnx", "span_4x_256.onnx",
                             "SCUNet-GAN.onnx", "NAFNet-GoPro-width64.onnx", "rife_v425lite.onnx"
                         })
                    if (!File.Exists(Path.Combine(downloads, file))) return false;

                FindRun("runtimes", "ncnn", "installed-ncnn-runtimes.json");
                FindRun("runtimes", "llama.cpp", "llama-cpp-b12110-vulkan", "llama-server.exe");
                FindRun("models", "ocr", "xiaomi-ocr-0", "Xiaomi-OCR-0-Q4_K_M.gguf");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SeedExpansionModels(string dataDirectory)
    {
        var models = Path.Combine(dataDirectory, "models");
        Directory.CreateDirectory(models);
        var downloads = Path.Combine(RepoPaths.Root(), "third_party", "models", "downloads");
        CopyFile(downloads, models, "real_esrgan_x4.onnx", "real_esrgan_x4plus.onnx");
        CopyFile(downloads, models, "real_esrgan_x2.onnx", "real_esrgan_x2plus.onnx");
        CopyFile(downloads, models, "realesr-animevideov3.onnx", "realesr-animevideov3.onnx");
        CopyFile(downloads, models, "realesr-general-x4v3.onnx", "realesr-general-x4v3.onnx");
        CopyFile(downloads, models, "realesrgan_anime6b.onnx", "realesrgan_x4plus_anime_6b.onnx");
        CopyFile(downloads, models, "swinir_realsr_x4.onnx", "swinir_realsr_x4.onnx");
        CopyFile(downloads, models, "span_4x_256.onnx", "span_4x_256.onnx");
        CopyFile(downloads, models, "SCUNet-GAN.onnx", "SCUNet-GAN.onnx");
        CopyFile(downloads, models, "NAFNet-GoPro-width64.onnx", "NAFNet-GoPro-width64.onnx");
        CopyFile(downloads, models, "rife_v425lite.onnx", "rife_v425lite.onnx");

        CopyDirectory(FindRun("runtimes", "ncnn", "installed-ncnn-runtimes.json"),
            Path.Combine(dataDirectory, "runtimes", "ncnn"));
        CopyDirectory(FindRun("runtimes", "llama.cpp", "llama-cpp-b12110-vulkan", "llama-server.exe"),
            Path.Combine(dataDirectory, "runtimes", "llama.cpp", "llama-cpp-b12110-vulkan"));
        CopyDirectory(FindRun("models", "ocr", "xiaomi-ocr-0", "Xiaomi-OCR-0-Q4_K_M.gguf"),
            Path.Combine(models, "ocr", "xiaomi-ocr-0"));

        var localMedium = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixelForge", "models", "ocr", "ppocrv6-medium");
        if (Directory.Exists(localMedium))
            CopyDirectory(localMedium, Path.Combine(models, "ocr", "ppocrv6-medium"));
    }

    private static string FindRun(params string[] parts)
    {
        var runs = Path.Combine(RepoPaths.Root(), "testdata", "runs");
        foreach (var run in Directory.GetDirectories(runs).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var candidate = Path.Combine(new[] { run, "appdata" }.Concat(parts).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
        }
        throw new DirectoryNotFoundException($"Could not find a test run containing {Path.Combine(parts)}.");
    }

    private static void CopyFile(string sourceDirectory, string destinationDirectory,
        string sourceName, string destinationName)
    {
        var source = Path.Combine(sourceDirectory, sourceName);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Required UI-test model file is missing: {source}");
        File.Copy(source, Path.Combine(destinationDirectory, destinationName), overwrite: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Required UI-test data is missing: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
