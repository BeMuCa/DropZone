using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dropzone.App.Model;
using Dropzone.LocalSend;
using Dropzone.Mtp;

namespace Dropzone.App;

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

        History = new TransferHistory(settings.HistoryPath);
        Scripts = new ScriptRegistry(settings.ScriptsFolder, settings.ScriptConfigPath);

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
                // Almost always "address already in use" — another Dropzone or the real
                // LocalSend app owns the port. Stay alive with receiving off.
                await receiver.DisposeAsync();
                throw new InvalidOperationException(
                    $"Port {LocalSendProtocol.DefaultPort} is already in use — is LocalSend or another " +
                    $"Dropzone running? ({ex.InnerException?.Message ?? ex.Message})");
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
        var decision = ScriptGate.Evaluate(message.Text, Settings.AllowRemoteScripts, Scripts);

        if (decision.Outcome == ScriptGateOutcome.NotACommand) return;

        if (!decision.IsAllowed)
        {
            ScriptInvoked?.Invoke(new ScriptInvocation(
                message.Text, message.Sender.Alias, false, decision.Detail));
            return;
        }

        var script = decision.Script!;
        try
        {
            ScriptRegistry.Run(script, decision.Arguments);
            ScriptInvoked?.Invoke(new ScriptInvocation(script.DisplayName, message.Sender.Alias, true, "Started"));
        }
        catch (Exception ex)
        {
            ScriptInvoked?.Invoke(new ScriptInvocation(script.DisplayName, message.Sender.Alias, false, ex.Message));
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
    public async Task SendTextAsync(Peer peer, string text)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dropzone-message-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(temp, text);

        try
        {
            using var sender = new LocalSendSender(_self);
            await sender.SendAsync(peer, [temp]);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public PhoneStatus PhoneStatus() => _phone.Status();

    public Task<IReadOnlyList<MtpItem>> ScanPhoneAsync(IProgress<string>? progress, CancellationToken token) =>
        Task.Run(() => _phone.EnumerateMedia(progress, token), token);

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
