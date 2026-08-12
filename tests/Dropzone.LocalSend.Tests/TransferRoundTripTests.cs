using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dropzone.LocalSend;

namespace Dropzone.LocalSend.Tests;

/// <summary>
/// Drives the real Kestrel receiver over loopback with the real sender, so the wire format,
/// session handshake and token checks are all exercised end to end.
/// </summary>
public sealed class TransferRoundTripTests : IAsyncLifetime
{
    const int Port = 53318;

    LocalSendReceiver _receiver = null!;
    string _downloadFolder = null!;
    DeviceInfo _receiverInfo = null!;

    public async Task InitializeAsync()
    {
        var certificate = SelfSignedCertificate.Create("dropzone-test");

        _receiverInfo = new DeviceInfo
        {
            Alias = "test-receiver",
            DeviceModel = "Windows",
            DeviceType = "desktop",
            Fingerprint = SelfSignedCertificate.FingerprintOf(certificate),
            Port = Port,
            Protocol = "https",
            Download = false
        };

        _downloadFolder = Directory.CreateTempSubdirectory("dropzone-test-").FullName;

        _receiver = new LocalSendReceiver(_receiverInfo, certificate) { DownloadFolder = () => _downloadFolder };
        await _receiver.StartAsync(Port);
    }

    public async Task DisposeAsync()
    {
        await _receiver.DisposeAsync();
        try { Directory.Delete(_downloadFolder, recursive: true); } catch (IOException) { }
    }

    Peer ReceiverPeer => new("test-receiver", _receiverInfo.Fingerprint, IPAddress.Loopback, Port, "https");

    static DeviceInfo SenderInfo() => new()
    {
        Alias = "test-sender",
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = "sender-fingerprint",
        Port = 53319,
        Protocol = "https"
    };

    static string WriteTempFile(byte[] payload, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, payload);
        return path;
    }

    [Fact]
    public async Task Transfers_a_file_byte_for_byte()
    {
        var payload = RandomNumberGenerator.GetBytes(256 * 1024);
        var source = WriteTempFile(payload, $"dropzone-{Guid.NewGuid():N}.bin");

        var arrived = new TaskCompletionSource<ReceivedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.FileReceived += f => arrived.TrySetResult(f);

        using var sender = new LocalSendSender(SenderInfo());
        await sender.SendAsync(ReceiverPeer, [source]);

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(payload, await File.ReadAllBytesAsync(received.SavedPath));
        Assert.Equal(Path.GetFileName(source), Path.GetFileName(received.SavedPath));
        File.Delete(source);
    }

    [Fact]
    public async Task Transfers_several_files_and_reports_progress()
    {
        var sources = Enumerable.Range(0, 3)
            .Select(i => WriteTempFile(RandomNumberGenerator.GetBytes(8 * 1024), $"dropzone-multi-{i}-{Guid.NewGuid():N}.bin"))
            .ToList();

        var count = 0;
        _receiver.FileReceived += _ => Interlocked.Increment(ref count);

        var reports = new List<SendProgress>();
        using var sender = new LocalSendSender(SenderInfo());
        await sender.SendAsync(ReceiverPeer, sources, new Progress<SendProgress>(p => { lock (reports) reports.Add(p); }));

        // Progress is reported asynchronously; give the last callbacks a moment to land.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref count) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(3, Volatile.Read(ref count));
        Assert.Equal(3, Directory.GetFiles(_downloadFolder, "*", SearchOption.AllDirectories).Length);

        foreach (var s in sources) File.Delete(s);
    }

    [Fact]
    public async Task Rejected_transfer_surfaces_as_an_exception()
    {
        _receiver.ApproveTransfer = _ => false;
        var source = WriteTempFile([1, 2, 3], $"dropzone-rejected-{Guid.NewGuid():N}.bin");

        using var sender = new LocalSendSender(SenderInfo());

        var ex = await Assert.ThrowsAsync<SendRejectedException>(
            () => sender.SendAsync(ReceiverPeer, [source]));

        Assert.Contains("declined", ex.Message);
        _receiver.ApproveTransfer = _ => true;
        File.Delete(source);
    }

    [Fact]
    public async Task Remote_filename_cannot_escape_the_download_folder()
    {
        // The sender uses the local file name, so drive the endpoint directly with a hostile name.
        var certificate = SelfSignedCertificate.Create();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler);

        var prepare = new PrepareUploadRequest
        {
            Info = SenderInfo(),
            Files = new Dictionary<string, FileDto>
            {
                ["0"] = new() { Id = "0", FileName = @"..\..\escaped.txt", Size = 3, FileType = "text/plain" }
            }
        };

        var prepared = await http.PostAsJsonAsync(
            $"https://127.0.0.1:{Port}{LocalSendProtocol.PrepareUploadPath}", prepare, LocalSendJson.Options);
        prepared.EnsureSuccessStatusCode();

        var session = await prepared.Content.ReadFromJsonAsync<PrepareUploadResponse>(LocalSendJson.Options);

        var url = $"https://127.0.0.1:{Port}{LocalSendProtocol.UploadPath}" +
                  $"?sessionId={session!.SessionId}&fileId=0&token={session.Files["0"]}";

        var upload = await http.PostAsync(url, new ByteArrayContent([1, 2, 3]));
        upload.EnsureSuccessStatusCode();

        var landed = Directory.GetFiles(_downloadFolder, "*", SearchOption.AllDirectories);
        Assert.Single(landed);
        Assert.Equal("escaped.txt", Path.GetFileName(landed[0]));
        Assert.StartsWith(_downloadFolder, Path.GetFullPath(landed[0]));
    }

    [Fact]
    public async Task Completed_transfer_fires_once_with_every_file_and_the_sender()
    {
        var sources = Enumerable.Range(0, 3)
            .Select(i => WriteTempFile(RandomNumberGenerator.GetBytes(1024), $"dropzone-grp-{i}-{Guid.NewGuid():N}.bin"))
            .ToList();

        var completed = new TaskCompletionSource<CompletedTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fireCount = 0;
        _receiver.TransferCompleted += t => { Interlocked.Increment(ref fireCount); completed.TrySetResult(t); };

        using var sender = new LocalSendSender(SenderInfo());
        await sender.SendAsync(ReceiverPeer, sources);

        var transfer = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, transfer.Files.Count);
        Assert.Equal("test-sender", transfer.Sender.Alias);
        Assert.Equal("desktop", transfer.Sender.DeviceType);
        Assert.True(Directory.Exists(transfer.Folder));

        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref fireCount));

        foreach (var s in sources) File.Delete(s);
    }

    [Fact]
    public async Task A_text_file_surfaces_as_a_text_message()
    {
        var source = WriteTempFile("run backup"u8.ToArray(), $"dropzone-msg-{Guid.NewGuid():N}.txt");

        var text = new TaskCompletionSource<ReceivedText>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.TextReceived += t => text.TrySetResult(t);

        using var sender = new LocalSendSender(SenderInfo());
        await sender.SendAsync(ReceiverPeer, [source]);

        var message = await text.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("run backup", message.Text);
        Assert.Equal("test-sender", message.Sender.Alias);
        File.Delete(source);
    }

    [Fact]
    public async Task Each_transfer_lands_in_its_own_folder()
    {
        var a = WriteTempFile([1, 2, 3], $"dropzone-f1-{Guid.NewGuid():N}.bin");
        var b = WriteTempFile([4, 5, 6], $"dropzone-f2-{Guid.NewGuid():N}.bin");

        using var sender = new LocalSendSender(SenderInfo());
        await sender.SendAsync(ReceiverPeer, [a]);
        await Task.Delay(1100); // folder name is second-resolution
        await sender.SendAsync(ReceiverPeer, [b]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.GetDirectories(_downloadFolder).Length < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(2, Directory.GetDirectories(_downloadFolder).Length);
        File.Delete(a);
        File.Delete(b);
    }

    [Fact]
    public async Task Upload_with_a_bad_token_is_refused()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler);

        var url = $"https://127.0.0.1:{Port}{LocalSendProtocol.UploadPath}?sessionId=x&fileId=0&token=wrong";
        var response = await http.PostAsync(url, new ByteArrayContent([1]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_returns_our_device_info()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler);

        var response = await http.PostAsync($"https://127.0.0.1:{Port}{LocalSendProtocol.RegisterPath}", null);
        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<DeviceInfo>(LocalSendJson.Options);

        Assert.Equal("test-receiver", info!.Alias);
        Assert.Equal(_receiverInfo.Fingerprint, info.Fingerprint);
        Assert.Equal("2.0", info.Version);
    }
}
