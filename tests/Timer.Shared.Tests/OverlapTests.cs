using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

// Add and Paste can be asked to allow overlap, so time shares hours as an edit already can. The block being recorded still refuses it, and the default stays strict.
public sealed class OverlapTests
{
    private static DateTime Day => new(2026, 10, 9);
    private static TrackingEngine Engine(Func<DateTime>? clock = null) => new(clock: clock ?? (() => Day.AddHours(15)));
    private static SessionRow Row(string start, string minutes, string account = "Account one") => new("2026-10-09", account, start, "added", minutes);

    [Fact]
    public void AddRefusesAnOverlapByDefaultAndTakesItWhenAllowed()
    {
        var engine = Engine(); engine.Sessions.Add(Row("09:00:00", "60")); var editor = new LedgerEditor(engine);
        Assert.Throws<ArgumentException>(() => editor.Add("Account two", Day.AddHours(9.5), Day.AddHours(10.5), ""));
        editor.Add("Account two", Day.AddHours(9.5), Day.AddHours(10.5), "stacked", allowOverlap: true);
        Assert.Equal(2, engine.Sessions.Count); Assert.Equal("stacked", editor.Note(engine.Sessions[1]));
        editor.Undo(); Assert.Single(engine.Sessions); // One undo step, as before.
    }

    [Fact]
    public void AllowedOverlapStillRefusesTheBlockBeingRecordedAndAnExactDuplicateKey()
    {
        var now = Day.AddHours(11); var engine = Engine(() => now); var editor = new LedgerEditor(engine);
        engine.ToggleAccount("Account one"); now = Day.AddHours(12); // Recording since 11:00.
        Assert.Throws<ArgumentException>(() => editor.Add("Account two", Day.AddHours(11.5), Day.AddHours(11.75), "", allowOverlap: true));
        engine.Sessions.Add(Row("08:00:00", "30"));
        Assert.Throws<ArgumentException>(() => editor.Add("Account one", Day.AddHours(8), Day.AddHours(8.25), "", allowOverlap: true)); // The same account and start would share one note.
    }

    [Fact]
    public void PasteCanLandOnRecordedTimeAndCopiedBlocksMayOverlapEachOtherWhenAllowed()
    {
        var engine = Engine(); engine.Sessions.Add(Row("09:00:00", "60")); var editor = new LedgerEditor(engine);
        var copied = new[] { new LedgerEditor.ClipBlock("Account two", TimeSpan.Zero, "30.00", "a"), new LedgerEditor.ClipBlock("Account three", TimeSpan.FromMinutes(15), "30.00", "b") };
        Assert.Throws<ArgumentException>(() => editor.Paste(copied, Day.AddHours(9.25)));
        Assert.Single(engine.Sessions); // Whole or nothing.
        editor.Paste(copied, Day.AddHours(9.25), allowOverlap: true);
        Assert.Equal(3, engine.Sessions.Count); Assert.Equal("b", editor.Note(engine.Sessions[2]));
        editor.Undo(); Assert.Single(engine.Sessions);
    }

    [Fact]
    public void PasteStillRefusesMidnightAndTheRunningBlockAndTwoCopiesWithTheSameKey()
    {
        var now = Day.AddHours(11); var engine = Engine(() => now); var editor = new LedgerEditor(engine);
        var one = new[] { new LedgerEditor.ClipBlock("Account one", TimeSpan.Zero, "60.00", "") };
        Assert.Throws<ArgumentException>(() => editor.Paste(one, Day.AddHours(23.5), allowOverlap: true));
        var twins = new[] { new LedgerEditor.ClipBlock("Account one", TimeSpan.Zero, "30.00", ""), new LedgerEditor.ClipBlock("Account one", TimeSpan.Zero, "20.00", "") };
        Assert.Throws<ArgumentException>(() => editor.Paste(twins, Day.AddHours(8), allowOverlap: true));
        engine.ToggleAccount("Account two"); now = Day.AddHours(12);
        Assert.Throws<ArgumentException>(() => editor.Paste(one, Day.AddHours(11.5), allowOverlap: true));
        Assert.Empty(engine.Sessions);
    }
}
