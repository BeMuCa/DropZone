using System.IO;
using DropZone.App.Model;

namespace DropZone.App.Tests;

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

    [Theory]
    [InlineData("run somethingelse")]
    [InlineData("somethingelse")]
    [InlineData("running late, sorry")]
    [InlineData("please run backup")]
    public void A_name_matching_no_script_never_runs(string message)
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate(message, allowRemoteScripts: true, Registry);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ScriptGateOutcome.NotACommand, decision.Outcome);
    }

    [Fact]
    public void The_script_name_alone_is_enough()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("backup", allowRemoteScripts: true, Registry);

        Assert.True(decision.IsAllowed);
        Assert.Equal("backup.ps1", decision.Script!.Name);
    }

    [Fact]
    public void Asking_for_help_is_recognised_and_runs_nothing()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var decision = ScriptGate.Evaluate("help", allowRemoteScripts: true, Registry);

        Assert.Equal(ScriptGateOutcome.HelpRequested, decision.Outcome);
        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Script);
    }

    [Fact]
    public void The_help_reply_lists_only_remotely_enabled_scripts()
    {
        MakeScript("backup.ps1", remoteEnabled: true);
        MakeScript("secret.ps1", remoteEnabled: false);

        var reply = ScriptGate.BuildHelpReply(allowRemoteScripts: true, Registry);

        Assert.Contains("backup", reply);
        Assert.DoesNotContain("secret", reply);
    }

    [Fact]
    public void The_help_reply_says_so_when_remote_is_off()
    {
        MakeScript("backup.ps1", remoteEnabled: true);

        var reply = ScriptGate.BuildHelpReply(allowRemoteScripts: false, Registry);

        Assert.Contains("switched off", reply);
        Assert.DoesNotContain("backup", reply);
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
