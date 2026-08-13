using System.Text.Json;

namespace DropZone.Core;

/// <summary>
/// How the MCP server reaches the running tray app: one JSON line in, one JSON line out, one
/// request per connection. Everything is carried as text because every caller renders text.
/// </summary>
public static class DropZoneIpc
{
    /// <summary>Local to this machine and this user — the two processes are always the same person.</summary>
    public const string PipeName = "DropZone.Ipc";

    /// <summary>Long enough to tell "not running" from "busy", short enough not to stall a tool call.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string line) => JsonSerializer.Deserialize<T>(line, Options);
}

public sealed record IpcRequest(string Command, Dictionary<string, string>? Arguments = null)
{
    public string? Argument(string name) =>
        Arguments is not null && Arguments.TryGetValue(name, out var value) ? value : null;
}

public sealed record IpcResponse(bool Ok, string Text);
