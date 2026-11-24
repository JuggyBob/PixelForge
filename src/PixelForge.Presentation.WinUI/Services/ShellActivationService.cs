using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PixelForge.Presentation.Services;

public static class ShellActivationService
{
    private const int MaxMessageChars = 128 * 1024;
    private static readonly string PipeName = BuildPipeName();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string BuildPipeName()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { user = Environment.UserName; }
        return "PixelForge.Activation.v1." + user.Replace('\\', '_');
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException(
            "The current Windows user SID is unavailable; shell activation is disabled.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static void StartServer(Action<string[]> onActivated)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 8192, leaveOpen: true);
                    var builder = new StringBuilder();
                    var buffer = new char[8192];
                    int read;
                    while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        builder.Append(buffer, 0, read);
                        if (builder.Length > MaxMessageChars)
                            throw new InvalidDataException("The activation request is too large.");
                    }
                    var args = JsonSerializer.Deserialize<string[]>(builder.ToString(), JsonOptions);
                    if (args is { Length: > 0 and <= 256 } &&
                        args.All(arg => !string.IsNullOrWhiteSpace(arg) &&
                                        arg.Length <= 32768 &&
                                        !arg.Any(char.IsControl)))
                    {
                        onActivated(args);
                    }
                }
                catch
                {
                    await Task.Delay(250);
                }
            }
        });
    }

    public static async Task SendToRunningInstanceAsync(string[] args)
    {
        if (args.Length == 0) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(250);
                await using var writer = new StreamWriter(client);
                await writer.WriteAsync(JsonSerializer.Serialize(args));
                await writer.FlushAsync();
                return;
            }
            catch
            {
                if (attempt < 7) await Task.Delay(150);
            }
        }
    }
}
