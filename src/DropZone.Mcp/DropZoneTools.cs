using System.ComponentModel;
using System.Text.Json;
using DropZone.LocalSend;
using DropZone.Mtp;
using ModelContextProtocol.Server;

namespace DropZone.Mcp;

/// <summary>
/// DropZone's two transfer paths as MCP tools: the cable (iPhone over MTP) and the wireless
/// LocalSend path. Every call is self-contained, so the tray app does not have to be running,
/// and nothing here holds the phone or the discovery socket open after it returns.
/// </summary>
[McpServerToolType]
public static class DropZoneTools
{
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
        var target = destination ?? Path.Combine(Config.Root, "iPhone");

        // Share the tray app's ledger so an import here is not repeated there, and vice versa.
        var importer = new PhoneImporter(source, new FileImportLedger(Path.Combine(Config.Root, "imported.txt")));

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
    [Description("Announce this PC on the local network and list the DropZone or LocalSend devices that answer. A phone only answers while its LocalSend app is open.")]
    public static async Task<string> DiscoverPeers(
        [Description("How long to listen, in seconds. Default 3.")] int seconds = 3,
        CancellationToken cancellationToken = default)
    {
        var (peers, bindFailure) = await FindPeersAsync(seconds, cancellationToken);

        if (peers.Count == 0)
            return "No peers answered." +
                   (bindFailure is null ? "" : $" The discovery port is taken by another app: {bindFailure}");

        var lines = peers.Select(peer =>
            $"{peer.Alias,-20} {peer.Address}:{peer.Port}  {peer.Protocol}  {peer.Fingerprint[..12]}");

        return $"{peers.Count} peer(s):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "send_files")]
    [Description("Send files from this PC to a peer listed by discover_peers, matched on its alias or fingerprint.")]
    public static async Task<string> SendFiles(
        [Description("Alias or fingerprint of the receiving device, as shown by discover_peers.")] string peer,
        [Description("Full paths of the files to send.")] string[] paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Length == 0) return "No files given.";

        var missing = paths.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0) return $"Not found: {string.Join(", ", missing)}";

        var (peers, bindFailure) = await FindPeersAsync(3, cancellationToken);

        var match = peers.FirstOrDefault(candidate =>
            candidate.Alias.Equals(peer, StringComparison.OrdinalIgnoreCase) ||
            candidate.Fingerprint.StartsWith(peer, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return peers.Count == 0
                ? "No peers answered, so there is nobody to send to." +
                  (bindFailure is null ? "" : $" The discovery port is taken by another app: {bindFailure}")
                : $"No peer matched \"{peer}\". Visible: {string.Join(", ", peers.Select(p => p.Alias))}";

        using var sender = new LocalSendSender(Self());

        try
        {
            await sender.SendAsync(match, paths, null, cancellationToken);
        }
        catch (SendRejectedException ex)
        {
            return $"{match.Alias} refused the transfer: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {match.Alias} at {match.BaseUrl}: {ex.Message}";
        }

        return $"Sent {paths.Length} file(s) to {match.Alias} ({match.Address}).";
    }

    [McpServerTool(Name = "transfer_history")]
    [Description("The most recent transfers recorded by the DropZone tray app, newest first, as raw history entries.")]
    public static string TransferHistory(
        [Description("How many transfers to return. Default 20.")] int limit = 20)
    {
        var path = Path.Combine(Config.Root, "history.json");
        if (!File.Exists(path)) return "No history yet.";

        List<string> entries;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

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

    static async Task<(IReadOnlyList<Peer> Peers, string? BindFailure)> FindPeersAsync(
        int seconds, CancellationToken cancellationToken)
    {
        using var discovery = new PeerDiscovery(Self());
        discovery.Start();
        await discovery.AnnounceAsync();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), cancellationToken);

        return (discovery.Peers.ToList(), discovery.BindFailure);
    }

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

    // A peer identifies us by this fingerprint, so mint one identity per process rather than
    // a new one on every call.
    static readonly string Fingerprint = CreateFingerprint();

    static readonly (string Root, string Alias) Config = ReadSettings();

    static string CreateFingerprint()
    {
        using var certificate = SelfSignedCertificate.Create("dropzone");
        return SelfSignedCertificate.FingerprintOf(certificate);
    }

    /// <summary>
    /// The tray app's settings file decides where DropZone keeps its files. Only the folder and
    /// the alias matter here, so they are read straight out of the JSON rather than shared.
    /// </summary>
    static (string Root, string Alias) ReadSettings()
    {
        var fallback = (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DropZone"),
            Environment.MachineName);

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DropZone", "settings.json");

        try
        {
            if (!File.Exists(path)) return fallback;

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            var root = document.RootElement.TryGetProperty("RootFolder", out var folder) ? folder.GetString() : null;
            var alias = document.RootElement.TryGetProperty("Alias", out var name) ? name.GetString() : null;

            return (root ?? fallback.Item1, alias ?? fallback.Item2);
        }
        catch (Exception)
        {
            // A missing or damaged settings file just means the defaults are right.
            return fallback;
        }
    }
}
