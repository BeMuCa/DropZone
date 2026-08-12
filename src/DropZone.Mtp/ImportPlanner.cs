namespace DropZone.Mtp;

/// <summary>A file on the device. <paramref name="Path"/> is the MTP path and is the identity used for dedupe.</summary>
public sealed record MtpItem(string Path, string Name, long Size, DateTime? Modified);

public sealed record PlannedCopy(MtpItem Item, string DestinationFolder);

public sealed record ImportPlan(
    IReadOnlyList<PlannedCopy> ToCopy,
    IReadOnlyList<MtpItem> Skipped,
    long TotalBytes);

public static class ImportPlanner
{
    public const string UnsortedFolderName = "Unsorted";

    public static ImportPlan Plan(
        IEnumerable<MtpItem> items,
        string destinationRoot,
        IReadOnlyCollection<string> alreadyImported)
    {
        var seen = new HashSet<string>(alreadyImported, StringComparer.OrdinalIgnoreCase);

        var toCopy = new List<PlannedCopy>();
        var skipped = new List<MtpItem>();

        foreach (var item in items)
        {
            if (!MediaClassifier.IsMedia(item.Name) || !seen.Add(item.Path))
            {
                skipped.Add(item);
                continue;
            }

            toCopy.Add(new PlannedCopy(item, DestinationFor(item, destinationRoot)));
        }

        return new ImportPlan(toCopy, skipped, toCopy.Sum(c => c.Item.Size));
    }

    static string DestinationFor(MtpItem item, string destinationRoot)
    {
        var parent = ParentFolderName(item.Path);

        return AppleFolder.TryParse(parent, out var folder)
            ? Path.Combine(destinationRoot, folder.Year.ToString(), $"{folder.Year:D4}-{folder.Month:D2}")
            : Path.Combine(destinationRoot, UnsortedFolderName);
    }

    static string ParentFolderName(string mtpPath)
    {
        var segments = mtpPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : string.Empty;
    }
}
