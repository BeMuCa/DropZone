using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropZone.LocalSend;

public sealed record ReceivedFile(FileDto File, string SavedPath, string SessionId, DeviceInfo Sender);

public sealed record CompletedTransfer(
    string SessionId, DeviceInfo Sender, IReadOnlyList<ReceivedFile> Files, string Folder);

/// <summary>
/// A text message sent from a peer — LocalSend delivers these as small text/plain files.
/// The address is carried so a reply can be sent without depending on discovery having
/// already seen that device.
/// </summary>
public sealed record ReceivedText(string Text, DeviceInfo Sender, string RemoteAddress);

/// <summary>
/// The receiving half of the protocol: a Kestrel host exposing the four v2 endpoints.
/// </summary>
public sealed class LocalSendReceiver : IAsyncDisposable
{
    const long MaxTextMessageBytes = 8 * 1024;

    readonly DeviceInfo _self;
    readonly X509Certificate2 _certificate;
    readonly SessionStore _sessions = new();
    readonly Lock _bufferGate = new();
    readonly Dictionary<string, List<ReceivedFile>> _bySession = [];
    WebApplication? _app;

    /// <summary>Root for incoming files. Each transfer lands in its own dated subfolder.</summary>
    public Func<string> DownloadFolder { get; set; } = () =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Return false to reject an incoming transfer (403). Defaults to accepting.</summary>
    public Func<PrepareUploadRequest, bool> ApproveTransfer { get; set; } = _ => true;

    public event Action<ReceivedFile>? FileReceived;
    public event Action<CompletedTransfer>? TransferCompleted;
    public event Action<ReceivedText>? TextReceived;
    public event Action<PrepareUploadRequest>? TransferRequested;

    public LocalSendReceiver(DeviceInfo self, X509Certificate2 certificate)
    {
        _self = self;
        _certificate = certificate;
    }

    public async Task StartAsync(int port = LocalSendProtocol.DefaultPort)
    {
        // CreateSlimBuilder with an explicit local content root: the default builder points the
        // content root at the app's base directory and watches it for config changes, which hangs
        // when that directory is a UNC path onto the WSL share.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "DropZone",
            ContentRootPath = Path.GetTempPath()
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(port, o => o.UseHttps(_certificate));
            k.Limits.MaxRequestBodySize = null; // videos are large
        });

        var app = builder.Build();

        app.MapPost(LocalSendProtocol.RegisterPath, () => Results.Json(Response(), LocalSendJson.Options));

        app.MapPost(LocalSendProtocol.PrepareUploadPath, async (HttpContext ctx) =>
        {
            PrepareUploadRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<PrepareUploadRequest>(LocalSendJson.Options);
            }
            catch (Exception)
            {
                return Results.BadRequest();
            }

            if (request is null || request.Files.Count == 0)
                return Results.BadRequest();

            TransferRequested?.Invoke(request);

            if (!ApproveTransfer(request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                var session = _sessions.Create(request.Files, RemoteAddress(ctx), request.Info);
                lock (_bufferGate) _bySession[session.SessionId] = [];

                return Results.Json(
                    new PrepareUploadResponse
                    {
                        SessionId = session.SessionId,
                        Files = session.FileTokens.ToDictionary(p => p.Key, p => p.Value)
                    },
                    LocalSendJson.Options);
            }
            catch (SessionBusyException)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }
        });

        app.MapPost(LocalSendProtocol.UploadPath, async (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Query["sessionId"].ToString();
            var fileId = ctx.Request.Query["fileId"].ToString();
            var token = ctx.Request.Query["token"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(token))
                return Results.BadRequest();

            if (!_sessions.TryValidate(sessionId, fileId, token, RemoteAddress(ctx), out var file))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var sender = _sessions.Active?.Sender ?? new DeviceInfo { Alias = "Unknown" };
            var folder = FolderForSession(sessionId, sender);
            Directory.CreateDirectory(folder);

            var target = SafeFileName.Deduplicate(SafeFileName.ResolveInside(folder, file!.FileName));

            await using (var output = File.Create(target))
                await ctx.Request.Body.CopyToAsync(output);

            var received = new ReceivedFile(file, target, sessionId, sender);
            FileReceived?.Invoke(received);

            if (LooksLikeText(file))
                RaiseTextReceived(target, sender, RemoteAddress(ctx));

            lock (_bufferGate)
            {
                if (_bySession.TryGetValue(sessionId, out var list)) list.Add(received);
            }

            if (_sessions.MarkReceived(sessionId, fileId) is { } finished)
            {
                List<ReceivedFile> files;
                lock (_bufferGate)
                {
                    _bySession.Remove(sessionId, out var buffered);
                    files = buffered ?? [];
                }

                TransferCompleted?.Invoke(new CompletedTransfer(sessionId, finished.Sender, files, folder));
            }

            return Results.Ok();
        });

        app.MapPost(LocalSendProtocol.CancelPath, (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["sessionId"].ToString();
            _sessions.Cancel(id);
            lock (_bufferGate) _bySession.Remove(id);
            return Results.Ok();
        });

        _app = app;
        await app.StartAsync();
    }

    readonly Dictionary<string, string> _sessionFolders = [];

    string FolderForSession(string sessionId, DeviceInfo sender)
    {
        lock (_bufferGate)
        {
            if (_sessionFolders.TryGetValue(sessionId, out var existing)) return existing;

            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            var who = SafeFileName.Of(string.IsNullOrWhiteSpace(sender.Alias) ? "Unknown" : sender.Alias);
            var folder = Path.Combine(DownloadFolder(), $"{stamp} {who}");
            _sessionFolders[sessionId] = folder;
            return folder;
        }
    }

    static bool LooksLikeText(FileDto file) =>
        file.Size <= MaxTextMessageBytes &&
        ((file.FileType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false) ||
         Path.GetExtension(file.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase));

    void RaiseTextReceived(string path, DeviceInfo sender, string remoteAddress)
    {
        try
        {
            TextReceived?.Invoke(new ReceivedText(File.ReadAllText(path).Trim(), sender, remoteAddress));
        }
        catch (IOException)
        {
            // Unreadable text is simply not a command.
        }
    }

    DeviceInfo Response() => new()
    {
        Alias = _self.Alias,
        Version = _self.Version,
        DeviceModel = _self.DeviceModel,
        DeviceType = _self.DeviceType,
        Fingerprint = _self.Fingerprint,
        Download = _self.Download
    };

    static string RemoteAddress(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
