using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DropZone.Core;
using DropZone.LocalSend;
using DropZone.Mtp;

namespace DropZone.App;

public sealed record ScriptInvocation(string ScriptName, string Sender, bool Allowed, string Detail);

/// <summary>
/// Owns the long-lived pieces — certificate, receiver, discovery, phone source, history,
/// scripts — and gives the UI one surface to talk to.
/// </summary>
public sealed class TransferService : IAsyncDisposable
{
    readonly X509Certificate2 _certificate;
    readonly DeviceInfo _self;
    readonly IPhoneSource _phone = new MediaDevicesPhoneSource();

    LocalSendReceiver? _receiver;
    PeerDiscovery? _discovery;

    /// <summary>Questions sent to a peer, keyed by its address, waiting for the reply to come back.</summary>
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _awaitingReply = new();

    public Settings Settings { get; }
    public TransferHistory History { get; }
    public ScriptRegistry Scripts { get; }

    public bool IsReceiving => _receiver is not null;
    public IReadOnlyList<Peer> Peers => _discovery?.Peers.ToList() ?? [];

    public event Action? PeersChanged;
    public event Action? HistoryChanged;
    public event Action<ScriptInvocation>? ScriptInvoked;

    public TransferService(Settings settings)
    {
        Settings = settings;
        settings.EnsureFolders();
        ExampleScripts.SeedIfEmpty(settings.ScriptsFolder);

        // Write settings back on every start so the file always shows the current defaults —
        // otherwise the interpreter map is invisible until someone edits one in the UI.
        settings.Save();

        History = new TransferHistory(settings.HistoryPath);
        Scripts = new ScriptRegistry(settings.ScriptsFolder, settings.ScriptConfigPath, settings.Interpreters);

        _certificate = SelfSignedCertificate.Create("dropzone");

        _self = new DeviceInfo
        {
            Alias = settings.Alias,
            DeviceModel = "Windows",
            DeviceType = "desktop",
            Fingerprint = SelfSignedCertificate.FingerprintOf(_certificate),
            Port = LocalSendProtocol.DefaultPort,
            Protocol = "https",
            Download = false
        };
    }

    /// <summary>Set when something started but degraded — shown in the UI rather than thrown.</summary>
    public string? StartupWarning { get; private set; }

    /// <summary>Interfaces discovery actually joined on — empty means nobody can find us.</summary>
    public IReadOnlyList<System.Net.IPAddress> ListeningOn => _discovery?.JoinedInterfaces ?? [];

    public async Task StartAsync()
    {
        _discovery = new PeerDiscovery(_self);
        _discovery.PeerFound += _ => PeersChanged?.Invoke();
        _discovery.Start();

        if (!_discovery.CanListen)
            StartupWarning = "Another app owns the discovery port — devices will not appear automatically.";

        if (Settings.ReceiveOnStart)
        {
            try
            {
                await SetReceivingAsync(true);
            }
            catch (InvalidOperationException ex)
            {
                StartupWarning = ex.Message;
            }
        }

        try
        {
            await _discovery.AnnounceAsync();
        }
        catch (SocketException)
        {
            // Announcing is best effort; a blocked multicast must not stop startup.
        }
    }

    public async Task SetReceivingAsync(bool on)
    {
        if (on == IsReceiving) return;

        if (on)
        {
            var receiver = new LocalSendReceiver(_self, _certificate)
            {
                DownloadFolder = () => Settings.ReceivedFolder
            };

            receiver.TransferCompleted += OnTransferCompleted;
            receiver.TextReceived += OnTextReceived;

            try
            {
                await receiver.StartAsync(LocalSendProtocol.DefaultPort);
            }
            catch (IOException ex)
            {
                // Almost always "address already in use" — another DropZone or the real
                // LocalSend app owns the port. Stay alive with receiving off.
                await receiver.DisposeAsync();
                throw new InvalidOperationException(
                    $"Port {LocalSendProtocol.DefaultPort} is already in use — is LocalSend or another " +
                    $"DropZone running? ({ex.InnerException?.Message ?? ex.Message})");
            }

            _receiver = receiver;

            if (_discovery is not null)
                await _discovery.AnnounceAsync();
        }
        else
        {
            var receiver = _receiver;
            _receiver = null;
            if (receiver is not null) await receiver.DisposeAsync();
        }
    }

    void OnTransferCompleted(CompletedTransfer transfer)
    {
        History.Add(new TransferEntry
        {
            Direction = TransferDirection.Received,
            PeerAlias = transfer.Sender.Alias,
            PeerKind = PeerKindMapper.From(transfer.Sender.DeviceType),
            When = DateTime.Now,
            Folder = transfer.Folder,
            Files = transfer.Files
                .Select(f => new TransferFile(f.File.FileName, f.SavedPath, f.File.Size))
                .ToList()
        });

        HistoryChanged?.Invoke();
    }

    void OnTextReceived(ReceivedText message)
    {
        // An answer to something we asked that device, not an instruction for us.
        if (_awaitingReply.TryRemove(message.RemoteAddress, out var waiter))
        {
            waiter.TrySetResult(message.Text);
            return;
        }

        var decision = ScriptGate.Evaluate(message.Text, Settings.AllowRemoteScripts, Scripts);

        if (decision.Outcome == ScriptGateOutcome.NotACommand) return;

        if (decision.Outcome == ScriptGateOutcome.HelpRequested)
        {
            _ = ReplyAsync(message, ScriptGate.BuildHelpReply(Settings.AllowRemoteScripts, Scripts));
            ScriptInvoked?.Invoke(new ScriptInvocation("help", message.Sender.Alias, true, "Sent command list"));
            return;
        }

        if (!decision.IsAllowed)
        {
            ScriptInvoked?.Invoke(new ScriptInvocation(
                message.Text, message.Sender.Alias, false, decision.Detail));
            _ = ReplyAsync(message, $"DropZone: {decision.Detail}. Send \"help\" to see what is available.");
            return;
        }

        var script = decision.Script!;
        try
        {
            Scripts.Run(script, decision.Arguments);
            ScriptInvoked?.Invoke(new ScriptInvocation(script.DisplayName, message.Sender.Alias, true, "Started"));
            _ = ReplyAsync(message, $"DropZone: started {script.HowToCall}.");
        }
        catch (Exception ex)
        {
            ScriptInvoked?.Invoke(new ScriptInvocation(script.DisplayName, message.Sender.Alias, false, ex.Message));
            _ = ReplyAsync(message, $"DropZone: {script.HowToCall} failed - {ex.Message}");
        }
    }

    /// <summary>Answers the device that sent a command, using the address the request arrived on.</summary>
    async Task ReplyAsync(ReceivedText message, string text)
    {
        try
        {
            if (!System.Net.IPAddress.TryParse(message.RemoteAddress, out var address)) return;

            var peer = new Peer(
                message.Sender.Alias,
                message.Sender.Fingerprint,
                address,
                message.Sender.Port ?? LocalSendProtocol.DefaultPort,
                message.Sender.Protocol ?? "https");

            await SendTextAsync(peer, text, recordInHistory: false);
        }
        catch (Exception)
        {
            // A reply is a courtesy; failing to deliver it must not affect the command itself.
        }
    }

    public async Task ScanAsync()
    {
        if (_discovery is not null)
            await _discovery.AnnounceAsync();

        PeersChanged?.Invoke();
    }

    public async Task SendAsync(Peer peer, IReadOnlyList<string> files, IProgress<SendProgress>? progress)
    {
        using var sender = new LocalSendSender(_self);
        await sender.SendAsync(peer, files, progress);

        History.Add(new TransferEntry
        {
            Direction = TransferDirection.Sent,
            PeerAlias = peer.Alias,
            PeerKind = PeerKind.Unknown,
            When = DateTime.Now,
            Folder = Path.GetDirectoryName(files[0]),
            Files = files.Select(p => new TransferFile(Path.GetFileName(p), p, new FileInfo(p).Length)).ToList()
        });

        HistoryChanged?.Invoke();
    }

    /// <summary>Sends a plain text message — this is how a script is triggered on another device.</summary>
    public async Task SendTextAsync(Peer peer, string text, bool recordInHistory = true)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dropzone-message-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(temp, text);

        try
        {
            using var sender = new LocalSendSender(_self);
            await sender.SendAsync(peer, [temp]);

            if (recordInHistory)
            {
                History.Add(new TransferEntry
                {
                    Direction = TransferDirection.Sent,
                    PeerAlias = peer.Alias,
                    PeerKind = PeerKind.Unknown,
                    When = DateTime.Now,
                    Files = [new TransferFile(Shorten(text), temp, text.Length)]
                });

                HistoryChanged?.Invoke();
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// Asks a peer something and waits for its answer. The answer comes back as an ordinary text
    /// message on our receiver, which is why this only works while receiving is on — and why it
    /// has to happen here rather than in a second process that does not own the port.
    /// </summary>
    public async Task<string?> AskAsync(Peer peer, string text, TimeSpan timeout)
    {
        if (!IsReceiving)
            throw new InvalidOperationException(
                "Receiving is off, so the answer would never arrive. Turn receiving on first.");

        var address = peer.Address.ToString();
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingReply[address] = waiter;

        try
        {
            await SendTextAsync(peer, text, recordInHistory: false);

            return await Task.WhenAny(waiter.Task, Task.Delay(timeout)) == waiter.Task
                ? await waiter.Task
                : null;
        }
        finally
        {
            _awaitingReply.TryRemove(address, out _);
        }
    }

    static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "...";

    public PhoneStatus PhoneStatus() => _phone.Status();

    /// <summary>
    /// Streams results so the list fills as the phone is walked. A full library is minutes of
    /// MTP round-trips; handing back one list at the end looks like a hang.
    /// </summary>
    public Task ScanPhoneAsync(
        Action<MtpItem> onItem, IProgress<ScanProgress>? progress, CancellationToken token) =>
        Task.Run(() => _phone.Scan(onItem, progress, token), token);

    public Task<ImportResult> ImportPhoneAsync(
        IReadOnlyList<MtpItem> items, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Settings.PhotoFolder);
        var importer = new PhoneImporter(_phone, new FileImportLedger(Settings.LedgerPath));
        return importer.ImportAsync(items, Settings.PhotoFolder, progress, token);
    }

    public async ValueTask DisposeAsync()
    {
        _discovery?.Dispose();
        if (_receiver is not null) await _receiver.DisposeAsync();
        _certificate.Dispose();
    }
}
