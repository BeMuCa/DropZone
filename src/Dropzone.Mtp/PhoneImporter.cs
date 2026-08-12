namespace Dropzone.Mtp;

public sealed record ImportProgress(string Stage, int Done, int Total, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal == 0 ? 0 : (double)BytesDone / BytesTotal;
}

public sealed record ImportResult(int Copied, int Skipped, int Failed, long BytesCopied, IReadOnlyList<string> Errors);

/// <summary>Runs a full import: enumerate the phone, plan, copy, and remember what was taken.</summary>
public sealed class PhoneImporter(IPhoneSource source, IImportLedger ledger)
{
    public async Task<ImportResult> ImportAsync(
        string destinationRoot,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(new ImportProgress("Scanning iPhone…", 0, 0, 0, 0));

            var items = source.EnumerateMedia(
                new Progress<string>(s => progress?.Report(new ImportProgress(s, 0, 0, 0, 0))),
                cancellationToken);

            var plan = ImportPlanner.Plan(items, destinationRoot, ledger.AlreadyImported());

            var copied = 0;
            var failed = 0;
            long bytes = 0;
            var errors = new List<string>();

            foreach (var entry in plan.ToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = Path.Combine(entry.DestinationFolder, entry.Item.Name);
                try
                {
                    if (!File.Exists(target))
                        source.CopyTo(entry.Item, target, cancellationToken);

                    ledger.MarkImported(entry.Item.Path);
                    copied++;
                    bytes += entry.Item.Size;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{entry.Item.Name}: {ex.Message}");
                }

                progress?.Report(new ImportProgress(
                    $"Copying {entry.Item.Name}", copied + failed, plan.ToCopy.Count, bytes, plan.TotalBytes));
            }

            ledger.Save();
            return new ImportResult(copied, plan.Skipped.Count, failed, bytes, errors);
        }, cancellationToken);
    }
}

public interface IImportLedger
{
    IReadOnlyCollection<string> AlreadyImported();
    void MarkImported(string mtpPath);
    void Save();
}

/// <summary>Remembers imported MTP paths in a plain text file so re-imports skip what is already on disk.</summary>
public sealed class FileImportLedger : IImportLedger
{
    readonly string _path;
    readonly HashSet<string> _seen;

    public FileImportLedger(string path)
    {
        _path = path;
        _seen = File.Exists(path)
            ? new HashSet<string>(File.ReadAllLines(path), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AlreadyImported() => _seen;

    public void MarkImported(string mtpPath) => _seen.Add(mtpPath);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, _seen);
    }
}
