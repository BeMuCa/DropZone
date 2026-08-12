using System.IO;
using Dropzone.App.Model;

namespace Dropzone.App.Tests;

/// <summary>
/// Every route a device on the LAN could take to start code on this PC goes through ScriptGate,
/// so each refusal is pinned down here.
/// </summary>
public sealed class ScriptGateTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dropzone-gate-").FullName;
    ScriptRegistry Registry => new(_dir, Path.Combine(_dir, "scripts.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void MakeScript(string name, bool remoteEnabled)
    {
        File.WriteAllText(Path.Combine(_dir, name), "echo hi");
        if (remoteEnabled) Registry.SetRemoteEnabled(name, true);
    }

    [Fact]
    public void Plain_text_is_not_a_command()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("hey, are you there?", true, Registry);

        Assert.Equal(ScriptGateOutcome.NotACommand, decision.Outcome);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Master_switch_off_blocks_an_otherwise_valid_command()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("run backup", allowRemoteScripts: false, Registry);

        Assert.Equal(ScriptGateOutcome.MasterSwitchOff, decision.Outcome);
        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Script);
    }

    [Fact]
    public void A_script_not_ticked_for_remote_is_refused()
    {
        MakeScript("backup.ps1", remoteEnabled: false);

        var decision = ScriptGate.Evaluate("run backup", allowRemoteScripts: true, Registry);

        Assert.Equal(ScriptGateOutcome.NotEnabled, decision.Outcome);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void An_unknown_script_is_refused()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("run somethingelse", allowRemoteScripts: true, Registry);

        Assert.Equal(ScriptGateOutcome.NotEnabled, decision.Outcome);
    }

    [Fact]
    public void Both_switches_on_and_a_known_script_is_allowed()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("run backup", allowRemoteScripts: true, Registry);

        Assert.True(decision.IsAllowed);
        Assert.Equal("backup.ps1", decision.Script!.Name);
        Assert.Null(decision.Arguments);
    }

    [Fact]
    public void Arguments_survive_to_the_decision()
    {
        MakeScript("play.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("run play Bohemian Rhapsody", allowRemoteScripts: true, Registry);

        Assert.True(decision.IsAllowed);
        Assert.Equal("Bohemian Rhapsody", decision.Arguments);
    }

    [Theory]
    [InlineData(@"run ..\..\windows\system32\calc")]
    [InlineData("run scripts/backup")]
    public void Path_traversal_never_reaches_the_registry(string message)
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate(message, allowRemoteScripts: true, Registry);

        Assert.Equal(ScriptGateOutcome.NotACommand, decision.Outcome);
    }

    [Fact]
    public void A_document_mentioning_run_is_never_executed()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("Hi team,\nrun backup before you leave", true, Registry);

        Assert.Equal(ScriptGateOutcome.NotACommand, decision.Outcome);
    }
}
