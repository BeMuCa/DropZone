using MediaDevices;

namespace DropZone.Mtp;

public sealed record PhoneStatus(bool Connected, bool Unlocked, string? Name)
{
    public static readonly PhoneStatus Absent = new(false, false, null);

    public string Describe() => this switch
    {
        { Connected: false } => "No iPhone connected",
        { Unlocked: false } => "Unlock your iPhone and tap Trust",
        _ => Name ?? "iPhone"
    };
}

public sealed record ScanProgress(string Stage, int FoldersDone, int FoldersTotal, int FilesFound)
{
    public double Fraction => FoldersTotal == 0 ? 0 : (double)FoldersDone / FoldersTotal;
}

public interface IPhoneSource
{
    PhoneStatus Status();

    /// <summary>
    /// Walks the phone, handing back each media file as it is found. Streaming matters here:
    /// a full library is tens of thousands of MTP round-trips, so waiting for a complete list
    /// means staring at nothing for minutes.
    /// </summary>
    void Scan(Action<MtpItem> onItem, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);

    void CopyTo(MtpItem item, string destinationFile, CancellationToken cancellationToken = default);
}

public static class PhoneSourceExtensions
{
    public static IReadOnlyList<MtpItem> EnumerateMedia(
        this IPhoneSource source,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<MtpItem>();
        source.Scan(items.Add, progress, cancellationToken);
        return items;
    }
}

/// <summary>
/// Reads the iPhone over MTP/WPD using the inbox Windows driver — no iTunes and no Apple
/// usermode service involved. The device is read-only over MTP: writes are refused with
/// STG_E_ACCESSDENIED, so this is an import-only source by design.
/// </summary>
public sealed class MediaDevicesPhoneSource : IPhoneSource
{
    // One MTP device tolerates exactly one conversation at a time. Without this, opening the
    // window during a scan runs a status check whose Disconnect() kills the scan's connection.
    readonly SemaphoreSlim _device = new(1, 1);
    readonly Func<string, bool> _isPhone;

    public MediaDevicesPhoneSource(Func<string, bool>? isPhone = null) =>
        _isPhone = isPhone ?? (name => name.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("iPad", StringComparison.OrdinalIgnoreCase));

    MediaDevice? FindPhone() =>
        MediaDevice.GetDevices()
            .FirstOrDefault(d => _isPhone(d.FriendlyName ?? "") || _isPhone(d.Description ?? ""));

    T WithDevice<T>(Func<MediaDevice?, T> work, CancellationToken cancellationToken = default)
    {
        _device.Wait(cancellationToken);
        MediaDevice? device = null;
        try
        {
            device = FindPhone();
            if (device is null) return work(null);

            device.Connect();
            return work(device);
        }
        finally
        {
            if (device is not null)
            {
                try { device.Disconnect(); } catch (Exception) { /* already gone */ }
            }

            _device.Release();
        }
    }

    public PhoneStatus Status() => WithDevice(device =>
    {
        // WPD hides a locked phone entirely, so ask Windows whether one is plugged in at all.
        if (device is null)
            return PnpPhoneDetector.IsPhysicallyAttached()
                ? new PhoneStatus(true, false, "iPhone")
                : PhoneStatus.Absent;

        try
        {
            var unlocked = device.GetDirectories(@"\").Any();
            return new PhoneStatus(true, unlocked, device.FriendlyName);
        }
        catch (Exception)
        {
            return new PhoneStatus(true, false, device.FriendlyName);
        }
    });

    public void Scan(Action<MtpItem> onItem, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        WithDevice(device =>
        {
            if (device is null)
            {
                progress?.Report(new ScanProgress("No iPhone connected", 0, 0, 0));
                return 0;
            }

            var folders = new List<(string Path, string Name, AppleFolder? Parsed)>();

            foreach (var storage in device.GetDirectories(@"\"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // iOS exposes YYYYMM-coded folders directly under storage; there is no DCIM.
                foreach (var path in device.GetDirectories(storage))
                {
                    var name = Path.GetFileName(path.TrimEnd('\\'));
                    folders.Add((path, name, AppleFolder.TryParse(name, out var parsed) ? parsed : null));
                }
            }

            // Newest first, so the files someone actually wants appear in the first seconds.
            folders = folders
                .OrderByDescending(f => f.Parsed is null ? 0 : f.Parsed.Year * 100 + f.Parsed.Month)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var found = 0;
            var done = 0;

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ScanProgress($"Scanning {folder.Name}", done, folders.Count, found));

                IEnumerable<string> files;
                try
                {
                    files = device.GetFiles(folder.Path);
                }
                catch (Exception)
                {
                    done++;
                    continue; // an unreadable folder should not end the scan
                }

                foreach (var filePath in files)
                {
                    var name = Path.GetFileName(filePath);
                    if (!MediaClassifier.IsMedia(name)) continue;

                    // Deliberately no GetFileInfo call here. It costs a round-trip per file and
                    // turns a scan into tens of thousands of them; the folder name already
                    // carries the date, and the size is only needed once a file is imported.
                    onItem(new MtpItem(filePath, name, 0, DateFrom(folder.Parsed)));
                    found++;
                }

                done++;
                progress?.Report(new ScanProgress($"Scanning {folder.Name}", done, folders.Count, found));
            }

            progress?.Report(new ScanProgress($"{found} files", folders.Count, folders.Count, found));
            return found;
        }, cancellationToken);
    }

    static DateTime? DateFrom(AppleFolder? folder) =>
        folder is null ? null : new DateTime(folder.Year, folder.Month, 1);

    public void CopyTo(MtpItem item, string destinationFile, CancellationToken cancellationToken = default)
    {
        WithDevice(device =>
        {
            if (device is null) throw new InvalidOperationException("iPhone is not connected.");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            var temporary = destinationFile + ".partial";
            using (var output = File.Create(temporary))
                device.DownloadFile(item.Path, output);

            File.Move(temporary, destinationFile, overwrite: true);
            return 0;
        }, cancellationToken);
    }
}
