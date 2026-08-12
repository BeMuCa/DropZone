using Dropzone.Mtp;

namespace Dropzone.Mtp.Tests;

public class MediaKindTests
{
    [Theory]
    [InlineData("IMG_6441.JPG", MediaKind.Photo)]
    [InlineData("IMG_0001.heic", MediaKind.Photo)]
    [InlineData("photo.PNG", MediaKind.Photo)]
    [InlineData("IMG_1234.MOV", MediaKind.Video)]
    [InlineData("clip.mp4", MediaKind.Video)]
    [InlineData("notes.txt", MediaKind.Other)]
    [InlineData("noextension", MediaKind.Other)]
    public void Classifies_by_extension(string fileName, MediaKind expected)
    {
        Assert.Equal(expected, MediaClassifier.Classify(fileName));
    }
}

public class ImportPlannerTests
{
    static MtpItem Item(string name, long size = 100, string? folder = "202508_b") =>
        new(Path: $@"\Internal Storage\{folder}\{name}", Name: name, Size: size, Modified: new DateTime(2025, 8, 16));

    [Fact]
    public void Includes_photos_and_videos_only()
    {
        var items = new[] { Item("a.JPG"), Item("b.MOV"), Item("c.txt") };

        var plan = ImportPlanner.Plan(items, destinationRoot: @"D:\Photos", alreadyImported: []);

        Assert.Equal(2, plan.ToCopy.Count);
        Assert.DoesNotContain(plan.ToCopy, e => e.Item.Name == "c.txt");
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void Skips_items_already_imported()
    {
        var items = new[] { Item("a.JPG"), Item("b.JPG") };

        var plan = ImportPlanner.Plan(items, @"D:\Photos", alreadyImported: [@"\Internal Storage\202508_b\a.JPG"]);

        Assert.Single(plan.ToCopy);
        Assert.Equal("b.JPG", plan.ToCopy[0].Item.Name);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void Lays_out_destination_by_year_and_month()
    {
        var items = new[] { Item("a.JPG", folder: "202508_b") };

        var plan = ImportPlanner.Plan(items, @"D:\Photos", []);

        Assert.Equal(Path.Combine(@"D:\Photos", "2025", "2025-08"), plan.ToCopy[0].DestinationFolder);
    }

    [Fact]
    public void Falls_back_to_unsorted_when_folder_is_not_date_coded()
    {
        var items = new[] { Item("a.JPG", folder: "WeirdFolder") };

        var plan = ImportPlanner.Plan(items, @"D:\Photos", []);

        Assert.Equal(Path.Combine(@"D:\Photos", "Unsorted"), plan.ToCopy[0].DestinationFolder);
    }

    [Fact]
    public void Reports_total_bytes_to_copy()
    {
        var items = new[] { Item("a.JPG", size: 1000), Item("b.MOV", size: 2500), Item("c.txt", size: 999) };

        var plan = ImportPlanner.Plan(items, @"D:\Photos", []);

        Assert.Equal(3500, plan.TotalBytes);
    }

    [Fact]
    public void Empty_source_yields_empty_plan()
    {
        var plan = ImportPlanner.Plan([], @"D:\Photos", []);

        Assert.Empty(plan.ToCopy);
        Assert.Equal(0, plan.TotalBytes);
    }
}
