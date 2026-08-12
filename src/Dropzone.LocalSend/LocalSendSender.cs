using System.Net.Http.Json;
using System.Text.Json;

namespace Dropzone.LocalSend;

public sealed record SendProgress(string FileName, int FileIndex, int FileCount, long BytesSent, long BytesTotal);

public sealed class SendRejectedException(string reason) : Exception(reason);

/// <summary>
/// The sending half: prepare-upload to negotiate a session, then one POST per file.
/// </summary>
public sealed class LocalSendSender : IDisposable
{
    readonly DeviceInfo _self;
    readonly HttpClient _http;

    public LocalSendSender(DeviceInfo self)
    {
        _self = self;

        // Peers use self-signed certificates by design; the protocol pins trust to the
        // fingerprint instead of a CA chain, so chain validation is not meaningful here.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task SendAsync(
        Peer peer,
        IReadOnlyList<string> filePaths,
        IProgress<SendProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = filePaths.Select((path, i) =>
        {
            var info = new FileInfo(path);
            return new FileDto
            {
                Id = i.ToString(),
                FileName = info.Name,
                Size = info.Length,
                FileType = MimeTypes.For(info.Name),
                Metadata = new FileMetadata { Modified = info.LastWriteTimeUtc }
            };
        }).ToDictionary(f => f.Id);

        var prepare = new PrepareUploadRequest { Info = _self, Files = files };

        var response = await _http.PostAsJsonAsync(
            $"{peer.BaseUrl}{LocalSendProtocol.PrepareUploadPath}", prepare, LocalSendJson.Options, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new SendRejectedException("The other device declined the transfer.");
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new SendRejectedException("The other device is busy with another transfer.");

        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<PrepareUploadResponse>(
            LocalSendJson.Options, cancellationToken);

        if (session is null) throw new SendRejectedException("The other device returned no session.");

        var totalBytes = files.Values.Sum(f => f.Size);
        long sentSoFar = 0;
        var index = 0;

        foreach (var (id, dto) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!session.Files.TryGetValue(id, out var token))
                continue; // receiver chose not to accept this one

            var path = filePaths[int.Parse(id)];
            await using var stream = File.OpenRead(path);

            var url = $"{peer.BaseUrl}{LocalSendProtocol.UploadPath}" +
                      $"?sessionId={Uri.EscapeDataString(session.SessionId)}" +
                      $"&fileId={Uri.EscapeDataString(id)}" +
                      $"&token={Uri.EscapeDataString(token)}";

            using var content = new StreamContent(stream);
            var upload = await _http.PostAsync(url, content, cancellationToken);
            upload.EnsureSuccessStatusCode();

            sentSoFar += dto.Size;
            index++;
            progress?.Report(new SendProgress(dto.FileName, index, files.Count, sentSoFar, totalBytes));
        }
    }

    public async Task CancelAsync(Peer peer, string sessionId)
    {
        try
        {
            await _http.PostAsync(
                $"{peer.BaseUrl}{LocalSendProtocol.CancelPath}?sessionId={Uri.EscapeDataString(sessionId)}", null);
        }
        catch (HttpRequestException)
        {
            // Cancelling is best-effort.
        }
    }

    public void Dispose() => _http.Dispose();
}

public static class MimeTypes
{
    public static string For(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };
}
