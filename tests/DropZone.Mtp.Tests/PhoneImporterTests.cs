using DropZone.Mtp;

namespace DropZone.Mtp.Tests;

sealed class FakePhoneSource(params MtpItem[] items) : IPhoneSource
{
    public List<string> Copied { get; } = [];
    public Func<MtpItem, bool>? FailOn { get; set; }

    public PhoneStatus Status() => new(true, true, "Fake iPhone");

    public void Scan(Action<MtpItem> onItem, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            onItem(item);
            progress?.Report(new ScanProgress("scanning", ++done, items.Length, done));
        }
    }

    public void CopyTo(MtpItem item, string destinationFile, CancellationToken ct = default)
    {
        if (FailOn?.Invoke(item) == true)
            throw new IOException($"device error on {item.Name}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.WriteAllBytes(destinationFile, new byte[item.Size]);
        Copied.Add(item.Path);
    }
}

sealed class FakeLedger : IImportLedger
{
    public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SaveCount { get; private set; }

    public IReadOnlyCollection<string> AlreadyImported() => Seen;
    public void MarkImported(string mtpPath) => Seen.Add(mtpPath);
    public void Save() => SaveCount++;
}

public sealed class PhoneImporterTests : IDisposable
{
    readonly string _destination = Directory.CreateTempSubdirectory("dropzone-import-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_destination, recursive: true); } catch (IOException) { }
    }

    static MtpItem Photo(string name, long size = 10, string folder = "202508_b") =>
        new($@"\Internal Storage\{folder}\{name}", name, size, new DateTime(2025, 8, 16));

    [Fact]
    public async Task Copies_media_and_records_it_in_the_ledger()
    {
        var source = new FakePhoneSource(Photo("a.JPG"), Photo("b.MOV"), Photo("notes.txt"));
        var ledger = new FakeLedger();

        var result = await new PhoneImporter(source, ledger).ImportAsync(_destination);

        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, ledger.Seen.Count);
        Assert.Equal(1, ledger.SaveCount);
    }

    [Fact]
    public async Task Writes_into_year_and_month_folders()
    {
        var source = new FakePhoneSource(Photo("a.JPG", folder: "202508_b"));

        await new PhoneImporter(source, new FakeLedger()).ImportAsync(_destination);

        Assert.True(File.Exists(Path.Combine(_destination, "2025", "2025-08", "a.JPG")));
    }

    [Fact]
    public async Task Second_run_skips_what_the_ledger_already_has()
    {
        var source = new FakePhoneSource(Photo("a.JPG"), Photo("b.JPG"));
        var ledger = new FakeLedger();
        var importer = new PhoneImporter(source, ledger);

        await importer.ImportAsync(_destination);
        source.Copied.Clear();

        var second = await importer.ImportAsync(_destination);

        Assert.Equal(0, second.Copied);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(source.Copied);
    }

    [Fact]
    public async Task A_failing_file_does_not_abort_the_rest()
    {
        var source = new FakePhoneSource(Photo("good1.JPG"), Photo("bad.JPG"), Photo("good2.JPG"))
        {
            FailOn = i => i.Name == "bad.JPG"
        };

        var result = await new PhoneImporter(source, new FakeLedger()).ImportAsync(_destination);

        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Contains("bad.JPG", result.Errors[0]);
    }

    [Fact]
    public async Task Reports_progress_while_copying()
    {
        var source = new FakePhoneSource(Photo("a.JPG", 100), Photo("b.JPG", 200));
        var reports = new List<ImportProgress>();

        await new PhoneImporter(source, new FakeLedger())
            .ImportAsync(_destination, new Progress<ImportProgress>(p => { lock (reports) reports.Add(p); }));

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.BytesTotal == 300);
    }

    [Fact]
    public async Task Cancellation_stops_the_import()
    {
        var source = new FakePhoneSource(Photo("a.JPG"), Photo("b.JPG"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PhoneImporter(source, new FakeLedger()).ImportAsync(_destination, null, cts.Token));
    }
}

public sealed class FileImportLedgerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dropzone-ledger-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        var path = Path.Combine(_dir, "ledger.txt");

        var first = new FileImportLedger(path);
        first.MarkImported(@"\Internal Storage\202508_b\a.JPG");
        first.Save();

        var second = new FileImportLedger(path);

        Assert.Contains(@"\Internal Storage\202508_b\a.JPG", second.AlreadyImported());
    }

    [Fact]
    public void Starts_empty_when_no_file_exists()
    {
        Assert.Empty(new FileImportLedger(Path.Combine(_dir, "missing.txt")).AlreadyImported());
    }
}
