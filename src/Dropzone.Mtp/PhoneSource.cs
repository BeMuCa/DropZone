using MediaDevices;

namespace Dropzone.Mtp;

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

public interface IPhoneSource
{
    PhoneStatus Status();
    IReadOnlyList<MtpItem> EnumerateMedia(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    void CopyTo(MtpItem item, string destinationFile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the iPhone over MTP/WPD using the inbox Windows driver — no iTunes and no Apple
/// usermode service involved. The device is read-only over MTP: writes are refused with
/// STG_E_ACCESSDENIED, so this is an import-only source by design.
/// </summary>
public sealed class MediaDevicesPhoneSource : IPhoneSource
{
    readonly Func<string, bool> _isPhone;

    public MediaDevicesPhoneSource(Func<string, bool>? isPhone = null) =>
        _isPhone = isPhone ?? (name => name.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("iPad", StringComparison.OrdinalIgnoreCase));

    MediaDevice? FindPhone() =>
        MediaDevice.GetDevices()
            .FirstOrDefault(d => _isPhone(d.FriendlyName ?? "") || _isPhone(d.Description ?? ""));

    public PhoneStatus Status()
    {
        var device = FindPhone();

        // WPD hides a locked phone entirely, so ask Windows whether one is plugged in at all.
        if (device is null)
            return PnpPhoneDetector.IsPhysicallyAttached()
                ? new PhoneStatus(true, false, "iPhone")
                : PhoneStatus.Absent;

        try
        {
            device.Connect();
            // A locked or untrusted phone enumerates as a device but exposes no storage.
            var unlocked = device.GetDirectories(@"\").Any();
            return new PhoneStatus(true, unlocked, device.FriendlyName);
        }
        catch (Exception)
        {
            return new PhoneStatus(true, false, device.FriendlyName);
        }
        finally
        {
            TryDisconnect(device);
        }
    }

    public IReadOnlyList<MtpItem> EnumerateMedia(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var device = FindPhone();
        if (device is null) return [];

        var items = new List<MtpItem>();
        device.Connect();
        try
        {
            foreach (var storage in device.GetDirectories(@"\"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // iOS exposes YYYYMM-coded folders directly under storage; there is no DCIM.
                var folders = device.GetDirectories(storage)
                    .Select(path => (Path: path, Name: System.IO.Path.GetFileName(path.TrimEnd('\\'))))
                    .Select(f => (f.Path, f.Name, Parsed: AppleFolder.TryParse(f.Name, out var af) ? af : null))
                    .OrderByDescending(f => f.Parsed)
                    .ToList();

                var done = 0;
                foreach (var folder in folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"Scanning {folder.Name} ({++done}/{folders.Count})");

                    foreach (var filePath in device.GetFiles(folder.Path))
                    {
                        var name = System.IO.Path.GetFileName(filePath);
                        if (!MediaClassifier.IsMedia(name)) continue;

                        long size = 0;
                        DateTime? created = null;
                        try
                        {
                            var info = device.GetFileInfo(filePath);
                            size = (long)info.Length;
                            created = info.CreationTime;
                        }
                        catch (Exception)
                        {
                            // Metadata can fail on individual objects; the file is still importable.
                        }

                        items.Add(new MtpItem(filePath, name, size, created));
                    }
                }
            }
        }
        finally
        {
            TryDisconnect(device);
        }

        return items;
    }

    public void CopyTo(MtpItem item, string destinationFile, CancellationToken cancellationToken = default)
    {
        var device = FindPhone() ?? throw new InvalidOperationException("iPhone is not connected.");

        device.Connect();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            var temporary = destinationFile + ".partial";
            using (var output = File.Create(temporary))
                device.DownloadFile(item.Path, output);

            File.Move(temporary, destinationFile, overwrite: true);
        }
        finally
        {
            TryDisconnect(device);
        }
    }

    static void TryDisconnect(MediaDevice device)
    {
        try { device.Disconnect(); } catch (Exception) { /* already gone */ }
    }
}
