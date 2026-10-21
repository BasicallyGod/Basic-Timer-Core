using System.Globalization;
using System.Text.RegularExpressions;

namespace Timer.Core;

/// <summary>The activity type, independent of its date and time.</summary>
public enum DayNoteKind { Task = 0, Meeting = 1, Deadline = 2 }

// A null kind preserves the interpretation used before explicit types were introduced. Day is retained as capture context for an undated task,
// but HasDate is authoritative: that day must never become a due date or put the task in a calendar view.
/// <summary>A task or scheduled item. HasDate determines whether Day is a schedule date or only retained capture context.</summary>
public sealed record DayNote(long Id, DateTime Day, TimeSpan? At, string Text, bool Done = false, TimeSpan? End = null, long? SeriesId = null, DayNoteKind? Kind = null, bool HasDate = true);

public static class DayNotes
{
    private static readonly Regex Time = new(@"(?:^|\s)@?([01]?\d|2[0-3]):([0-5]\d)(?=\s|$)", RegexOptions.Compiled);

    /// <summary>Pulls a time such as 14:30 (or @14:30) out of what was typed. A note with no time is just a note for the day.</summary>
    public static (string Text, TimeSpan? At) Parse(string typed)
    {
        var match = Time.Match(typed);
        if (!match.Success) return (typed.Trim(), null);
        var at = new TimeSpan(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        var text = Regex.Replace(typed.Remove(match.Index, match.Length).Trim(), @"\s{2,}", " ");
        return (text.Length == 0 ? typed.Trim() : text, at);
    }

    /// <summary>The notes still to come: those today or later that are not done, soonest first, within a number of days.</summary>
    public static List<DayNote> ComingUp(IEnumerable<DayNote> notes, DateTime now, int days)
        => notes.Where(n => n.HasDate && !n.Done && n.Day.Date >= now.Date && n.Day.Date <= now.Date.AddDays(days) && (n.Day.Date > now.Date || IsTask(n) || n.At is null || n.Day.Date + n.At.Value >= now.AddHours(-1)))
            .OrderBy(n => n.Day.Date).ThenBy(n => n.At ?? TimeSpan.FromDays(1)).ThenBy(n => n.Id).ToList();

    /// <summary>A task can have an optional date and time; setting a time never turns an explicitly typed task into a meeting.</summary>
    public static bool IsTask(DayNote note) => KindOf(note) == DayNoteKind.Task;
    /// <summary>A bounded meeting suitable for drawing on a timeline. Use KindOf to also recognise open-ended meeting events.</summary>
    public static bool IsMeeting(DayNote note) => note.HasDate && (note.Kind is null || note.Kind == DayNoteKind.Meeting) && note.At is { } start && note.End is { } end && end > start;
    /// <summary>When a meeting ends: its day at its end time, or null for a note that is not a meeting.</summary>
    public static DateTime? EndsAt(DayNote note) => IsMeeting(note) ? note.Day.Date + note.End!.Value : null;
    /// <summary>
    /// The to-do list: tasks dated today or earlier that are still open, so one not finished carries over to the next day, plus the ones ticked off today.
    /// Tasks dated later wait in Coming up until their day.
    /// </summary>
    public static List<DayNote> Todos(IEnumerable<DayNote> notes, DateTime now)
        => notes.Where(n => IsTask(n) && (!n.HasDate || n.Day.Date <= now.Date) && (!n.Done || n.HasDate && n.Day.Date == now.Date)).OrderBy(n => n.Day.Date).ThenBy(n => n.Id).ToList();
    /// <summary>A note whose words start with ! is a deadline. The marker stays in the text, so it shows where it is edited.</summary>
    public static bool IsDeadline(DayNote note) => KindOf(note) == DayNoteKind.Deadline;
    /// <summary>The explicit item type, with the old time/marker interpretation for notes written by earlier versions. A meeting need not have an end.</summary>
    public static DayNoteKind KindOf(DayNote note) => note.Kind ?? (note.Text.StartsWith('!') ? DayNoteKind.Deadline : note.At is not null ? DayNoteKind.Meeting : DayNoteKind.Task);

    /// <summary>Checks new scheduling data before storage. Legacy untyped rows keep their previous interpretation.</summary>
    public static void Validate(DayNote note)
    {
        if (note.Kind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("Unknown note kind.", nameof(note));
        foreach (var time in new[] { note.At, note.End })
            if (time is { } value && (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))) throw new ArgumentException("Times must fall within one day.", nameof(note));
        if (!note.HasDate && (!IsTask(note) || note.At is not null || note.End is not null || note.SeriesId is not null))
            throw new ArgumentException("Only a task without times or recurrence can be undated.", nameof(note));
        if (note.Kind == DayNoteKind.Meeting && note.At is null) throw new ArgumentException("A meeting needs a start time.", nameof(note));
        if (note.Kind == DayNoteKind.Meeting && note.End is { } end && end <= note.At) throw new ArgumentException("The end must be after the start.", nameof(note));
        if (note.Kind is DayNoteKind.Task or DayNoteKind.Deadline && note.End is not null) throw new ArgumentException("Only meetings have an end time.", nameof(note));
    }
    /// <summary>The words without the deadline marker, for showing where the marker itself would be noise.</summary>
    public static string DisplayText(DayNote note) => note.Kind is null || IsDeadline(note) ? note.Text.TrimStart('!', ' ') : note.Text;
    /// <summary>When a deadline falls due: its day at its time, or at 17:00 when it has none.</summary>
    public static DateTime DueAt(DayNote note) => note.HasDate ? note.Day.Date + (note.At ?? TimeSpan.FromHours(17)) : throw new InvalidOperationException("An undated task has no due time.");
    /// <summary>The deadline to watch: the soonest one not yet ticked off. One already past stays until it is done, so it is not forgotten.</summary>
    public static DayNote? NextDeadline(IEnumerable<DayNote> notes) => notes.Where(n => n.HasDate && !n.Done && IsDeadline(n)).OrderBy(DueAt).ThenBy(n => n.Id).FirstOrDefault();
}
