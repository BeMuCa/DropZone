namespace DropZone.App.Model;

public sealed record ScriptCommand(string ScriptName, string? Arguments);

/// <summary>
/// Parses text messages arriving over LocalSend into script invocations. A message only counts
/// as a command when it opens with the trigger word, so ordinary text is never executed.
/// </summary>
public static class ScriptCommandParser
{
    public const string Trigger = "run";

    public static bool TryParse(string? message, out ScriptCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = message.Trim();

        // Only ever treat the first line as a command; a pasted document is not an instruction.
        var firstBreak = text.IndexOfAny(['\r', '\n']);
        if (firstBreak >= 0) text = text[..firstBreak].Trim();

        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        if (!parts[0].Equals(Trigger, StringComparison.OrdinalIgnoreCase)) return false;

        var name = parts[1];
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return false;

        command = new ScriptCommand(name, parts.Length > 2 ? parts[2] : null);
        return true;
    }
}
