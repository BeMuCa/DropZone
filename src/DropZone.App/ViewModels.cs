using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DropZone.Core;
using DropZone.LocalSend;
using DropZone.Mtp;

namespace DropZone.App;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Human
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B"
    };

    public static string When(DateTime when)
    {
        var age = DateTime.Now - when;
        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
            { TotalDays: < 7 } => $"{(int)age.TotalDays} d ago",
            _ => when.ToString("d MMM yyyy")
        };
    }
}

public sealed class PeerRow(Peer peer)
{
    public Peer Peer { get; } = peer;
    public string Display => $"{Peer.Alias}  ({Peer.Address})";
}

public sealed class HistoryRow(TransferEntry entry)
{
    public TransferEntry Entry { get; } = entry;
    public string Glyph => PeerKindMapper.Glyph(Entry.PeerKind);
    public string Summary => Entry.Summary;
    public string Detail => $"{Entry.PeerAlias} · {Human.When(Entry.When)}";
    public string SizeText => Human.Bytes(Entry.TotalBytes);
}

public sealed class MediaRow(MtpItem item) : Notifier
{
    bool _selected;

    public MtpItem Item { get; } = item;
    public string Name => Item.Name;

    /// <summary>Blank until the file is imported — sizes are not read during a scan.</summary>
    public string SizeText => Item.Size > 0 ? Human.Bytes(Item.Size) : "";

    public string DateText => Item.Modified?.ToString("MMM yyyy") ?? "";

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

public sealed class ScriptRow(ScriptInfo info, ScriptRegistry registry) : Notifier
{
    bool _remoteEnabled = info.RemoteEnabled;
    string _arguments = "";

    public ScriptInfo Info { get; } = info;

    public bool RemoteEnabled
    {
        get => _remoteEnabled;
        set => Set(ref _remoteEnabled, value);
    }

    /// <summary>Parameter typed in the UI and passed on the next manual run.</summary>
    public string Arguments
    {
        get => _arguments;
        set
        {
            Set(ref _arguments, value);
            Raise(nameof(CallHint));
            Raise(nameof(CommandLine));
        }
    }

    /// <summary>Exactly what to send from the phone, so nobody has to open the file to find out.</summary>
    public string CallHint => string.IsNullOrWhiteSpace(_arguments)
        ? Info.HowToCall
        : $"{Info.HowToCall} {_arguments.Trim()}";

    /// <summary>The resolved command line, so a wrong interpreter is visible before it fails.</summary>
    public string CommandLine =>
        registry.CommandLineFor(Info, string.IsNullOrWhiteSpace(_arguments) ? null : _arguments);
}

/// <summary>One editable extension-to-interpreter mapping.</summary>
public sealed class InterpreterRow(string extension, string command, Action<string, string> onChanged) : Notifier
{
    string _command = command;

    public string Extension { get; } = extension;

    public string Command
    {
        get => _command;
        set
        {
            Set(ref _command, value);
            onChanged(Extension, value);
        }
    }
}
