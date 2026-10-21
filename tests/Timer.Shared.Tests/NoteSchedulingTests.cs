using Microsoft.Data.Sqlite;
using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class NoteSchedulingTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "timer-note-scheduling-" + Guid.NewGuid());
    private static readonly DateTime Day = new(2026, 10, 21);
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    [Fact]
    public void ATimedTaskRemainsActionableAndAStartOnlyMeetingRemainsAnEvent()
    {
        var task = new DayNote(1, Day, TimeSpan.FromHours(9), "Send report", Kind: DayNoteKind.Task);
        var meeting = task with { Id = 2, Text = "Review", Kind = DayNoteKind.Meeting };
        Assert.True(DayNotes.IsTask(task)); Assert.False(DayNotes.IsMeeting(task));
        Assert.Equal(DayNoteKind.Meeting, DayNotes.KindOf(meeting)); Assert.False(DayNotes.IsTask(meeting));
        Assert.Null(DayNotes.EndsAt(meeting)); // There is no invented duration to draw or record.
        DayNotes.Validate(task); DayNotes.Validate(meeting);
        Assert.Contains(task, DayNotes.ComingUp(new[] { task, meeting }, Day.AddHours(12), 7)); // An unfinished task does not vanish an hour after its due time.
        Assert.DoesNotContain(meeting, DayNotes.ComingUp(new[] { task, meeting }, Day.AddHours(12), 7));
        Assert.True(DayNotes.IsMeeting(meeting with { End = TimeSpan.FromHours(10) }));
    }

    [Fact]
    public void AnUndatedTaskHasNoCalendarOrDeadlinePosition()
    {
        var task = new DayNote(1, Day.AddDays(30), null, "Capture for later", Kind: DayNoteKind.Task, HasDate: false);
        DayNotes.Validate(task);
        Assert.Single(DayNotes.Todos(new[] { task }, Day));
        Assert.Empty(DayNotes.ComingUp(new[] { task }, Day, 180));
        Assert.Null(DayNotes.NextDeadline(new[] { task }));
        Assert.Throws<InvalidOperationException>(() => DayNotes.DueAt(task));
        Assert.Empty(DayNotes.Todos(new[] { task with { Done = true } }, Day));
    }

    [Fact]
    public void AnExplicitTypeDoesNotDependOnTitlePunctuation()
    {
        var task = new DayNote(1, Day, null, "! Keep this literal title", Kind: DayNoteKind.Task);
        var deadline = task with { Text = "Report due", Kind = DayNoteKind.Deadline };
        Assert.False(DayNotes.IsDeadline(task)); Assert.Equal(task.Text, DayNotes.DisplayText(task));
        Assert.True(DayNotes.IsDeadline(deadline)); Assert.Equal(Day.AddHours(17), DayNotes.DueAt(deadline));
        Assert.True(DayNotes.IsDeadline(task with { Kind = null })); // Legacy markers still mean the same thing.
        var legacyRange = task with { Kind = null, At = TimeSpan.FromHours(9), End = TimeSpan.FromHours(10) };
        Assert.True(DayNotes.IsMeeting(legacyRange)); // Old data could combine a marker and a range; do not reinterpret it on upgrade.
        Assert.False(DayNotes.IsMeeting(legacyRange with { Kind = DayNoteKind.Deadline }));
    }

    [Fact]
    public void TypesDatesTimesAndCompletionSurviveAReopen()
    {
        using (var store = new TimerStore(folder))
        {
            var task = store.AddDayNote(Day, TimeSpan.FromHours(14), "Send report #AccountOne", kind: DayNoteKind.Task);
            store.AddDayNote(Day, null, "Capture for later", kind: DayNoteKind.Task, hasDate: false);
            store.AddDayNote(Day, TimeSpan.FromHours(15), "Review", kind: DayNoteKind.Meeting);
            store.AddDayNote(Day, TimeSpan.FromHours(16), "Submit report", kind: DayNoteKind.Deadline);
            store.UpdateDayNote(store.LoadDayNotes().Single(n => n.Id == task) with { Done = true });
        }
        using (var store = new TimerStore(folder))
        {
            var notes = store.LoadDayNotes(); Assert.Equal(4, notes.Count);
            var task = notes.Single(n => n.Text.StartsWith("Send report"));
            Assert.True(DayNotes.IsTask(task)); Assert.True(task.Done); Assert.Equal(TimeSpan.FromHours(14), task.At);
            Assert.Equal("Account one", NoteShorthand.AccountOf(task.Text, new[] { "Account one" }));
            Assert.False(notes.Single(n => n.Text == "Capture for later").HasDate);
            var meeting = notes.Single(n => n.Text == "Review"); Assert.Equal(DayNoteKind.Meeting, meeting.Kind); Assert.Null(meeting.End);
            Assert.True(DayNotes.IsDeadline(notes.Single(n => n.Text == "Submit report")));
            Assert.Equal(0L, store.Database.Scalar("SELECT COUNT(*) FROM entries"));
        }
    }

    [Fact]
    public void ARecurringTimedTaskKeepsItsTypeWhenItsTemplateIsUpdated()
    {
        long seriesId;
        using (var store = new TimerStore(folder))
        {
            seriesId = store.AddSeries(new NoteSeries(0, new Recurrence(1, Recurrence.Weekdays), Day, Day, "Send update", TimeSpan.FromHours(16), null, DayNoteKind.Task));
            store.AddDayNote(Day, TimeSpan.FromHours(16), "Send update", seriesId: seriesId, kind: DayNoteKind.Task);
            var template = store.LoadSeries().Single(); store.UpdateSeries(template with { At = TimeSpan.FromHours(17) });
        }
        using var reopened = new TimerStore(folder);
        var series = Assert.Single(reopened.LoadSeries()); Assert.Equal(seriesId, series.Id);
        Assert.Equal(DayNoteKind.Task, series.Kind); Assert.Equal(TimeSpan.FromHours(17), series.At);
        Assert.True(DayNotes.IsTask(Assert.Single(reopened.LoadDayNotes())));
    }

    [Fact]
    public void InvalidSchedulesAreRejectedBeforeAnyRecordChanges()
    {
        using var store = new TimerStore(folder);
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, null, "Undated meeting", kind: DayNoteKind.Meeting, hasDate: false));
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, null, "No start", kind: DayNoteKind.Meeting));
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, TimeSpan.FromHours(14), "Reverse", TimeSpan.FromHours(13), kind: DayNoteKind.Meeting));
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, TimeSpan.FromHours(14), "Undated timed task", kind: DayNoteKind.Task, hasDate: false));
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, null, "Undated repeat", seriesId: 1, kind: DayNoteKind.Task, hasDate: false));
        Assert.Throws<ArgumentException>(() => store.AddDayNote(Day, TimeSpan.FromHours(24), "Bad time", kind: DayNoteKind.Task));
        Assert.Empty(store.LoadDayNotes());
        var id = store.AddDayNote(Day, null, "Keep me", kind: DayNoteKind.Task);
        var original = Assert.Single(store.LoadDayNotes());
        Assert.Throws<ArgumentException>(() => store.UpdateDayNote(original with { Kind = DayNoteKind.Meeting }));
        Assert.Equal(original, Assert.Single(store.LoadDayNotes()));
        Assert.Throws<ArgumentException>(() => store.AddSeries(new NoteSeries(0, new Recurrence(1, 0), Day, Day, "Invalid", null, null, DayNoteKind.Meeting)));
        Assert.Empty(store.LoadSeries());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void OlderFormatsUpgradeWithoutChangingExistingNoteMeaning(int version)
    {
        Directory.CreateDirectory(folder);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, TimerDatabase.FileName), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE day_notes (id INTEGER PRIMARY KEY, day TEXT NOT NULL, at_time TEXT, text TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0"
                + (version >= 4 ? ", end_time TEXT" : "") + (version >= 5 ? ", series_id INTEGER" : "") + ");"
                + "INSERT INTO day_notes (id, day, at_time, text, done) VALUES (1, '2026-10-21', '14:00', 'Legacy call #AccountOne', 1), (2, '2026-10-21', NULL, '! Legacy deadline', 0);"
                + (version >= 5 ? "CREATE TABLE note_series (id INTEGER PRIMARY KEY, rule TEXT, start_day TEXT, generated_to TEXT, text TEXT, at_time TEXT, end_time TEXT);" : "")
                + $"PRAGMA user_version = {version};";
            command.ExecuteNonQuery();
        }
        using (var store = new TimerStore(folder))
        {
            Assert.Equal(6, store.Database.Version); var notes = store.LoadDayNotes(); Assert.Equal(2, notes.Count);
            Assert.All(notes, n => { Assert.Null(n.Kind); Assert.True(n.HasDate); });
            var call = notes.Single(n => n.Id == 1); var deadline = notes.Single(n => n.Id == 2);
            Assert.Equal(DayNoteKind.Meeting, DayNotes.KindOf(call)); Assert.True(call.Done);
            Assert.True(DayNotes.IsDeadline(deadline)); Assert.Equal("Legacy call", NoteShorthand.Display(call, new[] { "Account one" }));
        }
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(folder, "Backups"), "timer-before-format-6-*.db"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
            Assert.Equal((long)version, command.ExecuteScalar());
        }
        Assert.Throws<DatabaseTooNewException>(() => TimerDatabase.Open(folder, targetVersion: 5));
    }
}
