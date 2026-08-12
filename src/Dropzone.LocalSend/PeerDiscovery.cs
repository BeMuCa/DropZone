using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dropzone.LocalSend;

public sealed record Peer(string Alias, string Fingerprint, IPAddress Address, int Port, string Protocol)
{
    public string BaseUrl => $"{Protocol}://{Address}:{Port}";
}

/// <summary>
/// UDP multicast announce/listen on 224.0.0.167:53317. Announcing with "announce": true asks
/// peers to reply; a peer that hears us replies with "announce": false so both sides learn
/// about each other without polling.
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    readonly DeviceInfo _self;
    readonly UdpClient _udp;
    readonly IPEndPoint _multicastEndpoint;
    readonly ConcurrentDictionary<string, Peer> _peers = new();
    CancellationTokenSource? _cts;

    public event Action<Peer>? PeerFound;

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToArray();

    public PeerDiscovery(DeviceInfo self)
    {
        _self = self;
        var group = IPAddress.Parse(LocalSendProtocol.MulticastAddress);
        _multicastEndpoint = new IPEndPoint(group, LocalSendProtocol.DefaultPort);

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, LocalSendProtocol.DefaultPort));
            _udp.JoinMulticastGroup(group);
            _udp.MulticastLoopback = false;
        }
        catch (SocketException ex)
        {
            // Another LocalSend-speaking app already owns the discovery port. Sending still works;
            // we simply will not hear announcements, so the app stays usable instead of dying.
            _udp.Dispose();
            _udp = new UdpClient(AddressFamily.InterNetwork);
            BindFailure = ex.Message;
        }
    }

    /// <summary>Non-null when the discovery port could not be bound — listening is disabled.</summary>
    public string? BindFailure { get; }

    public bool CanListen => BindFailure is null;

    public void Start()
    {
        if (!CanListen) return;

        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    /// <summary>Broadcasts our presence and asks peers to announce themselves back.</summary>
    public Task AnnounceAsync() => SendAsync(announce: true);

    async Task SendAsync(bool announce)
    {
        var payload = new DeviceInfo
        {
            Alias = _self.Alias,
            Version = _self.Version,
            DeviceModel = _self.DeviceModel,
            DeviceType = _self.DeviceType,
            Fingerprint = _self.Fingerprint,
            Port = _self.Port,
            Protocol = _self.Protocol,
            Download = _self.Download,
            Announce = announce
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LocalSendJson.Options);
        await _udp.SendAsync(bytes, bytes.Length, _multicastEndpoint);
    }

    async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            DeviceInfo? info;
            try
            {
                info = JsonSerializer.Deserialize<DeviceInfo>(
                    Encoding.UTF8.GetString(result.Buffer), LocalSendJson.Options);
            }
            catch (JsonException) { continue; }

            if (info is null || string.IsNullOrEmpty(info.Fingerprint)) continue;
            if (info.Fingerprint == _self.Fingerprint) continue; // ourselves

            var peer = new Peer(
                info.Alias,
                info.Fingerprint,
                result.RemoteEndPoint.Address,
                info.Port ?? LocalSendProtocol.DefaultPort,
                info.Protocol ?? "http");

            if (_peers.TryAdd(peer.Fingerprint, peer))
                PeerFound?.Invoke(peer);

            // Someone asked who is here — reply directly so they learn about us.
            if (info.Announce == true)
                await SendAsync(announce: false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp.Dispose();
    }
}
