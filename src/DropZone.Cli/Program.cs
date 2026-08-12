using DropZone.Mtp;

// Diagnostics for the cable path — the half that needs real hardware to verify.
var command = args.FirstOrDefault() ?? "status";
var source = new MediaDevicesPhoneSource();

switch (command)
{
    case "status":
    {
        var status = source.Status();
        Console.WriteLine($"connected : {status.Connected}");
        Console.WriteLine($"unlocked  : {status.Unlocked}");
        Console.WriteLine($"name      : {status.Name ?? "-"}");
        Console.WriteLine($"=> {status.Describe()}");
        return status is { Connected: true, Unlocked: true } ? 0 : 1;
    }

    case "scan":
    {
        var scanned = source.EnumerateMedia(
            new Progress<ScanProgress>(p => Console.WriteLine(
                $"  {p.Stage} — {p.FilesFound} found ({p.FoldersDone}/{p.FoldersTotal})")));
        Console.WriteLine($"media files found: {scanned.Count}");
        foreach (var item in scanned.Take(10))
            Console.WriteLine($"  {item.Name,-24} {item.Size,12:N0} bytes  {item.Path}");
        if (scanned.Count > 10) Console.WriteLine($"  … and {scanned.Count - 10} more");
        return scanned.Count > 0 ? 0 : 1;
    }

    case "import":
    {
        var destination = args.ElementAtOrDefault(1)
            ?? Path.Combine(Path.GetTempPath(), "dropzone-import");
        var ledgerPath = Path.Combine(destination, ".dropzone-ledger.txt");

        // A full library is thousands of files, so allow importing just the newest few.
        var limit = int.TryParse(args.ElementAtOrDefault(2), out var n) ? n : 0;

        Console.WriteLine($"importing into {destination}{(limit > 0 ? $" (newest {limit})" : "")}");

        var importer = new PhoneImporter(source, new FileImportLedger(ledgerPath));
        ImportResult result;

        if (limit > 0)
        {
            var picked = new List<MtpItem>();
            using var stop = new CancellationTokenSource();

            try
            {
                source.Scan(item =>
                {
                    picked.Add(item);
                    if (picked.Count >= limit) stop.Cancel();
                }, null, stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: we stop the walk as soon as we have enough.
            }

            Console.WriteLine($"  selected {picked.Count} file(s)");
            result = await importer.ImportAsync(
                picked, destination,
                new Progress<ImportProgress>(p => Console.WriteLine($"  {p.Stage}")));
        }
        else
        {
            result = await importer.ImportAsync(
                destination,
                new Progress<ImportProgress>(p => Console.WriteLine($"  {p.Stage}")));
        }

        foreach (var file in new DirectoryInfo(destination).EnumerateFiles("*", SearchOption.AllDirectories).Take(10))
            Console.WriteLine($"  on disk: {file.Name}  {file.Length:N0} bytes");

        Console.WriteLine($"copied={result.Copied} skipped={result.Skipped} failed={result.Failed} bytes={result.BytesCopied:N0}");
        foreach (var error in result.Errors.Take(10)) Console.WriteLine($"  error: {error}");
        return result.Failed == 0 ? 0 : 1;
    }

    default:
        Console.WriteLine("usage: DropZone.Cli [status|scan|import <folder> [count]]");
        return 2;
}
