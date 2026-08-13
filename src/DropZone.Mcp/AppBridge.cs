using System.IO;
using System.IO.Pipes;
using DropZone.Core;

namespace DropZone.Mcp;

/// <summary>
/// Talks to the running tray app. Whatever the app owns — the receiver, the live peer list, a
/// reply coming back from a peer — is only reachable through it, so tools ask here first and
/// fall back to working in this process wherever that is possible at all.
/// </summary>
static class AppBridge
{
    /// <summary>Null when the tray app is not running; otherwise whatever it answered.</summary>
    public static async Task<IpcResponse?> AskAsync(
        string command,
        Dictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", DropZoneIpc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)DropZoneIpc.ConnectTimeout.TotalMilliseconds, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return null;
        }

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync(DropZoneIpc.Write(new IpcRequest(command, arguments)));

        var line = await reader.ReadLineAsync(cancellationToken);

        return line is null ? null : DropZoneIpc.Read<IpcResponse>(line);
    }

    public static string Text(IpcResponse response) => response.Ok ? response.Text : $"DropZone: {response.Text}";

    public const string AppNotRunning =
        "DropZone is not running, and this needs it — only the app holds the receiver and the live peer list. " +
        "Start DropZone from the Start Menu and try again.";
}
