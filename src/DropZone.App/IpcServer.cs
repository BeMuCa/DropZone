using System.IO;
using System.IO.Pipes;
using DropZone.Core;
using DropZone.LocalSend;

namespace DropZone.App;

/// <summary>
/// Lets the MCP server drive this instance instead of standing up a second one. The receiver,
/// the live peer list and any reply a peer sends back all belong to this process — only one
/// process can hold port 53317, so a second copy could never see them.
///
/// One request per connection: a JSON line in, a JSON line out.
/// </summary>
public sealed class IpcServer(TransferService service) : IDisposable
{
    readonly CancellationTokenSource _cts = new();

    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                DropZoneIpc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                // A caller that vanished mid-handshake must not end the loop.
                await pipe.DisposeAsync();
                continue;
            }

            _ = ServeAsync(pipe, token);
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            var line = await reader.ReadLineAsync(token);
            if (line is null) return;

            IpcResponse response;

            try
            {
                var request = DropZoneIpc.Read<IpcRequest>(line);

                response = request is null
                    ? new IpcResponse(false, "Unreadable request.")
                    : await ExecuteAsync(request, token);
            }
            catch (Exception ex)
            {
                // The caller is an agent on the other end of a pipe: it wants the reason as text,
                // not a dead connection.
                response = new IpcResponse(false, ex.Message);
            }

            await writer.WriteLineAsync(DropZoneIpc.Write(response));
        }
        catch (IOException)
        {
            // The caller hung up; there is nobody left to answer.
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    async Task<IpcResponse> ExecuteAsync(IpcRequest request, CancellationToken token)
    {
        switch (request.Command)
        {
            case "status":
                return new IpcResponse(true, $"""
                    app            : running
                    receiving      : {(service.IsReceiving ? "on" : "off")}
                    alias          : {service.Settings.Alias}
                    root folder    : {service.Settings.RootFolder}
                    remote scripts : {(service.Settings.AllowRemoteScripts ? "allowed" : "off (master switch)")}
                    peers visible  : {service.Peers.Count}
                    announcing on  : {(service.ListeningOn.Count == 0 ? "-" : string.Join(", ", service.ListeningOn))}
                    """);

            case "peers":
            {
                await service.ScanAsync();

                // Announcements come back asynchronously; answering instantly would report an
                // emptier network than the UI shows.
                var seconds = int.TryParse(request.Argument("seconds"), out var asked) ? asked : 3;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), token);

                return new IpcResponse(true, Describe(service.Peers));
            }

            case "receive":
            {
                var on = request.Argument("on")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                await service.SetReceivingAsync(on);

                return new IpcResponse(true, $"Receiving is now {(service.IsReceiving ? "on" : "off")}.");
            }

            case "send_files":
            {
                var peer = Resolve(request.Argument("peer"));
                var paths = (request.Argument("paths") ?? "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (paths.Length == 0) return new IpcResponse(false, "No files given.");

                var missing = paths.Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0) return new IpcResponse(false, $"Not found: {string.Join(", ", missing)}");

                await service.SendAsync(peer, paths, null);

                return new IpcResponse(true, $"Sent {paths.Length} file(s) to {peer.Alias} ({peer.Address}).");
            }

            case "send_text":
            {
                var peer = Resolve(request.Argument("peer"));
                var text = request.Argument("text") ?? "";

                if (string.IsNullOrWhiteSpace(text)) return new IpcResponse(false, "Nothing to send.");

                await service.SendTextAsync(peer, text);

                return new IpcResponse(true, $"Sent to {peer.Alias}: {text}");
            }

            case "scripts":
            {
                service.Scripts.Reload();
                var scripts = service.Scripts.All();

                if (scripts.Count == 0)
                    return new IpcResponse(true, $"No scripts in {service.Settings.ScriptsFolder}.");

                var lines = scripts.Select(s =>
                    $"{s.HowToCall,-20} {(s.RemoteEnabled ? "remote-enabled" : "local only  ")}  {s.Name}");

                return new IpcResponse(true,
                    $"{scripts.Count} script(s) in {service.Settings.ScriptsFolder}:\n{string.Join("\n", lines)}\n" +
                    $"master switch: {(service.Settings.AllowRemoteScripts ? "allowed" : "off")}");
            }

            case "run_script":
            {
                var name = request.Argument("name");
                service.Scripts.Reload();

                var script = service.Scripts.All().FirstOrDefault(s =>
                    s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (script is null) return new IpcResponse(false, $"No script called \"{name}\".");

                var arguments = request.Argument("arguments");
                service.Scripts.Run(script, arguments);

                return new IpcResponse(true, $"Started: {service.Scripts.CommandLineFor(script, arguments)}");
            }

            case "remote_scripts":
            {
                var peer = Resolve(request.Argument("peer"));
                var reply = await service.AskAsync(peer, "help", TimeSpan.FromSeconds(10));

                return new IpcResponse(true, reply ??
                    $"{peer.Alias} did not answer within 10s. It answers only if it is running DropZone " +
                    "with remote scripts allowed.");
            }

            case "run_remote_script":
            {
                var peer = Resolve(request.Argument("peer"));
                var name = request.Argument("name");
                var arguments = request.Argument("arguments");

                if (string.IsNullOrWhiteSpace(name)) return new IpcResponse(false, "Which script?");

                var command = string.IsNullOrWhiteSpace(arguments) ? name : $"{name} {arguments}";
                var reply = await service.AskAsync(peer, command, TimeSpan.FromSeconds(10));

                return new IpcResponse(true, reply ??
                    $"Sent \"{command}\" to {peer.Alias}, which did not answer. It runs the script only if " +
                    "that script is ticked for remote start there.");
            }

            default:
                return new IpcResponse(false, $"Unknown command \"{request.Command}\".");
        }
    }

    static string Describe(IReadOnlyList<Peer> peers) =>
        peers.Count == 0
            ? "No peers are visible."
            : $"{peers.Count} peer(s):\n" + string.Join("\n", peers.Select(p =>
                $"{p.Alias,-20} {p.Address}:{p.Port}  {p.Protocol}  {p.Fingerprint[..12]}"));

    Peer Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Which peer?");

        var peers = service.Peers;

        var match = peers.FirstOrDefault(p =>
            p.Alias.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            p.Fingerprint.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(peers.Count == 0
            ? "No peers are visible — open DropZone or LocalSend on the other device."
            : $"No peer matched \"{key}\". Visible: {string.Join(", ", peers.Select(p => p.Alias))}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
