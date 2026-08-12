using Dropzone.Mtp;

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
        var scanned = source.EnumerateMedia(new Progress<string>(Console.WriteLine));
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

        Console.WriteLine($"importing into {destination}");

        var importer = new PhoneImporter(source, new FileImportLedger(ledgerPath));
        var result = await importer.ImportAsync(
            destination,
            new Progress<ImportProgress>(p => Console.WriteLine($"  {p.Stage}")));

        Console.WriteLine($"copied={result.Copied} skipped={result.Skipped} failed={result.Failed} bytes={result.BytesCopied:N0}");
        foreach (var error in result.Errors.Take(10)) Console.WriteLine($"  error: {error}");
        return result.Failed == 0 ? 0 : 1;
    }

    default:
        Console.WriteLine("usage: Dropzone.Cli [status|scan|import <folder>]");
        return 2;
}
