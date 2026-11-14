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
        var edit = dialog!.FindAllDescendants(cf => cf.ByAutomationId("1148"))
            .FirstOrDefault(e => Safe(() => e.ControlType.ToString()) == "Edit")
            ?? dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("File name:")));
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

    public static UiAppContext Launch(Action<AppSettings>? configure = null, Action<string>? seed = null)
    {
        var root = FindRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, "src", "PixelForge.Presentation.WinUI", "bin", "x64", "Release",
                "net10.0-windows10.0.26100.0", "win-x64", "PixelForge.exe"),
            Path.Combine(root, "src", "PixelForge.Presentation.WinUI", "bin", "x64", "Debug",
                "net10.0-windows10.0.26100.0", "win-x64", "PixelForge.exe")
        };
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
                LastPage = "Home"
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
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Could not start PixelForge.");
        return new UiAppContext(dataDirectory, outputDirectory, process);
    }

    public AutomationElement Attach(UIA3Automation automation)
    {
        var app = Application.Attach(_process.Id);
        AutomationElement? window = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is not null && Safe(() => window.Name) == "PixelForge") return window;
            Thread.Sleep(300);
        }
        throw new TimeoutException("PixelForge main window did not appear.");
    }

    public void Click(AutomationElement window, string automationId)
    {
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is null) throw new InvalidOperationException($"Element '{automationId}' was not found.");
        element.Focus();
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
