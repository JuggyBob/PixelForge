using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

var pid = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
if (pid <= 0) { Console.Error.WriteLine("Usage: UiProbe <pid> [automationId]"); return 2; }
using var automation = new UIA3Automation();
var app = Application.Attach(pid);
var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
if (window is null) { Console.Error.WriteLine("Main window not found"); return 3; }
Console.WriteLine($"Window: '{Safe(() => window.Title)}' id={Safe(() => window.AutomationId)} class={Safe(() => window.ClassName)} rect={window.BoundingRectangle}");
if (args.Length > 1)
{
    var target = args[1].StartsWith("name:", StringComparison.OrdinalIgnoreCase)
        ? window.FindFirstDescendant(cf => cf.ByName(args[1][5..]))
        : window.FindFirstDescendant(cf => cf.ByAutomationId(args[1]));
    if (target is null)
    {
        Console.WriteLine($"Element {args[1]} not found");
        return 4;
    }
    Console.WriteLine($"Found: {Safe(() => target.ControlType.ToString())} '{Safe(() => target.Name)}' enabled={Safe(() => target.IsEnabled.ToString())} rect={target.BoundingRectangle}");
    try
    {
        target.Focus();
        if (target.Patterns.ExpandCollapse.IsSupported)
        {
            target.Patterns.ExpandCollapse.Pattern.Expand();
            Console.WriteLine("Expanded");
        }
        else if (target.Patterns.Invoke.IsSupported)
        {
            target.Patterns.Invoke.Pattern.Invoke();
            Console.WriteLine("Invoked");
        }
        else
        {
            target.Click();
            Console.WriteLine("Clicked");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Click failed: " + ex.Message);
    }
    return 0;
}

var count = 0;
foreach (var element in window.FindAllDescendants())
{
    if (count++ > 260) break;
    var id = Safe(() => element.AutomationId);
    var name = Safe(() => element.Name);
    var type = Safe(() => element.ControlType.ToString());
    if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) continue;
    Console.WriteLine($"{type,-22} id={id,-34} name={Truncate(name, 70)}");
}
return 0;

static string Safe(Func<string> get)
{
    try { return get() ?? ""; } catch { return "?"; }
}
static string Truncate(string text, int max) => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
