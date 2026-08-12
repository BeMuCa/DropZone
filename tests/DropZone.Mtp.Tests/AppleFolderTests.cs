using DropZone.Mtp;

namespace DropZone.Mtp.Tests;

public class AppleFolderTests
{
    [Theory]
    [InlineData("202508_b", 2025, 8)]
    [InlineData("202607__", 2026, 7)]
    [InlineData("201811__", 2018, 11)]
    [InlineData("202412_a", 2024, 12)]
    [InlineData("202001__", 2020, 1)]
    public void Parses_date_coded_folders(string name, int year, int month)
    {
        Assert.True(AppleFolder.TryParse(name, out var folder));
        Assert.Equal(year, folder.Year);
        Assert.Equal(month, folder.Month);
    }

    [Theory]
    [InlineData("DCIM")]
    [InlineData("100APPLE")]
    [InlineData("20250")]
    [InlineData("")]
    [InlineData("abcdef__")]
    public void Rejects_non_date_folders(string name)
    {
        Assert.False(AppleFolder.TryParse(name, out _));
    }

    [Theory]
    [InlineData("202513__")]
    [InlineData("202500__")]
    public void Rejects_impossible_months(string name)
    {
        Assert.False(AppleFolder.TryParse(name, out _));
    }

    [Fact]
    public void Orders_chronologically()
    {
        var names = new[] { "202508_b", "201811__", "202607__", "202001__" };
        var parsed = names.Select(n => { AppleFolder.TryParse(n, out var f); return f; })
                          .OrderBy(f => f)
                          .Select(f => f.Name)
                          .ToArray();

        Assert.Equal(new[] { "201811__", "202001__", "202508_b", "202607__" }, parsed);
    }
}
