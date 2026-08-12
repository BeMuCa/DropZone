using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dropzone.App.Model;

public enum TransferDirection { Sent, Received }

public enum PeerKind { Unknown, Desktop, Mobile }

public sealed record TransferFile(string FileName, string Path, long Size);

/// <summary>One transfer — all files that moved together in a single session stay grouped here.</summary>
public sealed record TransferEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TransferDirection Direction { get; init; }
    public string PeerAlias { get; init; } = "Unknown";
    public PeerKind PeerKind { get; init; }
    public DateTime When { get; init; } = DateTime.Now;
    public IReadOnlyList<TransferFile> Files { get; init; } = [];

    /// <summary>Folder to reveal when the entry is clicked. For sends, the first file's folder.</summary>
    public string? Folder { get; init; }

    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);

    [JsonIgnore]
    public string Summary => Files.Count == 1
        ? Files[0].FileName
        : $"{Files.Count} files";
}

public static class PeerKindMapper
{
    public static PeerKind From(string? deviceType) => deviceType?.ToLowerInvariant() switch
    {
        "mobile" => PeerKind.Mobile,
        "desktop" or "server" or "headless" or "web" => PeerKind.Desktop,
        _ => PeerKind.Unknown
    };

    public static string Glyph(PeerKind kind) => kind switch
    {
        PeerKind.Mobile => "\U0001F4F1",
        PeerKind.Desktop => "\U0001F5A5",
        _ => "❓"
    };
}

/// <summary>Append-only log of transfers, newest first, persisted as JSON next to the files.</summary>
public sealed class TransferHistory
{
    readonly string _path;
    readonly List<TransferEntry> _entries;
    readonly Lock _gate = new();

    public TransferHistory(string path)
    {
        _path = path;
        _entries = Read(path);
    }

    static List<TransferEntry> Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<TransferEntry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception)
        {
            // Treat a damaged history as empty rather than failing to start.
        }

        return [];
    }

    public IReadOnlyList<TransferEntry> Entries
    {
        get { lock (_gate) return _entries.OrderByDescending(e => e.When).ToList(); }
    }

    public IReadOnlyList<TransferEntry> By(TransferDirection direction) =>
        Entries.Where(e => e.Direction == direction).ToList();

    public void Add(TransferEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Save();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // History is a convenience; losing a write must not break a transfer.
        }
    }
}
