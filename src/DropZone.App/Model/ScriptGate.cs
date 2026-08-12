namespace DropZone.App.Model;

public enum ScriptGateOutcome
{
    /// <summary>Ordinary text — no script by that name is available to this sender.</summary>
    NotACommand,

    /// <summary>The sender asked what they are allowed to start.</summary>
    HelpRequested,

    /// <summary>Remote invocation is switched off for the whole app.</summary>
    MasterSwitchOff,

    /// <summary>No such script, or it is not ticked for remote start.</summary>
    NotEnabled,

    Allowed
}

public sealed record ScriptGateDecision(
    ScriptGateOutcome Outcome, ScriptInfo? Script, string? Arguments, string Detail)
{
    public bool IsAllowed => Outcome == ScriptGateOutcome.Allowed;
}

/// <summary>
/// The single decision point for "may this incoming message start a script?". Kept separate from
/// the transport so the rule can be tested directly — every path a remote device could take runs
/// through here.
/// </summary>
public static class ScriptGate
{
    public static ScriptGateDecision Evaluate(string? message, bool allowRemoteScripts, ScriptRegistry registry)
    {
        if (ScriptCommandParser.IsHelpRequest(message))
            return new ScriptGateDecision(ScriptGateOutcome.HelpRequested, null, null, "Listing commands");

        if (!ScriptCommandParser.TryParse(message, out var command))
            return new ScriptGateDecision(ScriptGateOutcome.NotACommand, null, null, "Not a command");

        if (!allowRemoteScripts)
        {
            // Only complain when the word actually names a script; otherwise it was just a message.
            var known = registry.All().Any(s =>
                s.DisplayName.Equals(command.ScriptName, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(command.ScriptName, StringComparison.OrdinalIgnoreCase));

            return known
                ? new ScriptGateDecision(
                    ScriptGateOutcome.MasterSwitchOff, null, command.Arguments, "Remote scripts are switched off")
                : new ScriptGateDecision(ScriptGateOutcome.NotACommand, null, null, "Not a command");
        }

        var script = registry.FindRemotelyInvocable(command.ScriptName);
        if (script is null)
        {
            var known = registry.All().Any(s =>
                s.DisplayName.Equals(command.ScriptName, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(command.ScriptName, StringComparison.OrdinalIgnoreCase));

            return known
                ? new ScriptGateDecision(
                    ScriptGateOutcome.NotEnabled, null, command.Arguments, "Not enabled for remote start")
                : new ScriptGateDecision(ScriptGateOutcome.NotACommand, null, null, "Not a command");
        }

        return new ScriptGateDecision(ScriptGateOutcome.Allowed, script, command.Arguments, "Started");
    }

    /// <summary>The reply sent when a device asks what it may run.</summary>
    public static string BuildHelpReply(bool allowRemoteScripts, ScriptRegistry registry)
    {
        if (!allowRemoteScripts)
            return "DropZone: remote scripts are switched off on that PC.";

        var callable = registry.All().Where(s => s.RemoteEnabled).ToList();
        if (callable.Count == 0)
            return "DropZone: no scripts are enabled for remote start yet.";

        var lines = callable.Select(s => $"  {s.HowToCall}");
        return "DropZone commands you can send:\n" + string.Join("\n", lines) +
               "\n\nSend the name on its own, with any arguments after it.";
    }
}
