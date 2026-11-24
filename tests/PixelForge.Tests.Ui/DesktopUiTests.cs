using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PixelForge.Tests.Ui;

internal static class DpiBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            NativeWindows.SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor aware v2
        }
        catch
        {
            // The test host may already have set an awareness context.
        }
    }
}

public sealed class DesktopUiTests
{
    private readonly ITestOutputHelper _output;

    public DesktopUiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Settings_theme_change_is_persisted()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavSettings", "SettingsTitle");
        SelectCombo(window, "ThemeCombo", "Light");
        Thread.Sleep(1500);

        var settings = app.ReadSettings();
        Assert.Equal(ThemeMode.Light, settings.General.Theme);

        app.Navigate(window, "NavHome", null);
    }

    [Fact]
    public void Language_switch_is_immediate_and_persisted()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavConverter", "ConverterTitle");
        app.Navigate(window, "NavSettings", "SettingsTitle");
        SelectCombo(window, "LanguageCombo", "Deutsch");
        app.WaitForName(window, "Einstellungen", TimeSpan.FromSeconds(15));
        app.WaitForName(window, "Darstellung", TimeSpan.FromSeconds(10));
        app.WaitForName(window, "Allgemein", TimeSpan.FromSeconds(10));
        Assert.Equal("de-DE", app.ReadSettings().General.Language);

        Thread.Sleep(800);
        app.Navigate(window, "NavConverter", null);
        app.WaitForName(window, "Konverter", TimeSpan.FromSeconds(15));
        app.Navigate(window, "NavHome", null);
        app.WaitForName(window, "Start", TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Pipelines_page_opens_an_editor_and_refreshes_after_a_language_change()
    {
        string? convertNodeId = null;
        using var app = UiAppContext.Launch(seed: directory =>
        {
            var definition = PipelineDefinition.CreateEmpty("Seeded pipeline");
            definition.OutputNode!.Output.Directory = Path.Combine(directory, "output");
            var convert = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = "image.convert",
                X = 340,
                Y = 280,
                ConfigurationJson = "{\"OutputFormatId\":\"webp\"}"
            };
            definition.Nodes.Insert(1, convert);
            convertNodeId = convert.Id;
            definition.Connections.Clear();
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = definition.InputNode!.Id,
                ToNodeId = convert.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = convert.Id,
                ToNodeId = definition.OutputNode.Id
            });
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            File.WriteAllText(Path.Combine(directory, "pipelines.json"),
                JsonSerializer.Serialize(new { SchemaVersion = 1, Pipelines = new[] { definition } }, options));
        });
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavPipelines", "PipelinesTitle");
        app.WaitForName(window, "Seeded pipeline", TimeSpan.FromSeconds(10));
        app.Click(window, "OpenPipelineButton");
        app.WaitForId(window, "PipelineNameBox", TimeSpan.FromSeconds(10));
        app.WaitForId(window, "PipelineNode_" + convertNodeId, TimeSpan.FromSeconds(10));
        app.Click(window, "PipelineNode_" + convertNodeId);
        app.WaitForName(window, "Output format", TimeSpan.FromSeconds(10));

        app.Click(window, "ActionDrawerToggle");
        app.WaitForId(window, "PipelineAction_image.resize", TimeSpan.FromSeconds(10));

        // A click must never insert an action; insertion is drag-only.
        app.Click(window, "PipelineAction_image.resize");
        Thread.Sleep(1200);
        Assert.Equal(1, ReadSavedActionCount(app));

        app.WaitForId(window, "PipelineCanvas", TimeSpan.FromSeconds(10));
        var actionCard = window.FindFirstDescendant(cf => cf.ByAutomationId("PipelineAction_image.resize"));
        var canvas = window.FindFirstDescendant(cf => cf.ByAutomationId("PipelineCanvas"));
        Assert.NotNull(actionCard);
        Assert.NotNull(canvas);
        var canvasBounds = canvas!.BoundingRectangle;
        var windowBounds = window.BoundingRectangle;

        // The canvas layout extends below the window, so a fraction of its
        // whole bounding rect can land on the action drawer. Intersect with
        // the window and release comfortably inside the visible canvas.
        var visibleLeft = Math.Max(canvasBounds.Left, windowBounds.Left);
        var visibleTop = Math.Max(canvasBounds.Top, windowBounds.Top);
        var visibleRight = Math.Min(canvasBounds.Left + canvasBounds.Width, windowBounds.Left + windowBounds.Width);
        var visibleBottom = Math.Min(canvasBounds.Top + canvasBounds.Height, windowBounds.Top + windowBounds.Height);
        var dropX = visibleLeft + (int)((visibleRight - visibleLeft) * 0.55);
        var dropY = visibleTop + (int)((visibleBottom - visibleTop) * 0.45);
        app.DragTo(actionCard!, dropX, dropY);
        var savedActions = WaitForSavedActionCount(app, 2, TimeSpan.FromSeconds(15));
        Assert.Equal(2, savedActions);

        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(app.DataDirectory, "pipelines.json"))))
        {
            var pipeline = saved.RootElement.GetProperty("Pipelines").EnumerateArray().First();
            Assert.Equal(2, pipeline.GetProperty("Connections").GetArrayLength());
        }

        app.Navigate(window, "NavSettings", "SettingsTitle");
        SelectCombo(window, "LanguageCombo", "Español");
        app.WaitForName(window, "Ajustes", TimeSpan.FromSeconds(15));
        app.Navigate(window, "NavPipelines", null);
        app.WaitForId(window, "PipelineNameBox", TimeSpan.FromSeconds(10));
        app.WaitForName(window, "Ejecutar canalización", TimeSpan.FromSeconds(10));

        app.Click(window, "PipelineBackButton");
        app.WaitForName(window, "Canalizaciones", TimeSpan.FromSeconds(10));
        app.WaitForName(window, "Seeded pipeline", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Pipelines_preserve_unavailable_actions_when_the_editor_saves()
    {
        string? pipelineFile = null;
        string? unknownNodeId = null;
        using var app = UiAppContext.Launch(seed: directory =>
        {
            var definition = PipelineDefinition.CreateEmpty("Future pipeline");
            definition.OutputNode!.Output.Directory = Path.Combine(directory, "output");
            var unknown = new PipelineNodeDefinition
            {
                Kind = PipelineNodeKind.Action,
                ActionId = "future.experimental.action",
                X = 340,
                Y = 280,
                ConfigurationJson = "{\"Value\":42}"
            };
            definition.Nodes.Insert(1, unknown);
            unknownNodeId = unknown.Id;
            definition.Connections.Clear();
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = definition.InputNode!.Id,
                ToNodeId = unknown.Id
            });
            definition.Connections.Add(new PipelineConnectionDefinition
            {
                FromNodeId = unknown.Id,
                ToNodeId = definition.OutputNode.Id
            });
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            pipelineFile = Path.Combine(directory, "pipelines.json");
            File.WriteAllText(pipelineFile,
                JsonSerializer.Serialize(new { SchemaVersion = 1, Pipelines = new[] { definition } }, options));
        });
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavPipelines", "PipelinesTitle");
        app.WaitForName(window, "Future pipeline", TimeSpan.FromSeconds(10));
        app.Click(window, "OpenPipelineButton");
        app.WaitForId(window, "PipelineNode_" + unknownNodeId, TimeSpan.FromSeconds(10));
        app.Click(window, "PipelineNode_" + unknownNodeId);
        app.WaitForName(window, "This operation is not available in this version.", TimeSpan.FromSeconds(10));

        app.Click(window, "PipelineBackButton");
        app.WaitForName(window, "Pipelines", TimeSpan.FromSeconds(10));
        var saved = File.ReadAllText(pipelineFile!);
        Assert.Contains("future.experimental.action", saved, StringComparison.Ordinal);
        Assert.Contains("Value", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_remains_usable_after_repeated_language_changes_and_navigation()
    {
        using var app = UiAppContext.Launch(settings => settings.Converter.ImageQuality = 77);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        var languages = new (string Locale, string Native, string Title, string Language, string Appearance,
            string FollowTheme, string LightTheme, string DarkTheme)[]
        {
            ("de-DE", "Deutsch", "Einstellungen", "Sprache", "Darstellung", "Folgen Sie Windows", "Licht", "Dunkel"),
            ("es-ES", "Español", "Ajustes", "Idioma", "Apariencia", "Seguir ventanas", "Luz", "oscuro"),
            ("pt-BR", "Português (Brasil)", "Configurações", "Idioma", "Aparência", "Siga o Windows", "Luz", "Escuro"),
            ("ko-KR", "한국어", "설정", "언어", "모양", "윈도우 팔로우", "빛", "어둠"),
            ("it-IT", "Italiano", "Impostazioni", "Lingua", "Aspetto", "Segui Windows", "Luce", "Buio"),
            ("fr-FR", "Français", "Paramètres", "Langue", "Apparence", "Suivre Windows", "Lumière", "Sombre"),
            ("ja-JP", "日本語", "設定", "言語", "外観", "ウィンドウズをフォローする", "ライト", "暗い"),
            ("zh-CN", "简体中文", "设置", "语言", "外观", "关注Windows", "光", "黑暗"),
            ("ru-RU", "Русский", "Настройки", "Язык", "Внешний вид", "Следуйте за Windows", "Свет", "Темный"),
            ("en-US", "English", "Settings", "Language", "Appearance", "Follow Windows", "Light", "Dark")
        };

        app.Navigate(window, "NavSettings", "SettingsTitle");
        foreach (var (locale, native, title, languageLabel, appearanceLabel,
                     followTheme, lightTheme, darkTheme) in languages)
        {
            SelectCombo(window, "LanguageCombo", native);
            app.WaitForName(window, title, TimeSpan.FromSeconds(15));
            app.WaitForName(window, languageLabel, TimeSpan.FromSeconds(10));
            app.WaitForName(window, appearanceLabel, TimeSpan.FromSeconds(10));

            var themeCombo = window.FindFirstDescendant(cf => cf.ByAutomationId("ThemeCombo"));
            Assert.NotNull(themeCombo);
            Assert.False(string.IsNullOrWhiteSpace(Safe(() => themeCombo!.Name)),
                $"ThemeCombo has no visible text after switching to {native}.");
            var selectedTheme = app.GetComboSelection(window, "ThemeCombo");
            Assert.False(string.IsNullOrWhiteSpace(selectedTheme),
                $"ThemeCombo selection is blank after switching to {native}.");
            Assert.Contains(selectedTheme, new[] { followTheme, lightTheme, darkTheme });
            var selectedLanguage = app.GetComboSelection(window, "LanguageCombo");
            Assert.Equal(native, selectedLanguage);
            WaitForSavedSettings(app, locale, imageQuality: 77, TimeSpan.FromSeconds(5));

            var started = Stopwatch.StartNew();
            if (themeCombo!.Patterns.ExpandCollapse.IsSupported)
                themeCombo.Patterns.ExpandCollapse.Pattern.Expand();
            else
                themeCombo.Click();

            var options = new[] { followTheme, lightTheme, darkTheme };
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var visibleOptions = new HashSet<string>(StringComparer.Ordinal);
            while (DateTime.UtcNow < deadline && visibleOptions.Count < options.Length)
            {
                foreach (var item in window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                {
                    var name = Safe(() => item.Name);
                    if (options.Contains(name, StringComparer.Ordinal)) visibleOptions.Add(name);
                }
                if (visibleOptions.Count < options.Length) Thread.Sleep(50);
            }

            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
                $"Dropdown took {started.Elapsed.TotalMilliseconds:0} ms after switching to {native}.");
            Assert.Equal(options.Length, visibleOptions.Count);
            Keyboard.Press(VirtualKeyShort.ESCAPE);

            app.Navigate(window, "NavConverter", "ConverterTitle");
            app.Navigate(window, "NavSettings", null);
            app.WaitForId(window, "SettingsTitle", TimeSpan.FromSeconds(15));
            app.WaitForName(window, title, TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Cached_page_dropdowns_keep_their_selection_after_language_change()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        // Visit the pages first so they are cached and registered for refresh.
        app.Navigate(window, "NavOcr", "OcrTitle");
        app.WaitForId(window, "OcrSortMode", TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrWhiteSpace(app.GetComboSelection(window, "OcrSortMode")));

        app.Navigate(window, "NavUpscaler", "UpscalerTitle");
        app.WaitForId(window, "UpscalerScalePicker", TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrWhiteSpace(app.GetComboSelection(window, "UpscalerScalePicker")));

        app.Navigate(window, "NavSettings", "SettingsTitle");
        SelectCombo(window, "LanguageCombo", "Deutsch");
        app.WaitForName(window, "Einstellungen", TimeSpan.FromSeconds(15));

        app.Navigate(window, "NavOcr", null);
        app.WaitForId(window, "OcrTitle", TimeSpan.FromSeconds(15));
        app.WaitForId(window, "OcrSortMode", TimeSpan.FromSeconds(10));
        var ocrSelection = app.GetComboSelection(window, "OcrSortMode");
        Assert.False(string.IsNullOrWhiteSpace(ocrSelection),
            "The OCR sort dropdown is blank after a language change.");
        _output.WriteLine($"OCR sort after language change: {ocrSelection}");

        app.Navigate(window, "NavUpscaler", null);
        app.WaitForId(window, "UpscalerTitle", TimeSpan.FromSeconds(15));
        app.WaitForId(window, "UpscalerScalePicker", TimeSpan.FromSeconds(10));
        var scaleSelection = app.GetComboSelection(window, "UpscalerScalePicker");
        Assert.False(string.IsNullOrWhiteSpace(scaleSelection),
            "The upscaler scale dropdown is blank after a language change.");
        _output.WriteLine($"Upscaler scale after language change: {scaleSelection}");

        app.Navigate(window, "NavConverter", null);
        app.WaitForId(window, "ConverterTitle", TimeSpan.FromSeconds(15));
    }

    private static int ReadSavedActionCount(UiAppContext app)
    {
        var path = Path.Combine(app.DataDirectory, "pipelines.json");
        if (!File.Exists(path)) return 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("Pipelines").EnumerateArray().First()
                .GetProperty("Nodes").EnumerateArray()
                .Count(node => node.GetProperty("Kind").GetString() == "Action");
        }
        catch (JsonException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static int WaitForSavedActionCount(UiAppContext app, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var path = Path.Combine(app.DataDirectory, "pipelines.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var count = document.RootElement.GetProperty("Pipelines").EnumerateArray().First()
                        .GetProperty("Nodes").EnumerateArray()
                        .Count(node => node.GetProperty("Kind").GetString() == "Action");
                    if (count >= expected) return count;
                }
            }
            catch (JsonException)
            {
                // The store writes atomically, but a read can still race a replace.
            }
            catch (IOException)
            {
                // Same as above.
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException($"Pipeline did not autosave {expected} actions.");
    }

    private static void WaitForSavedSettings(UiAppContext app, string locale, int imageQuality, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var saved = app.ReadSettings();
                if (string.Equals(saved.General.Language, locale, StringComparison.Ordinal) &&
                    saved.Converter.ImageQuality == imageQuality)
                    return;
            }
            catch (IOException)
            {
                // The settings writer may be replacing the file on this poll.
            }
            Thread.Sleep(100);
        }

        var final = app.ReadSettings();
        Assert.Equal(locale, final.General.Language);
        Assert.Equal(imageQuality, final.Converter.ImageQuality);
    }

    [Theory]
    [InlineData("de-DE", "Einstellungen")]
    [InlineData("ja-JP", "設定")]
    [InlineData("ru-RU", "Настройки")]
    public void Startup_uses_the_saved_language(string locale, string settingsTitle)
    {
        using var app = UiAppContext.Launch(settings => settings.General.Language = locale);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavSettings", "SettingsTitle");
        app.WaitForName(window, settingsTitle, TimeSpan.FromSeconds(15));
        Assert.Equal(locale, app.ReadSettings().General.Language);
    }

    [Fact]
    public void Converter_imports_queues_and_verifies_a_real_png()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavConverter", "ConverterTitle");
        app.Click(window, "ConverterChooseFiles");

        var dialogHandle = WaitForDialog(TimeSpan.FromSeconds(15));
        Assert.NotEqual(IntPtr.Zero, dialogHandle);
        SetFileDialogPath(automation, dialogHandle, RepoPaths.Fixture("transparent.png"));
        WaitForDialogClose(dialogHandle, TimeSpan.FromSeconds(15));

        app.WaitForName(window, "transparent.png", TimeSpan.FromSeconds(20));
        app.WaitForEnabled(window, "ConverterAddToQueue", TimeSpan.FromSeconds(20));
        app.Click(window, "ConverterAddToQueue");
        app.WaitForId(window, "QueueTitle", TimeSpan.FromSeconds(20));

        var output = Path.Combine(app.OutputDirectory, "transparent.png");
        WaitUntil(() => File.Exists(output), TimeSpan.FromSeconds(30), "The converted PNG was not created.");
        Assert.True(new FileInfo(output).Length > 1000);

        using (var image = System.Drawing.Image.FromFile(output))
        {
            Assert.Equal(640, image.Width);
            Assert.Equal(420, image.Height);
        }

        app.Click(window, "NavHistory");
        app.WaitForId(window, "HistoryTitle", TimeSpan.FromSeconds(20));
        app.WaitForName(window, "Image conversion", TimeSpan.FromSeconds(20));
        app.WaitForName(window, "Completed", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Navigation_pages_are_responsive()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        var routes = new (string Nav, string Page)[]
        {
            ("NavConverter", "ConverterTitle"),
            ("NavTools", "ToolsTitle"),
            ("NavUpscaler", "UpscalerTitle"),
            ("NavOcr", "OcrTitle"),
            ("NavVideoUpscaler", "VideoUpscalerTitle"),
            ("NavWatch", "WatchTitle"),
            ("NavQueue", "QueueTitle"),
            ("NavHistory", "HistoryTitle"),
            ("NavSettings", "SettingsTitle"),
            ("NavHome", "HomeTitle")
        };

        var evidenceDir = Path.Combine(RepoPaths.Root(), "artifacts", "test-evidence");
        Directory.CreateDirectory(evidenceDir);
        for (var round = 0; round < 2; round++)
        {
            foreach (var (nav, page) in routes)
                app.Navigate(window, nav, page);

            Thread.Sleep(800);
            var roundLog = app.ReadLog();
            File.WriteAllText(Path.Combine(evidenceDir,
                round == 0 ? "navigation-before-app.log" : "navigation-cached-app.log"), roundLog);
        }

        var log = app.ReadLog();
        foreach (var line in log.Split('\n').Where(l => l.Contains("Navigation ", StringComparison.Ordinal) ||
                                                       l.Contains("Settings diagnostics", StringComparison.Ordinal)))
            _output.WriteLine(line.Trim());
    }

    [Fact]
    public void First_run_onboarding_explains_features_and_can_be_skipped()
    {
        using var app = UiAppContext.Launch(settings => settings.General.OnboardingCompleted = false);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.WaitForId(window, "OnboardingSkip", TimeSpan.FromSeconds(20));
        app.WaitForName(window, "Welcome to PixelForge", TimeSpan.FromSeconds(10));
        app.Capture(window, "25-onboarding-welcome");
        app.Click(window, "OnboardingNext");
        app.WaitForName(window, "Convert almost anything", TimeSpan.FromSeconds(10));
        app.Capture(window, "26-onboarding-converter");
        app.Click(window, "OnboardingSkip");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingSkip")) is null)
                break;
            Thread.Sleep(200);
        }

        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingSkip")));
        var settingsDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < settingsDeadline && !app.ReadSettings().General.OnboardingCompleted)
            Thread.Sleep(200);
        Assert.True(app.ReadSettings().General.OnboardingCompleted);
    }

    [Fact]
    public void Watch_rule_actions_stay_right_and_editor_reopens_after_save()
    {
        using var app = UiAppContext.Launch(seed: SeedWatchRule);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.Navigate(window, "NavWatch", "WatchTitle");
        app.WaitForName(window, "Test watch rule", TimeSpan.FromSeconds(15));

        var list = window.FindFirstDescendant(cf => cf.ByAutomationId("WatchRuleList"));
        var actions = window.FindFirstDescendant(cf => cf.ByAutomationId("WatchRuleExpand"));
        Assert.NotNull(list);
        Assert.NotNull(actions);
        Assert.True(list!.BoundingRectangle.Right - actions!.BoundingRectangle.Right < 48,
            $"Watch actions are not right-aligned (list right {list.BoundingRectangle.Right}, chevron right {actions.BoundingRectangle.Right}).");

        app.Click(window, "WatchRuleExpand");
        app.WaitForName(window, "Edit watch rule", TimeSpan.FromSeconds(10));
        app.Click(window, "WatchRuleSave");
        WaitForNameToDisappear(window, "Edit watch rule", TimeSpan.FromSeconds(10));

        app.Click(window, "WatchRuleExpand");
        app.WaitForName(window, "Edit watch rule", TimeSpan.FromSeconds(10));
    }

    private static void SeedWatchRule(string dataDirectory)
    {
        var input = Path.Combine(dataDirectory, "watch-input");
        var output = Path.Combine(dataDirectory, "watch-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var settings = JsonSerializer.Serialize(new
        {
            Operation = "ImageConvert",
            OutputFormatId = "png"
        });
        var document = new
        {
            Rules = new[]
            {
                new
                {
                    Id = Guid.NewGuid(),
                    Name = "Test watch rule",
                    InputDirectory = input,
                    OutputDirectory = output,
                    IncludeSubdirectories = false,
                    IgnorePreviouslyProcessed = true,
                    IsEnabled = false,
                    Operation = "ImageConvert",
                    SettingsJson = settings,
                    CreatedAt = DateTimeOffset.Now,
                    LastTriggeredAt = (DateTimeOffset?)null,
                    LastError = (string?)null
                }
            }
        };
        File.WriteAllText(Path.Combine(dataDirectory, "watch-folders.json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WaitForNameToDisappear(AutomationElement window, string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindFirstDescendant(cf => cf.ByName(name)) is null) return;
            Thread.Sleep(150);
        }
        throw new TimeoutException($"Element named '{name}' did not disappear.");
    }

    [Fact]
    public void Notification_center_shows_and_clears_recent_notifications()
    {
        using var app = UiAppContext.Launch();
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.WaitForId(window, "NotificationCenterButton", TimeSpan.FromSeconds(20));
        app.Click(window, "NotificationCenterButton");
        WaitForNameAnywhere(automation, window, "Notifications", TimeSpan.FromSeconds(10));
        WaitForIdAnywhere(automation, window, "NotificationHistoryItem", TimeSpan.FromSeconds(45));
        WaitForNameAnywhere(automation, window, "PixelForge is ready", TimeSpan.FromSeconds(20));

        InvokeAnywhere(automation, window, "NotificationClear");
        WaitForNameAnywhere(automation, window, "No notifications yet", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Explorer_activation_stages_a_file_without_queueing()
    {
        var fixture = RepoPaths.Fixture("transparent.png");
        using var app = UiAppContext.Launch(args: ["--shell", "convert-images", fixture]);
        using var automation = new UIA3Automation();
        var window = app.Attach(automation);

        app.WaitForId(window, "ConverterTitle", TimeSpan.FromSeconds(25));
        app.WaitForNameContaining(window, "transparent.png", TimeSpan.FromSeconds(20));

        app.Navigate(window, "NavQueue", "QueueTitle");
        app.WaitForName(window, "Nothing queued", TimeSpan.FromSeconds(10));
    }

    private static void InvokeAnywhere(UIA3Automation automation, AutomationElement window, string automationId)
    {
        var element = FindAnywhere(automation, window, automationId);
        Assert.NotNull(element);
        if (element!.Patterns.Invoke.IsSupported)
            element.Patterns.Invoke.Pattern.Invoke();
        else
            element.Click();
        Thread.Sleep(400);
    }

    private static void WaitForNameAnywhere(UIA3Automation automation, AutomationElement window,
        string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindFirstDescendant(cf => cf.ByName(name)) is not null) return;
            try
            {
                if (automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name)) is not null) return;
            }
            catch
            {
                // The desktop tree can be temporarily unavailable.
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Element named '{name}' did not appear in the app or a popup.");
    }

    private static void WaitForIdAnywhere(UIA3Automation automation, AutomationElement window,
        string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindAnywhere(automation, window, automationId) is not null) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Element '{automationId}' did not appear in the app or a popup.");
    }

    private static AutomationElement? FindAnywhere(UIA3Automation automation, AutomationElement window,
        string automationId)
    {
        var found = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (found is not null) return found;
        try
        {
            return automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch
        {
            return null;
        }
    }

    private static void SetFileDialogPath(UIA3Automation automation, IntPtr hwnd, string path)
    {
        var dialog = automation.FromHandle(hwnd);
        Assert.NotNull(dialog);
        AutomationElement? edit = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && edit is null)
        {
            edit = dialog!.FindAllDescendants(cf => cf.ByAutomationId("1148"))
                       .FirstOrDefault(e => Safe(() => e.ControlType.ToString()) == "Edit")
                   ?? dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("File name:")));
            if (edit is null) Thread.Sleep(250);
        }
        Assert.NotNull(edit);
        edit!.Focus();
        Thread.Sleep(250);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(path);
        Thread.Sleep(400);
        SetForegroundWindow(hwnd);
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.RETURN);
    }

    private static void SelectCombo(AutomationElement window, string automationId, string value)
    {
        var combo = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        Assert.NotNull(combo);
        combo!.Focus();
        if (combo.Patterns.ExpandCollapse.IsSupported)
            combo.Patterns.ExpandCollapse.Pattern.Expand();
        else
            combo.Click();
        Thread.Sleep(600);

        var item = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(e => string.Equals(Safe(() => e.Name), value, StringComparison.Ordinal));
        if (item is null)
        {
            item = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(e => string.Equals(Safe(() => e.Name), value, StringComparison.Ordinal));
        }
        Assert.NotNull(item);
        item!.Focus();
        item.Click();
        Thread.Sleep(300);
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

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(150);
        }
        Assert.Fail(message);
    }

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

internal static class RepoPaths
{
    public static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "PixelForge.sln")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the PixelForge repository root.");
    }

    public static string Fixture(string name) => Path.Combine(Root(), "testdata", "fixtures", name);
}

internal sealed class UiAppContext : IDisposable
{
    private readonly string _dataDirectory;
    private readonly Process _process;

    private UiAppContext(string dataDirectory, string outputDirectory, Process process)
    {
        _dataDirectory = dataDirectory;
        OutputDirectory = outputDirectory;
        _process = process;
    }

    public string OutputDirectory { get; }
    public string DataDirectory => _dataDirectory;
    public bool KeepData { get; set; }

    public string ReadLog()
    {
        var directory = Path.Combine(_dataDirectory, "logs");
        if (!Directory.Exists(directory)) return "";
        var file = Directory.GetFiles(directory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) return "";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static UiAppContext Launch(Action<AppSettings>? configure = null, Action<string>? seed = null,
        IReadOnlyList<string>? args = null)
    {
        var root = FindRepoRoot();
        var overrideExe = Environment.GetEnvironmentVariable("PIXELFORGE_UI_EXE");
        var candidates = string.IsNullOrWhiteSpace(overrideExe)
            ? new[]
            {
                Path.Combine(root, "src", "PixelForge.Presentation.WinUI", "bin", "x64", "Release",
                    "net10.0-windows10.0.26100.0", "win-x64", "PixelForge.exe"),
                Path.Combine(root, "src", "PixelForge.Presentation.WinUI", "bin", "x64", "Debug",
                    "net10.0-windows10.0.26100.0", "win-x64", "PixelForge.exe")
            }
            : [overrideExe];
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
            throw new FileNotFoundException("PixelForge.exe was not found. Build the WinUI project before running UI tests.");

        var dataDirectory = Environment.GetEnvironmentVariable("PIXELFORGE_UI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(Path.GetTempPath(), "PixelForge.UiTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDirectory);
        }
        else
        {
            dataDirectory = Path.GetFullPath(dataDirectory);
            try
            {
                if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
            }
            catch
            {
                // A previous test run may still be releasing a handle.
            }
            Directory.CreateDirectory(dataDirectory);
        }
        var outputDirectory = Path.Combine(dataDirectory, "output");
        Directory.CreateDirectory(outputDirectory);

        var settings = new AppSettings
        {
            General = new GeneralSettings
            {
                Theme = ThemeMode.System,
                DefaultOutputDirectory = outputDirectory,
                LastPage = "Home",
                OnboardingCompleted = true
            },
            Upscaler = new UpscalerDefaults
            {
                DefaultOutputDirectory = Path.Combine(dataDirectory, "upscaled"),
                ModelDirectory = Path.Combine(dataDirectory, "models")
            },
            Processing = new ProcessingSettings
            {
                TempDirectory = Path.Combine(dataDirectory, "temp")
            },
            Window = new WindowPlacement { Width = 1280, Height = 820, Left = -1, Top = -1 }
        };
        configure?.Invoke(settings);
        seed?.Invoke(dataDirectory);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(Path.Combine(dataDirectory, "settings.json"), JsonSerializer.Serialize(settings, options));

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            Environment = { ["PIXELFORGE_DATA_DIR"] = dataDirectory }
        };
        if (args is not null)
        {
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        }
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Could not start PixelForge.");
        return new UiAppContext(dataDirectory, outputDirectory, process)
        {
            KeepData = Environment.GetEnvironmentVariable("PIXELFORGE_UI_KEEP_DATA") == "1"
        };
    }

    public AutomationElement Attach(UIA3Automation automation)
    {
        var app = Application.Attach(_process.Id);
        AutomationElement? window = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is not null && Safe(() => window.Name) == "PixelForge")
            {
                try
                {
                    var handle = window.Properties.NativeWindowHandle.Value;
                    if (handle != IntPtr.Zero) NativeWindows.SetForegroundWindow(handle);
                    window.Focus();
                }
                catch
                {
                    // Some UIA providers do not expose a native handle.
                }
                return window;
            }
            Thread.Sleep(300);
        }
        throw new TimeoutException("PixelForge main window did not appear.");
    }

    public void DragTo(AutomationElement source, int targetX, int targetY)
    {
        try
        {
            _process.Refresh();
            var handle = _process.MainWindowHandle;
            if (handle == IntPtr.Zero) handle = source.Properties.NativeWindowHandle.Value;
            if (handle != IntPtr.Zero) NativeWindows.SetForegroundWindow(handle);
        }
        catch
        {
            // Some UIA providers do not expose a native handle.
        }

        var bounds = source.BoundingRectangle;
        var startX = bounds.Left + bounds.Width / 2;
        var startY = bounds.Top + bounds.Height / 2;

        const uint move = NativeWindows.MOUSEEVENTF_MOVE | NativeWindows.MOUSEEVENTF_ABSOLUTE;
        NativeWindows.SendMouse(move, (int)startX, (int)startY);
        Thread.Sleep(200);
        NativeWindows.SendMouse(NativeWindows.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(250);
        const int steps = 14;
        for (var i = 1; i <= steps; i++)
        {
            var x = startX + (targetX - startX) * i / steps;
            var y = startY + (targetY - startY) * i / steps;
            NativeWindows.SendMouse(move, (int)x, (int)y);
            Thread.Sleep(25);
        }

        NativeWindows.SendMouse(NativeWindows.MOUSEEVENTF_LEFTUP);
        Thread.Sleep(600);
    }
    public void Click(AutomationElement window, string automationId)
    {
        try
        {
            var handle = window.Properties.NativeWindowHandle.Value;
            if (handle != IntPtr.Zero) NativeWindows.SetForegroundWindow(handle);
        }
        catch
        {
            // Some UIA providers do not expose the native handle.
        }
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is null) throw new InvalidOperationException($"Element '{automationId}' was not found.");
        element.Focus();
        if (element.Patterns.Invoke.IsSupported)
            element.Patterns.Invoke.Pattern.Invoke();
        else
            element.Click();
        Thread.Sleep(500);
    }

    public void Navigate(AutomationElement window, string navigationId, string? expectedPageId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Click(window, navigationId);
            if (expectedPageId is null) return;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (window.FindFirstDescendant(cf => cf.ByAutomationId(expectedPageId)) is not null) return;
                Thread.Sleep(250);
            }
        }
        throw new TimeoutException($"Navigation '{navigationId}' did not reach '{expectedPageId}'.");
    }

    public void WaitForId(AutomationElement window, string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Element '{automationId}' did not appear.");
    }

    public void WaitForName(AutomationElement window, string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindAllDescendants(cf => cf.ByName(name)).Length > 0) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Element named '{name}' did not appear.");
    }

    public void WaitForNameContaining(AutomationElement window, string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Any(e => Safe(() => e.Name).Contains(text, StringComparison.OrdinalIgnoreCase))) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Text containing '{text}' did not appear.");
    }

    public bool ScrollToName(AutomationElement window, string containerId, string name, int attempts = 12)
    {
        var container = window.FindFirstDescendant(cf => cf.ByAutomationId(containerId));
        if (container is null) return false;
        var bounds = container.BoundingRectangle;
        Mouse.MoveTo(bounds.Left + bounds.Width / 2, bounds.Top + Math.Min(bounds.Height / 2, 300));
        for (var i = 0; i < attempts; i++)
        {
            var found = container.FindFirstDescendant(cf => cf.ByName(name));
            if (found is not null && !SafeBool(() => found.Properties.IsOffscreen.Value)) return true;
            Mouse.Scroll(-3);
            Thread.Sleep(400);
        }
        var last = container.FindFirstDescendant(cf => cf.ByName(name));
        return last is not null && !SafeBool(() => last.Properties.IsOffscreen.Value);
    }

    public string GetComboSelection(AutomationElement window, string automationId)
    {
        var combo = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (combo is null) throw new InvalidOperationException($"Combo '{automationId}' was not found.");
        if (combo.Patterns.Selection.IsSupported)
        {
            var selected = combo.Patterns.Selection.Pattern.Selection.Value;
            if (selected.Length > 0) return Safe(() => selected[0].Name);
        }
        return Safe(() => combo.AsComboBox().SelectedItem?.Name);
    }

    public void WaitForEnabled(AutomationElement window, string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is not null && element.IsEnabled) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Element '{automationId}' did not become enabled.");
    }

    public string Capture(AutomationElement window, string name)
    {
        var evidence = Path.Combine(RepoPaths.Root(), "artifacts", "test-evidence", "ui-expansion-2026-11-12");
        Directory.CreateDirectory(evidence);
        try
        {
            var handle = window.Properties.NativeWindowHandle.Value;
            if (handle != IntPtr.Zero) NativeWindows.SetForegroundWindow(handle);
        }
        catch
        {
            // Some UIA providers do not expose the native handle.
        }
        Thread.Sleep(400);
        using var image = FlaUI.Core.Capturing.Capture.Element(window);
        var path = Path.Combine(evidence, name + ".png");
        image.ToFile(path);
        return path;
    }

    public void SelectCombo(AutomationElement window, string automationId, string value)
    {
        var combo = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        Assert.NotNull(combo);
        combo!.Focus();
        if (combo.Patterns.ExpandCollapse.IsSupported)
            combo.Patterns.ExpandCollapse.Pattern.Expand();
        else
            combo.Click();
        Thread.Sleep(500);

        var item = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(e => string.Equals(Safe(() => e.Name), value, StringComparison.Ordinal));
        if (item is null)
        {
            item = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(e => string.Equals(Safe(() => e.Name), value, StringComparison.Ordinal));
        }
        if (item is null)
        {
            item = combo.FindAllDescendants(cf => cf.ByName(value))
                .FirstOrDefault(e => Safe(() => e.ControlType.ToString()) == "ListItem");
        }
        if (item is null)
        {
            var label = window.FindAllDescendants(cf => cf.ByName(value)).FirstOrDefault();
            var ancestor = label;
            while (ancestor is not null && Safe(() => ancestor.ControlType.ToString()) != "ListItem")
                ancestor = ancestor.Parent;
            item = ancestor;
        }
        if (item is null)
        {
            item = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(e => Safe(() => e.Name).Contains(value, StringComparison.Ordinal));
        }
        Assert.NotNull(item);
        item!.Focus();
        item.Click();
        Thread.Sleep(400);
    }

    public void SetText(AutomationElement window, string automationId, string value)
    {
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is null) throw new InvalidOperationException($"Element '{automationId}' was not found.");
        element.Focus();
        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(value);
        }
        else
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(value);
        }
        Thread.Sleep(250);
    }

    public AppSettings ReadSettings()
    {
        var path = Path.Combine(_dataDirectory, "settings.json");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options)!;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _process.Dispose();
            try
            {
                var evidence = Path.Combine(RepoPaths.Root(), "artifacts", "test-evidence",
                    "ui-expansion-2026-11-12", "logs");
                Directory.CreateDirectory(evidence);
                var logs = Directory.GetFiles(Path.Combine(_dataDirectory, "logs"), "*.log");
                foreach (var log in logs)
                    File.Copy(log, Path.Combine(evidence, $"{Path.GetFileNameWithoutExtension(log)}-{Path.GetFileName(_dataDirectory)}.log"),
                        overwrite: true);
            }
            catch
            {
                // Log copying is best-effort.
            }
            try
            {
                if (!KeepData && Directory.Exists(_dataDirectory))
                    Directory.Delete(_dataDirectory, recursive: true);
            }
            catch
            {
                // SQLite or thumbnail handles can take a moment to release; leave temp data for inspection.
            }
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "PixelForge.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the PixelForge repository root.");
    }

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); } catch { return true; }
    }
}

internal static class NativeWindows
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // Real input events are required for WinUI 3 pointer gestures. SetCursorPos
    // (FlaUI's Mouse.MoveTo) moves the cursor without producing the press/move
    // sequence, so send injected mouse input instead.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public static void SendMouse(uint flags, int x = 0, int y = 0)
    {
        // Absolute coordinates are normalized over the physical screen, which
        // keeps this correct at 125% DPI where UIA and screen pixels agree.
        var input = new Input
        {
            type = 0,
            mi = new MouseInput
            {
                dx = (int)((long)x * 65535 / GetSystemMetrics(SM_CXSCREEN)),
                dy = (int)((long)y * 65535 / GetSystemMetrics(SM_CYSCREEN)),
                dwFlags = flags
            }
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static IntPtr FindOpenDialog()
    {
        var result = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            if (!string.Equals(cls.ToString(), "#32770", StringComparison.OrdinalIgnoreCase)) return true;
            var title = new StringBuilder(128);
            GetWindowText(h, title, title.Capacity);
            if (title.ToString().Contains("Open", StringComparison.OrdinalIgnoreCase))
            {
                result = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
