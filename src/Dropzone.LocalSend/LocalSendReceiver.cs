using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dropzone.LocalSend;

public sealed record ReceivedFile(FileDto File, string SavedPath);

/// <summary>
/// The receiving half of the protocol: a Kestrel host exposing the four v2 endpoints.
/// </summary>
public sealed class LocalSendReceiver : IAsyncDisposable
{
    readonly DeviceInfo _self;
    readonly X509Certificate2 _certificate;
    readonly SessionStore _sessions = new();
    WebApplication? _app;

    /// <summary>Where incoming files are written. Read on each upload so the user can change it live.</summary>
    public Func<string> DownloadFolder { get; set; } = () =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Return false to reject an incoming transfer (403). Defaults to accepting.</summary>
    public Func<PrepareUploadRequest, bool> ApproveTransfer { get; set; } = _ => true;

    public event Action<ReceivedFile>? FileReceived;
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
            ApplicationName = "Dropzone",
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
                var session = _sessions.Create(request.Files, RemoteAddress(ctx));
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

            var folder = DownloadFolder();
            Directory.CreateDirectory(folder);

            var target = SafeFileName.Deduplicate(SafeFileName.ResolveInside(folder, file!.FileName));

            await using (var output = File.Create(target))
                await ctx.Request.Body.CopyToAsync(output);

            FileReceived?.Invoke(new ReceivedFile(file, target));
            return Results.Ok();
        });

        app.MapPost(LocalSendProtocol.CancelPath, (HttpContext ctx) =>
        {
            _sessions.Cancel(ctx.Request.Query["sessionId"].ToString());
            return Results.Ok();
        });

        _app = app;
        await app.StartAsync();
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
