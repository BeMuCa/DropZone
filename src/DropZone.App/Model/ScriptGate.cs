namespace DropZone.App.Model;

public enum ScriptGateOutcome
{
    /// <summary>Ordinary text — nothing to run.</summary>
    NotACommand,

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
        if (!ScriptCommandParser.TryParse(message, out var command))
            return new ScriptGateDecision(ScriptGateOutcome.NotACommand, null, null, "Not a command");

        if (!allowRemoteScripts)
            return new ScriptGateDecision(
                ScriptGateOutcome.MasterSwitchOff, null, command.Arguments, "Remote scripts are switched off");

        var script = registry.FindRemotelyInvocable(command.ScriptName);
        if (script is null)
            return new ScriptGateDecision(
                ScriptGateOutcome.NotEnabled, null, command.Arguments, "Not enabled for remote start");

        return new ScriptGateDecision(ScriptGateOutcome.Allowed, script, command.Arguments, "Started");
    }
}
