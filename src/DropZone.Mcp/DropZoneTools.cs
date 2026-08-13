using System.ComponentModel;
using System.Text.Json;
using DropZone.Core;
using DropZone.LocalSend;
using DropZone.Mtp;
using ModelContextProtocol.Server;

namespace DropZone.Mcp;

/// <summary>
/// Everything a person can do with DropZone, as MCP tools.
///
/// Anything the tray app owns — the receiver, the peer list it is already maintaining, replies
/// arriving from a peer — is done by asking the app over its pipe. The rest is done here, so the
/// phone, sending and local scripts still work with the app closed. Tools that cannot work
/// without it say so instead of quietly doing something different.
/// </summary>
[McpServerToolType]
public static class DropZoneTools
{
    [McpServerTool(Name = "dropzone_status")]
    [Description("Overall state: whether the tray app is running, whether it is receiving, the alias and folders in use, and whether remote script invocation is allowed.")]
    public static async Task<string> DropZoneStatus(CancellationToken cancellationToken = default)
    {
        var answer = await AppBridge.AskAsync("status", null, cancellationToken);
        if (answer is not null) return AppBridge.Text(answer);

        return $"""
                app            : not running (phone, sending and local scripts still work)
                receiving       : off — only the tray app can receive
                alias          : {Config.Alias}
                root folder    : {Config.RootFolder}
                remote scripts : {(Config.AllowRemoteScripts ? "allowed" : "off (master switch)")}
                """;
    }

    [McpServerTool(Name = "phone_status")]
    [Description("Report whether an iPhone is attached over USB and unlocked. Nothing can be scanned or imported while it is locked.")]
    public static string PhoneStatus()
    {
        var status = new MediaDevicesPhoneSource().Status();

        return $"""
                connected : {status.Connected}
                unlocked  : {status.Unlocked}
                name      : {status.Name ?? "-"}
                => {status.Describe()}
                """;
    }

    [McpServerTool(Name = "phone_scan")]
    [Description("List media files on the attached iPhone, newest first. A whole library is thousands of files and takes minutes to walk, so the scan stops as soon as it has enough.")]
    public static string PhoneScan(
        [Description("How many files to list. Default 50.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var found = TakeNewest(new MediaDevicesPhoneSource(), Math.Clamp(limit, 1, 1000), cancellationToken);

        if (found.Count == 0) return "No media found. Is the phone plugged in and unlocked?";

        var lines = found.Select(item =>
            $"{item.Name,-24} {item.Size,12:N0} bytes  {item.Modified:yyyy-MM-dd}  {item.Path}");

        return $"{found.Count} file(s), newest first:\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "phone_import")]
    [Description("Copy photos and videos off the iPhone onto this PC into dated folders, skipping anything an earlier import already took.")]
    public static async Task<string> PhoneImport(
        [Description("Destination folder. Defaults to the DropZone iPhone folder.")] string? destination = null,
        [Description("Import only the newest N files. 0, the default, imports everything not yet imported and can take many minutes.")] int count = 0,
        CancellationToken cancellationToken = default)
    {
        var source = new MediaDevicesPhoneSource();
        var target = destination ?? Config.PhotoFolder;

        // Share the tray app's ledger so an import here is not repeated there, and vice versa.
        var importer = new PhoneImporter(source, new FileImportLedger(Config.LedgerPath));

        var result = count > 0
            ? await importer.ImportAsync(
                TakeNewest(source, count, cancellationToken), target, null, cancellationToken)
            : await importer.ImportAsync(target, null, cancellationToken);

        var report = $"into {target}\n" +
                     $"copied={result.Copied} skipped={result.Skipped} failed={result.Failed} bytes={result.BytesCopied:N0}";

        return result.Errors.Count == 0
            ? report
            : $"{report}\n{string.Join("\n", result.Errors.Take(10).Select(error => $"error: {error}"))}";
    }

    [McpServerTool(Name = "discover_peers")]
    [Description("Find the other devices on this network — PCs running DropZone, and phones with LocalSend open. Use the alias it returns to send anything.")]
    public static async Task<string> DiscoverPeers(
        [Description("How long to listen, in seconds. Default 3.")] int seconds = 3,
        CancellationToken cancellationToken = default)
    {
        var answer = await AppBridge.AskAsync(
            "peers", new() { ["seconds"] = seconds.ToString() }, cancellationToken);

        if (answer is not null) return AppBridge.Text(answer);

        var (peers, bindFailure) = await FindPeersAsync(seconds, cancellationToken);

        if (peers.Count == 0)
            return "No peers answered." +
                   (bindFailure is null ? "" : $" The discovery port is taken by another app: {bindFailure}");

        return Describe(peers);
    }

    [McpServerTool(Name = "send_files")]
    [Description("Send files from this PC to another device, matched on the alias or fingerprint that discover_peers reported.")]
    public static async Task<string> SendFiles(
        [Description("Alias or fingerprint of the receiving device.")] string peer,
        [Description("Full paths of the files to send.")] string[] paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Length == 0) return "No files given.";

        var missing = paths.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0) return $"Not found: {string.Join(", ", missing)}";

        var answer = await AppBridge.AskAsync(
            "send_files",
            new() { ["peer"] = peer, ["paths"] = string.Join("\n", paths) },
            cancellationToken);

        if (answer is not null) return AppBridge.Text(answer);

        var match = await ResolveStandaloneAsync(peer, cancellationToken);
        if (match.Peer is null) return match.Problem!;

        using var sender = new LocalSendSender(Self());

        try
        {
            await sender.SendAsync(match.Peer, paths, null, cancellationToken);
        }
        catch (SendRejectedException ex)
        {
            return $"{match.Peer.Alias} refused the transfer: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {match.Peer.Alias} at {match.Peer.BaseUrl}: {ex.Message}";
        }

        return $"Sent {paths.Length} file(s) to {match.Peer.Alias} ({match.Peer.Address}).";
    }

    [McpServerTool(Name = "send_text")]
    [Description("Send a text message to another device — the same thing as typing in the Send tab's message box.")]
    public static async Task<string> SendText(
        [Description("Alias or fingerprint of the receiving device.")] string peer,
        [Description("The message to send.")] string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Nothing to send.";

        var answer = await AppBridge.AskAsync(
            "send_text", new() { ["peer"] = peer, ["text"] = text }, cancellationToken);

        if (answer is not null) return AppBridge.Text(answer);

        return await SendTextStandaloneAsync(peer, text, cancellationToken);
    }

    [McpServerTool(Name = "set_receiving")]
    [Description("Turn receiving on or off — the switch on the Receive tab. Only the tray app can receive, because it holds the port.")]
    public static async Task<string> SetReceiving(
        [Description("True to accept incoming transfers, false to stop.")] bool on,
        CancellationToken cancellationToken = default)
    {
        var answer = await AppBridge.AskAsync(
            "receive", new() { ["on"] = on ? "true" : "false" }, cancellationToken);

        return answer is null ? AppBridge.AppNotRunning : AppBridge.Text(answer);
    }

    [McpServerTool(Name = "list_scripts")]
    [Description("The scripts on this PC, showing which are ticked for another device to start and whether the master switch allows that at all.")]
    public static async Task<string> ListScripts(CancellationToken cancellationToken = default)
    {
        var answer = await AppBridge.AskAsync("scripts", null, cancellationToken);
        if (answer is not null) return AppBridge.Text(answer);

        var scripts = Scripts().All();
        if (scripts.Count == 0) return $"No scripts in {Config.ScriptsFolder}.";

        var lines = scripts.Select(script =>
            $"{script.HowToCall,-20} {(script.RemoteEnabled ? "remote-enabled" : "local only  ")}  {script.Name}");

        return $"{scripts.Count} script(s) in {Config.ScriptsFolder}:\n{string.Join("\n", lines)}\n" +
               $"master switch: {(Config.AllowRemoteScripts ? "allowed" : "off")}";
    }

    [McpServerTool(Name = "create_script")]
    [Description("Write a new script into the DropZone Scripts folder, where it can then be run. It is created local-only: letting another device start it is a tick the user makes in the Scripts tab.")]
    public static string CreateScript(
        [Description("File name including extension, for example \"backup.ps1\". One of the extensions DropZone knows how to launch.")] string name,
        [Description("The full contents of the script.")] string content)
    {
        try
        {
            var created = Scripts().Create(name, content);

            return $"Created {created.Path}\n" +
                   $"Run it with run_script(\"{created.DisplayName}\"). It is local-only until it is ticked " +
                   "for remote start in the Scripts tab.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return $"Not created: {ex.Message}";
        }
    }

    [McpServerTool(Name = "run_script")]
    [Description("Run one of this PC's scripts, optionally with arguments — the Run button on the Scripts tab.")]
    public static async Task<string> RunScript(
        [Description("Script name, with or without its extension.")] string name,
        [Description("Arguments passed after the script path.")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, string> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(arguments)) request["arguments"] = arguments;

        var answer = await AppBridge.AskAsync("run_script", request, cancellationToken);
        if (answer is not null) return AppBridge.Text(answer);

        var registry = Scripts();

        var script = registry.All().FirstOrDefault(candidate =>
            candidate.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (script is null) return $"No script called \"{name}\" in {Config.ScriptsFolder}.";

        try
        {
            registry.Run(script, arguments);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Could not start {script.Name}: {ex.Message}";
        }

        return $"Started: {registry.CommandLineFor(script, arguments)}";
    }

    [McpServerTool(Name = "list_remote_scripts")]
    [Description("Ask another device which of its scripts this PC is allowed to start. It answers only if it runs DropZone with remote scripts allowed.")]
    public static async Task<string> ListRemoteScripts(
        [Description("Alias or fingerprint of the other device.")] string peer,
        CancellationToken cancellationToken = default)
    {
        var answer = await AppBridge.AskAsync(
            "remote_scripts", new() { ["peer"] = peer }, cancellationToken);

        return answer is null ? AppBridge.AppNotRunning : AppBridge.Text(answer);
    }

    [McpServerTool(Name = "run_remote_script")]
    [Description("Start a script on another device. It runs only if that device has ticked that script for remote start and has its master switch on.")]
    public static async Task<string> RunRemoteScript(
        [Description("Alias or fingerprint of the other device.")] string peer,
        [Description("Script name as that device reports it.")] string name,
        [Description("Arguments passed to the script.")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Which script?";

        var request = new Dictionary<string, string> { ["peer"] = peer, ["name"] = name };
        if (!string.IsNullOrWhiteSpace(arguments)) request["arguments"] = arguments;

        var answer = await AppBridge.AskAsync("run_remote_script", request, cancellationToken);
        if (answer is not null) return AppBridge.Text(answer);

        // Without the app there is no receiver, so the command can be sent but its answer is lost.
        var command = string.IsNullOrWhiteSpace(arguments) ? name : $"{name} {arguments}";
        var sent = await SendTextStandaloneAsync(peer, command, cancellationToken);

        return $"{sent}\nDropZone is not running here, so whatever it replied could not be read.";
    }

    [McpServerTool(Name = "transfer_history")]
    [Description("The most recent transfers recorded by the DropZone tray app, newest first, as raw history entries.")]
    public static string TransferHistory(
        [Description("How many transfers to return. Default 20.")] int limit = 20)
    {
        if (!File.Exists(Config.HistoryPath)) return "No history yet.";

        List<string> entries;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Config.HistoryPath));

            // The log is append-only, so the newest entries are the last ones in the file.
            entries = document.RootElement.EnumerateArray()
                .TakeLast(Math.Clamp(limit, 1, 200))
                .Reverse()
                .Select(entry => entry.GetRawText())
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return $"History could not be read: {ex.Message}";
        }

        return entries.Count == 0 ? "No history yet." : $"[{string.Join(",", entries)}]";
    }

    /// <summary>
    /// Walks the phone only until enough files have been seen. The scan streams newest-first,
    /// so stopping early is what keeps this to seconds instead of the ~8 minutes a full walk costs.
    /// </summary>
    static List<MtpItem> TakeNewest(IPhoneSource source, int limit, CancellationToken cancellationToken)
    {
        var picked = new List<MtpItem>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            source.Scan(item =>
            {
                picked.Add(item);
                if (picked.Count >= limit) stop.Cancel();
            }, null, stop.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expected: we stopped the walk ourselves once we had enough.
        }

        return picked;
    }

    static async Task<string> SendTextStandaloneAsync(string peer, string text, CancellationToken cancellationToken)
    {
        var match = await ResolveStandaloneAsync(peer, cancellationToken);
        if (match.Peer is null) return match.Problem!;

        // LocalSend carries a message as a small text file, which is also how a script is triggered.
        var temp = Path.Combine(Path.GetTempPath(), $"dropzone-message-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(temp, text, cancellationToken);

        try
        {
            using var sender = new LocalSendSender(Self());
            await sender.SendAsync(match.Peer, [temp], null, cancellationToken);
        }
        catch (SendRejectedException ex)
        {
            return $"{match.Peer.Alias} refused the message: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {match.Peer.Alias} at {match.Peer.BaseUrl}: {ex.Message}";
        }
        finally
        {
            File.Delete(temp);
        }

        return $"Sent to {match.Peer.Alias}: {text}";
    }

    static async Task<(Peer? Peer, string? Problem)> ResolveStandaloneAsync(
        string key, CancellationToken cancellationToken)
    {
        var (peers, bindFailure) = await FindPeersAsync(3, cancellationToken);

        var match = peers.FirstOrDefault(candidate =>
            candidate.Alias.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            candidate.Fingerprint.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (match is not null) return (match, null);

        return (null, peers.Count == 0
            ? "No peers answered, so there is nobody to send to." +
              (bindFailure is null ? "" : $" The discovery port is taken by another app: {bindFailure}")
            : $"No peer matched \"{key}\". Visible: {string.Join(", ", peers.Select(p => p.Alias))}");
    }

    static async Task<(IReadOnlyList<Peer> Peers, string? BindFailure)> FindPeersAsync(
        int seconds, CancellationToken cancellationToken)
    {
        using var discovery = new PeerDiscovery(Self());
        discovery.Start();
        await discovery.AnnounceAsync();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), cancellationToken);

        return (discovery.Peers.ToList(), discovery.BindFailure);
    }

    static string Describe(IReadOnlyList<Peer> peers) =>
        $"{peers.Count} peer(s):\n" + string.Join("\n", peers.Select(peer =>
            $"{peer.Alias,-20} {peer.Address}:{peer.Port}  {peer.Protocol}  {peer.Fingerprint[..12]}"));

    static DeviceInfo Self() => new()
    {
        Alias = Config.Alias,
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = Fingerprint,
        Port = LocalSendProtocol.DefaultPort,
        Protocol = "https",
        Download = false
    };

    static ScriptRegistry Scripts() =>
        new(Config.ScriptsFolder, Config.ScriptConfigPath, Config.Interpreters);

    /// <summary>The same settings file the tray app reads, so both agree on folders and alias.</summary>
    static readonly Settings Config = Settings.Load();

    // A peer identifies us by this fingerprint, so mint one identity per process rather than
    // a new one on every call.
    static readonly string Fingerprint = CreateFingerprint();

    static string CreateFingerprint()
    {
        using var certificate = SelfSignedCertificate.Create("dropzone");
        return SelfSignedCertificate.FingerprintOf(certificate);
    }
}
