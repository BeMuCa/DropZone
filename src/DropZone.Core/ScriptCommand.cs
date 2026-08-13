namespace DropZone.Core;

public sealed record ScriptCommand(string ScriptName, string? Arguments);

/// <summary>
/// Parses text messages arriving over LocalSend into script invocations.
///
/// The script name alone is enough — "Timer 5" works. A leading "run" is accepted and ignored
/// so older habits and the on-screen hint both keep working. Safety does not come from this
/// parser: nothing runs unless the master switch is on *and* that specific script is ticked
/// for remote start, so an unmatched word is simply a message.
/// </summary>
public static class ScriptCommandParser
{
    public const string OptionalPrefix = "run";

    /// <summary>Words that ask DropZone itself what can be started, rather than naming a script.</summary>
    static readonly string[] HelpWords = ["help", "commands", "?", "list"];

    public static bool IsHelpRequest(string? message)
    {
        var first = FirstLine(message);
        if (first is null) return false;

        var words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return false;

        var candidate = words[0].Equals(OptionalPrefix, StringComparison.OrdinalIgnoreCase) && words.Length > 1
            ? words[1]
            : words[0];

        return HelpWords.Contains(candidate, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? message, out ScriptCommand command)
    {
        command = null!;

        var text = FirstLine(message);
        if (text is null) return false;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        // "run Timer 5" and "Timer 5" mean the same thing.
        var start = 0;
        if (tokens[0].Equals(OptionalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length < 2) return false;
            start = 1;
        }

        var name = tokens[start];
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return false;

        var arguments = tokens.Length > start + 1 ? string.Join(' ', tokens[(start + 1)..]) : null;
        command = new ScriptCommand(name, arguments);
        return true;
    }

    /// <summary>Only ever the first line — a pasted document is not an instruction.</summary>
    static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = message.Trim();
        var breakAt = text.IndexOfAny(['\r', '\n']);
        if (breakAt >= 0) text = text[..breakAt].Trim();

        return text.Length == 0 ? null : text;
    }
}
