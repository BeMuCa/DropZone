using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dropzone.LocalSend;

public sealed record Peer(string Alias, string Fingerprint, IPAddress Address, int Port, string Protocol)
{
    public string BaseUrl => $"{Protocol}://{Address}:{Port}";
}

/// <summary>
/// UDP multicast announce/listen on 224.0.0.167:53317.
///
/// Both the join and the send are done per interface, on purpose. Letting Windows choose picks by
/// route metric, and a Hyper-V/WSL virtual adapter advertises 10 Gbps against real WiFi's ~780 Mbps
/// — so the default choice announces into the virtual network where no phone can ever hear it.
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    readonly DeviceInfo _self;
    readonly UdpClient _listener;
    readonly IPAddress _group;
    readonly IPEndPoint _multicastEndpoint;
    readonly ConcurrentDictionary<string, Peer> _peers = new();
    CancellationTokenSource? _cts;

    public event Action<Peer>? PeerFound;

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToArray();

    /// <summary>Interfaces we actually joined on — surfaced so the UI can explain a silent network.</summary>
    public IReadOnlyList<IPAddress> JoinedInterfaces { get; private set; } = [];

    /// <summary>Non-null when the discovery port could not be bound — listening is disabled.</summary>
    public string? BindFailure { get; }

    public bool CanListen => BindFailure is null;

    public PeerDiscovery(DeviceInfo self)
    {
        _self = self;
        _group = IPAddress.Parse(LocalSendProtocol.MulticastAddress);
        _multicastEndpoint = new IPEndPoint(_group, LocalSendProtocol.DefaultPort);

        _listener = new UdpClient(AddressFamily.InterNetwork);
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, LocalSendProtocol.DefaultPort));
            JoinedInterfaces = JoinOnEveryInterface();
        }
        catch (SocketException ex)
        {
            // Another LocalSend-speaking app already owns the discovery port. Sending still works;
            // we simply will not hear announcements, so the app stays usable instead of dying.
            _listener.Dispose();
            _listener = new UdpClient(AddressFamily.InterNetwork);
            BindFailure = ex.Message;
        }
    }

    IReadOnlyList<IPAddress> JoinOnEveryInterface()
    {
        var joined = new List<IPAddress>();

        foreach (var address in UsableInterfaceAddresses())
        {
            try
            {
                _listener.JoinMulticastGroup(_group, address);
                joined.Add(address);
            }
            catch (SocketException)
            {
                // Some adapters refuse the join (disconnected, no multicast). Skip and keep going.
            }
        }

        return joined;
    }

    /// <summary>Every up, multicast-capable, non-loopback IPv4 address on this machine.</summary>
    public static IReadOnlyList<IPAddress> UsableInterfaceAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .Distinct()
            .ToList();

    public void Start()
    {
        if (!CanListen) return;

        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    /// <summary>Broadcasts our presence on every interface and asks peers to announce themselves back.</summary>
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

        foreach (var address in UsableInterfaceAddresses())
        {
            try
            {
                // Binding the sender to a specific local address forces the datagram out of that
                // interface instead of whichever one the routing table prefers.
                using var sender = new UdpClient(new IPEndPoint(address, 0));
                sender.Client.SetSocketOption(
                    SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    address.GetAddressBytes());

                await sender.SendAsync(bytes, bytes.Length, _multicastEndpoint);
            }
            catch (SocketException)
            {
                // One dead interface must not stop the others.
            }
        }
    }

    async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _listener.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

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

            // Someone asked who is here — reply so they learn about us.
            if (info.Announce == true)
                await SendAsync(announce: false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Dispose();
    }
}
