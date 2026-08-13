using System.IO;
using DropZone.Core;

namespace DropZone.App.Tests;

public class ScriptCommandParserTests
{
    [Theory]
    [InlineData("run backup", "backup", null)]
    [InlineData("run backup now", "backup", "now")]
    [InlineData("RUN Backup", "Backup", null)]
    [InlineData("  run   backup   arg one  ", "backup", "arg one")]
    [InlineData("run play-spotify Bohemian Rhapsody", "play-spotify", "Bohemian Rhapsody")]
    public void Parses_commands(string message, string script, string? args)
    {
        Assert.True(ScriptCommandParser.TryParse(message, out var command));
        Assert.Equal(script, command.ScriptName);
        Assert.Equal(args, command.Arguments);
    }

    [Theory]
    [InlineData("backup", "backup", null)]
    [InlineData("Timer 5", "Timer", "5")]
    [InlineData("play-spotify Bohemian Rhapsody", "play-spotify", "Bohemian Rhapsody")]
    public void The_run_prefix_is_optional(string message, string script, string? args)
    {
        Assert.True(ScriptCommandParser.TryParse(message, out var command));
        Assert.Equal(script, command.ScriptName);
        Assert.Equal(args, command.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("run")]
    public void Nothing_to_name_is_not_a_command(string message)
    {
        Assert.False(ScriptCommandParser.TryParse(message, out _));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("Help")]
    [InlineData("commands")]
    [InlineData("?")]
    [InlineData("run help")]
    public void Recognises_a_request_for_the_command_list(string message)
    {
        Assert.True(ScriptCommandParser.IsHelpRequest(message));
    }

    [Theory]
    [InlineData("Timer 5")]
    [InlineData("helpful hints")]
    public void Ordinary_messages_are_not_help_requests(string message)
    {
        Assert.False(ScriptCommandParser.IsHelpRequest(message));
    }

    [Theory]
    [InlineData(@"run ..\..\evil")]
    [InlineData("run sub/dir")]
    [InlineData(@"run C:\windows\system32\calc")]
    public void Rejects_paths_in_the_script_name(string message)
    {
        Assert.False(ScriptCommandParser.TryParse(message, out _));
    }

    [Fact]
    public void Only_reads_the_first_line()
    {
        Assert.True(ScriptCommandParser.TryParse("run backup\nrm -rf /", out var command));
        Assert.Equal("backup", command.ScriptName);
        Assert.Null(command.Arguments);
    }

    [Fact]
    public void A_command_on_a_later_line_is_never_reached()
    {
        // Safety for pasted documents now rests on this plus the gate's whitelist, not on
        // requiring a "run" prefix.
        Assert.True(ScriptCommandParser.TryParse("Dear team,\nrun backup tonight please", out var command));
        Assert.Equal("Dear", command.ScriptName);
        Assert.DoesNotContain("backup", command.Arguments ?? "");
    }
}

public sealed class ScriptRegistryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dropzone-scripts-").FullName;
    string ConfigPath => Path.Combine(_dir, "scripts.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void MakeScript(string name) => File.WriteAllText(Path.Combine(_dir, name), "echo hi");

    [Fact]
    public void Lists_only_script_files()
    {
        MakeScript("a.ps1");
        MakeScript("b.bat");
        MakeScript("notes.txt");

        var names = new ScriptRegistry(_dir, ConfigPath).All().Select(s => s.Name).ToList();

        Assert.Equal(["a.ps1", "b.bat"], names.Order().ToList());
    }

    [Fact]
    public void Remote_is_off_by_default()
    {
        MakeScript("a.ps1");

        Assert.False(new ScriptRegistry(_dir, ConfigPath).All().Single().RemoteEnabled);
    }

    [Fact]
    public void Remote_toggle_persists()
    {
        MakeScript("a.ps1");
        var registry = new ScriptRegistry(_dir, ConfigPath);
        registry.SetRemoteEnabled("a.ps1", true);

        Assert.True(new ScriptRegistry(_dir, ConfigPath).All().Single().RemoteEnabled);
    }

    [Fact]
    public void A_script_that_is_not_enabled_cannot_be_invoked_remotely()
    {
        MakeScript("backup.ps1");

        Assert.Null(new ScriptRegistry(_dir, ConfigPath).FindRemotelyInvocable("backup"));
    }

    [Fact]
    public void An_enabled_script_resolves_by_name_with_or_without_extension()
    {
        MakeScript("backup.ps1");
        var registry = new ScriptRegistry(_dir, ConfigPath);
        registry.SetRemoteEnabled("backup.ps1", true);

        Assert.NotNull(registry.FindRemotelyInvocable("backup"));
        Assert.NotNull(registry.FindRemotelyInvocable("backup.ps1"));
        Assert.Null(registry.FindRemotelyInvocable("other"));
    }

    [Fact]
    public void Missing_folder_yields_no_scripts()
    {
        Assert.Empty(new ScriptRegistry(Path.Combine(_dir, "nope"), ConfigPath).All());
    }

    [Fact]
    public void Creates_a_script_that_is_then_listed()
    {
        var registry = new ScriptRegistry(_dir, ConfigPath);
        var created = registry.Create("backup.ps1", "echo hi");

        Assert.Equal(Path.Combine(_dir, "backup.ps1"), created.Path);
        Assert.Equal("echo hi", File.ReadAllText(created.Path));
        Assert.Equal("backup.ps1", registry.All().Single().Name);
    }

    [Fact]
    public void A_created_script_is_not_callable_from_another_device()
    {
        var registry = new ScriptRegistry(_dir, ConfigPath);
        registry.Create("backup.ps1", "echo hi");

        Assert.False(registry.All().Single().RemoteEnabled);
        Assert.Null(registry.FindRemotelyInvocable("backup"));
    }

    [Theory]
    [InlineData(@"..\escape.ps1")]
    [InlineData("../escape.ps1")]
    [InlineData(@"sub\backup.ps1")]
    [InlineData(@"C:\Windows\evil.ps1")]
    [InlineData("")]
    public void A_name_that_is_really_a_path_is_refused(string name)
    {
        var registry = new ScriptRegistry(_dir, ConfigPath);

        Assert.Throws<ArgumentException>(() => registry.Create(name, "echo hi"));
        Assert.Empty(registry.All());
    }

    [Fact]
    public void A_script_nothing_can_launch_is_refused()
    {
        var registry = new ScriptRegistry(_dir, ConfigPath);

        Assert.Throws<ArgumentException>(() => registry.Create("payload.exe", "echo hi"));
        Assert.False(File.Exists(Path.Combine(_dir, "payload.exe")));
    }

    [Fact]
    public void Creating_over_an_existing_script_is_refused()
    {
        MakeScript("backup.ps1");
        var registry = new ScriptRegistry(_dir, ConfigPath);

        Assert.Throws<IOException>(() => registry.Create("backup.ps1", "echo replaced"));
        Assert.Equal("echo hi", File.ReadAllText(Path.Combine(_dir, "backup.ps1")));
    }
}

public sealed class TransferHistoryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dropzone-history-").FullName;
    string Path_ => Path.Combine(_dir, "history.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static TransferEntry Entry(TransferDirection direction, DateTime when, params string[] files) => new()
    {
        Direction = direction,
        PeerAlias = "peer",
        PeerKind = PeerKind.Mobile,
        When = when,
        Files = files.Select(f => new TransferFile(f, $@"C:\x\{f}", 10)).ToList()
    };

    [Fact]
    public void Round_trips_through_disk()
    {
        var history = new TransferHistory(Path_);
        history.Add(Entry(TransferDirection.Received, DateTime.Now, "a.jpg", "b.jpg"));

        var reloaded = new TransferHistory(Path_);

        Assert.Single(reloaded.Entries);
        Assert.Equal(2, reloaded.Entries[0].Files.Count);
        Assert.Equal(PeerKind.Mobile, reloaded.Entries[0].PeerKind);
    }

    [Fact]
    public void Newest_first()
    {
        var history = new TransferHistory(Path_);
        history.Add(Entry(TransferDirection.Sent, new DateTime(2026, 1, 1), "old.jpg"));
        history.Add(Entry(TransferDirection.Sent, new DateTime(2026, 8, 1), "new.jpg"));

        Assert.Equal("new.jpg", history.Entries[0].Files[0].FileName);
    }

    [Fact]
    public void Filters_by_direction()
    {
        var history = new TransferHistory(Path_);
        history.Add(Entry(TransferDirection.Sent, DateTime.Now, "s.jpg"));
        history.Add(Entry(TransferDirection.Received, DateTime.Now, "r.jpg"));

        Assert.Single(history.By(TransferDirection.Sent));
        Assert.Equal("r.jpg", history.By(TransferDirection.Received)[0].Files[0].FileName);
    }

    [Fact]
    public void Groups_files_from_one_transfer_into_a_single_entry()
    {
        var history = new TransferHistory(Path_);
        history.Add(Entry(TransferDirection.Received, DateTime.Now, "1.jpg", "2.jpg", "3.jpg"));

        Assert.Single(history.Entries);
        Assert.Equal("3 files", history.Entries[0].Summary);
    }

    [Fact]
    public void A_single_file_is_summarised_by_its_name()
    {
        var history = new TransferHistory(Path_);
        history.Add(Entry(TransferDirection.Sent, DateTime.Now, "only.jpg"));

        Assert.Equal("only.jpg", history.Entries[0].Summary);
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty()
    {
        File.WriteAllText(Path_, "{ not json");

        Assert.Empty(new TransferHistory(Path_).Entries);
    }
}

public class PeerKindMapperTests
{
    [Theory]
    [InlineData("mobile", PeerKind.Mobile)]
    [InlineData("desktop", PeerKind.Desktop)]
    [InlineData("headless", PeerKind.Desktop)]
    [InlineData(null, PeerKind.Unknown)]
    [InlineData("something", PeerKind.Unknown)]
    public void Maps_protocol_device_types(string? deviceType, PeerKind expected)
    {
        Assert.Equal(expected, PeerKindMapper.From(deviceType));
    }
}
