using System.Globalization;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

var pid = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
var action = args.Length > 1 ? args[1] : "tree";
if (pid <= 0) { Console.Error.WriteLine("Usage: UiFlow <pid> <tree|converter|openfile|files> [path]"); return 2; }
using var automation = new UIA3Automation();
var app = Application.Attach(pid);
var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
if (window is null) { Console.Error.WriteLine("No window"); return 3; }

static string S(Func<string> get) { try { return get() ?? ""; } catch { return "?"; } }
void Dump(AutomationElement root, int max = 200)
{
    var count = 0;
    foreach (var e in root.FindAllDescendants())
    {
        if (count++ > max) break;
        var id = S(() => e.AutomationId);
        var name = S(() => e.Name);
        var type = S(() => e.ControlType.ToString());
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) continue;
        Console.WriteLine($"{type,-20} id={id,-32} name={(name.Length > 70 ? name[..70] + "…" : name)}");
    }
}

if (action == "rect")
{
    foreach (var e in window.FindAllDescendants())
    {
        var id = S(() => e.AutomationId);
        if (string.IsNullOrWhiteSpace(id)) continue;
        if (args.Length > 2 && !id.Contains(args[2], StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"{id,-40} {e.BoundingRectangle}");
    }
    return 0;
}

if (action == "clickid")
{
    var id = args.Length > 2 ? args[2] : "";
    if (string.IsNullOrWhiteSpace(id)) { Console.Error.WriteLine("ID required"); return 5; }
    ClickId(id);
    return 0;
}

if (action == "tree")
{
    Dump(window);
    return 0;
}

void ClickId(string id)
{
    var element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
    if (element is null) throw new InvalidOperationException($"Element {id} not found");
    element.Focus();
    element.Click();
    Console.WriteLine("Clicked " + id);
}

AutomationElement? FindOpenDialog()
{
    foreach (var w in automation.GetDesktop().FindAllChildren())
    {
        var cls = S(() => w.ClassName);
        var name = S(() => w.Name);
        if (cls.Contains("32770") && name.Contains("Open", StringComparison.OrdinalIgnoreCase))
            return w;
    }
    foreach (var handle in NativeWindow.FindWindowsByClass("#32770"))
    {
        var element = automation.FromHandle(handle);
        if (element is not null && S(() => element.Name).Contains("Open", StringComparison.OrdinalIgnoreCase))
            return element;
    }
    return null;
}

if (action == "converter")
{
    ClickId("NavConverter");
    Thread.Sleep(2500);
    ClickId("ConverterChooseFiles");
    Thread.Sleep(2500);
    var dialog = FindOpenDialog();
    Console.WriteLine(dialog is null ? "Open dialog not found" : "Open dialog found");
    if (dialog is not null)
    {
        Console.WriteLine("--- dialog tree ---");
        Dump(dialog);
    }
    return 0;
}

if (action == "dialogpath")
{
    var dialog = FindOpenDialog();
    if (dialog is null) { Console.WriteLine("Open dialog not found"); return 4; }
    var edit = dialog.FindAllDescendants(cf => cf.ByAutomationId("1148"))
        .FirstOrDefault(e => S(() => e.ControlType.ToString()) == "Edit");
    if (edit is null) { Console.WriteLine("File name edit not found"); return 5; }
    edit.Focus();
    edit.AsTextBox().Text = args.Length > 2 ? args[2] : "";
    Thread.Sleep(300);
    NativeWindow.SetForegroundWindow((IntPtr)dialog.Properties.NativeWindowHandle.Value);
    Thread.Sleep(200);
    Keyboard.Press(VirtualKeyShort.RETURN);
    Console.WriteLine("Selected path in Open dialog");
    Thread.Sleep(2500);
    return 0;
}

if (action == "dialogfolder")
{
    AutomationElement? dialog = null;
    foreach (var w in automation.GetDesktop().FindAllChildren())
    {
        var cls = S(() => w.ClassName);
        var name = S(() => w.Name);
        if (cls.Contains("32770") && name.Contains("Folder", StringComparison.OrdinalIgnoreCase))
        {
            dialog = w;
            break;
        }
    }
    foreach (var handle in NativeWindow.FindWindowsByClass("#32770"))
    {
        var candidate = automation.FromHandle(handle);
        if (candidate is not null && S(() => candidate.Name).Contains("Folder", StringComparison.OrdinalIgnoreCase))
        {
            dialog = candidate;
            break;
        }
    }
    dialog ??= FindOpenDialog();
    if (dialog is null) { Console.WriteLine("Folder dialog not found"); return 4; }
    var edit = dialog.FindAllDescendants(cf => cf.ByAutomationId("1152"))
        .FirstOrDefault(e => S(() => e.ControlType.ToString()) == "Edit");
    if (edit is null) { Console.WriteLine("Folder name edit not found"); return 5; }
    edit.Focus();
    edit.AsTextBox().Text = args.Length > 2 ? args[2] : "";
    Thread.Sleep(400);
    Keyboard.Press(VirtualKeyShort.RETURN);
    Thread.Sleep(900);
    var select = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .FirstOrDefault(b => S(() => b.Name).Contains("Use this folder", StringComparison.OrdinalIgnoreCase));
    if (select is not null)
    {
        select.Focus();
        select.Click();
        Console.WriteLine("Selected folder");
    }
    else
    {
        Keyboard.Press(VirtualKeyShort.RETURN);
        Console.WriteLine("Pressed Enter for folder selection");
    }
    Thread.Sleep(2500);
    return 0;
}

if (action == "dialogenter")
{
    var dialog = FindOpenDialog();
    if (dialog is null) { Console.WriteLine("Open dialog not found"); return 4; }
    var edit = dialog.FindAllDescendants(cf => cf.ByAutomationId("1148"))
        .FirstOrDefault(e => S(() => e.ControlType.ToString()) == "Edit");
    if (edit is not null) edit.Focus();
    Thread.Sleep(200);
    NativeWindow.SetForegroundWindow((IntPtr)dialog.Properties.NativeWindowHandle.Value);
    Thread.Sleep(200);
    Keyboard.Press(VirtualKeyShort.RETURN);
    Console.WriteLine("Pressed Enter in Open dialog");
    Thread.Sleep(2000);
    return 0;
}

if (action == "listbutton")
{
    var listIndex = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
    var buttonIndex = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
    var items = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
        .Where(i => S(() => i.Name).Contains("HistoryItemViewModel", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (items.Count <= listIndex) { Console.WriteLine($"List item {listIndex} not found ({items.Count} candidates)"); return 4; }
    var buttons = items[listIndex].FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).ToList();
    if (buttons.Count <= buttonIndex) { Console.WriteLine($"Button {buttonIndex} not found ({buttons.Count} buttons)"); return 4; }
    Console.WriteLine($"Clicking button {buttonIndex} in history item {listIndex}");
    buttons[buttonIndex].Focus();
    buttons[buttonIndex].Click();
    return 0;
}

if (action == "sections")
{
    var wanted = args.Length > 2 ? args[2] : "";
    var list = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSections"));
    if (list is null) { Console.WriteLine("Settings sections list not found"); return 4; }
    var item = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
        .FirstOrDefault(e => string.Equals(S(() => e.Name), wanted, StringComparison.Ordinal));
    if (item is null) { Console.WriteLine($"Settings section '{wanted}' not found"); return 4; }
    Console.WriteLine($"Selecting settings section '{wanted}'");
    item.Focus();
    item.Click();
    return 0;
}

if (action == "dialogbutton")
{
    var buttonName = args.Length > 2 ? args[2] : "OK";
    foreach (var handle in NativeWindow.FindWindowsByClass("#32770"))
    {
        var dlg = automation.FromHandle(handle);
        if (dlg is null) continue;
        var button = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(buttonName)));
        if (button is null) continue;
        Console.WriteLine($"Clicking '{buttonName}' in dialog '{S(() => dlg.Name)}'");
        button.Focus();
        button.Click();
        return 0;
    }
    Console.WriteLine("Dialog button not found");
    return 4;
}

if (action == "comboset")
{
    var id = args.Length > 2 ? args[2] : "";
    var value = args.Length > 3 ? args[3] : "";
    var combo = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
    if (combo is null) { Console.WriteLine($"Combo '{id}' not found"); return 4; }
    combo.Focus();
    try
    {
        if (combo.Patterns.ExpandCollapse.IsSupported) combo.Patterns.ExpandCollapse.Pattern.Expand();
        else combo.Click();
    }
    catch (Exception ex) { Console.WriteLine("Expand failed: " + ex.Message); combo.Click(); }
    Thread.Sleep(700);

    static bool NameIs(AutomationElement e, string wanted) => string.Equals(S(() => e.Name), wanted, StringComparison.Ordinal);
    var scopes = new List<AutomationElement> { combo, window };
    scopes.AddRange(automation.GetDesktop().FindAllChildren());
    AutomationElement? target = null;
    foreach (var scope in scopes)
    {
        var items = scope.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(e => NameIs(e, value)).ToList();
        if (items.Count == 0)
            items = scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(e => NameIs(e, value)).ToList();
        if (items.Count > 0) { target = items[0]; break; }
    }
    if (target is null) { Console.WriteLine($"Item '{value}' not found after expanding '{id}'"); return 4; }
    Console.WriteLine($"Selecting '{value}'");
    target.Focus();
    target.Click();
    Thread.Sleep(500);
    return 0;
}

if (action == "setvalue")
{
    var id = args.Length > 2 ? args[2] : "";
    var value = args.Length > 3 ? args[3] : "";
    var element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
    if (element is null) { Console.WriteLine($"Element '{id}' not found"); return 4; }
    element.Focus();
    if (!element.Patterns.Value.IsSupported) { Console.WriteLine("Value pattern unsupported"); return 4; }
    element.Patterns.Value.Pattern.SetValue(value);
    Console.WriteLine($"Set '{id}' = '{value}'");
    return 0;
}

if (action == "setrange")
{
    var id = args.Length > 2 ? args[2] : "";
    var value = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0;
    var element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
    if (element is null) { Console.WriteLine($"Element '{id}' not found"); return 4; }
    element.Focus();
    if (!element.Patterns.RangeValue.IsSupported) { Console.WriteLine("Range value pattern unsupported"); return 4; }
    element.Patterns.RangeValue.Pattern.SetValue(value);
    Console.WriteLine($"Set range '{id}' = {value}");
    return 0;
}

if (action == "clickname")
{
    var name = args.Length > 2 ? args[2] : "";
    var index = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
    if (string.IsNullOrWhiteSpace(name)) { Console.Error.WriteLine("Name required"); return 5; }
    var matches = window.FindAllDescendants(cf => cf.ByName(name));
    var shown = matches.Where(m => { try { return m.IsOffscreen == false; } catch { return true; } }).ToList();
    var pool = shown.Count > index ? shown : matches.ToList();
    var target = pool.ElementAtOrDefault(index);
    if (target is null) { Console.WriteLine($"Element named '{name}' [{index}] not found"); return 4; }
    Console.WriteLine($"Clicking {S(() => target.ControlType.ToString())} '{name}'");
    target.Focus();
    target.Click();
    return 0;
}

if (action is "openfile" or "files" or "dialogfile")
{
    var path = args.Length > 2 ? Path.GetFullPath(args[2]) : "";
    if (string.IsNullOrWhiteSpace(path)) { Console.Error.WriteLine("Path required"); return 5; }
    var dialog = FindOpenDialog();
    if (dialog is null && action != "dialogfile")
    {
        var title = window.FindFirstDescendant(cf => cf.ByAutomationId("ConverterTitle"));
        if (title is null)
        {
            ClickId("NavConverter");
            Thread.Sleep(2000);
        }
        ClickId("ConverterChooseFiles");
        Thread.Sleep(2500);
        dialog = FindOpenDialog();
    }
    if (dialog is null)
    {
        Console.WriteLine("Open dialog not found");
        return 4;
    }

    Console.WriteLine("Dialog: " + S(() => dialog.Name));
    var edit = dialog.FindAllDescendants(cf => cf.ByAutomationId("1148"))
        .FirstOrDefault(e => S(() => e.ControlType.ToString()) == "Edit")
        ?? dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("File name:")));
    if (edit is null)
    {
        Console.WriteLine("File name edit not found");
        Dump(dialog, 120);
        return 6;
    }

    edit.Focus();
    Thread.Sleep(250);
    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
    Keyboard.Type(path);
    Thread.Sleep(500);
    Console.WriteLine("Path typed: " + path);
    if (edit.Patterns.Value.IsSupported)
        Console.WriteLine("Edit value now: " + S(() => edit.Patterns.Value.Pattern.Value.Value));

    var open = dialog.FindAllDescendants(cf => cf.ByAutomationId("1"))
        .FirstOrDefault(e => S(() => e.ControlType.ToString()) == "SplitButton");
    open ??= dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .FirstOrDefault(e => S(() => e.Name) == "Open");
    if (open is not null)
    {
        open.Focus();
        open.Click();
        Console.WriteLine("Clicked Open");
    }
    else
    {
        Keyboard.Press(VirtualKeyShort.RETURN);
        Console.WriteLine("Pressed Enter");
    }

    for (var i = 0; i < 30; i++)
    {
        Thread.Sleep(300);
        if (FindOpenDialog() is null) break;
    }
    Thread.Sleep(2500);

    Console.WriteLine("--- after import ---");
    var convTitle = window.FindFirstDescendant(cf => cf.ByAutomationId("ConverterTitle"));
    Console.WriteLine("Converter title present=" + (convTitle is not null));
    Dump(window, 120);
    return 0;
}
return 0;

internal static class NativeWindow
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    public static IEnumerable<IntPtr> FindWindowsByClass(string className)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (string.Equals(sb.ToString(), className, StringComparison.OrdinalIgnoreCase)) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
