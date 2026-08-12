using System.IO;
using System.Security.Cryptography.X509Certificates;
using Dropzone.LocalSend;
using Dropzone.Mtp;

namespace Dropzone.App;

/// <summary>
/// Owns the long-lived pieces — certificate, receiver, discovery, phone source — and gives
/// the UI one surface to talk to.
/// </summary>
public sealed class TransferService : IAsyncDisposable
{
    readonly X509Certificate2 _certificate;
    readonly DeviceInfo _self;
    readonly IPhoneSource _phone = new MediaDevicesPhoneSource();

    LocalSendReceiver? _receiver;
    PeerDiscovery? _discovery;

    public Settings Settings { get; }
    public bool IsReceiving => _receiver is not null;

    public IReadOnlyList<Peer> Peers => _discovery?.Peers.ToList() ?? [];

    public event Action? PeersChanged;
    public event Action<ReceivedFile>? FileReceived;

    public TransferService(Settings settings)
    {
        Settings = settings;
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

    public async Task StartAsync()
    {
        _discovery = new PeerDiscovery(_self);
        _discovery.PeerFound += _ => PeersChanged?.Invoke();
        _discovery.Start();

        if (Settings.ReceiveOnStart)
            await SetReceivingAsync(true);

        await _discovery.AnnounceAsync();
    }

    public async Task SetReceivingAsync(bool on)
    {
        if (on == IsReceiving) return;

        if (on)
        {
            var receiver = new LocalSendReceiver(_self, _certificate)
            {
                DownloadFolder = () => Settings.DownloadFolder
            };
            receiver.FileReceived += f => FileReceived?.Invoke(f);

            await receiver.StartAsync(LocalSendProtocol.DefaultPort);
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
    }

    public PhoneStatus PhoneStatus() => _phone.Status();

    public Task<ImportResult> ImportPhoneAsync(IProgress<ImportProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Settings.PhotoFolder);
        var importer = new PhoneImporter(_phone, new FileImportLedger(Settings.LedgerPath));
        return importer.ImportAsync(Settings.PhotoFolder, progress, token);
    }

    public async ValueTask DisposeAsync()
    {
        _discovery?.Dispose();
        if (_receiver is not null) await _receiver.DisposeAsync();
        _certificate.Dispose();
    }
}
