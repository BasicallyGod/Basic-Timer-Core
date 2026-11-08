using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

// Blocks may cross midnight and a day can begin when the user says (0.9.0): a night shift is one block and one day, and every total follows the day it starts on.
public sealed class DayBoundaryTests
{
    private static DateTime Day => new(2026, 10, 9);
    private static TrackingEngine Engine(DateTime? now = null, TimeSpan? dayStart = null) => new(clock: () => now ?? Day.AddDays(3)) { DayStart = dayStart ?? TimeSpan.Zero };

    [Fact]
    public void ABlockMayCrossMidnightWhenAddedPastedOrEdited_AndCountsOnTheDayItStarts()
    {
        var engine = Engine(); var editor = new LedgerEditor(engine);
        editor.Add("Account one", Day.AddHours(22), Day.AddDays(1).AddHours(2), "night", allowOverlap: true);
        var row = Assert.Single(engine.Sessions); Assert.Equal("2026-10-09", row.Date); Assert.Equal("22:00:00", row.Start); Assert.Equal(Day.AddDays(1).AddHours(2), row.To);
        Assert.Equal(TimeSpan.FromHours(4), engine.Credited("Account one", Day, Day.AddDays(1))); // All on the day it starts...
        Assert.Equal(TimeSpan.Zero, engine.Credited("Account one", Day.AddDays(1), Day.AddDays(2))); // ...and none on the next.
        editor.Edit(row, "Account one", Day.AddHours(21), Day.AddDays(1).AddHours(5), "longer", allowOverlap: true);
        Assert.Equal(TimeSpan.FromHours(8), engine.Sessions[0].Duration);
        editor.Paste(new[] { new LedgerEditor.ClipBlock("Account two", TimeSpan.Zero, "180.00", "") }, Day.AddDays(2).AddHours(23), allowOverlap: true);
        Assert.Equal(Day.AddDays(3).AddHours(2), engine.Sessions[1].To);
        editor.Undo(); Assert.Single(engine.Sessions);
        Assert.Throws<ArgumentException>(() => editor.Add("Account one", Day, Day.AddHours(25), "")); // But never more than 24 hours.
    }

    [Fact]
    public void ARecordingIsCutWhereTheDayBeginsNotAtMidnight_AndMidnightIsTheDefault()
    {
        var engine = Engine(); engine.AddSession("Account one", Day.AddHours(23), Day.AddDays(1).AddHours(1));
        Assert.Equal(new[] { "2026-10-09", "2026-10-10" }, engine.Sessions.Select(s => s.Date)); // Cut at midnight, as always.
        var night = Engine(dayStart: TimeSpan.FromHours(18)); night.AddSession("Account one", Day.AddHours(22), Day.AddDays(1).AddHours(6));
        var shift = Assert.Single(night.Sessions); Assert.Equal(TimeSpan.FromHours(8), shift.Duration); // One block across midnight.
        night.AddSession("Account two", Day.AddHours(17), Day.AddHours(19)); // A recording across 18:00 is cut there.
        Assert.Equal(new[] { "17:00:00", "18:00:00" }, night.Sessions.Skip(1).Select(s => s.Start));
    }

    [Fact]
    public void DaysAreBucketedByTheDayBoundary_ForCreditAndReviewAndTheWeekExport()
    {
        var engine = Engine(dayStart: TimeSpan.FromHours(18)); var editor = new LedgerEditor(engine);
        editor.Add("Account one", Day.AddHours(22), Day.AddDays(1).AddHours(6), "shift", allowOverlap: true); // Fri 22:00 to Sat 06:00: Friday's day.
        editor.Add("Account one", Day.AddDays(1).AddHours(19), Day.AddDays(1).AddHours(20), "", allowOverlap: true); // Sat 19:00: Saturday's day.
        editor.Add("Account one", Day.AddDays(1).AddHours(2), Day.AddDays(1).AddHours(3), "", allowOverlap: true); // Sat 02:00 is still Friday's day.
        Assert.Equal(TimeSpan.FromHours(9), engine.Credited("Account one", Day, Day.AddDays(1)));
        Assert.Equal(TimeSpan.FromHours(1), engine.Credited("Account one", Day.AddDays(1), Day.AddDays(2)));
        var entries = editor.Entries(Day, Day.AddDays(1)); Assert.Equal(2, entries.Count); Assert.All(entries, e => Assert.Equal(Day, e.Day));
        var week = new WeekExport(editor, engine, Day.AddDays(-4), 7);
        Assert.Equal(1000, week.TotalUnits); // 10 hours in all is 1000 hundredths.
        Assert.Equal(900, week.Accounts["Account one"][4]); Assert.Equal(100, week.Accounts["Account one"][5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.DayStart = TimeSpan.FromHours(24));
    }

    [Fact]
    public void ExportChecksSavedRowsAtBothEdgesOfTheWorkdayPeriod()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-workday-export-" + Guid.NewGuid());
        try
        {
            using var store = new TimerStore(folder);
            var engine = Engine(dayStart: TimeSpan.FromHours(18));
            var settings = store.Load(engine);
            engine.AddSession("Account one", Day.AddHours(17), Day.AddHours(18));
            engine.AddSession("Account one", Day.AddHours(18), Day.AddHours(19));
            engine.AddSession(BreakAccount.Name, Day.AddDays(1).AddHours(2), Day.AddDays(1).AddHours(2.25));
            engine.AddSession("Account one", Day.AddDays(1).AddHours(18), Day.AddDays(1).AddHours(19));
            store.Save(engine, settings);

            var export = new WeekExport(store.Editor!, engine, Day, days: 1);
            Assert.Equal(100, export.TotalUnits);
            Assert.Equal(25, export.LunchUnits);
            export.VerifyAgainst(store.Database);

            store.Database.Execute("UPDATE entries SET seconds = seconds + 60 WHERE kind = 'break'");
            Assert.Throws<InvalidDataException>(() => export.VerifyAgainst(store.Database));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheRunningBlockCountsOnTheDayItBelongsTo()
    {
        var clock = Day.AddDays(1).AddHours(3); var engine = new TrackingEngine(clock: () => clock) { DayStart = TimeSpan.FromHours(18) }; // 03:00 Saturday, in Friday's day.
        clock = Day.AddHours(23); engine.ToggleAccount("Account one"); clock = Day.AddDays(1).AddHours(3);
        Assert.Equal(TimeSpan.FromHours(4), engine.ActiveElapsedOn(Day)); Assert.Equal(TimeSpan.Zero, engine.ActiveElapsedOn(Day.AddDays(1)));
        Assert.Equal(Day, engine.WorkDay(clock)); Assert.Equal(Day.AddHours(18), engine.DayBegins(Day));
        Assert.Equal(TimeSpan.FromHours(4), engine.Credited("Account one", Day, Day.AddDays(1)));
    }

    [Fact]
    public void ADragNeverNeedsToStayInsideADay()
    {
        var engine = Engine(); engine.Sessions.Add(new("2026-10-09", "Account one", "22:00:00", "added", "60.00")); var editor = new LedgerEditor(engine);
        editor.EditTimeline(engine.Sessions[0], Day.AddHours(23), Day.AddDays(1).AddHours(2)); // Moved and stretched across midnight.
        Assert.Equal(Day.AddDays(1).AddHours(2), engine.Sessions[0].To);
    }
}
