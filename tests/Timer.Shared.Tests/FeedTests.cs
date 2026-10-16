using Timer.Core;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class FeedTests
{
    private static readonly DateTime FileTime = new(2026, 10, 16, 9, 0, 0);

    [Fact]
    public void AnEntryRunsFromAHeadingToTheNextAndTakesItsDateAndIdOutOfTheWords()
    {
        var text = "## Acme: supplier delay\nDate: 2026-10-16 08:30\nID: Acme-Delay-1016\n#AccountOne\nShipment slipped to Tuesday.\n\n## Second thing\nBody two\n";
        var entries = Feed.Parse(text, "daily", FileTime);
        Assert.Equal(2, entries.Count);
        var first = entries[0];
        Assert.Equal("Acme: supplier delay", first.Headline); Assert.Equal("id:acme-delay-1016", first.Id); Assert.True(first.HasDate);
        Assert.Equal(new DateTime(2026, 10, 16, 8, 30, 0), first.When); Assert.Equal("#AccountOne\nShipment slipped to Tuesday.", first.Body); Assert.Equal("daily", first.Source);
        Assert.False(entries[1].HasDate); Assert.Equal("Body two", entries[1].Body);
    }

    [Fact]
    public void AnEntryWithoutADateTakesTheFileTimeAndEarlierInTheFileIsNewer()
    {
        var entries = Feed.Parse("## A\none\n## B\ntwo\n", "wire", FileTime);
        Assert.Equal(FileTime, entries[0].When); Assert.True(entries[0].When > entries[1].When);
    }

    [Fact]
    public void AFileWithNoHeadingsIsOneEntryNamedAfterTheFile()
    {
        var entries = Feed.Parse("Just some words\nover two lines", "morning", FileTime);
        var only = Assert.Single(entries); Assert.Equal("morning", only.Headline); Assert.Equal("Just some words\nover two lines", only.Body);
        Assert.Empty(Feed.Parse("", "empty", FileTime)); Assert.Empty(Feed.Parse("\n  \n", "blank", FileTime));
    }

    [Fact]
    public void ATagLineIsNotAHeadingAndAnUrgentMarkIsRemovedFromTheHeadline()
    {
        var entries = Feed.Parse("## ! Call back today\n#AccountOne is waiting\n", "wire", FileTime);
        var only = Assert.Single(entries); Assert.True(only.Urgent); Assert.Equal("Call back today", only.Headline); Assert.Contains("#AccountOne", only.Body);
    }

    [Fact]
    public void WithoutAnIdTheSameEntryKeepsTheSameIdWhateverTheFileTimeAndChangedWordsMakeANewOne()
    {
        var a = Feed.Parse("## Same\nDate: 2026-10-10\nwords", "x", FileTime)[0];
        var b = Feed.Parse("## Same\nDate: 2026-10-10\nwords  \n", "y", FileTime.AddDays(3))[0]; // Trailing spaces and a later file time change nothing.
        var c = Feed.Parse("## Same\nDate: 2026-10-10\nother words", "x", FileTime)[0];
        Assert.Equal(a.Id, b.Id); Assert.NotEqual(a.Id, c.Id);
    }

    [Fact]
    public void MergingJoinsSourcesNewestFirstAndShowsRepeatedEntriesOnce()
    {
        var one = Feed.Parse("## Old\nDate: 2026-10-01\nx\n## Shared\nID: s1\nDate: 2026-10-10\ny", "a", FileTime);
        var two = Feed.Parse("## New\nDate: 2026-10-15\nz\n## Shared\nID: s1\nDate: 2026-10-10\ny", "b", FileTime);
        var merged = Feed.Merge(one.Concat(two));
        Assert.Equal(new[] { "New", "Shared", "Old" }, merged.Select(e => e.Headline));
    }

    [Fact]
    public void TheAgeLimitHidesOldEntriesAtThirtyAndNinetyDays()
    {
        var now = new DateTime(2026, 10, 16);
        var entry = Feed.Parse("## Old\nDate: 2026-09-01\nx", "a", FileTime)[0]; // 45 days old.
        Assert.False(Feed.Within(entry, now, Feed.RetentionDays(false))); Assert.True(Feed.Within(entry, now, Feed.RetentionDays(true)));
        Assert.Equal(30, Feed.DefaultRetentionDays); Assert.Equal(90, Feed.LongRetentionDays);
    }

    [Fact]
    public void AFolderIsScannedForMarkdownAndTextFilesOnlyWithBigFilesSkipped()
    {
        var folder = Path.Combine(Path.GetTempPath(), "feed-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "one.md"), "## A\nDate: 2026-10-15\nx");
            File.WriteAllText(Path.Combine(folder, "two.txt"), "## B\nDate: 2026-10-14\ny");
            File.WriteAllText(Path.Combine(folder, "ignore.docx"), "## C\nz");
            Directory.CreateDirectory(Path.Combine(folder, "sub")); File.WriteAllText(Path.Combine(folder, "sub", "deep.md"), "## D\nz");
            File.WriteAllText(Path.Combine(folder, "huge.md"), new string('a', (int)Feed.MaxFileBytes + 10));
            var scan = Feed.ScanFolder(folder);
            Assert.Equal(new[] { "A", "B" }, scan.Entries.Select(e => e.Headline)); Assert.Equal(2, scan.Files);
            Assert.Contains(scan.Skipped, s => s.StartsWith("huge.md", StringComparison.Ordinal));
            Assert.Empty(Feed.ScanFolder(Path.Combine(folder, "missing")).Entries); Assert.Empty(Feed.ScanFolder(null).Entries);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheFolderNoteReportsSizeCountsAndAWarningWhenLarge()
    {
        var small = new Feed.Scan(Array.Empty<FeedEntry>(), 18, 2_400_000, Array.Empty<string>());
        Assert.Equal("2.4 MB in 18 files. 412 entries showing, 120 hidden as older than 30 days.", Feed.Describe(small, 412, 120, 30));
        var one = new Feed.Scan(Array.Empty<FeedEntry>(), 1, 4_000, Array.Empty<string>());
        Assert.Equal("4 KB in 1 file. 1 entry showing, 0 hidden as older than 90 days.", Feed.Describe(one, 1, 0, 90));
        var large = new Feed.Scan(Array.Empty<FeedEntry>(), 400, 60_000_000, Array.Empty<string>());
        Assert.EndsWith("old files can be cleared out by hand.", Feed.Describe(large, 1, 0, 30));
        Assert.Equal("No feed files found in the folder.", Feed.Describe(new Feed.Scan(Array.Empty<FeedEntry>(), 0, 0, Array.Empty<string>()), 0, 0, 30));
    }
}
