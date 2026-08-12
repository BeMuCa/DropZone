using System.Security.Cryptography;

namespace Dropzone.LocalSend;

public sealed class SessionBusyException : Exception
{
    public SessionBusyException() : base("Another transfer session is already active.") { }
}

public sealed record ReceiveSession(
    string SessionId,
    string RemoteAddress,
    DeviceInfo Sender,
    IReadOnlyDictionary<string, FileDto> Files,
    IReadOnlyDictionary<string, string> FileTokens);

/// <summary>
/// Tracks the single in-flight receive session. The spec allows only one at a time —
/// a second concurrent prepare-upload is answered with 409.
/// </summary>
public sealed class SessionStore
{
    readonly Lock _gate = new();
    ReceiveSession? _active;
    readonly HashSet<string> _completedFileIds = [];

    public ReceiveSession? Active
    {
        get { lock (_gate) return _active; }
    }

    public ReceiveSession Create(
        IReadOnlyDictionary<string, FileDto> files, string remoteAddress, DeviceInfo? sender = null)
    {
        lock (_gate)
        {
            if (_active is not null)
                throw new SessionBusyException();

            var tokens = files.Keys.ToDictionary(id => id, _ => RandomToken());
            _completedFileIds.Clear();
            _active = new ReceiveSession(
                RandomToken(), remoteAddress, sender ?? new DeviceInfo { Alias = "Unknown" }, files, tokens);
            return _active;
        }
    }

    public bool TryValidate(string sessionId, string fileId, string token, string remoteAddress, out FileDto? file)
    {
        file = null;
        lock (_gate)
        {
            if (_active is null || _active.SessionId != sessionId)
                return false;

            if (!string.Equals(_active.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!_active.FileTokens.TryGetValue(fileId, out var expected))
                return false;

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(expected),
                    System.Text.Encoding.UTF8.GetBytes(token)))
                return false;

            return _active.Files.TryGetValue(fileId, out file);
        }
    }

    /// <summary>Records a finished upload. Returns the session when that was the last file.</summary>
    public ReceiveSession? MarkReceived(string sessionId, string fileId)
    {
        lock (_gate)
        {
            if (_active is null || _active.SessionId != sessionId) return null;

            _completedFileIds.Add(fileId);
            if (_completedFileIds.Count < _active.Files.Count) return null;

            var finished = _active;
            _active = null;
            _completedFileIds.Clear();
            return finished;
        }
    }

    public void Cancel(string sessionId)
    {
        lock (_gate)
        {
            if (_active?.SessionId == sessionId)
            {
                _active = null;
                _completedFileIds.Clear();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _active = null;
            _completedFileIds.Clear();
        }
    }

    static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
