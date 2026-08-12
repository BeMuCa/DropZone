namespace DropZone.Mtp;

/// <summary>
/// A top-level folder on the iPhone's MTP storage. iOS exposes these directly under
/// "Internal Storage" as YYYYMM plus a two-character suffix ("202508_b", "202607__");
/// there is no DCIM folder.
/// </summary>
public sealed record AppleFolder(string Name, int Year, int Month) : IComparable<AppleFolder>
{
    public static bool TryParse(string? name, out AppleFolder folder)
    {
        folder = null!;
        if (string.IsNullOrEmpty(name) || name.Length < 6)
            return false;

        for (var i = 0; i < 6; i++)
            if (!char.IsAsciiDigit(name[i]))
                return false;

        // Anything after the date must be a suffix, e.g. "__" or "_b".
        if (name.Length > 6 && name[6] != '_')
            return false;

        var year = int.Parse(name.AsSpan(0, 4));
        var month = int.Parse(name.AsSpan(4, 2));

        if (month is < 1 or > 12)
            return false;

        folder = new AppleFolder(name, year, month);
        return true;
    }

    public int CompareTo(AppleFolder? other)
    {
        if (other is null) return 1;
        var byYear = Year.CompareTo(other.Year);
        if (byYear != 0) return byYear;
        var byMonth = Month.CompareTo(other.Month);
        return byMonth != 0 ? byMonth : string.CompareOrdinal(Name, other.Name);
    }
}
