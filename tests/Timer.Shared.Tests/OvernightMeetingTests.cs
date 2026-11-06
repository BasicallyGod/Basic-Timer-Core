using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class OvernightMeetingTests
{
    private static readonly DateTime Day = new(2026, 11, 6);

    [Fact]
    public void OvernightMeetingAndSeriesKeepTheirEndDateAcrossReopening()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-overnight-meetings-" + Guid.NewGuid());
        try
        {
            using (var store = new TimerStore(folder))
            {
                var template = new NoteSeries(0, new Recurrence(1, 0), Day, Day, "Night", TimeSpan.FromHours(23), TimeSpan.FromHours(26), DayNoteKind.Meeting);
                var series = store.AddSeries(template);
                var id = store.AddDayNote(Day, template.At, "Night", template.End, series, DayNoteKind.Meeting);
                var note = Assert.Single(store.LoadDayNotes());
                Assert.Equal(Day.AddDays(1).AddHours(2), DayNotes.EndsAt(note));
                store.UpdateDayNote(note with { End = TimeSpan.FromHours(27) });
                store.UpdateSeries(template with { Id = series, End = TimeSpan.FromHours(27) });
            }
            using (var store = new TimerStore(folder))
            {
                Assert.Equal(TimeSpan.FromHours(27), Assert.Single(store.LoadDayNotes()).End);
                Assert.Equal(TimeSpan.FromHours(27), Assert.Single(store.LoadSeries()).End);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void OvernightMeetingsRequireAValidStartAndAtMostTwentyFourHours()
    {
        var note = new DayNote(1, Day, TimeSpan.FromHours(23), "Night", End: TimeSpan.FromHours(26), Kind: DayNoteKind.Meeting);
        DayNotes.Validate(note); DayNotes.Validate(note with { End = TimeSpan.FromHours(47) });
        Assert.Throws<ArgumentException>(() => DayNotes.Validate(note with { At = TimeSpan.FromHours(24) }));
        Assert.Throws<ArgumentException>(() => DayNotes.Validate(note with { End = TimeSpan.FromHours(48) }));
        Assert.Throws<ArgumentException>(() => DayNotes.Validate(note with { End = TimeSpan.FromHours(22) }));
    }

    [Fact]
    public void FormatSixIsBackedUpAndOlderReadersRefuseTheOvernightEncoding()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-overnight-upgrade-" + Guid.NewGuid());
        try
        {
            using (var database = TimerDatabase.Open(folder))
            {
                database.Execute("INSERT INTO day_notes(day, at_time, text, end_time, kind, has_date) VALUES ('2026-11-06', '09:00', 'Existing', '10:00', 1, 1)");
                database.Execute("PRAGMA user_version = 6"); // Formats 6 and 7 have the same tables; only the allowed end-time encoding changes.
            }
            using (var store = new TimerStore(folder))
            {
                Assert.Equal(7, store.Database.Version);
                Assert.Equal(TimeSpan.FromHours(10), Assert.Single(store.LoadDayNotes()).End);
            }
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "Backups"), "timer-before-format-7-*.db"));
            Assert.Throws<DatabaseTooNewException>(() => { using var older = TimerDatabase.Open(folder, targetVersion: 6); });
        }
        finally { Directory.Delete(folder, true); }
    }
}
